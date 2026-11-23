#nullable enable
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using OmenMax16.Models;
using OmenMax16.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: SuppressMessage("", "WFO1000")]

namespace OmenMax16.Forms;

public sealed record CurvePoint(int TemperatureCelsius, int FanPercent);

public sealed record FanCurveDefinition(IReadOnlyList<CurvePoint> CpuPoints, IReadOnlyList<CurvePoint> GpuPoints)
{
    public IReadOnlyList<CurvePoint> Points => CpuPoints;
}

/// <summary>
/// 팬 커브 저장 파일 형식. COOL 커브(fan-curve.json)와 QUIET 커브(quiet-fan-curve.json)가
/// 같은 형식을 공유합니다.
/// </summary>
public sealed class FanCurveFile
{
    public CurvePoint[] Cpu { get; set; } = Array.Empty<CurvePoint>();
    public CurvePoint[] Gpu { get; set; } = Array.Empty<CurvePoint>();
}

public static class FanCurveLimits
{
    public const int CoolMaxPercent = 100;
    public const int QuietMaxPercent = 100;
}

/// <summary>
/// 메인 폼이 이미 수집한 하드웨어 상태의 요약본. SendUIRefresh(WebView) 와
/// FormGameHud(네이티브 오버레이) 가 이 하나의 스냅샷을 공유해서 중복 계산/폴링을 피합니다.
/// </summary>
internal sealed record HudSnapshot(
    float CpuUsage, float? CurrentFps, float CpuTemp, float CpuClockGhz, float CpuPower, float? CpuVoltage,
    bool GpuActive, float GpuUsage, float GpuTemp, float GpuClockGhz, float GpuPower,
    int? FanCpuRpm, int? FanGpuRpm, bool FanCpuEstimated, bool FanGpuEstimated,
    int? FanCpuPercent, int? FanGpuPercent,
    int BatteryPercent, float BatteryWatts, bool BatteryCharging,
    string PowerProfileLabel, uint RefreshRateHz,
    bool GpuPresent, bool GpuEnabled, string GpuModeStatus, int GpuRequestedWatts
);

public static class GpuPowerEnforcer
{
    public static bool IsGpuEnabled()
    {
        try
        {
            using ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                "SELECT ConfigManagerErrorCode FROM Win32_PnPEntity WHERE PNPClass = 'Display' AND Name LIKE '%NVIDIA%'");
            foreach (ManagementObject mo in searcher.Get())
            {
                uint errorCode = (uint)(mo["ConfigManagerErrorCode"] ?? 0u);
                return errorCode == 0;
            }
        }
        catch { }
        return true;
    }

    public static bool IsGpuPresent()
    {
        try
        {
            using ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_PnPEntity WHERE PNPClass = 'Display' AND Name LIKE '%NVIDIA%'");
            foreach (ManagementObject _ in searcher.Get()) return true;
        }
        catch { }
        return false;
    }

    public static bool SetDiscreteGpuState(bool enable, out string message)
    {
        try
        {
            using ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_PnPEntity WHERE PNPClass = 'Display' AND Name LIKE '%NVIDIA%'");
            bool isProcessed = false;
            foreach (ManagementObject mo in searcher.Get())
            {
                mo.InvokeMethod(enable ? "Enable" : "Disable", null);
                isProcessed = true;
            }
            if (isProcessed)
            {
                message = enable ? "GPU 가 활성화되었습니다." : "GPU 가 강제로 차단되었습니다.";
                return true;
            }
            message = "대상 GPU 를 찾을 수 없습니다.";
            return false;
        }
        catch (Exception ex)
        {
            message = $"GPU 제어 실패: {ex.Message}";
            return false;
        }
    }
}

public partial class FormMain : Form
{
    private static readonly bool UseQuietAutoCurve = true;

    private HardwareCombinedMonitoringService? _hwMonitor;
    private BatteryMonitoringService? _battery;

    private bool _gpuActive;
    private int _gpuD0Samples;

    private readonly object _wmiLock = new();

    private readonly PowerProfileService _powerProfile = new();
    private readonly HpOmenBiosTransport _biosTransport = new();
    private readonly HpOmenFanController _fanController;
    private readonly HpOmenGpuPowerController _gpuPower;
    private bool _gpuCtgp = true;
    private byte _gpuDState = 1;
    private readonly PowerLimitService _powerLimitService = new();

    private readonly CancellationTokenSource _monitoringCts = new();

    private readonly object _stateLock = new();
    private readonly object _gpuLimitLock = new();

    private NotifyIcon? _tray;
    private WebView2 _webView = null!;
    private int _webViewRetryCount = 0;
    private bool _isWebViewRebuilding = false;

    private FormGameHud? _hud;
    private FormMediaHud? _mediaHud;
    private bool _isDarkTheme = false;
    private ToolStripMenuItem? _trayItemMediaHud;
    private CpuStatus _lastCpu = new();
    private GpuStatus _lastGpu = new();
    private BatteryStatus _lastBattery = new();

    private HpOmenFanReadings? _lastFan;

    private FanCurveDefinition? _coolCurve;
    private FanCurveDefinition? _quietCurve;

    private const int QuietCurveMaxPercent = FanCurveLimits.QuietMaxPercent;
    private const int CoolCurveMaxPercent = FanCurveLimits.CoolMaxPercent;

    private DateTime _lastFanApplyUtc = DateTime.MinValue;
    private int? _gpuClockCapMhz;
    private int? _gpuClockCapAppliedMhz;
    private int _clockCapFailCount;
    private int _clockCapBusy;
    private AppSettings _settings = new();
    private readonly object _settingsLock = new();
    private System.Threading.Timer? _sessionTimer;
    private int? _lastCpuFanRequest;
    private int? _lastGpuFanRequest;
    private int _fanDecreaseHoldCount;

    private ToolStripMenuItem? _trayItemHud;
    private ToolStripMenuItem? _trayItemFanAuto;
    private ToolStripMenuItem? _trayItemFanQuiet;
    private ToolStripMenuItem? _trayItemFanCool;
    private ToolStripMenuItem? _trayItemPowerEco;
    private ToolStripMenuItem? _trayItemPowerBalanced;
    private ToolStripMenuItem? _trayItemPowerMax;

    private string _fanError = "";
    private bool _fanCommandBusy;
    private bool _fanSupported = true;
    private bool _coolMode;
    private bool _quietMode;
    private bool _fanOverrideApplied;
    private bool _closing;
    private int _sensorReadFailCount = 0;

    private HpOmenFanPreset _currentFanPreset = HpOmenFanPreset.Auto;
    private PerformanceProfile _currentPowerProfile = PerformanceProfile.Balanced;

    private bool? _wasCharging = null;
    private PerformanceProfile _lastAcPowerProfile = PerformanceProfile.Balanced;
    private bool _isBatteryLimitActive = false;
    private bool _gpuDynamicBoost = true;
    private int _gpuModeWatts = 175;
    private int? _gpuCommandedWatts;
    private string _gpuModeStatus = "GPU 대기 중";
    private string _lastCpuTuningStatus = "Intel 275HX 게이밍 전력 제어 준비됨";

    // Intel Core Ultra 9 275HX (15~160W) + TPP 250W (80W + 170W) 게이밍 상태
    private int _cpuPl1Watts = AppSettings.DefaultPl1Watts;
    private int _cpuPl2Watts = AppSettings.DefaultPl2Watts;
    private int _cpuTauSeconds = AppSettings.DefaultTauSeconds;
    private bool _tpp250AutoBalance = true;
    private int _tppCpuSimultaneousWatts = AppSettings.DefaultTppCpuWatts;

    [DllImport("user32.dll")] private static extern ushort ReleaseCapture();
    [DllImport("user32.dll")] private static extern uint SendMessage(IntPtr hWnd, uint msg, uint wp, uint lp);
    public static void DragWindow(IntPtr handle) { ReleaseCapture(); SendMessage(handle, 0x112, 0xF012, 0); }

    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern int ChangeDisplaySettings(ref DEVMODE devMode, uint flags);

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const uint CDS_UPDATEREGISTRY = 0x01;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion; public ushort dmDriverVersion; public ushort dmSize;
        public ushort dmDriverExtra; public uint dmFields; public int dmPositionX; public int dmPositionY;
        public int dmDisplayOrientation; public int dmDisplayFixedOutput; public short dmColor;
        public short dmDuplex; public short dmYResolution; public short dmTTOption; public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels; public uint dmBitsPerPel; public uint dmPelsWidth; public uint dmPelsHeight;
        public uint dmDisplayFlags; public uint dmDisplayFrequency; public uint dmICMMethod;
        public uint dmICMIntent; public uint dmMediaType; public uint dmDitherType;
        public uint dmReserved1; public uint dmReserved2; public uint dmPanningWidth; public uint dmPanningHeight;
    }

    /// <summary>
    /// NVIDIA GeForce RTX 5070 Ti Laptop GPU(및 모든 NVIDIA 외장 GPU)를 절전 슬립(D3cold) 상태에서
    /// 깨우지 않고 D0 활성 상태 여부만 SetupAPI 장치 전원 속성으로 패시브 조회합니다.
    /// </summary>
    private static class PassiveNvidiaGpuPowerState
    {
        private const uint DigcfPresent = 0x00000002;
        private const uint SpdrpFriendlyName = 0x0000000C;
        private const uint SpdrpDeviceDesc = 0x00000000;
        private const uint SpdrpDevicePowerData = 0x0000001E;
        private static readonly Guid DisplayClass = new("4d36e968-e325-11ce-bfc1-08002be10318");

        [StructLayout(LayoutKind.Sequential)]
        private struct DevInfo { public int cbSize; public Guid classGuid; public uint devInst; public IntPtr reserved; }

        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true)]
        private static extern IntPtr GetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", EntryPoint = "SetupDiEnumDeviceInfo", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDeviceInfo(IntPtr set, uint index, ref DevInfo data);
        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceRegistryPropertyW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProperty(IntPtr set, ref DevInfo data, uint property, out uint propertyType, [Out] byte[] bytes, uint capacity, out uint required);
        [DllImport("setupapi.dll", EntryPoint = "SetupDiDestroyDeviceInfoList", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyDeviceInfoList(IntPtr set);

        private static byte[]? ReadProperty(IntPtr set, ref DevInfo info, uint property)
        {
            byte[] bytes = new byte[512];
            return GetProperty(set, ref info, property, out _, bytes, (uint)bytes.Length, out uint required)
                   && required <= bytes.Length ? bytes[..(int)required] : null;
        }

        public static bool IsD0()
        {
            try
            {
                Guid display = DisplayClass;
                IntPtr set = GetClassDevs(ref display, IntPtr.Zero, IntPtr.Zero, DigcfPresent);
                if (set == new IntPtr(-1)) return false;
                try
                {
                    for (uint index = 0; index < 64; index++)
                    {
                        DevInfo info = new() { cbSize = Marshal.SizeOf<DevInfo>() };
                        if (!EnumDeviceInfo(set, index, ref info)) break;
                        byte[]? nameBytes = ReadProperty(set, ref info, SpdrpFriendlyName) ?? ReadProperty(set, ref info, SpdrpDeviceDesc);
                        if (nameBytes == null) continue;
                        string name = Encoding.Unicode.GetString(nameBytes).TrimEnd('\0');
                        bool isNvidiaDiscrete =
                            (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                             name.Contains("GeForce", StringComparison.OrdinalIgnoreCase)) &&
                            (name.Contains("RTX", StringComparison.OrdinalIgnoreCase) ||
                             name.Contains("GTX", StringComparison.OrdinalIgnoreCase) ||
                             name.Contains("Laptop", StringComparison.OrdinalIgnoreCase));
                        if (!isNvidiaDiscrete) continue;

                        byte[]? power = ReadProperty(set, ref info, SpdrpDevicePowerData);
                        return power is { Length: >= 8 } && BitConverter.ToUInt32(power, 0) >= 8 && BitConverter.ToUInt32(power, 4) == 1;
                    }
                    return false;
                }
                finally { DestroyDeviceInfoList(set); }
            }
            catch { return false; }
        }
    }

    public FormMain()
    {
        InitializeComponent();
        _fanController = new HpOmenFanController(_biosTransport);
        _gpuPower = new HpOmenGpuPowerController(_biosTransport);
        _fanSupported = _fanController.IsAvailable(out string fanAvailabilityMessage);
        if (!_fanSupported) _fanError = $"팬 인터페이스 미지원: {fanAvailabilityMessage}";
        LoadSavedCurves();
        InitPersistedSettings();
        Text = "OMEN MAX 16";
        ClientSize = new Size(720, 560);
        MinimumSize = Size; MaximumSize = Size;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        BackColor = Color.FromArgb(7, 7, 10);

        _webView = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_webView);

        _isBatteryLimitActive = GetHpBatteryHealthManagerStatus();

        Load += async (_, _) =>
        {
            PositionAboveTaskbar();

            if (Environment.TickCount64 < 120_000)
            {
                await Task.Delay(3000);
            }

            await InitializeWebViewAsync();
            _ = Task.Run(HardwareMonitoringLoop);
        };
        Deactivate += (_, _) =>
        {
            if (!_closing) Hide();
        };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { Hide(); e.Handled = true; } };

        CreateTray();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            if (_webView.CoreWebView2 == null)
            {
                string userData = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OmenMax16", "WebView2");
                System.IO.Directory.CreateDirectory(userData);
                var env = await CoreWebView2Environment.CreateAsync(null, userData, new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--disable-gpu" });

                await _webView.EnsureCoreWebView2Async(env);
                if (IsDisposed || _webView.IsDisposed) return;
            }

            _webView.DefaultBackgroundColor = Color.FromArgb(7, 7, 10);
            var core = _webView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsZoomControlEnabled = false;

            core.ProcessFailed -= OnWebViewProcessFailed;
            core.ProcessFailed += OnWebViewProcessFailed;

            core.NavigationCompleted -= OnWebViewNavigationCompleted;
            core.NavigationCompleted += OnWebViewNavigationCompleted;

            core.WebMessageReceived -= OnWebViewMessageReceived;
            core.WebMessageReceived += OnWebViewMessageReceived;

            string webDir = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "web");
            bool localOk = System.IO.File.Exists(System.IO.Path.Combine(webDir, "tailwind.js"))
                        && System.IO.File.Exists(System.IO.Path.Combine(webDir, "react.production.min.js"))
                        && System.IO.File.Exists(System.IO.Path.Combine(webDir, "react-dom.production.min.js"))
                        && System.IO.File.Exists(System.IO.Path.Combine(webDir, "babel.min.js"));
            if (localOk)
                core.SetVirtualHostNameToFolderMapping("omenmax.local", webDir, CoreWebView2HostResourceAccessKind.Allow);

            string html = GetHtmlContent();
            if (localOk)
            {
                html = html
                    .Replace("https://cdn.tailwindcss.com", "https://omenmax.local/tailwind.js")
                    .Replace("https://unpkg.com/react@18/umd/react.production.min.js", "https://omenmax.local/react.production.min.js")
                    .Replace("https://unpkg.com/react-dom@18/umd/react-dom.production.min.js", "https://omenmax.local/react-dom.production.min.js")
                    .Replace("https://unpkg.com/@babel/standalone/babel.min.js", "https://omenmax.local/babel.min.js");
            }
            core.NavigateToString(html);

            _webViewRetryCount = 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WebView2 초기화 실패: {ex}");
            TriggerWebViewRecovery();
        }
    }

    private void OnWebViewProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Debug.WriteLine($"WebView2 프로세스 실패: {e.ProcessFailedKind}");
        TriggerWebViewRecovery();
    }

    private void OnWebViewNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            Debug.WriteLine($"WebView2 네비게이션 실패: {e.WebErrorStatus}");
            TriggerWebViewRecovery();
        }
    }

    private async void TriggerWebViewRecovery()
    {
        if (_isWebViewRebuilding || _closing || IsDisposed) return;

        if (_webViewRetryCount >= 3)
        {
            Debug.WriteLine("WebView2 복구 3 회 초과. 애플리케이션을 재시작합니다.");
            Application.Restart();
            Environment.Exit(0);
            return;
        }

        _isWebViewRebuilding = true;
        _webViewRetryCount++;

        try
        {
            await Task.Delay(2000);
            if (IsDisposed) return;

            Controls.Remove(_webView);
            _webView.Dispose();

            _webView = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_webView);

            await InitializeWebViewAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WebView2 복구 중 예외 발생: {ex}");
        }
        finally
        {
            _isWebViewRebuilding = false;
        }
    }

    private void OnWebViewMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            string jsonText = e.WebMessageAsJson;
            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.String)
            {
                using var inner = JsonDocument.Parse(root.GetString() ?? "{}");
                ProcessCommand(inner.RootElement);
            }
            else ProcessCommand(root);
        }
        catch { }
    }

    private void PostToWeb(object payload)
    {
        try
        {
            if (_webView.CoreWebView2 == null) return;
            _webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
        }
        catch { }
    }

    private static string PayloadString(JsonElement root) =>
        root.TryGetProperty("payload", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";

    private static string NormalizeCurveKind(string? kind) =>
        string.Equals(kind, "quiet", StringComparison.OrdinalIgnoreCase) ? "quiet" : "cool";

    private void ProcessCommand(JsonElement root)
    {
        string cmd = root.TryGetProperty("cmd", out var cmdProp) ? cmdProp.GetString() ?? "" : "";
        switch (cmd)
        {
            case "DRAG_WINDOW": DragWindow(Handle); break;
            case "CLOSE_WINDOW": Hide(); break;
            case "TOGGLE_HUD": ToggleHud(); break;
            case "TOGGLE_MEDIA_HUD": ToggleMediaHud(); break;
            case "SET_THEME":
                bool nextDark = root.TryGetProperty("isDark", out var darkProp) && darkProp.ValueKind == JsonValueKind.True;
                _isDarkTheme = nextDark;
                _hud?.SetDarkTheme(nextDark);
                _mediaHud?.SetDarkTheme(nextDark);
                break;
            case "OPEN_CPU_POWER":
            case "OPEN_CPU_UV":
                PostToWeb(new
                {
                    cmd = "OPEN_CPU_POWER_MODAL",
                    pl1 = _cpuPl1Watts,
                    pl2 = _cpuPl2Watts,
                    tau = _cpuTauSeconds,
                    tpp250AutoBalance = _tpp250AutoBalance,
                    tppCpuWatts = _tppCpuSimultaneousWatts,
                    status = _lastCpuTuningStatus
                });
                break;
            case "SAVE_CPU_POWER":
            case "SAVE_CPU_UV":
                if (root.TryGetProperty("payload", out var cpuProp) && cpuProp.ValueKind == JsonValueKind.Object)
                {
                    int pl1 = cpuProp.TryGetProperty("pl1", out var pl1P) && pl1P.TryGetInt32(out int p1v) ? p1v : _cpuPl1Watts;
                    int pl2 = cpuProp.TryGetProperty("pl2", out var pl2P) && pl2P.TryGetInt32(out int p2v) ? p2v : _cpuPl2Watts;
                    int tau = cpuProp.TryGetProperty("tau", out var tauP) && tauP.TryGetInt32(out int tv) ? tv : _cpuTauSeconds;
                    bool tppAuto = cpuProp.TryGetProperty("tpp250AutoBalance", out var tppP) &&
                        (tppP.ValueKind == JsonValueKind.True || tppP.ValueKind == JsonValueKind.False)
                        ? tppP.GetBoolean()
                        : _tpp250AutoBalance;
                    int tppCpuW = cpuProp.TryGetProperty("tppCpuWatts", out var tppWProp) && tppWProp.TryGetInt32(out int twv)
                        ? twv
                        : _tppCpuSimultaneousWatts;

                    _cpuPl1Watts = Math.Clamp(pl1, AppSettings.MinCpuWatts, AppSettings.MaxCpuWatts);
                    _cpuPl2Watts = Math.Clamp(Math.Max(pl2, _cpuPl1Watts), AppSettings.MinCpuWatts, AppSettings.MaxCpuWatts);
                    _cpuTauSeconds = Math.Clamp(tau, 1, 56);
                    _tpp250AutoBalance = tppAuto;
                    _tppCpuSimultaneousWatts = Math.Clamp(tppCpuW, 50, 110);

                    bool gpuHeavyNow;
                    lock (_stateLock)
                    {
                        gpuHeavyNow = _gpuActive && ((_lastGpu.PowerWatts ?? 0f) >= 55f || (_lastGpu.UsagePercent ?? 0f) >= 40f);
                    }

                    PersistSettings();
                    SendUIRefresh();

                    int snapPl1 = _cpuPl1Watts, snapPl2 = _cpuPl2Watts, snapTau = _cpuTauSeconds, snapTppCpu = _tppCpuSimultaneousWatts;
                    bool snapTppAuto = _tpp250AutoBalance;
                    _ = Task.Run(() =>
                    {
                        IntelCpuTuningService.ApplyTuning(
                            snapPl1,
                            snapPl2,
                            snapTau,
                            gpuHeavyNow,
                            snapTppAuto,
                            snapTppCpu);
                        _lastCpuTuningStatus = IntelCpuTuningService.StatusMessage;
                        if (Visible && !_closing)
                        {
                            try { BeginInvoke(new Action(SendUIRefresh)); } catch { }
                        }
                    });
                }
                break;
            case "SET_POWER":
                string power = PayloadString(root);
                if (power == "efficient") ManualSetPowerProfile(PerformanceProfile.BestPowerEfficiency);
                else if (power == "balanced") ManualSetPowerProfile(PerformanceProfile.Balanced);
                else if (power == "performance") ManualSetPowerProfile(PerformanceProfile.BestPerformance);
                break;
            case "SET_FAN_MODE":
                string mode = PayloadString(root).ToLowerInvariant();
                if (mode == "auto") ApplyPreset(HpOmenFanPreset.Auto);
                else if (mode == "quiet") ApplyPreset(HpOmenFanPreset.Quiet);
                else if (mode is "cool" or "custom") ApplyPreset(HpOmenFanPreset.Performance);
                break;

            case "OPEN_FAN_CURVE": OpenFanCurve(NormalizeCurveKind(PayloadString(root)), false); break;
            case "GET_DEFAULT_CURVE": OpenFanCurve(NormalizeCurveKind(PayloadString(root)), true); break;
            case "OPEN_QUIET_CURVE": OpenFanCurve("quiet", false); break;
            case "OPEN_COOL_CURVE":
            case "OPEN_CUSTOM_CURVE": OpenFanCurve("cool", false); break;

            case "SAVE_FAN_CURVE": SaveFanCurve(root, null); break;
            case "SAVE_QUIET_CURVE": SaveFanCurve(root, "quiet"); break;
            case "SAVE_COOL_CURVE":
            case "SAVE_CUSTOM_CURVE": SaveFanCurve(root, "cool"); break;

            case "TOGGLE_REFRESH_RATE":
                ApplyDisplayRefreshRate(GetCurrentDisplayRefreshRate() == 60 ? 240u : 60u);
                SendUIRefresh();
                break;
            case "TOGGLE_BATTERY_LOCK": ToggleHpBatteryHealthManager(); break;
            case "SET_GPU_POWER":
                if (root.TryGetProperty("payload", out var gpProp) && gpProp.ValueKind == JsonValueKind.Object)
                {
                    bool ppab = gpProp.TryGetProperty("ppab", out var ppabP) && ppabP.ValueKind == JsonValueKind.True;
                    bool ctgp = gpProp.TryGetProperty("ctgp", out var ctgpP) && ctgpP.ValueKind == JsonValueKind.True;
                    int dstate = gpProp.TryGetProperty("dstate", out var dsP) && dsP.TryGetInt32(out int dsv) ? dsv : 1;
                    HandleSetGpuPower(ctgp, ppab, dstate);
                }
                break;
            case "SET_GPU_CLOCK_CAP":
                if (root.TryGetProperty("payload", out var capProp) &&
                    capProp.ValueKind == JsonValueKind.Number && capProp.TryGetInt32(out int cap))
                    HandleSetGpuClockCap(cap > 0 ? Math.Clamp(cap, 1200, 2800) : (int?)null);
                break;
            case "SET_GPU_MODE":
                if (root.TryGetProperty("payload", out var gpuProp) && gpuProp.TryGetInt32(out int watts))
                {
                    HandleSetGpuMode(watts);
                }
                break;
            case "SET_GPU_DYNAMIC_BOOST":
                if (root.TryGetProperty("payload", out var boostProp) &&
                    (boostProp.ValueKind == JsonValueKind.True || boostProp.ValueKind == JsonValueKind.False))
                {
                    bool nextBoost = boostProp.GetBoolean();
                    HandleSetGpuPower(_gpuCtgp, nextBoost, _gpuDState);
                }
                break;
        }
    }

    private void ToggleHud()
    {
        if (_hud is null || _hud.IsDisposed)
        {
            _hud = new FormGameHud(this, _isDarkTheme);
            _hud.Show();
        }
        else
        {
            _hud.Close();
            _hud = null;
        }
    }

    private void ToggleMediaHud()
    {
        if (_mediaHud is null || _mediaHud.IsDisposed)
        {
            _mediaHud = new FormMediaHud(_isDarkTheme);
            _mediaHud.Show();
        }
        else
        {
            _mediaHud.Close();
            _mediaHud = null;
        }
    }

    internal HudSnapshot GetHudSnapshot() => BuildSnapshot();

    private HudSnapshot BuildSnapshot()
    {
        CpuStatus snapCpu; GpuStatus snapGpu; BatteryStatus snapBattery;
        bool snapGpuActive;
        HpOmenFanReadings? snapFan;
        PerformanceProfile snapPowerProfile;

        lock (_stateLock)
        {
            snapCpu = _lastCpu; snapGpu = _lastGpu; snapBattery = _lastBattery; snapGpuActive = _gpuActive;
            snapFan = _lastFan;
            snapPowerProfile = _currentPowerProfile;
        }

        int? cpuFanPct = snapFan?.CpuLevel;
        int? gpuFanPct = snapFan?.GpuLevel;

        int? measuredCpuRpm = snapFan?.CpuRpm;
        int? measuredGpuRpm = snapFan?.GpuRpm;

        bool cpuMeasured = measuredCpuRpm is >= 0 and <= 20000;
        bool gpuMeasured = measuredGpuRpm is >= 0 and <= 20000;

        int? cpuRpmOutput = cpuMeasured ? measuredCpuRpm
            : (FanRpmEstimator.CalculateRpmFromRawLevel(snapFan?.RawNotebookFanLevel) ?? FanRpmEstimator.CalculateRpm(cpuFanPct));
        int? gpuRpmOutput = gpuMeasured ? measuredGpuRpm
            : (FanRpmEstimator.CalculateRpmFromRawLevel(snapFan?.RawNotebookFanLevelGpu) ?? FanRpmEstimator.CalculateRpm(gpuFanPct));

        float batWatts = ReadBatteryWatts(snapBattery) ?? 0f;

        int currentGpuWatts; string currentGpuStatus;
        lock (_gpuLimitLock) { currentGpuWatts = _gpuModeWatts; currentGpuStatus = _gpuModeStatus; }

        string profileLabel = snapPowerProfile == PerformanceProfile.BestPowerEfficiency ? "ECO"
            : (snapPowerProfile == PerformanceProfile.BestPerformance ? "MAX" : "BALANCED");

        return new HudSnapshot(
            CpuUsage: snapCpu.UsagePercent ?? 0,
            CurrentFps: FpsMonitorService.CurrentFps,
            CpuTemp: snapCpu.TemperatureCelsius ?? 0,
            CpuClockGhz: (snapCpu.ClockMHz ?? 0) / 1000f,
            CpuPower: snapCpu.PowerWatts ?? 0,
            GpuActive: snapGpuActive,
            GpuUsage: snapGpuActive ? snapGpu.UsagePercent ?? 0 : 0,
            GpuTemp: snapGpuActive ? snapGpu.TemperatureCelsius ?? 0 : 0,
            GpuClockGhz: snapGpuActive ? (snapGpu.CoreClockMHz ?? 0) / 1000f : 0,
            GpuPower: snapGpuActive ? snapGpu.PowerWatts ?? 0 : 0,
            CpuVoltage: snapCpu.VoltageVolts,
            FanCpuRpm: cpuRpmOutput,
            FanGpuRpm: gpuRpmOutput,
            FanCpuEstimated: !cpuMeasured && cpuRpmOutput.HasValue,
            FanGpuEstimated: !gpuMeasured && gpuRpmOutput.HasValue,
            FanCpuPercent: cpuFanPct,
            FanGpuPercent: gpuFanPct,
            BatteryPercent: Math.Clamp(snapBattery.ChargePercent ?? 0, 0, 100),
            BatteryWatts: batWatts,
            BatteryCharging: snapBattery.IsCharging,
            PowerProfileLabel: profileLabel,
            RefreshRateHz: GetCurrentDisplayRefreshRate(),
            GpuPresent: GpuPowerEnforcer.IsGpuPresent(),
            GpuEnabled: GpuPowerEnforcer.IsGpuEnabled(),
            GpuModeStatus: currentGpuStatus,
            GpuRequestedWatts: currentGpuWatts
        );
    }

    private void HandleSetGpuMode(int watts)
    {
        Task.Run(() =>
        {
            if (watts < 0)
            {
                GpuPowerEnforcer.SetDiscreteGpuState(false, out string _);
                lock (_gpuLimitLock)
                {
                    _gpuModeWatts = -1;
                    _gpuCommandedWatts = null;
                    _gpuModeStatus = "GPU 비활성화됨 (OFF)";
                }
                if (Visible && !_closing) { try { BeginInvoke(new Action(SendUIRefresh)); } catch { } }
                return;
            }

            bool wasEnabled = GpuPowerEnforcer.IsGpuEnabled();
            if (!wasEnabled)
            {
                lock (_gpuLimitLock) { _gpuModeStatus = "GPU 재활성화 중..."; }
                if (Visible && !_closing) { try { BeginInvoke(new Action(SendUIRefresh)); } catch { } }

                GpuPowerEnforcer.SetDiscreteGpuState(true, out string _);
                Thread.Sleep(1500);
            }

            lock (_gpuLimitLock)
            {
                _gpuModeWatts = watts;
                _gpuCommandedWatts = null;
                _gpuModeStatus = _gpuActive ? "명령 준비 중" : "GPU 대기 중 · 활성화 시 적용됨";
            }
            QueueGpuLimitIfAwake();
            if (Visible && !_closing) { try { BeginInvoke(new Action(SendUIRefresh)); } catch { } }
        });
    }

    private void HandleSetGpuClockCap(int? mhz)
    {
        lock (_gpuLimitLock)
        {
            _gpuClockCapMhz = mhz;
            if (mhz is null) _gpuClockCapAppliedMhz = null;
            if (_gpuModeWatts < 0) _gpuModeWatts = (_gpuCtgp && _gpuDynamicBoost) ? 175 : 140;
            _clockCapFailCount = 0;
            _gpuModeStatus = "적용 중...";
        }
        PersistSettings();
        AppSettingsStore.Log($"[GPU클럭] 적용 요청: {(mhz?.ToString() ?? "해제")}");
        if (Visible && !_closing) { try { BeginInvoke(new Action(SendUIRefresh)); } catch { } }

        Task.Run(() =>
        {
            bool ok = _powerLimitService.SetGpuClockCap(mhz, out string msg);
            AppSettingsStore.Log($"[GPU클럭] 결과 ok={ok}: {msg}");
            lock (_gpuLimitLock)
            {
                if (ok) { _gpuClockCapAppliedMhz = mhz; _gpuModeStatus = $"{msg} (적용 완료)"; }
                else { _gpuModeStatus = $"적용 실패: {msg}"; }
            }
            if (Visible && !_closing) { try { BeginInvoke(new Action(SendUIRefresh)); } catch { } }
        });
    }

    private void InitPersistedSettings()
    {
        AppSettings s = AppSettingsStore.Load();

        if (s.SessionActive)
        {
            bool systemForced = WasSystemForceShutdownSince(s.LastActiveUtc);
            AppSettingsStore.Log(systemForced
                ? $"이전 세션이 비정상 종료되었고 시스템 강제 종료가 감지되어 설정을 초기화합니다 (마지막 활동: {s.LastActiveUtc:u})."
                : $"이전 세션이 비정상 종료되었지만 시스템 강제 종료는 없어 설정을 유지합니다 (마지막 활동: {s.LastActiveUtc:u}).");
            if (systemForced) s.ResetTuning();
        }

        s.SessionActive = true;
        s.LastActiveUtc = DateTime.UtcNow;
        AppSettingsStore.Save(s);
        _settings = s;

        _cpuPl1Watts = s.CpuPl1Watts;
        _cpuPl2Watts = s.CpuPl2Watts;
        _cpuTauSeconds = s.CpuTauSeconds;
        _tpp250AutoBalance = s.Tpp250AutoBalance;
        _tppCpuSimultaneousWatts = s.TppCpuSimultaneousWatts;
        _gpuClockCapMhz = s.GpuClockCapMhz > 0 ? s.GpuClockCapMhz : null;
        _gpuCtgp = s.GpuCtgpEnabled;
        _gpuDynamicBoost = s.GpuDynamicBoostEnabled;
        _gpuModeWatts = (_gpuCtgp && _gpuDynamicBoost) ? 175 : (_gpuDynamicBoost ? 155 : 140);

        IntelCpuTuningService.ApplyTuning(
            _cpuPl1Watts,
            _cpuPl2Watts,
            _cpuTauSeconds,
            isGpuHeavy: false,
            _tpp250AutoBalance,
            _tppCpuSimultaneousWatts);

        _sessionTimer = new System.Threading.Timer(_ => PersistSettings(), null, 30_000, 30_000);
    }

    private void PersistSettings(bool markCleanExit = false)
    {
        lock (_settingsLock)
        {
            _settings.CpuPl1Watts = _cpuPl1Watts;
            _settings.CpuPl2Watts = _cpuPl2Watts;
            _settings.CpuTauSeconds = _cpuTauSeconds;
            _settings.Tpp250AutoBalance = _tpp250AutoBalance;
            _settings.TppCpuSimultaneousWatts = _tppCpuSimultaneousWatts;
            _settings.GpuClockCapMhz = _gpuClockCapMhz ?? 0;
            _settings.GpuCtgpEnabled = _gpuCtgp;
            _settings.GpuDynamicBoostEnabled = _gpuDynamicBoost;
            _settings.LastActiveUtc = DateTime.UtcNow;
            _settings.SessionActive = !markCleanExit;
            AppSettingsStore.Save(_settings);
        }
    }

    private static bool WasSystemForceShutdownSince(DateTime sinceUtc)
    {
        if (sinceUtc == DateTime.MinValue) return false;
        try
        {
            string since = ManagementDateTimeConverter.ToDmtfDateTime(sinceUtc.ToLocalTime());
            string wql = "SELECT EventCode FROM Win32_NTLogEvent WHERE Logfile='System' AND TimeGenerated >= '" + since +
                         "' AND (EventCode=41 OR EventCode=6008)";
            bool found = false;
            var task = Task.Run(() =>
            {
                using var searcher = new ManagementObjectSearcher(wql);
                foreach (ManagementBaseObject _ in searcher.Get()) { found = true; break; }
            });
            if (!task.Wait(8000))
            {
                AppSettingsStore.Log("시스템 로그 조회 시간 초과 → 안전을 위해 초기화");
                return true;
            }
            return found;
        }
        catch (Exception ex)
        {
            AppSettingsStore.Log($"시스템 로그 조회 실패 → 안전을 위해 초기화: {ex.Message}");
            return true;
        }
    }

    private void HandleSetGpuPower(bool ctgp, bool ppab, int dstate)
    {
        Task.Run(() =>
        {
            if (!GpuPowerEnforcer.IsGpuEnabled())
            {
                lock (_gpuLimitLock) { _gpuModeStatus = "GPU 재활성화 중..."; }
                GpuPowerEnforcer.SetDiscreteGpuState(true, out string _);
                Thread.Sleep(1500);
            }

            var requested = new GpuPowerMode(ctgp, ppab, (byte)Math.Clamp(dstate, 1, 4), 87);
            string status = "";
            bool ok = false;
            GpuPowerMode? readback = null;
            bool lockAcquired = false;
            try
            {
                Monitor.TryEnter(_wmiLock, 4000, ref lockAcquired);
                if (lockAcquired) ok = _gpuPower.TrySet(requested, out readback, out status);
                else status = "WMI 응답 지연 (GPU 전력 모드)";
            }
            catch (Exception ex) { status = $"GPU 전력 모드 오류: {ex.Message}"; }
            finally { if (lockAcquired) Monitor.Exit(_wmiLock); }

            lock (_gpuLimitLock)
            {
                if (ok)
                {
                    _gpuCtgp = requested.Ctgp;
                    _gpuDynamicBoost = requested.Ppab;
                    _gpuDState = requested.DState;
                    _gpuModeWatts = (_gpuCtgp && _gpuDynamicBoost) ? 175 : (_gpuDynamicBoost ? 155 : 140);
                }
                else if (_gpuModeWatts < 0)
                {
                    _gpuModeWatts = 140;
                }
                _gpuModeStatus = status;
            }
            PersistSettings();
            if (Visible && !_closing) { try { BeginInvoke(new Action(SendUIRefresh)); } catch { } }
        });
    }

    private async Task HardwareMonitoringLoop()
    {
        try { _hwMonitor ??= new HardwareCombinedMonitoringService(); _battery ??= new BatteryMonitoringService(); }
        catch (Exception ex) { Debug.WriteLine($"센서 초기화 실패: {ex}"); }

        try
        {
            // 초기 부팅 시 RTX 5070 Ti 140~175W cTGP/PPAB 상태 동기화
            var desiredMode = new GpuPowerMode(_gpuCtgp, _gpuDynamicBoost, 1, 87);
            if (_gpuPower.TryGet(out GpuPowerMode cur, out _))
            {
                if (cur.Ctgp != desiredMode.Ctgp || cur.Ppab != desiredMode.Ppab)
                {
                    _ = _gpuPower.TrySet(desiredMode, out var rb, out _);
                    if (rb.HasValue) cur = rb.Value;
                }
                lock (_gpuLimitLock)
                {
                    _gpuCtgp = cur.Ctgp;
                    _gpuDynamicBoost = cur.Ppab;
                    _gpuDState = cur.DState;
                    _gpuModeWatts = (_gpuCtgp && _gpuDynamicBoost) ? 175 : (_gpuDynamicBoost ? 155 : 140);
                }
            }
        }
        catch { }

        while (!_monitoringCts.IsCancellationRequested)
        {
            try
            {
                PerformHardwarePolling();
                _lastCpuTuningStatus = IntelCpuTuningService.StatusMessage;
                float cpuW, gpuW, gpuUsage;
                bool isGpuActive;
                lock (_stateLock)
                {
                    cpuW = _lastCpu.PowerWatts ?? 0f;
                    isGpuActive = _gpuActive;
                    gpuW = _lastGpu.PowerWatts ?? 0f;
                    gpuUsage = _lastGpu.UsagePercent ?? 0f;
                }

                IntelCpuTuningService.CheckAndMaintain(
                    cpuW,
                    isGpuActive,
                    gpuW,
                    gpuUsage,
                    _tpp250AutoBalance,
                    _tppCpuSimultaneousWatts);

                FpsMonitorService.Tick();
                HandlePowerStateAutoTransition(_lastBattery.IsAcConnected);

                if (isGpuActive) QueueGpuLimitIfAwake();

                if (Visible && !_closing)
                {
                    try { BeginInvoke(new Action(SendUIRefresh)); } catch { }
                }
            }
            catch (Exception ex) { Debug.WriteLine($"루프 오류: {ex}"); }

            try { await Task.Delay(1000, _monitoringCts.Token); } catch { break; }
        }
    }

    private void PerformHardwarePolling()
    {
        BatteryStatus newBattery = _lastBattery;
        try { if (_battery != null) newBattery = _battery.ReadStatus(); } catch { }

        bool d0 = PassiveNvidiaGpuPowerState.IsD0();
        _gpuD0Samples = d0 ? Math.Min(_gpuD0Samples + 1, 2) : 0;
        bool shouldMonitorGpu = (_gpuD0Samples >= 2 && d0);

        CpuStatus newCpu = _lastCpu;
        GpuStatus newGpu = new GpuStatus();
        bool newGpuActive = false;

        if (_hwMonitor != null)
        {
            var (c, g) = _hwMonitor.ReadStatus(shouldMonitorGpu);
            var (msrPl1, msrPl2) = IntelCpuTuningService.ReadCurrentPowerLimits();
            c.Pl1LimitWatts = msrPl1;
            c.Pl2LimitWatts = msrPl2;
            newCpu = c;
            if (shouldMonitorGpu && g.IsHardwareAvailable && g.IsDiscrete)
            {
                newGpu = g;
                newGpuActive = true;
            }
        }

        HpOmenFanReadings? newFan = null;
        string currentFanError = "";
        bool wmiLockAcquired = false;

        if (_fanSupported)
        {
            try
            {
                Monitor.TryEnter(_wmiLock, 500, ref wmiLockAcquired);
                if (wmiLockAcquired)
                {
                    if (_fanController.TryReadState(out var reading, out var readError))
                    {
                        newFan = reading;
                    }
                    else
                    {
                        currentFanError = $"팬 읽기 실패: {readError}";
                    }

                    ReapplyFanControlIfNeeded(newCpu, newGpu, ref currentFanError, ref newFan);
                }
                else
                {
                    currentFanError = "WMI 응답 지연 (팬)";
                }
            }
            catch (Exception ex) { currentFanError = $"WMI 오류: {ex.Message}"; }
            finally { if (wmiLockAcquired) Monitor.Exit(_wmiLock); }
        }

        lock (_stateLock)
        {
            _lastCpu = newCpu;
            _lastBattery = newBattery;
            _gpuActive = newGpuActive;
            _lastGpu = newGpu;

            if (newFan != null) _lastFan = newFan;

            if (!string.IsNullOrEmpty(currentFanError)) _fanError = currentFanError;
            else if (_fanError == "WMI 응답 지연 (팬)" || _fanError.StartsWith("팬 읽기 실패")) _fanError = "";
        }

        if (!newGpuActive)
        {
            lock (_gpuLimitLock)
            {
                _gpuClockCapAppliedMhz = null;
                _clockCapFailCount = 0;
                _gpuCommandedWatts = null;
                if (!_gpuModeStatus.Contains("적용 완료") && !_gpuModeStatus.Contains("비활성화됨") && !_gpuModeStatus.Contains("재활성화")
                    && !_gpuModeStatus.Contains("적용 중") && !_gpuModeStatus.Contains("실패"))
                {
                    _gpuModeStatus = "GPU 대기 중 · 활성화 시 적용됨";
                }
            }
        }
    }

    private int _uiRefreshBusy;

    private void SendUIRefresh()
    {
        if (_closing || _webView?.CoreWebView2 == null || !Visible) return;

        if (Interlocked.CompareExchange(ref _uiRefreshBusy, 1, 0) != 0) return;

        Task.Run(() =>
        {
            try
            {
                object payload = BuildUiPayload();
                if (!_closing && !IsDisposed)
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (!_closing && !IsDisposed) PostToWeb(payload);
                    }));
                }
            }
            catch (Exception ex)
            {
                AppSettingsStore.Log($"UI 갱신 페이로드 생성 실패: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _uiRefreshBusy, 0);
            }
        });
    }

    private object BuildUiPayload()
    {
        HudSnapshot snap = BuildSnapshot();

        string snapFanError; bool snapCoolMode, snapQuietMode;
        lock (_stateLock) { snapFanError = _fanError; snapCoolMode = _coolMode; snapQuietMode = _quietMode; }

        bool snapGpuBoost, snapGpuCtgp; byte snapGpuDState; int snapClockCap, snapClockCapApplied;
        lock (_gpuLimitLock)
        {
            snapGpuBoost = _gpuDynamicBoost;
            snapGpuCtgp = _gpuCtgp;
            snapGpuDState = _gpuDState;
            snapClockCap = _gpuClockCapMhz ?? 0;
            snapClockCapApplied = _gpuClockCapAppliedMhz ?? 0;
        }

        return new
        {
            cpu = new { usage = snap.CpuUsage, fps = snap.CurrentFps, temp = snap.CpuTemp, clock = snap.CpuClockGhz, power = snap.CpuPower, voltage = snap.CpuVoltage },
            gpu = new { active = snap.GpuActive, usage = snap.GpuUsage, temp = snap.GpuTemp, clock = snap.GpuClockGhz, power = snap.GpuPower },
            fan = new
            {
                cpuPercent = snap.FanCpuPercent,
                gpuPercent = snap.FanGpuPercent,
                cpuRpm = snap.FanCpuRpm,
                gpuRpm = snap.FanGpuRpm,
                cpuEstimated = snap.FanCpuEstimated,
                gpuEstimated = snap.FanGpuEstimated,
                error = snapFanError
            },
            battery = new { percent = snap.BatteryPercent, watts = snap.BatteryWatts, charging = snap.BatteryCharging },
            ui = new
            {
                powerProfile = snap.PowerProfileLabel == "ECO" ? "efficient" : (snap.PowerProfileLabel == "MAX" ? "performance" : "balanced"),
                fanMode = snapCoolMode ? "custom" : (snapQuietMode ? "quiet" : "auto"),
                refreshRate = snap.RefreshRateHz,
                gpuClockCap = snapClockCap,
                gpuClockCapApplied = snapClockCapApplied,
                isLocked = _isBatteryLimitActive,
                gpuModeStatus = snap.GpuModeStatus,
                gpuRequestedWatts = snap.GpuRequestedWatts,
                gpuDynamicBoost = snapGpuBoost,
                gpuCtgp = snapGpuCtgp,
                gpuDState = (int)snapGpuDState,
                isGpuEnabled = snap.GpuEnabled,
                isGpuPresent = snap.GpuPresent,
                cpuPl1Watts = _cpuPl1Watts,
                cpuPl2Watts = _cpuPl2Watts,
                cpuTauSeconds = _cpuTauSeconds,
                cpuEffectivePl1Watts = IntelCpuTuningService.EffectivePl1Watts,
                cpuEffectivePl2Watts = IntelCpuTuningService.EffectivePl2Watts,
                tpp250AutoBalance = _tpp250AutoBalance,
                tppCpuWatts = _tppCpuSimultaneousWatts,
                tpp250Active = IntelCpuTuningService.IsTpp250Active,
                cpuTuningStatus = IntelCpuTuningService.StatusMessage
            }
        };
    }

    private void QueueGpuLimitIfAwake()
    {
        int? want;
        lock (_gpuLimitLock)
        {
            want = _gpuClockCapMhz;
            if (want == _gpuClockCapAppliedMhz) return;
            if (_clockCapFailCount >= 3) return;
        }
        if (Interlocked.CompareExchange(ref _clockCapBusy, 1, 0) != 0) return;

        Task.Run(() =>
        {
            try
            {
                bool ok = _powerLimitService.SetGpuClockCap(want, out string msg);
                lock (_gpuLimitLock)
                {
                    if (ok) { _gpuClockCapAppliedMhz = want; _clockCapFailCount = 0; _gpuModeStatus = $"{msg} (적용 완료)"; }
                    else { _clockCapFailCount++; _gpuModeStatus = msg; }
                }
            }
            finally { Interlocked.Exchange(ref _clockCapBusy, 0); }
            if (Visible && !_closing) { try { BeginInvoke(new Action(SendUIRefresh)); } catch { } }
        });
    }

    private void HandlePowerStateAutoTransition(bool isAcOnline)
    {
        if (_wasCharging.HasValue && _wasCharging.Value == isAcOnline) return;
        if (!isAcOnline) { ApplyPowerProfileInternal(PerformanceProfile.BestPowerEfficiency); ApplyDisplayRefreshRate(60); }
        else { ApplyPowerProfileInternal(_lastAcPowerProfile); ApplyDisplayRefreshRate(240); }
        _wasCharging = isAcOnline;
    }

    private void ManualSetPowerProfile(PerformanceProfile prof)
    {
        ApplyPowerProfileInternal(prof);
        if (_lastBattery.IsAcConnected) _lastAcPowerProfile = prof;
        ApplyDisplayRefreshRate(prof == PerformanceProfile.BestPowerEfficiency ? 60u : 240u);

        // Windows 전원 모드 전환 시 HP BIOS 열 정책(0x1A)도 함께 동기화 (TPP 250W 해제용)
        HpOmenPerformanceMode omenMode = prof switch
        {
            PerformanceProfile.BestPowerEfficiency => HpOmenPerformanceMode.Cool,
            PerformanceProfile.BestPerformance => HpOmenPerformanceMode.Performance,
            _ => HpOmenPerformanceMode.Default
        };
        TrySetOmenThermalPolicy(omenMode);

        SendUIRefresh();
    }

    private void TrySetOmenThermalPolicy(HpOmenPerformanceMode mode)
    {
        Task.Run(() =>
        {
            bool lockAcquired = false;
            try
            {
                Monitor.TryEnter(_wmiLock, 2000, ref lockAcquired);
                if (lockAcquired)
                {
                    byte[] payload = new byte[128];
                    payload[0] = (byte)mode;
                    _ = _biosTransport.Invoke(0x00020008, 0x1A, 128, payload);
                }
            }
            catch { }
            finally { if (lockAcquired) Monitor.Exit(_wmiLock); }
        });
    }

    private void ApplyPowerProfileInternal(PerformanceProfile prof) { if (_powerProfile.TrySet(prof, out _)) lock (_stateLock) { _currentPowerProfile = prof; } }

    private bool GetHpBatteryHealthManagerStatus()
    {
        try
        {
            using ManagementObjectSearcher searcher = new ManagementObjectSearcher(@"root\hp\InstrumentedBIOS", "SELECT * FROM HP_BIOSSetting WHERE Name LIKE '%Battery Health%'");
            foreach (ManagementObject obj in searcher.Get())
            {
                string target = obj["CurrentValue"]?.ToString() ?? obj["Value"]?.ToString() ?? "";
                if (target.Contains("On", StringComparison.OrdinalIgnoreCase) && !target.Contains("Off", StringComparison.OrdinalIgnoreCase)) return true;
                if (target.Contains("Off", StringComparison.OrdinalIgnoreCase)) return false;
            }
        }
        catch { }
        return BatteryHelpers.GetBatteryChargeLimitState() ?? false;
    }

    private void ToggleHpBatteryHealthManager()
    {
        Task.Run(() =>
        {
            try
            {
                bool currentStatus = GetHpBatteryHealthManagerStatus();
                string targetValue = currentStatus ? "Maximize my battery health Off" : "Maximize my battery health On";
                bool success = false;

                try
                {
                    using ManagementObjectSearcher searcher = new ManagementObjectSearcher(@"root\hp\InstrumentedBIOS", "SELECT * FROM HP_BIOSSettingInterface");
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        using ManagementBaseObject inParams = obj.GetMethodParameters("SetBIOSSetting");
                        inParams["Name"] = "Battery Health Manager";
                        inParams["Value"] = targetValue;
                        inParams["Password"] = "<utf-16/>";
                        using ManagementBaseObject outParams = obj.InvokeMethod("SetBIOSSetting", inParams, null);
                        if (outParams != null && (uint)(outParams["Return"] ?? 9999u) == 0) success = true;
                        break;
                    }
                }
                catch { }

                try
                {
                    BatteryHelpers.SetBatteryChargeLimit(!currentStatus);
                    success = true;
                }
                catch { }

                if (success)
                {
                    _isBatteryLimitActive = !currentStatus;
                    if (Visible && !_closing) { try { BeginInvoke(new Action(SendUIRefresh)); } catch { } }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"배터리 제한 설정 변경 실패: {ex.Message}");
            }
        });
    }

    private static uint GetCurrentDisplayRefreshRate() { try { DEVMODE dm = new(); dm.dmSize = (ushort)Marshal.SizeOf(typeof(DEVMODE)); if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm)) return dm.dmDisplayFrequency; } catch { } return 60; }
    private static void ApplyDisplayRefreshRate(uint targetHz) { try { DEVMODE dm = new(); dm.dmSize = (ushort)Marshal.SizeOf(typeof(DEVMODE)); if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm) && dm.dmDisplayFrequency != targetHz) { dm.dmDisplayFrequency = targetHz; dm.dmFields = 0x00400000; ChangeDisplaySettings(ref dm, CDS_UPDATEREGISTRY); } } catch { } }

    private void ReapplyFanControlIfNeeded(CpuStatus cpu, GpuStatus gpu, ref string fanErrorMsg, ref HpOmenFanReadings? newFan)
    {
        bool isCoolMode;
        bool isQuietMode;
        FanCurveDefinition? coolCurve;
        FanCurveDefinition? quietCurve;

        lock (_stateLock)
        {
            isCoolMode = _coolMode;
            isQuietMode = _quietMode;
            coolCurve = _coolCurve;
            quietCurve = _quietCurve;
        }

        bool isAutoDriven = UseQuietAutoCurve && _fanSupported && !isCoolMode && !isQuietMode;

        if (!isCoolMode && !isQuietMode && !isAutoDriven)
        {
            bool overrideApplied;
            lock (_stateLock) { overrideApplied = _fanOverrideApplied; }
            if (overrideApplied)
            {
                lock (_stateLock) { _fanOverrideApplied = false; _lastCpuFanRequest = null; _lastGpuFanRequest = null; }
            }
            return;
        }

        DateTime now = DateTime.UtcNow;

        const int ReapplyIntervalSeconds = 2;
        if (now - _lastFanApplyUtc < TimeSpan.FromSeconds(ReapplyIntervalSeconds)) return;

        static int? Valid(float? value) => value.HasValue && float.IsFinite(value.Value) && value.Value >= 1 && value.Value <= 115 ? (int)Math.Round(value.Value) : null;
        int? cpuTemp = Valid(cpu.TemperatureCelsius);
        int? gpuTemp = gpu.UsagePercent.HasValue ? Valid(gpu.TemperatureCelsius) : null;

        if (!cpuTemp.HasValue && !gpuTemp.HasValue)
        {
            _sensorReadFailCount++;
            if (_sensorReadFailCount < 3)
            {
                fanErrorMsg = $"온도 센서 읽기 재시도 중 ({_sensorReadFailCount}/3)...";
                _lastFanApplyUtc = now;
                return;
            }
            _sensorReadFailCount = 0;
            fanErrorMsg = "온도 센서 읽기 실패 (유지 중)";
            _lastFanApplyUtc = now;
            return;
        }

        _sensorReadFailCount = 0;

        int cpuTempForCurve = cpuTemp ?? gpuTemp ?? 0;
        int gpuTempForCurve = gpuTemp ?? cpuTemp ?? 0;

        IReadOnlyList<CurvePoint> cpuPoints;
        IReadOnlyList<CurvePoint> gpuPoints;
        if (isQuietMode)
        {
            cpuPoints = quietCurve?.CpuPoints ?? QuietCurvePoints();
            gpuPoints = quietCurve?.GpuPoints ?? QuietCurvePoints();
        }
        else if (isCoolMode)
        {
            cpuPoints = coolCurve?.CpuPoints ?? CoolCurvePoints();
            gpuPoints = coolCurve?.GpuPoints ?? CoolCurvePoints();
        }
        else
        {
            cpuPoints = AutoCurvePoints();
            gpuPoints = AutoCurvePoints();
        }

        int? cpuTarget = InterpolateCurve(cpuPoints, cpuTempForCurve);
        int? gpuTarget = InterpolateCurve(gpuPoints, gpuTempForCurve);

        if (!cpuTarget.HasValue && !gpuTarget.HasValue) return;

        int rawCpuPercent = Math.Clamp(cpuTarget ?? gpuTarget ?? 0, 0, 100);
        int rawGpuPercent = Math.Clamp(gpuTarget ?? cpuTarget ?? 0, 0, 100);

        int cpuPercent = rawCpuPercent;
        int gpuPercent = rawGpuPercent;

        // QUIET / COOL 모드에서는 CPU/GPU 개별 온도 커브 목표치 중 높은 쪽에 맞춰 두 팬을 동일 속도로 동기화
        if (isQuietMode || isCoolMode)
        {
            int syncedPercent = Math.Max(rawCpuPercent, rawGpuPercent);
            cpuPercent = syncedPercent;
            gpuPercent = syncedPercent;
        }

        int? prevCpu;
        int? prevGpu;
        bool prevApplied;
        lock (_stateLock)
        {
            prevCpu = _lastCpuFanRequest;
            prevGpu = _lastGpuFanRequest;
            prevApplied = _fanOverrideApplied;
        }

        const int KeepAliveIntervalSeconds = 25;
        bool keepAliveDue = !prevApplied || (now - _lastFanApplyUtc >= TimeSpan.FromSeconds(KeepAliveIntervalSeconds));

        if (prevApplied && prevCpu.HasValue && prevGpu.HasValue)
        {
            bool anyIncrease = cpuPercent > prevCpu.Value || gpuPercent > prevGpu.Value;
            bool anyDecrease = cpuPercent < prevCpu.Value || gpuPercent < prevGpu.Value;

            if (anyIncrease)
            {
                _fanDecreaseHoldCount = 0;
            }
            else if (anyDecrease)
            {
                int maxDrop = Math.Max(prevCpu.Value - cpuPercent, prevGpu.Value - gpuPercent);
                _fanDecreaseHoldCount++;
                // 소폭(1~3%) 하락 시 3회(~6초) 유지 후 감속해 불필요한 팬 출렁임(hunting) 방지
                if (maxDrop < 4 && _fanDecreaseHoldCount < 3)
                {
                    cpuPercent = prevCpu.Value;
                    gpuPercent = prevGpu.Value;
                }
                else
                {
                    _fanDecreaseHoldCount = 0;
                }
            }
            else
            {
                _fanDecreaseHoldCount = 0;
            }

            if (cpuPercent == prevCpu.Value && gpuPercent == prevGpu.Value && !keepAliveDue)
            {
                return;
            }
        }

        _lastFanApplyUtc = now;

        if (_fanController.TrySetCustomLevels(cpuPercent, gpuPercent, out var readings, out var setError, isQuietCurve: false))
        {
            newFan = readings;
            lock (_stateLock)
            {
                _lastCpuFanRequest = cpuPercent;
                _lastGpuFanRequest = gpuPercent;
                _fanOverrideApplied = true;
            }
            fanErrorMsg = readings is null ? $"팬 CPU={cpuPercent}% GPU={gpuPercent}% 요청됨 · 재확인 실패: {setError}" : "";
        }
        else
        {
            fanErrorMsg = $"팬 CPU={cpuPercent}% GPU={gpuPercent}% 적용 실패: {setError}";
        }
    }

    private static int InterpolateCurve(IReadOnlyList<CurvePoint> points, int temp)
    {
        var sorted = points.OrderBy(p => p.TemperatureCelsius).ToList();
        if (temp <= sorted.First().TemperatureCelsius) return sorted.First().FanPercent;
        if (temp >= sorted.Last().TemperatureCelsius) return sorted.Last().FanPercent;
        for (int i = 0; i < sorted.Count - 1; i++)
        {
            if (temp >= sorted[i].TemperatureCelsius && temp <= sorted[i + 1].TemperatureCelsius)
            {
                double ratio = (temp - sorted[i].TemperatureCelsius) / (double)(sorted[i + 1].TemperatureCelsius - sorted[i].TemperatureCelsius);
                return (int)Math.Round(sorted[i].FanPercent + (sorted[i + 1].FanPercent - sorted[i].FanPercent) * ratio);
            }
        }
        return 25;
    }

    private static float? ReadBatteryWatts(BatteryStatus bat) { if (bat.ChargeWatts > 0.01f) return bat.ChargeWatts; if (bat.DischargeWatts > 0.01f) return -bat.DischargeWatts; return null; }

    private static CurvePoint[] QuietCurvePoints() => new[] {
        new CurvePoint(40, 0), new CurvePoint(60, 8), new CurvePoint(80, 20), new CurvePoint(100, 28)
    };

    private static CurvePoint[] CoolCurvePoints() => new[] {
        new CurvePoint(40, 20), new CurvePoint(50, 32), new CurvePoint(60, 45), new CurvePoint(70, 62),
        new CurvePoint(80, 82), new CurvePoint(90, 100), new CurvePoint(100, 100)
    };

    // 조용한 기본 AUTO 프로파일: 유휴/저부하 구간 정숙성 극대화 + 83°C 이상에서만 단계적 냉각 강화
    private static CurvePoint[] AutoCurvePoints() => new[] {
        new CurvePoint(45, 0), new CurvePoint(55, 8), new CurvePoint(65, 14),
        new CurvePoint(75, 22), new CurvePoint(83, 34), new CurvePoint(90, 55),
        new CurvePoint(96, 80), new CurvePoint(100, 100)
    };

    private static string CurveDir => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OmenMax16");
    private static string CoolCurveFilePath => System.IO.Path.Combine(CurveDir, "fan-curve.json");
    private static string QuietCurveFilePath => System.IO.Path.Combine(CurveDir, "quiet-fan-curve.json");

    private static CurvePoint[] ValidateCurve(IEnumerable<CurvePoint> input, int maxPercent)
    {
        _ = maxPercent;
        CurvePoint[] points = input.OrderBy(p => p.TemperatureCelsius).ToArray();
        if (points.Length < 2 || points.Length > 20) throw new InvalidOperationException("포인트는 2~20개 사이여야 합니다.");
        for (int i = 0; i < points.Length; i++)
        {
            points[i] = points[i] with { FanPercent = Math.Clamp(points[i].FanPercent, 0, 100) };
            if (i > 0 && points[i].TemperatureCelsius - points[i - 1].TemperatureCelsius < 1)
                throw new InvalidOperationException("같은 온도에 포인트를 둘 이상 둘 수 없습니다.");
        }
        return points;
    }

    private static FanCurveDefinition? TryLoadCurveFile(string path, int maxPercent)
    {
        try
        {
            if (!System.IO.File.Exists(path)) return null;
            var file = JsonSerializer.Deserialize<FanCurveFile>(System.IO.File.ReadAllText(path));
            if (file is not null && file.Cpu.Length >= 2 && file.Gpu.Length >= 2)
                return new FanCurveDefinition(ValidateCurve(file.Cpu, maxPercent), ValidateCurve(file.Gpu, maxPercent));
        }
        catch { }
        return null;
    }

    private void LoadSavedCurves()
    {
        _coolCurve = TryLoadCurveFile(CoolCurveFilePath, CoolCurveMaxPercent);
        _quietCurve = TryLoadCurveFile(QuietCurveFilePath, QuietCurveMaxPercent);
    }

    private void OpenFanCurve(string target, bool useDefaults)
    {
        if (_webView.CoreWebView2 == null) return;
        bool quiet = NormalizeCurveKind(target) == "quiet";

        FanCurveDefinition? saved = useDefaults ? null : (quiet ? _quietCurve : _coolCurve);
        CurvePoint[] fallback = quiet ? QuietCurvePoints() : CoolCurvePoints();

        PostToWeb(new
        {
            cmd = "OPEN_CURVE",
            kind = quiet ? "quiet" : "cool",
            target = quiet ? "quiet" : "cool",
            isDefault = useDefaults,
            points = new
            {
                cpu = saved?.CpuPoints ?? fallback,
                gpu = saved?.GpuPoints ?? fallback
            }
        });
    }

    private void SaveFanCurve(JsonElement root, string? kind)
    {
        string? target = kind;
        if (target is null && root.TryGetProperty("kind", out var kindProp) && kindProp.ValueKind == JsonValueKind.String) target = kindProp.GetString();
        if (target is null && root.TryGetProperty("target", out var tgtProp) && tgtProp.ValueKind == JsonValueKind.String) target = tgtProp.GetString();
        bool quiet = NormalizeCurveKind(target) == "quiet";

        try
        {
            if (!root.TryGetProperty("points", out JsonElement pointsElement)) throw new InvalidOperationException("저장할 커브가 없습니다.");

            if (!pointsElement.TryGetProperty("cpu", out JsonElement cpuElement) || cpuElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("CPU 커브 데이터가 없습니다.");
            if (!pointsElement.TryGetProperty("gpu", out JsonElement gpuElement) || gpuElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("GPU 커브 데이터가 없습니다.");

            CurvePoint[]? incomingCpu = JsonSerializer.Deserialize<CurvePoint[]>(cpuElement.GetRawText());
            CurvePoint[]? incomingGpu = JsonSerializer.Deserialize<CurvePoint[]>(gpuElement.GetRawText());
            if (incomingCpu == null || incomingGpu == null) throw new InvalidOperationException("커브를 읽지 못했습니다.");

            int maxPercent = quiet ? QuietCurveMaxPercent : CoolCurveMaxPercent;
            CurvePoint[] validCpu = ValidateCurve(incomingCpu, maxPercent);
            CurvePoint[] validGpu = ValidateCurve(incomingGpu, maxPercent);

            var file = new FanCurveFile { Cpu = validCpu, Gpu = validGpu };

            string path = quiet ? QuietCurveFilePath : CoolCurveFilePath;
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            try { System.IO.File.WriteAllText(temp, JsonSerializer.Serialize(file)); System.IO.File.Move(temp, path, true); }
            finally { if (System.IO.File.Exists(temp)) System.IO.File.Delete(temp); }

            lock (_stateLock)
            {
                var def = new FanCurveDefinition(validCpu, validGpu);
                if (quiet)
                {
                    _quietCurve = def;
                    _quietMode = true; _coolMode = false;
                    _currentFanPreset = HpOmenFanPreset.Quiet;
                }
                else
                {
                    _coolCurve = def;
                    _coolMode = true; _quietMode = false;
                    _currentFanPreset = HpOmenFanPreset.Performance;
                }
                _lastFanApplyUtc = DateTime.MinValue;
                _lastCpuFanRequest = null; _lastGpuFanRequest = null;
                _fanError = string.Empty;
            }

            PostToWeb(new { cmd = "CURVE_SAVE_RESULT", ok = true, error = "" });
            SendUIRefresh();
        }
        catch (Exception ex)
        {
            PostToWeb(new { cmd = "CURVE_SAVE_RESULT", ok = false, error = ex.Message });
        }
    }

    private void SetFanModeState(bool cool, bool quiet, HpOmenFanPreset preset)
    {
        lock (_stateLock)
        {
            _coolMode = cool;
            _quietMode = quiet;
            _currentFanPreset = preset;
            _lastCpuFanRequest = null; _lastGpuFanRequest = null;
            _fanDecreaseHoldCount = 0;
            _lastFanApplyUtc = DateTime.MinValue;
            _fanError = string.Empty;
        }
    }

    private async void ApplyPreset(HpOmenFanPreset preset)
    {
        if (!_fanSupported) { lock (_stateLock) { _fanError = "팬 인터페이스 미지원"; } SendUIRefresh(); return; }

        if (preset == HpOmenFanPreset.Auto && !UseQuietAutoCurve)
        {
            if (_fanCommandBusy) return;
            _fanCommandBusy = true;
            try
            {
                await Task.Run(() =>
                {
                    bool lockAcquired = false;
                    try
                    {
                        Monitor.TryEnter(_wmiLock, 4000, ref lockAcquired);
                        if (lockAcquired)
                        {
                            if (_fanController.TrySetAuto(out var readings, out var error))
                            {
                                lock (_stateLock)
                                {
                                    _coolMode = false;
                                    _quietMode = false;
                                    _fanOverrideApplied = false;
                                    _lastCpuFanRequest = null; _lastGpuFanRequest = null;
                                    _currentFanPreset = HpOmenFanPreset.Auto;
                                    _fanError = string.Empty;
                                }
                            }
                            else
                            {
                                lock (_stateLock) { _fanError = $"AUTO 실패: {error}"; }
                            }
                        }
                        else
                        {
                            lock (_stateLock) { _fanError = "WMI AUTO 실패: 락 획득 시간 초과"; }
                        }
                    }
                    finally { if (lockAcquired) Monitor.Exit(_wmiLock); }
                });
            }
            finally
            {
                _fanCommandBusy = false;
                SendUIRefresh();
            }
            return;
        }

        switch (preset)
        {
            case HpOmenFanPreset.Quiet: SetFanModeState(cool: false, quiet: true, HpOmenFanPreset.Quiet); break;
            case HpOmenFanPreset.Performance: SetFanModeState(cool: true, quiet: false, HpOmenFanPreset.Performance); break;
            default: SetFanModeState(cool: false, quiet: false, HpOmenFanPreset.Auto); break;
        }
        SendUIRefresh();
    }

    private void CreateTray()
    {
        ContextMenuStrip tm = new();
        tm.Items.Add("열기", null, (_, _) => OpenFromTray());

        _trayItemHud = new ToolStripMenuItem("게임 오버레이 HUD", null, (_, _) => ToggleHud());
        _trayItemMediaHud = new ToolStripMenuItem("뮤직 / 영상 컨트롤", null, (_, _) => ToggleMediaHud());
        tm.Items.Add(_trayItemMediaHud);
        tm.Items.Add(_trayItemHud);
        tm.Items.Add(new ToolStripSeparator());

        _trayItemFanAuto = new ToolStripMenuItem("팬: AUTO (조용한 기본)", null, (_, _) => ApplyPreset(HpOmenFanPreset.Auto));
        _trayItemFanQuiet = new ToolStripMenuItem("팬: QUIET (동기화)", null, (_, _) => ApplyPreset(HpOmenFanPreset.Quiet));
        _trayItemFanCool = new ToolStripMenuItem("팬: COOL (동기화)", null, (_, _) => ApplyPreset(HpOmenFanPreset.Performance));
        tm.Items.Add(_trayItemFanAuto);
        tm.Items.Add(_trayItemFanQuiet);
        tm.Items.Add(_trayItemFanCool);
        tm.Items.Add(new ToolStripSeparator());

        _trayItemPowerEco = new ToolStripMenuItem("전원: ECO (저전력)", null, (_, _) => ManualSetPowerProfile(PerformanceProfile.BestPowerEfficiency));
        _trayItemPowerBalanced = new ToolStripMenuItem("전원: BALANCED (균형)", null, (_, _) => ManualSetPowerProfile(PerformanceProfile.Balanced));
        _trayItemPowerMax = new ToolStripMenuItem("전원: MAX (고성능)", null, (_, _) => ManualSetPowerProfile(PerformanceProfile.BestPerformance));
        tm.Items.Add(_trayItemPowerEco);
        tm.Items.Add(_trayItemPowerBalanced);
        tm.Items.Add(_trayItemPowerMax);
        tm.Items.Add(new ToolStripSeparator());

        tm.Items.Add("종료", null, (_, _) => { _closing = true; Application.Exit(); });
        tm.Opening += (_, _) => UpdateTrayChecks();

        _tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "OMEN MAX 16", ContextMenuStrip = tm, Visible = true };
        _tray.MouseClick += (_, ea) => { if (ea.Button == MouseButtons.Left) { if (Visible) Hide(); else OpenFromTray(); } };
    }

    private void UpdateTrayChecks()
    {
        bool cool, quiet;
        PerformanceProfile prof;
        lock (_stateLock)
        {
            cool = _coolMode;
            quiet = _quietMode;
            prof = _currentPowerProfile;
        }
        if (_trayItemHud != null) _trayItemHud.Checked = _hud is { IsDisposed: false, Visible: true };
        if (_trayItemFanAuto != null) _trayItemFanAuto.Checked = !cool && !quiet;
        if (_trayItemFanQuiet != null) _trayItemFanQuiet.Checked = quiet;
        if (_trayItemFanCool != null) _trayItemFanCool.Checked = cool;
        if (_trayItemPowerEco != null) _trayItemPowerEco.Checked = prof == PerformanceProfile.BestPowerEfficiency;
        if (_trayItemPowerBalanced != null) _trayItemPowerBalanced.Checked = prof == PerformanceProfile.Balanced;
        if (_trayItemPowerMax != null) _trayItemPowerMax.Checked = prof == PerformanceProfile.BestPerformance;
        if (_trayItemMediaHud != null) _trayItemMediaHud.Checked = _mediaHud is { IsDisposed: false, Visible: true };
    }

    private void OpenFromTray() { PositionAboveTaskbar(); Show(); Activate(); BringToFront(); SendUIRefresh(); }
    private void PositionAboveTaskbar() { Rectangle a = Screen.PrimaryScreen?.WorkingArea ?? Screen.FromControl(this).WorkingArea; Location = new Point(a.Left + (a.Width - Width) / 2, a.Bottom - Height - 8); }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_closing && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
        _closing = true;
        _sessionTimer?.Dispose();
        PersistSettings(markCleanExit: true);
        FpsMonitorService.Shutdown();
        IntelCpuTuningService.Shutdown();
        _monitoringCts.Cancel();

        _hud?.Close();
        _hud = null;
        _mediaHud?.Close();
        _mediaHud = null;

        Task.Run(() =>
        {
            bool wmiLockAcquired = false;
            try
            {
                Monitor.TryEnter(_wmiLock, 1500, ref wmiLockAcquired);
                if (wmiLockAcquired)
                {
                    bool overrideApplied; lock (_stateLock) { overrideApplied = _fanOverrideApplied; }
                    if (overrideApplied) _fanController.TrySetAuto(out _);
                }
            }
            finally { if (wmiLockAcquired) Monitor.Exit(_wmiLock); }
        }).Wait(2000);

        _hwMonitor?.Dispose();
        _biosTransport.ResetConnection(); _tray?.Dispose(); base.OnFormClosing(e);
    }

    private string GetHtmlContent()
    {
        var htmlPath = Path.Combine(AppContext.BaseDirectory, "Forms", "Form.html");
        if (File.Exists(htmlPath))
        {
            return File.ReadAllText(htmlPath, System.Text.Encoding.UTF8);
        }

        // 디버그/빌드 경로가 다를 경우 프로젝트 폴더 기준 fallback 탐색
        var devPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\Forms\Form.html"));
        if (File.Exists(devPath))
        {
            return File.ReadAllText(devPath, System.Text.Encoding.UTF8);
        }

        throw new FileNotFoundException($"Form.html 파일을 찾을 수 없습니다: {htmlPath}");
    }
}
