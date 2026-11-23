#nullable enable

namespace OmenMax16.Models;

/*
 * HP OMEN BIOS 팬 제어 상태 모델.
 *
 * LibreHardwareMonitor의 일반 Fan 센서와 별도로,
 * HP BIOS가 반환하는 FanCount / FanLevel / FanMax 값을 보관한다.
 */
public sealed class HpFanStatus
{
    public bool IsBackendAvailable { get; set; }

    public bool IsAdministrator { get; set; }

    public bool IsReadSupported { get; set; }

    public bool IsControlSupported { get; set; }

    public bool IsCustomControlEnabled { get; set; }

    public int? FanCount { get; set; }

    public int? CpuFanLevel { get; set; }

    public int? GpuFanLevel { get; set; }

    public int? BiosTemperatureCelsius { get; set; }

    public bool? IsMaximumFanEnabled { get; set; }

    public HpFanControlMode ActiveRequestedMode { get; set; } =
        HpFanControlMode.Auto;

    public string BackendPath { get; set; } =
        string.Empty;

    public string SourceText { get; set; } =
        "HP 팬 제어 백엔드 확인 중";

    public string ErrorMessage { get; set; } =
        string.Empty;

    public string RawOutput { get; set; } =
        string.Empty;

    /*
     * OmenMon FanLevel의 표시값은 보통
     * krpm × 10 수준으로 해석한다.
     *
     * 예:
     * Level 34 -> 약 3,400 RPM
     *
     * 실제 RPM은 BIOS/EC 구현에 따라 차이가 있을 수 있다.
     */
    public int? CpuFanApproximateRpm
    {
        get
        {
            return ConvertLevelToApproximateRpm(
                CpuFanLevel);
        }
    }

    public int? GpuFanApproximateRpm
    {
        get
        {
            return ConvertLevelToApproximateRpm(
                GpuFanLevel);
        }
    }

    public bool HasTwoFans
    {
        get
        {
            return FanCount == 2;
        }
    }

    public bool IsAvailable
    {
        get
        {
            return IsBackendAvailable &&
                   IsReadSupported;
        }
    }

    private static int? ConvertLevelToApproximateRpm(
        int? level)
    {
        if (!level.HasValue)
        {
            return null;
        }

        return level.Value * 100;
    }
}

/*
 * 앱에서 표현하는 HP BIOS 팬 제어 모드.
 */
public enum HpFanControlMode
{
    Auto,

    Cool,

    Performance,

    Maximum,

    Custom
}

/*
 * HP 팬 제어 가능 여부 수준.
 */
public enum HpFanCapabilityLevel
{
    Unavailable,

    ReadOnly,

    ControlReady,

    CustomReady
}

/*
 * BIOS 팬 명령 실행 결과.
 *
 * Success:
 * OmenMon 프로세스가 오류 없이 종료됐는지 여부.
 *
 * Applied:
 * 명령 실행 후 BIOS 팬 상태를 다시 읽어
 * 예상 상태를 확인했는지 여부.
 */
public sealed class HpFanControlResult
{
    public bool Success { get; set; }

    public bool Applied { get; set; }

    public bool RequiresAdministrator { get; set; }

    public HpFanControlMode RequestedMode { get; set; }

    public string Message { get; set; } =
        string.Empty;

    public string CommandText { get; set; } =
        string.Empty;

    public string StandardOutput { get; set; } =
        string.Empty;

    public string StandardError { get; set; } =
        string.Empty;
}

/*
 * HP OMEN 팬 제어 가능 여부.
 */
public sealed class HpFanCapability
{
    public HpFanCapabilityLevel Level { get; set; }

    public bool IsOmenMonFound { get; set; }

    public bool IsAdministrator { get; set; }

    public bool CanReadFanStatus { get; set; }

    public bool CanSetAuto { get; set; }

    public bool CanSetCool { get; set; }

    public bool CanSetPerformance { get; set; }

    public bool CanSetMaximum { get; set; }

    public bool CanSetCustom { get; set; }

    public string BackendPath { get; set; } =
        string.Empty;

    public string Description { get; set; } =
        string.Empty;
}

/*
 * Custom Fan UI 입력과 실제 BIOS FanLevel 변환 결과.
 */
public sealed class HpFanCustomRequest
{
    public int CpuPercent { get; set; }

    public int GpuPercent { get; set; }

    public int CpuFanLevel { get; set; }

    public int GpuFanLevel { get; set; }
}

/*
 * Models 폴더의 통합 진입점 마커.
 */
public static class ModelsPlaceholder
{
}