namespace OmenMax16.Models;

public sealed class CpuStatus
{
    public string Name { get; set; } =
        "CPU 정보 확인 중";

    public float? UsagePercent { get; set; }
    public float? VoltageVolts { get; set; }
    public float? ClockMHz { get; set; }

    public float? TemperatureCelsius { get; set; }

    public float? FanSpeedRpm { get; set; }

    public float? TargetFanSpeedRpm { get; set; }

    public float? PowerWatts { get; set; }

    public float? MaxPowerWatts { get; set; }

    public float? Pl1LimitWatts { get; set; }

    public float? Pl2LimitWatts { get; set; }

    public float? CoreVoltageVolts { get; set; }

    public bool IsHardwareSensorAvailable { get; set; }

    public string TemperatureSource { get; set; } =
        "CPU 센서 초기화 전";

    public string ClockSource { get; set; } =
        "CPU 센서 초기화 전";

    public string PowerSource { get; set; } =
        "CPU 센서 초기화 전";

    public string VoltageSource { get; set; } =
        "CPU 센서 초기화 전";

    public string FanControlMode { get; set; } =
        "팬 센서 초기화 전";

    public string ErrorMessage { get; set; } =
        string.Empty;

    public bool IsUsageAvailable => UsagePercent.HasValue;
    public bool IsClockAvailable => ClockMHz.HasValue;
    public bool IsTemperatureAvailable => TemperatureCelsius.HasValue;
    public bool IsPowerAvailable => PowerWatts.HasValue;
    public bool IsVoltageAvailable => CoreVoltageVolts.HasValue;
    public bool IsFanSpeedAvailable => FanSpeedRpm.HasValue;
}
