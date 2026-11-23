#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using System.Management;
using OmenMax16.Models;

namespace OmenMax16.Services;

/*
 * 앱 설정 저장소.
 *
 * CompactOverlayForm 또는 Custom Fan UI가 나중에
 * 설정을 저장해야 할 때 사용할 수 있다.
 */
public sealed class SettingsStore
{
    private const string FileName =
        "omenmax16.settings.json";

    public double CompactOverlayOpacity { get; set; } =
        0.95;

    public bool StartCompactOverlay { get; set; }

    public string PreferredGpu { get; set; } =
        "auto";

    // Optional CPU power limit percentage (user preference)
    public int? CpuPowerLimitPercent { get; set; }

    public List<CurvePoint>? CustomCurve { get; set; }

    public sealed record CurvePoint(
        int Temperature,
        int Percent);

    public static SettingsStore Load()
    {
        try
        {
            string path =
                GetPath();

            if (!File.Exists(path))
            {
                return new SettingsStore();
            }

            string json =
                File.ReadAllText(path);

            return JsonSerializer.Deserialize<SettingsStore>(
                       json)
                   ?? new SettingsStore();
        }
        catch
        {
            return new SettingsStore();
        }
    }

    public void Save()
    {
        try
        {
            string path =
                GetPath();

            string? directory =
                Path.GetDirectoryName(path);

            if (!string.IsNullOrWhiteSpace(
                    directory) &&
                !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json =
                JsonSerializer.Serialize(
                    this,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });

            File.WriteAllText(
                path,
                json);
        }
        catch
        {
            /*
             * 설정 파일 저장 실패가 앱 실행을 방해하지 않도록 한다.
             */
        }
    }

    private static string GetPath()
    {
        string directory =
            Environment.GetFolderPath(
                Environment.SpecialFolder
                .LocalApplicationData);

        return Path.Combine(
            directory,
            FileName);
    }
}

/*
 * 선택적 HP MUX 제어 DLL 로더.
 *
 * HpMuxControl.dll이 없으면 IsAvailable은 false이며,
 * 앱은 정상적으로 계속 실행된다.
 */
internal static class MuxControl
{
    private const string DefaultDllName =
        "HpMuxControl.dll";

    private delegate int IsAvailableDelegate();

    private delegate int ToggleDelegate();

    private static IntPtr _module =
        IntPtr.Zero;

    private static IsAvailableDelegate? _isAvailable;

    private static ToggleDelegate? _toggle;

    static MuxControl()
    {
        TryLoad();
    }

    public static bool IsAvailable
    {
        get
        {
            if (_isAvailable is null ||
                _toggle is null)
            {
                return false;
            }

            try
            {
                return _isAvailable() != 0;
            }
            catch
            {
                return false;
            }
        }
    }

    public static void ToggleMux()
    {
        if (_toggle is null)
        {
            throw new InvalidOperationException(
                "MUX 제어 라이브러리를 찾을 수 없습니다.");
        }

        int result =
            _toggle();

        if (result != 0)
        {
            throw new InvalidOperationException(
                "MUX 전환 실패. 코드: " +
                result);
        }
    }

    private static void TryLoad()
    {
        try
        {
            _module =
                LoadLibrary(
                    DefaultDllName);

            if (_module == IntPtr.Zero)
            {
                return;
            }

            IntPtr isAvailablePointer =
                GetProcAddress(
                    _module,
                    "IsMuxAvailable");

            IntPtr togglePointer =
                GetProcAddress(
                    _module,
                    "ToggleMux");

            if (isAvailablePointer != IntPtr.Zero)
            {
                _isAvailable =
                    Marshal.GetDelegateForFunctionPointer
                    <IsAvailableDelegate>(
                        isAvailablePointer);
            }

            if (togglePointer != IntPtr.Zero)
            {
                _toggle =
                    Marshal.GetDelegateForFunctionPointer
                    <ToggleDelegate>(
                        togglePointer);
            }
        }
        catch
        {
            _module = IntPtr.Zero;
            _isAvailable = null;
            _toggle = null;
        }
    }

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern IntPtr LoadLibrary(
        string fileName);

    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    private static extern IntPtr GetProcAddress(
        IntPtr module,
        string procedureName);
}

/*
 * Windows 기본 디스플레이 주사율 전환 도우미.
 *
 * 지정한 Hz가 현재 디스플레이 모드에 존재할 때만 변경한다.
 */
internal static class DisplayHelper
{
    private const int EnumCurrentSettings =
        -1;

    private const uint ChangeDisplaySettingsTest =
        0x00000002;

    private const uint ChangeDisplaySettingsUpdateRegistry =
        0x00000001;

    private const int DisplayChangeSuccessful =
        0;

    public static void ToggleRefreshRate(
        int firstRate,
        int secondRate)
    {
        DEVMODE currentMode =
            CreateDeviceMode();

        if (!EnumDisplaySettings(
                null,
                EnumCurrentSettings,
                ref currentMode))
        {
            throw new InvalidOperationException(
                "현재 디스플레이 설정을 읽을 수 없습니다.");
        }

        int currentRate =
            (int)currentMode.DisplayFrequency;

        int targetRate =
            currentRate == firstRate
                ? secondRate
                : firstRate;

        DEVMODE candidateMode =
            CreateDeviceMode();

        bool found =
            false;

        for (int index = 0;
             EnumDisplaySettings(
                 null,
                 index,
                 ref candidateMode);
             index++)
        {
            if (candidateMode.DisplayFrequency !=
                targetRate)
            {
                continue;
            }

            found = true;
            break;
        }

        if (!found)
        {
            throw new InvalidOperationException(
                "대상 주사율 " +
                targetRate +
                "Hz를 지원하지 않습니다.");
        }

        int testResult =
            ChangeDisplaySettingsEx(
                null,
                ref candidateMode,
                IntPtr.Zero,
                ChangeDisplaySettingsTest,
                IntPtr.Zero);

        if (testResult !=
            DisplayChangeSuccessful)
        {
            throw new InvalidOperationException(
                "주사율 변경 테스트에 실패했습니다.");
        }

        int applyResult =
            ChangeDisplaySettingsEx(
                null,
                ref candidateMode,
                IntPtr.Zero,
                ChangeDisplaySettingsUpdateRegistry,
                IntPtr.Zero);

        if (applyResult !=
            DisplayChangeSuccessful)
        {
            throw new InvalidOperationException(
                "주사율 변경 적용에 실패했습니다.");
        }
    }

    private static DEVMODE CreateDeviceMode()
    {
        DEVMODE mode =
            new DEVMODE();

        mode.Size =
            (ushort)Marshal.SizeOf<DEVMODE>();

        return mode;
    }

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(
        string? deviceName,
        int modeNumber,
        ref DEVMODE deviceMode);

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(
        string? deviceName,
        ref DEVMODE deviceMode,
        IntPtr windowHandle,
        uint flags,
        IntPtr parameter);

    [StructLayout(
        LayoutKind.Sequential,
        CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(
            UnmanagedType.ByValTStr,
            SizeConst = 32)]
        public string DeviceName;

        public ushort SpecVersion;

        public ushort DriverVersion;

        public ushort Size;

        public ushort DriverExtra;

        public uint Fields;

        public int PositionX;

        public int PositionY;

        public uint DisplayOrientation;

        public uint DisplayFixedOutput;

        public short Color;

        public short Duplex;

        public short YResolution;

        public short TTOption;

        public short Collate;

        [MarshalAs(
            UnmanagedType.ByValTStr,
            SizeConst = 32)]
        public string FormName;

        public ushort LogPixels;

        public uint BitsPerPixel;

        public uint Width;

        public uint Height;

        public uint DisplayFlags;

        public uint DisplayFrequency;

        public uint IcmMethod;

        public uint IcmIntent;

        public uint MediaType;

        public uint DitherType;

        public uint Reserved1;

        public uint Reserved2;

        public uint PanningWidth;

        public uint PanningHeight;
    }
}

/*
 * 충전 제한 요청 상태를 기존 BatteryMonitoringService와 연결한다.
 *
 * 실제 HP BIOS 충전 제한 적용 여부가 아니라,
 * 앱이 저장한 요청 상태를 반환한다.
 */
internal static class BatteryHelpers
{
    private static readonly BatteryChargeLimitRequestService
        RequestService = new();

    private static readonly BatteryMonitoringService
        BatteryMonitor = new();

    public static bool? GetBatteryChargeLimitState()
    {
        try
        {
            BatteryStatus status =
                BatteryMonitor.ReadStatus();

            if (!status.IsBatteryPresent)
            {
                return null;
            }

            return RequestService.IsRequested();
        }
        catch
        {
            return null;
        }
    }

    public static bool SetBatteryChargeLimit(
        bool enabled)
    {
        return RequestService.TrySetRequested(
            enabled,
            out _);
    }
}

/*
 * OmenMon.exe 실행과 결과 수집.
 *
 * OmenMon.exe는 사용자 컴퓨터에 이미 설치/다운로드된
 * 실행 파일을 호출할 뿐, 프로젝트에 포함하지 않는다.
 */
public sealed class OmenMonProcessRunner
{
    private const int TimeoutMilliseconds =
        15000;

    public string? FindExecutablePath()
    {
        string? configuredPath =
            Environment.GetEnvironmentVariable(
                "OMENMON_PATH");

        if (!string.IsNullOrWhiteSpace(
                configuredPath) &&
            File.Exists(configuredPath))
        {
            return configuredPath;
        }

        string localPath =
            Path.Combine(
                AppContext.BaseDirectory,
                "OmenMon.exe");

        if (File.Exists(localPath))
        {
            return localPath;
        }

        string downloadsPath =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder
                    .UserProfile),
                "Downloads");

        if (!Directory.Exists(downloadsPath))
        {
            return null;
        }

        try
        {
            return Directory
                .GetFiles(
                    downloadsPath,
                    "OmenMon.exe",
                    SearchOption.AllDirectories)
                .OrderByDescending(
                    File.GetLastWriteTime)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    public OmenMonProcessResult Run(
        string executablePath,
        params string[] arguments)
    {
        ProcessStartInfo startInfo =
            new ProcessStartInfo
            {
                FileName = executablePath,

                UseShellExecute = false,

                RedirectStandardOutput = true,

                RedirectStandardError = true,

                CreateNoWindow = true,

                StandardOutputEncoding = Encoding.UTF8,

                StandardErrorEncoding = Encoding.UTF8
            };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process? startedProcess =
            Process.Start(startInfo);

        if (startedProcess is null)
        {
            throw new InvalidOperationException(
                "OmenMon 프로세스를 시작할 수 없습니다.");
        }

        using Process process =
            startedProcess;

        string output =
            process.StandardOutput.ReadToEnd();

        string error =
            process.StandardError.ReadToEnd();

        if (!process.WaitForExit(
                TimeoutMilliseconds))
        {
            try
            {
                process.Kill(
                    entireProcessTree: true);
            }
            catch
            {
            }

            return new OmenMonProcessResult
            {
                ExitCode = -1,

                TimedOut = true,

                StandardOutput = output,

                StandardError =
                    error +
                    Environment.NewLine +
                    "OmenMon 명령 시간이 초과되었습니다."
            };
        }

        return new OmenMonProcessResult
        {
            ExitCode = process.ExitCode,

            TimedOut = false,

            StandardOutput = output,

            StandardError = error
        };
    }
}

public sealed class OmenMonProcessResult
{
    public int ExitCode { get; set; }

    public bool TimedOut { get; set; }

    public string StandardOutput { get; set; } =
        string.Empty;

    public string StandardError { get; set; } =
        string.Empty;

    public bool IsSuccess
    {
        get
        {
            return !TimedOut &&
                   ExitCode == 0;
        }
    }
}

/*
 * HP OMEN BIOS 팬 제어 서비스.
 *
 * 현재 이 앱에서 허용하는 BIOS 쓰기:
 *
 * FanMode=Default
 * FanMode=Cool
 * FanMode=Performance
 *
 * 현재 차단하는 BIOS 쓰기:
 *
 * FanMax=On
 * FanLevel=<Cpu>,<Gpu>
 *
 * 이유:
 * Max Fan On/Off 실제 동작과 Auto 복귀, Custom FanLevel 유지 동작을
 * 이 앱에서 아직 별도로 검증하지 않았기 때문이다.
 */
public sealed class HpFanControlService
{
    private const bool IsCustomFanWriteEnabled =
        false;

    private readonly object _syncRoot =
        new();

    private readonly OmenMonProcessRunner _runner =
        new();

    private FanProfile _requestedProfile =
        FanProfile.Auto;

    public HpFanCapability GetCapability()
    {
        lock (_syncRoot)
        {
            string? executablePath =
                _runner.FindExecutablePath();

            bool isAdministrator =
                IsRunningAsAdministrator();

            HpFanCapability capability =
                new HpFanCapability
                {
                    IsOmenMonFound =
                        !string.IsNullOrWhiteSpace(
                            executablePath),

                    IsAdministrator =
                        isAdministrator,

                    BackendPath =
                        executablePath ??
                        string.Empty
                };

            if (string.IsNullOrWhiteSpace(
                    executablePath))
            {
                capability.Level =
                    HpFanCapabilityLevel.Unavailable;

                capability.Description =
                    "OmenMon.exe를 찾을 수 없습니다.";

                return capability;
            }

            capability.CanReadFanStatus = true;

            if (!isAdministrator)
            {
                capability.Level =
                    HpFanCapabilityLevel.ReadOnly;

                capability.Description =
                    "팬 상태 조회는 가능하지만, " +
                    "팬 제어에는 관리자 권한이 필요합니다.";

                return capability;
            }

            capability.Level =
                HpFanCapabilityLevel.ControlReady;

            capability.CanSetAuto = true;

            capability.CanSetCool = true;

            capability.CanSetPerformance = true;

            /*
             * FAN MAX는 현재 앱에서는 의도적으로 차단한다.
             */
            capability.CanSetMaximum = false;

            capability.CanSetCustom =
                IsCustomFanWriteEnabled;

            if (capability.CanSetCustom)
            {
                capability.Level =
                    HpFanCapabilityLevel.CustomReady;
            }

            capability.Description =
                capability.CanSetCustom
                    ? "HP BIOS 팬 제어 및 Custom Fan 사용 가능"
                    : "HP BIOS 팬 제어 준비 완료 · " +
                      "FAN MAX와 Custom Fan은 검증 전까지 비활성화";

            return capability;
        }
    }

    public HpFanStatus ReadStatus()
    {
        lock (_syncRoot)
        {
            HpFanStatus status =
                new HpFanStatus
                {
                    ActiveRequestedMode =
                        ConvertProfileToControlMode(
                            _requestedProfile),

                    IsAdministrator =
                        IsRunningAsAdministrator(),

                    IsCustomControlEnabled =
                        IsCustomFanWriteEnabled
                };

            string? executablePath =
                _runner.FindExecutablePath();

            if (string.IsNullOrWhiteSpace(
                    executablePath))
            {
                status.SourceText =
                    "OmenMon.exe 없음";

                status.ErrorMessage =
                    "OMENMON_PATH 또는 Downloads 폴더를 확인하세요.";

                return status;
            }

            status.IsBackendAvailable = true;

            status.BackendPath = executablePath;

            try
            {
                OmenMonProcessResult result =
                    _runner.Run(
                        executablePath,
                        "-Bios",
                        "FanCount",
                        "FanType",
                        "FanLevel",
                        "FanMax",
                        "Temp");

                status.RawOutput =
                    result.StandardOutput;

                if (!result.IsSuccess)
                {
                    status.SourceText =
                        "HP BIOS 팬 상태 조회 실패";

                    status.ErrorMessage =
                        CreateErrorText(result);

                    return status;
                }

                ParseStatus(
                    result.StandardOutput,
                    status);

                status.IsReadSupported =
                    status.FanCount == 2 &&
                    status.CpuFanLevel.HasValue &&
                    status.GpuFanLevel.HasValue;

                status.IsControlSupported =
                    status.IsReadSupported &&
                    status.IsAdministrator;

                status.SourceText =
                    status.IsReadSupported
                        ? "HP BIOS WMI · OmenMon"
                        : "HP BIOS 팬 상태 파싱 실패";

                return status;
            }
            catch (Exception ex)
            {
                status.SourceText =
                    "OmenMon 실행 예외";

                status.ErrorMessage =
                    ex.GetType().Name +
                    ": " +
                    ex.Message;

                return status;
            }
        }
    }

    public HpFanControlResult ApplyProfile(
        FanProfile profile)
    {
        lock (_syncRoot)
        {
            return profile switch
            {
                FanProfile.Auto =>
                    ExecuteModeCommand(
                        FanProfile.Auto,
                        "FanMode=Default"),

                FanProfile.Quiet =>
                    ExecuteModeCommand(
                        FanProfile.Quiet,
                        "FanMode=Cool"),

                FanProfile.Performance =>
                    ExecuteModeCommand(
                        FanProfile.Performance,
                        "FanMode=Performance"),

                FanProfile.Max =>
                    CreateBlockedResult(
                        FanProfile.Max,
                        "FAN MAX는 아직 검증되지 않아 차단되어 있습니다."),

                FanProfile.Custom =>
                    CreateBlockedResult(
                        FanProfile.Custom,
                        "Custom Fan은 BIOS FanLevel 쓰기 검증 전까지 비활성화됩니다."),

                _ =>
                    CreateBlockedResult(
                        profile,
                        "알 수 없는 팬 프로필입니다.")
            };
        }
    }

    private HpFanControlResult ExecuteModeCommand(
        FanProfile profile,
        string modeArgument)
    {
        HpFanControlResult result =
            new HpFanControlResult
            {
                RequestedMode =
                    ConvertProfileToControlMode(
                        profile),

                CommandText =
                    "OmenMon.exe -Bios " +
                    modeArgument
            };

        if (!IsRunningAsAdministrator())
        {
            result.RequiresAdministrator = true;

            result.Message =
                "HP BIOS 팬 제어에는 관리자 권한이 필요합니다.";

            return result;
        }

        string? executablePath =
            _runner.FindExecutablePath();

        if (string.IsNullOrWhiteSpace(
                executablePath))
        {
            result.Message =
                "OmenMon.exe를 찾을 수 없습니다.";

            return result;
        }

        try
        {
            OmenMonProcessResult processResult =
                _runner.Run(
                    executablePath,
                    "-Bios",
                    modeArgument);

            result.Success =
                processResult.IsSuccess;

            result.StandardOutput =
                processResult.StandardOutput;

            result.StandardError =
                processResult.StandardError;

            if (!processResult.IsSuccess)
            {
                result.Message =
                    "HP BIOS 팬 모드 적용 실패: " +
                    CreateErrorText(
                        processResult);

                return result;
            }

            _requestedProfile = profile;

            result.Applied = true;

            result.Message = profile switch
            {
                FanProfile.Auto =>
                    "HP BIOS 팬을 Auto 정책으로 설정했습니다.",

                FanProfile.Quiet =>
                    "HP BIOS Cool 팬 정책을 요청했습니다.",

                FanProfile.Performance =>
                    "HP BIOS Performance 팬 정책을 요청했습니다.",

                _ =>
                    "HP BIOS 팬 정책을 설정했습니다."
            };

            return result;
        }
        catch (Exception ex)
        {
            result.Message =
                "HP BIOS 팬 모드 적용 예외: " +
                ex.GetType().Name +
                " - " +
                ex.Message;

            return result;
        }
    }

    private static HpFanControlResult CreateBlockedResult(
        FanProfile profile,
        string message)
    {
        return new HpFanControlResult
        {
            RequestedMode =
                ConvertProfileToControlMode(
                    profile),

            Success = false,

            Applied = false,

            Message = message
        };
    }

    private static HpFanControlMode
        ConvertProfileToControlMode(
            FanProfile profile)
    {
        return profile switch
        {
            FanProfile.Auto =>
                HpFanControlMode.Auto,

            FanProfile.Quiet =>
                HpFanControlMode.Cool,

            FanProfile.Performance =>
                HpFanControlMode.Performance,

            FanProfile.Max =>
                HpFanControlMode.Maximum,

            FanProfile.Custom =>
                HpFanControlMode.Custom,

            _ =>
                HpFanControlMode.Auto
        };
    }

    private static void ParseStatus(
        string output,
        HpFanStatus status)
    {
        status.FanCount = FindNumber(
            output,
            @"Fan Count:\s*0x[0-9a-fA-F]+\s*=\s*.*?=\s*(\d+)");

        status.CpuFanLevel = FindNumber(
            output,
            @"Fan #1 Level:\s*0x[0-9a-fA-F]+\s*=\s*.*?=\s*(\d+).*?\[Cpu\]");

        status.GpuFanLevel = FindNumber(
            output,
            @"Fan #2 Level:\s*0x[0-9a-fA-F]+\s*=\s*.*?=\s*(\d+).*?\[Gpu\]");

        status.BiosTemperatureCelsius = FindNumber(
            output,
            @"Temperature:\s*0x[0-9a-fA-F]+\s*=\s*.*?=\s*(\d+)");

        Match maximumFanMatch =
            Regex.Match(
                output,
                @"Maximum Fan Speed:\s*(Yes|No)",
                RegexOptions.IgnoreCase);

        if (maximumFanMatch.Success)
        {
            status.IsMaximumFanEnabled =
                string.Equals(
                    maximumFanMatch.Groups[1].Value,
                    "Yes",
                    StringComparison.OrdinalIgnoreCase);
        }
    }

    private static int? FindNumber(
        string text,
        string expression)
    {
        Match match =
            Regex.Match(
                text,
                expression,
                RegexOptions.IgnoreCase |
                RegexOptions.Singleline);

        if (!match.Success)
        {
            return null;
        }

        return int.TryParse(
            match.Groups[1].Value,
            out int value)
                ? value
                : null;
    }

    private static string CreateErrorText(
        OmenMonProcessResult result)
    {
        if (result.TimedOut)
        {
            return "명령 시간이 초과되었습니다.";
        }

        if (!string.IsNullOrWhiteSpace(
                result.StandardError))
        {
            return result.StandardError.Trim();
        }

        if (!string.IsNullOrWhiteSpace(
                result.StandardOutput))
        {
            return result.StandardOutput.Trim();
        }

        return "OmenMon 종료 코드: " +
               result.ExitCode;
    }

    private static bool IsRunningAsAdministrator()
    {
        try
        {
            using WindowsIdentity identity =
                WindowsIdentity.GetCurrent();

            WindowsPrincipal principal =
                new WindowsPrincipal(identity);

            return principal.IsInRole(
                WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}

/*
 * FormMain의 FAN AUTO / FAN QUIET / FAN PERFORMANCE /
 * FAN MAX / CUSTOM FAN 버튼에 연결할 서비스.
 */
public sealed class FanProfileService
{
    private readonly HpFanControlService _hpFan =
        new();

    private readonly SettingsStore _settings =
        SettingsStore.Load();

    public FanProfile RequestedProfile { get; private set; } =
        FanProfile.Auto;

    public int? CustomPercent { get; private set; }

    public List<(int Temperature, int Percent)>?
        CustomCurve
    {
        get;
        private set;
    }

    public HpFanControlResult LastResult { get; private set; } =
        new HpFanControlResult
        {
            Message = "팬 제어 요청 없음"
        };

    public FanProfileService()
    {
        if (_settings.CustomCurve is null ||
            _settings.CustomCurve.Count == 0)
        {
            return;
        }

        CustomCurve = _settings.CustomCurve
            .Select(
                point => (
                    point.Temperature,
                    point.Percent))
            .ToList();
    }

    public HpFanStatus ReadStatus()
    {
        return _hpFan.ReadStatus();
    }

    public HpFanCapability GetCapability()
    {
        return _hpFan.GetCapability();
    }

    public HpFanControlResult SetProfile(
        FanProfile profile,
        int? customPercent = null)
    {
        RequestedProfile = profile;

        if (profile == FanProfile.Custom)
        {
            CustomPercent = Math.Clamp(
                customPercent ?? 50,
                1,
                100);
        }
        else
        {
            CustomPercent = null;
        }

        LastResult =
            _hpFan.ApplyProfile(profile);

        return LastResult;
    }

    /*
     * Custom Curve는 현재 저장·표시만 수행한다.
     *
     * Custom FanLevel BIOS 쓰기는 의도적으로 비활성화되어 있다.
     */
    public void SetCustomCurve(
        IEnumerable<(int Temperature, int Percent)> points)
    {
        CustomCurve = points
            .Select(
                point => (
                    Temperature: Math.Clamp(
                        point.Temperature,
                        0,
                        100),

                    Percent: Math.Clamp(
                        point.Percent,
                        0,
                        100)))
            .OrderBy(
                point => point.Temperature)
            .ToList();

        _settings.CustomCurve = CustomCurve
            .Select(
                point => new SettingsStore.CurvePoint(
                    point.Temperature,
                    point.Percent))
            .ToList();

        _settings.Save();
    }

    public int EvaluateCustomPercent(
        int temperature)
    {
        if (CustomCurve is null ||
            CustomCurve.Count == 0)
        {
            return CustomPercent ?? 0;
        }

        if (temperature <=
            CustomCurve[0].Temperature)
        {
            return CustomCurve[0].Percent;
        }

        for (int index = 1;
             index < CustomCurve.Count;
             index++)
        {
            (int Temperature, int Percent) previous =
                CustomCurve[index - 1];

            (int Temperature, int Percent) current =
                CustomCurve[index];

            if (temperature > current.Temperature)
            {
                continue;
            }

            if (current.Temperature ==
                previous.Temperature)
            {
                return current.Percent;
            }

            double ratio =
                (double)(
                    temperature -
                    previous.Temperature) /
                (
                    current.Temperature -
                    previous.Temperature);

            return (int)Math.Round(
                previous.Percent +
                ratio *
                (
                    current.Percent -
                    previous.Percent));
        }

        return CustomCurve[^1].Percent;
    }

    public string GetDisplayText()
    {
        return RequestedProfile switch
        {
            FanProfile.Auto =>
                "Auto",

            FanProfile.Quiet =>
                "Cool",

            FanProfile.Performance =>
                "Performance",

            FanProfile.Max =>
                "Max 차단됨",

            FanProfile.Custom =>
                "Custom 비활성",

            _ =>
                "Auto"
        };
    }
}

/*
 * 앱의 충전 제한 요청 상태 저장소.
 *
 * 실제 BIOS 충전 제한 적용 여부가 아니라,
 * 사용자의 요청 ON/OFF 상태만 저장한다.
 */
public sealed class BatteryChargeLimitRequestService
{
    private const string KeyPath =
        @"Software\HP_Battery_Control";

    private const string ValueName =
        "Trigger";

    public bool IsRequested()
    {
        try
        {
            using RegistryKey? key =
                Registry.CurrentUser.OpenSubKey(
                    KeyPath,
                    writable: false);

            object? value =
                key?.GetValue(ValueName);

            return value switch
            {
                int number =>
                    number != 0,

                string text =>
                    text == "1" ||
                    text.Equals(
                        "true",
                        StringComparison.OrdinalIgnoreCase) ||
                    text.Equals(
                        "on",
                        StringComparison.OrdinalIgnoreCase),

                _ =>
                    false
            };
        }
        catch
        {
            return false;
        }
    }

    public bool TrySetRequested(
        bool enabled,
        out string message)
    {
        try
        {
            using RegistryKey key =
                Registry.CurrentUser.CreateSubKey(
                    KeyPath,
                    writable: true);

            key.SetValue(
                ValueName,
                enabled ? 1 : 0,
                RegistryValueKind.DWord);

            message = enabled
                ? "충전 제한 요청을 저장했습니다."
                : "충전 제한 요청을 해제했습니다.";

            return true;
        }
        catch (Exception ex)
        {
            message =
                "충전 제한 요청 저장 실패: " +
                ex.Message;

            return false;
        }
    }
}

public enum BatteryProtectionRequest
{
    Off,

    On
}

/*
 * 이전 BatteryControlSignalService 사용 코드와의 호환성 래퍼.
 */
public sealed class BatteryControlSignalService
{
    private readonly BatteryChargeLimitRequestService
        _requestService = new();

    public bool TrySetBatteryProtectionRequest(
        BatteryProtectionRequest request,
        out string message)
    {
        bool enabled =
            request == BatteryProtectionRequest.On;

        return _requestService.TrySetRequested(
            enabled,
            out message);
    }

    public bool TrySendBatteryProtectionOnPulse(
        out string message)
    {
        return _requestService.TrySetRequested(
            true,
            out message);
    }

    public BatteryProtectionRequest
        GetBatteryProtectionRequest()
    {
        return _requestService.IsRequested()
            ? BatteryProtectionRequest.On
            : BatteryProtectionRequest.Off;
    }

    public string GetCurrentRegistryPath()
    {
        return
            @"HKCU\Software\HP_Battery_Control\Trigger";
    }
}

/*
 * Windows powercfg 기반 전원 프로필 서비스.
 */
public sealed class PowerProfileService
{
    private static readonly Guid Balanced =
        new Guid(
            "381b4222-f694-41f0-9685-ff5bb260df2e");

    private static readonly Guid Saver =
        new Guid(
            "a1841308-3541-4fab-bc81-f71556f20b4a");

    private static readonly Guid HighPerformance =
        new Guid(
            "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    public string GetActiveDescription()
    {
        try
        {
            string output =
                RunPowerCfg(
                    "/getactivescheme");

            return string.IsNullOrWhiteSpace(output)
                ? "현재 전원 모드 확인 실패"
                : output.Trim();
        }
        catch (Exception ex)
        {
            return
                "전원 모드 확인 실패: " +
                ex.Message;
        }
    }

    public bool TrySet(
        PerformanceProfile profile,
        out string message)
    {
        Guid scheme = profile switch
        {
            PerformanceProfile.BestPowerEfficiency =>
                Saver,

            PerformanceProfile.BestPerformance =>
                HighPerformance,

            _ =>
                Balanced
        };

        try
        {
            RunPowerCfg(
                "/setactive " +
                scheme);

            message =
                "Windows 전원 프로필을 변경했습니다.";

            return true;
        }
        catch (Exception ex)
        {
            message =
                "전원 프로필 변경 실패: " +
                ex.Message;

            return false;
        }
    }

    private static string RunPowerCfg(
        string arguments)
    {
        ProcessStartInfo startInfo =
            new ProcessStartInfo(
                "powercfg",
                arguments)
            {
                UseShellExecute = false,

                RedirectStandardOutput = true,

                RedirectStandardError = true,

                CreateNoWindow = true
            };

        Process? startedProcess =
            Process.Start(startInfo);

        if (startedProcess is null)
        {
            throw new InvalidOperationException(
                "powercfg 프로세스를 시작할 수 없습니다.");
        }

        using Process process =
            startedProcess;

        string output =
            process.StandardOutput.ReadToEnd();

        string error =
            process.StandardError.ReadToEnd();

        if (!process.WaitForExit(5000))
        {
            try
            {
                process.Kill(
                    entireProcessTree: true);
            }
            catch
            {
            }

            throw new TimeoutException(
                "powercfg 명령 시간이 초과되었습니다.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? "powercfg 종료 코드: " +
                      process.ExitCode
                    : error.Trim());
        }

        return output;
    }
}

/*
 * 여러 모니터링 서비스를 단일 객체로 접근할 때 사용하는 facade.
 *
 * (CPU/GPU 모니터링 서비스 단일화 적용 완료됨)
 */
public sealed class MonitoringService : IDisposable
{
    private readonly HardwareCombinedMonitoringService _hwMonitor = new();
    private readonly BatteryMonitoringService _battery = new();
    private readonly FanProfileService _fan = new();
    private readonly PowerProfileService _power = new();

    private bool _isGpuMonitoringEnabled;

    // 통합된 HardwareCombinedMonitoringService를 사용하여 단 한 번의 폴링으로 CPU와 GPU 상태를 모두 반환
    public (CpuStatus Cpu, GpuStatus Gpu) ReadHardware() => _hwMonitor.ReadStatus(_isGpuMonitoringEnabled);

    public BatteryStatus ReadBattery() => _battery.ReadStatus();
    public HpFanStatus ReadFans() => _fan.ReadStatus();
    public HpFanCapability GetFanCapability() => _fan.GetCapability();
    public HpFanControlResult SetFanProfile(FanProfile profile, int? customPercent = null) => _fan.SetProfile(profile, customPercent);
    public string GetFanDisplayText() => _fan.GetDisplayText();

    public void SetGpuMonitoring(bool enabled) => _isGpuMonitoringEnabled = enabled;
    public bool IsGpuMonitoringEnabled => _isGpuMonitoringEnabled;

    public string GetActivePowerProfile() => _power.GetActiveDescription();
    public bool SetPowerProfile(PerformanceProfile profile, out string message) => _power.TrySet(profile, out message);

    public void Dispose()
    {
        _hwMonitor.Dispose();
        _battery.Dispose();
    }
}

public static class PlatformInfo
{
    // Detect whether a discrete GPU is present via Win32_VideoController enumeration
    public static bool HasDiscreteGpuInstalled()
    {
        try
        {
            using ManagementObjectSearcher searcher = new ManagementObjectSearcher("select Name, AdapterCompatibility from Win32_VideoController");
            foreach (ManagementObject mo in searcher.Get())
            {
                var name = (mo["Name"] as string) ?? string.Empty;
                var compat = (mo["AdapterCompatibility"] as string) ?? string.Empty;
                var combined = (name + " " + compat).ToLowerInvariant();
                if (combined.Contains("nvidia") || combined.Contains("amd") || combined.Contains("radeon"))
                    return true;
            }
        }
        catch
        {
            // ignore; assume no discrete GPU if detection fails
        }

        return false;
    }
}