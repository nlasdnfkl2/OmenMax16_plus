#nullable enable

// [배터리 절전 보호 최적화 1부]: dGPU 잠깨움 현상을 물리적으로 완벽히 격리 차단한 안전형 센서 레이어
// 전력(W) 우선순위: 센서 Rate(이름 유연 매칭) → 전압×전류 → 남은 용량 변화(이벤트 방식) → (방전 시) 커널 역산

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;
using OmenMax16.Models;

namespace OmenMax16.Services;

/// <summary>
/// 남은 용량(mWh)이 "바뀌는 시점"을 기준으로 전력(W)을 계산합니다. 충전 +, 방전 -.
/// 첫 변화가 감지되면 임시 값을 즉시 내고, 변화가 쌓이면 최대 120초 구간 평균으로 안정화합니다.
/// 용량 갱신이 거친 펌웨어를 위해 정체 판정은 최근 변화 간격의 3배(최소 120초)로 적응합니다.
/// </summary>
internal sealed class BatteryRateEstimator
{
    private readonly List<(DateTime Utc, double Mwh)> _points = new();
    private readonly TimeSpan _window = TimeSpan.FromSeconds(120);
    private readonly TimeSpan _minStale = TimeSpan.FromSeconds(120);
    private readonly TimeSpan _minSpan = TimeSpan.FromSeconds(1);

    public int ChangeCount => Math.Max(0, _points.Count - 1);
    public double SecondsSinceLastChange { get; private set; }

    /// <returns>W (충전 +, 방전 -). 변화가 아직 없으면 null, 한동안 변화가 없으면 0</returns>
    public float? Update(double remainingMwh)
    {
        DateTime now = DateTime.UtcNow;

        if (_points.Count == 0)
        {
            _points.Add((now, remainingMwh));
            SecondsSinceLastChange = 0;
            return null;
        }

        if (remainingMwh != _points[^1].Mwh)
        {
            _points.Add((now, remainingMwh));
        }

        var last = _points[^1];
        SecondsSinceLastChange = (now - last.Utc).TotalSeconds;

        while (_points.Count > 2 && _points[^1].Utc - _points[1].Utc >= _window)
        {
            _points.RemoveAt(0);
        }

        // 적응형 정체 판정: 최근 변화 간격의 3배(최소 120초) 동안 변화가 없으면 0W
        TimeSpan lastInterval = _points.Count >= 2 ? _points[^1].Utc - _points[^2].Utc : TimeSpan.Zero;
        TimeSpan stale = TimeSpan.FromTicks(Math.Max(_minStale.Ticks, lastInterval.Ticks * 3));
        if (now - last.Utc > stale) return 0f;

        if (_points.Count < 2) return null;

        var first = _points[0];
        TimeSpan span = last.Utc - first.Utc;
        if (span < _minSpan) return null;

        return (float)((last.Mwh - first.Mwh) / span.TotalHours / 1000.0);
    }

    public void Reset()
    {
        _points.Clear();
        SecondsSinceLastChange = 0;
    }
}

public sealed class BatteryMonitoringService : IDisposable
{
    private const float DeadBandWatts = 0.5f; // 이보다 작으면 0W 로 표시
    private const float MaxPlausibleWatts = 400f;

    private readonly object _syncRoot = new();
    private readonly BatteryRateEstimator _rateEstimator = new();
    private Computer? _computer;
    private bool _initializationAttempted;
    private bool? _lastAc;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    public BatteryStatus ReadStatus()
    {
        lock (_syncRoot)
        {
            System.Windows.Forms.PowerStatus powerStatus = System.Windows.Forms.SystemInformation.PowerStatus;
            System.Windows.Forms.BatteryChargeStatus chargeStatus = powerStatus.BatteryChargeStatus;

            BatteryStatus status = new()
            {
                IsBatteryPresent = !chargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.NoSystemBattery),
                IsAcConnected = powerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online,
                IsCharging = chargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.Charging)
            };

            if (status.IsBatteryPresent && powerStatus.BatteryLifePercent >= 0.0f)
            {
                status.ChargePercent = (int)Math.Round(powerStatus.BatteryLifePercent * 100.0f);
            }

            if (powerStatus.BatteryLifeRemaining >= 0)
            {
                status.RemainingMinutes = powerStatus.BatteryLifeRemaining / 60;
            }

            EnsureInitialized();

            bool hasPowerValue = false;

            if (_computer is not null)
            {
                _computer.Accept(new SafeBatteryUpdateVisitor());
                List<ISensor> sensors = new();
                List<ISensor> batterySensors = new();

                foreach (IHardware hardware in _computer.Hardware)
                {
                    if (hardware.HardwareType == HardwareType.Battery ||
                        hardware.HardwareType == HardwareType.Motherboard ||
                        hardware.HardwareType == HardwareType.SuperIO)
                    {
                        Collect(hardware, sensors);
                        if (hardware.HardwareType == HardwareType.Battery)
                        {
                            Collect(hardware, batterySensors);
                        }
                    }
                }

                status.BatteryVoltageVolts = Find(sensors, "Voltage");

                status.DesignCapacityMilliWattHours = Find(sensors, "Designed Capacity");
                status.FullChargedCapacityMilliWattHours = Find(sensors, "Fully-Charged Capacity", "Full Charged Capacity");
                status.RemainingCapacityMilliWattHours = Find(sensors, "Remaining Capacity");

                float? level = Find(sensors, "Charge Level");
                float? degradation = Find(sensors, "Degradation Level");

                if (!status.ChargePercent.HasValue && level is >= 0.0f and <= 100.0f)
                {
                    status.ChargePercent = (int)Math.Round(level.Value);
                }

                if (degradation is >= 0.0f and <= 100.0f)
                {
                    status.WearPercent = degradation;
                }

                if (status.DesignCapacityMilliWattHours is > 0.0f && status.FullChargedCapacityMilliWattHours is >= 0.0f)
                {
                    status.HealthPercent = Math.Clamp(status.FullChargedCapacityMilliWattHours.Value / status.DesignCapacityMilliWattHours.Value * 100.0f, 0.0f, 100.0f);
                    status.WearPercent ??= 100.0f - status.HealthPercent;
                }

                status.HardwareSource = sensors.Count > 0 ? "LibreHardwareMonitor · Battery" : "배터리 하드웨어 센서 없음";
                status.PowerRateSource = "실시간 배터리 전력 값 없음";

                // 남은 용량 변화 추정기는 항상 갱신 (AC 전환 시에만 초기화)
                float? measured = null;
                if (status.RemainingCapacityMilliWattHours is float remainingMwh)
                {
                    if (_lastAc != status.IsAcConnected)
                    {
                        _rateEstimator.Reset();
                        _lastAc = status.IsAcConnected;
                    }

                    measured = _rateEstimator.Update(remainingMwh);
                }

                // [1순위] 센서 Rate (이름 유연 매칭)
                float? sensorRate = FindSignedRate(batterySensors);
                // [2순위] 전압 × 전류
                float? currentPower = FindCurrentTimesVoltage(batterySensors, status.BatteryVoltageVolts);

                if (sensorRate.HasValue && IsUsable(sensorRate.Value))
                {
                    hasPowerValue = true;
                    ApplyWatts(status, sensorRate.Value);
                    status.PowerRateSource = "LibreHardwareMonitor · Rate 센서 (실시간)";
                }
                else if (currentPower.HasValue && IsUsable(currentPower.Value))
                {
                    hasPowerValue = true;
                    ApplyWatts(status, currentPower.Value);
                    status.PowerRateSource = "LibreHardwareMonitor · 전압 × 전류 (실시간)";
                }
                else if (measured.HasValue && Math.Abs(measured.Value) < MaxPlausibleWatts)
                {
                    // [3순위] 남은 용량 변화 (센서가 0 이거나 없을 때)
                    hasPowerValue = true;
                    ApplyWatts(status, measured.Value);
                    status.PowerRateSource =
                        $"남은 용량 변화로 계산 · 변화 {_rateEstimator.ChangeCount}회 · 마지막 변화 {_rateEstimator.SecondsSinceLastChange:0}초 전";
                }
                else
                {
                    status.PowerRateSource =
                        DescribeWaiting(batterySensors, status.RemainingCapacityMilliWattHours.HasValue)
                        + $" · 변화 {_rateEstimator.ChangeCount}회 · 마지막 변화 {_rateEstimator.SecondsSinceLastChange:0}초 전";
                }
            }

            // [커널 레벨 서브 백업] 방전 중이고 위에서 전력을 못 구했을 때만
            if (!hasPowerValue && status.IsBatteryPresent && !status.IsAcConnected)
            {
                if (GetSystemPowerStatus(out SYSTEM_POWER_STATUS winStatus) &&
                    winStatus.BatteryLifeTime != 0xFFFFFFFF && winStatus.BatteryLifeTime > 60)
                {
                    float fullWh = (status.FullChargedCapacityMilliWattHours ?? 80000f) / 1000f;
                    float currentPercent = (status.ChargePercent ?? 50) / 100f;
                    float remainingWh = fullWh * currentPercent;
                    float remainingHours = winStatus.BatteryLifeTime / 3600f;

                    float estDischargeWatts = remainingWh / remainingHours;
                    if (estDischargeWatts is > 1.0f and < 330.0f)
                    {
                        status.DischargeWatts = estDischargeWatts;
                        status.PowerRateSource = "Windows Kernel32 · BatteryLifeTime 역산 (추정)";
                    }
                }
            }

            status.PowerStateText = CreatePowerStateText(status);
            return status;
        }
    }

    private static bool IsUsable(float watts) =>
        Math.Abs(watts) >= DeadBandWatts && Math.Abs(watts) < MaxPlausibleWatts;

    private static void ApplyWatts(BatteryStatus status, float watts)
    {
        if (watts >= DeadBandWatts) status.ChargeWatts = watts;
        else if (watts <= -DeadBandWatts) status.DischargeWatts = -watts;
    }

    // "Charge Rate" / "Discharge Rate" / "Charge/Discharge Rate" 등 이름이 달라도 찾고, 이름으로 부호를 보정
    // 이름에 Rate 가 없어도 배터리의 Power 센서를 최후 후보로 사용
    private static float? FindSignedRate(IEnumerable<ISensor> batterySensors)
    {
        ISensor? fallback = null;

        foreach (ISensor sensor in batterySensors)
        {
            if (sensor.SensorType != SensorType.Power || !sensor.Value.HasValue) continue;

            if (sensor.Name.Contains("Rate", StringComparison.OrdinalIgnoreCase))
            {
                return ApplyNameSign(sensor.Name, sensor.Value.Value);
            }

            fallback ??= sensor;
        }

        return fallback is not null ? ApplyNameSign(fallback.Name, fallback.Value!.Value) : null;
    }

    private static float? FindCurrentTimesVoltage(IEnumerable<ISensor> batterySensors, float? volts)
    {
        if (!volts.HasValue || volts.Value <= 0.0f) return null;

        foreach (ISensor sensor in batterySensors)
        {
            if (sensor.SensorType == SensorType.Current && sensor.Value.HasValue &&
                sensor.Name.Contains("Current", StringComparison.OrdinalIgnoreCase))
            {
                return ApplyNameSign(sensor.Name, sensor.Value.Value * volts.Value);
            }
        }
        return null;
    }

    private static float ApplyNameSign(string name, float value)
    {
        if (name.StartsWith("Discharge", StringComparison.OrdinalIgnoreCase)) return -Math.Abs(value);
        if (name.StartsWith("Charge ", StringComparison.OrdinalIgnoreCase)) return Math.Abs(value);
        return value; // "Charge/Discharge ..." 는 센서가 주는 부호를 그대로 사용
    }

    // 값이 안 나올 때 원인 파악용: 배터리 Power/Current 센서의 이름과 값을 보여줌
    private static string DescribeWaiting(IEnumerable<ISensor> batterySensors, bool hasRemainingCapacity)
    {
        List<string> parts = new();
        foreach (ISensor sensor in batterySensors)
        {
            if (sensor.SensorType == SensorType.Power || sensor.SensorType == SensorType.Current)
            {
                parts.Add($"{sensor.Name}={(sensor.Value.HasValue ? sensor.Value.Value.ToString("0.0") : "null")}");
            }
        }

        string sensorText = parts.Count > 0 ? string.Join(", ", parts) : "Power/Current 센서 없음";
        string capText = hasRemainingCapacity ? "용량 변화 수집 중" : "Remaining Capacity 센서 없음";
        return $"측정 준비 중 · {capText} · {sensorText}";
    }

    private void EnsureInitialized()
    {
        if (_initializationAttempted) return;
        _initializationAttempted = true;
        try { _computer = new Computer { IsBatteryEnabled = true, IsMotherboardEnabled = true }; _computer.Open(); }
        catch { _computer = null; }
    }

    private static void Collect(IHardware hardware, ICollection<ISensor> destination)
    {
        foreach (ISensor sensor in hardware.Sensors) destination.Add(sensor);
        foreach (IHardware child in hardware.SubHardware) Collect(child, destination);
    }

    private static float? Find(IEnumerable<ISensor> sensors, params string[] names)
    {
        foreach (string name in names)
        {
            foreach (ISensor sensor in sensors)
            {
                if (string.Equals(sensor.Name, name, StringComparison.OrdinalIgnoreCase) && sensor.Value.HasValue)
                {
                    return sensor.Value.Value;
                }
            }
        }
        return null;
    }

    private static string CreatePowerStateText(BatteryStatus status)
    {
        if (!status.IsBatteryPresent) return "배터리 없음";
        if (status.IsCharging) return "충전 중";
        if (status.IsAcConnected) return "AC 연결됨";
        return "배터리 사용 중";
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            try { _computer?.Close(); } catch { }
            _computer = null;
        }
    }

    private sealed class SafeBatteryUpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            if (hardware.HardwareType != HardwareType.GpuNvidia &&
                hardware.HardwareType != HardwareType.GpuAmd &&
                hardware.HardwareType != HardwareType.GpuIntel)
            {
                hardware.Update();
            }

            foreach (IHardware child in hardware.SubHardware)
            {
                child.Accept(this);
            }
        }

        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }
}