#nullable enable
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OmenMax16.Services;

/// <summary>
/// AMD Ryzen (Ryzen AI 9 HX 375 / 475 / 365 / AI 7 350 등) 전용 전력 제한(STAPM/Slow/Fast),
/// Curve Optimizer 언더볼팅(--set-coall), GPU 고부하 TPP 동적 전력 배분, SMU PM Table 전압 판독 서비스.
/// 선택적으로 OmenMax16AmdBridge.dll(Ryzen Master SDK) 보조 텔레메트리도 지원합니다.
/// </summary>
public static class AmdCpuTuningService
{
    private static readonly object Sync = new();
    private static string? _exePath;

    private static int _offset;
    private static int _targetStapm = HardwarePlatformDetector.Current.CpuProfile.DefaultPl1Watts;
    private static int _targetSlow = HardwarePlatformDetector.Current.CpuProfile.DefaultPl1Watts;
    private static int _targetFast = HardwarePlatformDetector.Current.CpuProfile.DefaultPl2Watts;

    private static int _effectiveStapm = HardwarePlatformDetector.Current.CpuProfile.DefaultPl1Watts;
    private static int _effectiveSlow = HardwarePlatformDetector.Current.CpuProfile.DefaultPl1Watts;
    private static int _effectiveFast = HardwarePlatformDetector.Current.CpuProfile.DefaultPl2Watts;
    private static bool _isTppThrottled;

    private static volatile bool _stopped;
    private static int _running;
    private static int _overCount;
    private static int _gpuHeavySamples;
    private static int _gpuIdleSamples;
    private static DateTime _lastRunUtc = DateTime.MinValue;

    public static bool IsActive { get; private set; }
    public static bool IsTppActive { get { lock (Sync) { return _isTppThrottled; } } }
    public static int EffectivePl1Watts { get { lock (Sync) { return _effectiveStapm; } } }
    public static int EffectivePl2Watts { get { lock (Sync) { return _effectiveFast; } } }
    public static int CurrentUvOffset { get { lock (Sync) { return _offset; } } }

    private const float OvershootMarginWatts = 1.5f;
    private const int OvershootSamples = 5;
    private const int ReapplyCooldownSeconds = 30;
    private const float VoltageCorrectionOffset = 0.038f;

    private static readonly object VoltageSync = new();
    private static float? _lastCoreVoltage;
    public static float? LastCoreVoltage
    {
        get { lock (VoltageSync) { return _lastCoreVoltage; } }
        private set { lock (VoltageSync) { _lastCoreVoltage = value; } }
    }

    private static string _statusMessage = $"{HardwarePlatformDetector.Current.CpuShortName} 전력 제어 대기 중";
    public static string StatusMessage { get { lock (Sync) { return _statusMessage; } } }

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OmenMax16", "ryzenadj.log");

    private static readonly string[] VoltageRowCandidates =
    {
        "CORE VOLTAGE", "VDDCR_CPU", "VDDCR CPU", "CPU CORE VOLTAGE", "CORE VDD", "VDD VALUE"
    };

    private static readonly Regex TableRowRegex = new(
        @"\|\s*([A-Za-z0-9_ ]+?)\s*\|\s*([-+]?[0-9]*\.?[0-9]+)\s*\|",
        RegexOptions.Compiled);

    #region Optional OmenMax16AmdBridge P/Invoke

    [StructLayout(LayoutKind.Sequential)]
    public struct OmenAmdCpuTelemetry
    {
        public uint StructSize;
        public int SdkResultCode;
        public uint CoreCount;
        public uint ParkedCoreCount;
        public double TemperatureCelsius;
        public double AverageCoreVoltageVolts;
        public double PeakCoreVoltageVolts;
        public double PeakSpeedMHz;
        public double AverageEffectiveClockMHz;
        public double AverageCurrentClockMHz;
        public double AverageCoreTemperatureCelsius;
        public double AverageC0ResidencyPercent;
        public float PPTLimitWatts;
        public float PPTValueWatts;
        public float TDCLimitVddAmps;
        public float TDCValueVddAmps;
        public float EDCLimitVddAmps;
        public float EDCValueVddAmps;
        public float ThermalLimitCelsius;
        public float FabricClockMHz;
        public float CclkFmaxMHz;
        public float VddcrVddPowerWatts;
        public float VddcrSocPowerWatts;
    }

    [DllImport("OmenMax16AmdBridge.dll", EntryPoint = "OmenAmd_Read", CallingConvention = CallingConvention.StdCall)]
    private static extern int OmenAmd_Read(out OmenAmdCpuTelemetry telemetry);

    [DllImport("OmenMax16AmdBridge.dll", EntryPoint = "OmenAmd_Shutdown", CallingConvention = CallingConvention.StdCall)]
    private static extern void OmenAmd_Shutdown();

    private static bool _bridgeUnavailable;

    public static bool TryReadBridgeTelemetry(out OmenAmdCpuTelemetry telemetry)
    {
        telemetry = default;
        if (_bridgeUnavailable || !HardwarePlatformDetector.Current.IsAmd) return false;

        try
        {
            string dllPath = Path.Combine(AppContext.BaseDirectory, "OmenMax16AmdBridge.dll");
            if (!File.Exists(dllPath))
            {
                _bridgeUnavailable = true;
                return false;
            }

            int rc = OmenAmd_Read(out telemetry);
            if (rc == 0) return true;

            _bridgeUnavailable = true;
            return false;
        }
        catch
        {
            _bridgeUnavailable = true;
            return false;
        }
    }

    #endregion

    /// <summary>
    /// 감지된 AMD Ryzen 모델의 허용 범위 내에서 지속 전력(STAPM/Slow), 단기 부스트(Fast),
    /// Curve Optimizer 오프셋 및 GPU 동시 부하 시 TPP 균형 배분을 적용합니다.
    /// </summary>
    public static void ApplyPowerLimits(
        int pl1Watts,
        int pl2Watts,
        bool isGpuHeavy = false,
        bool tppAutoBalance = true,
        int tppCpuWatts = 50,
        int? uvOffset = null)
    {
        var cpu = HardwarePlatformDetector.Current.CpuProfile;
        lock (Sync)
        {
            int clampedPl1 = Math.Clamp(pl1Watts, cpu.MinWatts, cpu.MaxPl1Watts);
            int clampedPl2 = Math.Clamp(Math.Max(pl2Watts, clampedPl1), cpu.MinWatts, cpu.MaxPl2Watts);

            _targetStapm = clampedPl1;
            _targetSlow = clampedPl1;
            _targetFast = clampedPl2;

            if (uvOffset.HasValue)
            {
                _offset = Math.Clamp(uvOffset.Value, AppSettings.MinAmdUvOffset, AppSettings.MaxAmdUvOffset);
            }

            ComputeEffectiveLimitsLocked(isGpuHeavy, tppAutoBalance, tppCpuWatts);
        }

        _stopped = false;
        IsActive = true;
        _overCount = 0;
        RunOnce();
    }

    public static void ApplyTuning(int offset, int? stapmWatts = null, int? fastWatts = null, int? slowWatts = null)
    {
        var cpu = HardwarePlatformDetector.Current.CpuProfile;
        lock (Sync)
        {
            _offset = Math.Clamp(offset, AppSettings.MinAmdUvOffset, AppSettings.MaxAmdUvOffset);
            if (stapmWatts.HasValue) _targetStapm = Math.Clamp(stapmWatts.Value, cpu.MinWatts, cpu.MaxPl1Watts);
            if (slowWatts.HasValue) _targetSlow = Math.Clamp(slowWatts.Value, cpu.MinWatts, cpu.MaxPl1Watts);
            if (fastWatts.HasValue) _targetFast = Math.Clamp(fastWatts.Value, cpu.MinWatts, cpu.MaxPl2Watts);

            _effectiveStapm = _targetStapm;
            _effectiveSlow = _targetSlow;
            _effectiveFast = _targetFast;
            _isTppThrottled = false;
        }

        _stopped = false;
        IsActive = true;
        _overCount = 0;
        RunOnce();
    }

    /// <summary>
    /// 1초 모니터링 루프에서 호출합니다.
    /// 1) 외장 GPU 고부하 진입/이탈 시 AMD CPU STAPM/Slow/Fast 상한을 TPP 범위 내에서 자동 조절합니다.
    /// 2) HP EC 펌웨어가 SMU 한도를 초기화해 실측 전력이 Fast 한도를 연속 초과하면 재적용합니다.
    /// </summary>
    public static void CheckAndMaintain(
        float measuredCpuWatts,
        bool gpuActive,
        float gpuPowerWatts,
        float gpuUsagePercent,
        bool tppAutoBalance,
        int tppCpuWatts)
    {
        if (_stopped) return;

        bool rawGpuHeavy = gpuActive && (gpuPowerWatts >= 45f || gpuUsagePercent >= 40f);
        if (rawGpuHeavy)
        {
            _gpuHeavySamples = Math.Min(_gpuHeavySamples + 1, 10);
            _gpuIdleSamples = 0;
        }
        else
        {
            _gpuIdleSamples = Math.Min(_gpuIdleSamples + 1, 10);
            if (_gpuIdleSamples >= 6) _gpuHeavySamples = 0;
        }

        bool stableGpuHeavy = _gpuHeavySamples >= 3;
        int maxTpp = HardwarePlatformDetector.Current.MaxTppWatts;

        bool needApply = false;
        int effFast;

        lock (Sync)
        {
            int prevStapm = _effectiveStapm;
            int prevFast = _effectiveFast;
            bool prevTpp = _isTppThrottled;

            ComputeEffectiveLimitsLocked(stableGpuHeavy, tppAutoBalance, tppCpuWatts);

            if (_effectiveStapm != prevStapm || _effectiveFast != prevFast || _isTppThrottled != prevTpp)
            {
                needApply = true;
                Log($"[AMD TPP {maxTpp}W 전환] GPU고부하={stableGpuHeavy} → STAPM={_effectiveStapm}W, Fast={_effectiveFast}W");
            }

            effFast = _effectiveFast;
        }

        if (needApply)
        {
            _overCount = 0;
            RunOnce();
            return;
        }

        CheckOvershoot(measuredCpuWatts, effFast);
    }

    public static void CheckOvershoot(float measuredWatts)
    {
        int fast;
        lock (Sync) { fast = _effectiveFast; }
        CheckOvershoot(measuredWatts, fast);
    }

    private static void CheckOvershoot(float measuredWatts, int effectiveFastWatts)
    {
        if (!IsActive || _stopped) return;

        if (measuredWatts > effectiveFastWatts + OvershootMarginWatts) _overCount++;
        else _overCount = 0;

        if (_overCount >= OvershootSamples &&
            (DateTime.UtcNow - _lastRunUtc).TotalSeconds >= ReapplyCooldownSeconds)
        {
            _overCount = 0;
            Log($"[재적용] 측정 전력 {measuredWatts:F1}W 가 Fast {effectiveFastWatts}W 를 {OvershootSamples}회 연속 초과 → RyzenAdj 재적용");
            RunOnce();
        }
    }

    public static (float? Pl1Watts, float? Pl2Watts) ReadCurrentPowerLimits()
    {
        if (TryReadBridgeTelemetry(out var bridge) && bridge.PPTLimitWatts > 0f)
        {
            return (EffectivePl1Watts, bridge.PPTLimitWatts);
        }

        if (IsActive)
        {
            return (EffectivePl1Watts, EffectivePl2Watts);
        }

        return (null, null);
    }

    private static void ComputeEffectiveLimitsLocked(bool isGpuHeavy, bool tppAutoBalance, int tppCpuWatts)
    {
        var platform = HardwarePlatformDetector.Current;
        var cpu = platform.CpuProfile;
        int cappedCpu = Math.Clamp(tppCpuWatts, cpu.TppCpuMinWatts, platform.EffectiveTppCpuMaxWatts);

        if (tppAutoBalance && isGpuHeavy && (_targetStapm > cappedCpu || _targetFast > cappedCpu))
        {
            _effectiveStapm = Math.Min(_targetStapm, cappedCpu);
            _effectiveSlow = Math.Min(_targetSlow, cappedCpu);
            _effectiveFast = Math.Min(_targetFast, cappedCpu);
            _isTppThrottled = true;
        }
        else
        {
            _effectiveStapm = _targetStapm;
            _effectiveSlow = _targetSlow;
            _effectiveFast = _targetFast;
            _isTppThrottled = false;
        }
    }

    private static void RunOnce()
    {
        if (_stopped) return;
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        _lastRunUtc = DateTime.UtcNow;
        Task.Run(() =>
        {
            try { RunCommand(); }
            finally { Interlocked.Exchange(ref _running, 0); }
        });
    }

    public static void Shutdown()
    {
        _stopped = true;
        IsActive = false;
        if (!_bridgeUnavailable)
        {
            try { OmenAmd_Shutdown(); } catch { }
        }
    }

    private static string? ResolveExePath()
    {
        if (_exePath != null && File.Exists(_exePath)) return _exePath;
        string candidate = Path.Combine(AppContext.BaseDirectory, "ryzenadj.exe");
        if (File.Exists(candidate))
        {
            _exePath = candidate;
            return _exePath;
        }
        SetStatus("ryzenadj.exe 없음 — WMI/기본 정책으로 동작");
        Log($"[오류] ryzenadj.exe 를 찾을 수 없습니다: {candidate}");
        return null;
    }

    private static uint EncodeCoall(int offset) =>
        offset < 0 ? (uint)(0x100000 - (uint)(-offset)) : (uint)offset;

    private static void RunCommand()
    {
        string? exe = ResolveExePath();
        if (exe == null) return;

        int offset, stapm, fast, slow;
        bool tppActive;
        int maxTpp = HardwarePlatformDetector.Current.MaxTppWatts;

        lock (Sync)
        {
            offset = _offset;
            stapm = _effectiveStapm;
            fast = _effectiveFast;
            slow = _effectiveSlow;
            tppActive = _isTppThrottled;
        }

        string args = $"--stapm-limit={stapm * 1000} --fast-limit={fast * 1000} --slow-limit={slow * 1000} --dump-table" +
                      (offset != 0 ? $" --set-coall={EncodeCoall(offset)}" : "");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using Process? p = Process.Start(psi);
            if (p == null)
            {
                SetStatus("ryzenadj.exe 프로세스 시작 실패");
                Log("[오류] ryzenadj.exe 프로세스를 시작할 수 없습니다.");
                return;
            }

            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(3000))
            {
                SetStatus("ryzenadj.exe 응답 시간 초과");
                Log("[경고] ryzenadj.exe 응답 시간 초과");
                try { p.Kill(); } catch { }
                return;
            }

            if (p.ExitCode == 0)
            {
                float? voltage = ParseVoltageFromDumpTable(stdout);
                if (voltage.HasValue) LastCoreVoltage = voltage.Value + VoltageCorrectionOffset;

                string coText = offset < 0 ? $" · CO {offset}" : "";
                string tppTag = tppActive ? $" [TPP {maxTpp}W 배분]" : "";
                SetStatus($"STAPM {stapm}W / Fast {fast}W{coText} 적용 완료{tppTag}");
                Log($"[성공] {args} · voltage={(voltage?.ToString("F3") ?? "파싱 실패")}" +
                    (voltage.HasValue ? $" (보정 후={LastCoreVoltage:F3})" : ""));
            }
            else
            {
                SetStatus($"RyzenAdj 오류 (코드 {p.ExitCode})");
                Log($"[실패] ExitCode={p.ExitCode} args={args}\nstdout={stdout.Trim()}\nstderr={stderr.Trim()}");
            }
        }
        catch (Exception ex)
        {
            SetStatus($"RyzenAdj 예외: {ex.Message}");
            Log($"[예외] ryzenadj 실행 실패: {ex}");
        }
    }

    private static float? ParseVoltageFromDumpTable(string dumpOutput)
    {
        var rows = TableRowRegex.Matches(dumpOutput)
            .Select(m => (Name: m.Groups[1].Value.Trim(), Value: m.Groups[2].Value))
            .ToList();

        foreach (string candidate in VoltageRowCandidates)
        {
            var match = rows.FirstOrDefault(r => r.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (match.Name != null &&
                float.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) &&
                v is >= 0.3f and <= 1.6f)
            {
                return v;
            }
        }

        var fallback = rows.FirstOrDefault(r => r.Name.Contains("VOLT", StringComparison.OrdinalIgnoreCase));
        if (fallback.Name != null &&
            float.TryParse(fallback.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float fv) &&
            fv is >= 0.3f and <= 1.6f)
        {
            return fv;
        }

        return null;
    }

    private static void SetStatus(string msg)
    {
        lock (Sync) { _statusMessage = msg; }
    }

    private static void Log(string message)
    {
        Debug.WriteLine($"[AmdCpuTuning] {message}");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { }
    }
}