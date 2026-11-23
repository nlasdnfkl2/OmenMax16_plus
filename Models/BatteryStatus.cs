namespace OmenMax16.Models;

public sealed class BatteryStatus
{
    public int? ChargePercent { get; set; }
    public bool IsAcConnected { get; set; }
    public bool IsCharging { get; set; }
    public bool IsBatteryPresent { get; set; }
    public float? BatteryVoltageVolts { get; set; }
    public float? DischargeWatts { get; set; }
    public float? ChargeWatts { get; set; }
    public int? RemainingMinutes { get; set; }
    public float? DesignCapacityMilliWattHours { get; set; }
    public float? FullChargedCapacityMilliWattHours { get; set; }
    public float? RemainingCapacityMilliWattHours { get; set; }
    public float? HealthPercent { get; set; }
    public float? WearPercent { get; set; }
    public string PowerStateText { get; set; } = "배터리 상태 확인 중";
    public string PowerRateSource { get; set; } = "배터리 전력 센서 확인 중";
    public string HardwareSource { get; set; } = "LibreHardwareMonitor 배터리 센서 확인 중";
    public bool IsAvailable => IsBatteryPresent;
    public bool IsDischargeRateAvailable => DischargeWatts.HasValue;
    public bool IsChargeRateAvailable => ChargeWatts.HasValue;
    public bool IsHealthAvailable => HealthPercent.HasValue;
}
