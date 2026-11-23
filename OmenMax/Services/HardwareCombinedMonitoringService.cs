#nullable enable

using LibreHardwareMonitor.Hardware;
using OmenMax16.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace OmenMax16.Services;

public sealed class HardwareCombinedMonitoringService : IDisposable
{
    private readonly object _syncRoot = new();
    private Computer? _computer;
    private bool _sensorInitialized;
    private string _sensorInitializationError = string.Empty;
    private int _consecutiveErrors = 0;

    // Windows 커널 PDH 유효 클럭 카운터 (Arrow Lake 275HX의 IA32_PERF_STATUS 5.15GHz 고정 우회)
    private IntPtr _pdhQuery = IntPtr.Zero;
    private IntPtr _pdhPerfCounter = IntPtr.Zero;
    private IntPtr _pdhFreqCounter = IntPtr.Zero;
    private bool _pdhInitialized;
    private bool _pdhFailed;

    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhCstatusValidData = 0x00000000;
    private const uint PdhCstatusNewData = 0x00000001;

    private const int MaxConsecutiveErrorsBeforeReset = 10;
    private const int MaxGlitchTolerance = 10;

    // 활성 코어 가중 평균 온도 계산 파라미터
    private const float CoreActiveLoadThreshold = 10f;   // 10% 초과 시 활성 코어로 간주
    private const float ActiveCoreMaxBonusWeight = 0.5f; // 유휴 코어(1.0) 대비 활성 코어 최대 가중치(1.5)

    private static readonly Regex CoreTempIndexRegex = new(
        @"^(?:CPU\s+)?(?:P-|E-)?Core\s*#?\s*(\d+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CoreLoadIndexRegex = new(
        @"^(?:CPU\s+)?(?:P-|E-)?Core\s*#?\s*(\d+)(?:\s+Thread\s*#?\s*\d+)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // CPU 캐싱 (튐 현상 방지)
    private float _lastValidUsage = 0f;
    private int _usageGlitchCount = 0;
    private float _lastValidTemp = 0f;
    private int _tempGlitchCount = 0;
    private float _lastValidClock = 0f;
    private int _clockGlitchCount = 0;
    private float _lastValidPower = 0f;
    private int _powerGlitchCount = 0;
    private float _lastValidVoltage = 0f;
    private int _voltageGlitchCount = 0;
    private int _voltageDipCount = 0;
    private const int MaxVoltageDipHold = 5;

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct PdhFmtCounterValue
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhOpenQueryW(IntPtr szDataSource, IntPtr dwUserData, out IntPtr phQuery);

    [DllImport("pdh.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr hQuery, string szFullCounterPath, IntPtr dwUserData, out IntPtr phCounter);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCollectQueryData(IntPtr hQuery);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterValue(IntPtr hCounter, uint dwFormat, out uint lpdwType, out PdhFmtCounterValue pValue);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCloseQuery(IntPtr hQuery);

    public (CpuStatus Cpu, GpuStatus Gpu) ReadStatus(bool enableGpuMonitoring)
    {
        lock (_syncRoot)
        {
            CpuStatus cpuStatus = new()
            {
                Name = Environment.MachineName,
                FanControlMode = "HP 팬 컨트롤러 연결 대기"
            };

            GpuStatus gpuStatus = new()
            {
                IsMonitoringEnabled = enableGpuMonitoring,
                Name = enableGpuMonitoring ? "GPU 센서 초기화 중" : "GPU 대기 중",
                Source = enableGpuMonitoring ? "초기화 중" : "모니터링 비활성화됨"
            };

            EnsureSensorInitialized();

            if (!_sensorInitialized || _computer is null)
            {
                cpuStatus.IsHardwareSensorAvailable = false;
                cpuStatus.ErrorMessage = _sensorInitializationError;
                gpuStatus.ErrorMessage = _sensorInitializationError;
                return (cpuStatus, gpuStatus);
            }

            try
            {
                _computer.Accept(new CombinedUpdateVisitor(enableGpuMonitoring));

                List<ISensor> cpuSensors = new();
                List<GpuCandidate> gpuCandidates = new();

                foreach (IHardware hardware in _computer.Hardware)
                {
                    if (hardware.HardwareType == HardwareType.Cpu)
                    {
                        cpuStatus.Name = hardware.Name;
                        CollectSensors(hardware, cpuSensors);
                    }
                    else if (enableGpuMonitoring && IsGpuHardware(hardware))
                    {
                        List<ISensor> gSensors = new();
                        CollectSensors(hardware, gSensors);

                        float? gTemp = FindSensorValue(gSensors, SensorType.Temperature, "GPU Core", "Core");
                        float? gUsage = FindSensorValue(gSensors, SensorType.Load, "GPU Core", "Core", "D3D");
                        float? gClock = NormalizeClockMHz(FindSensorValue(gSensors, SensorType.Clock, "GPU Core", "Core"));
                        float? gPower = NormalizeGpuPowerWatts(FindSensorValue(gSensors, SensorType.Power, "GPU Package", "GPU Power", "GPU Core", "Power"));

                        if (hardware.HardwareType == HardwareType.GpuNvidia && !gTemp.HasValue && !gUsage.HasValue && !gClock.HasValue && (gPower ?? 0f) <= 0.1f)
                            continue;

                        gpuCandidates.Add(new GpuCandidate
                        {
                            HardwareType = hardware.HardwareType,
                            Name = hardware.Name,
                            TemperatureCelsius = gTemp,
                            UsagePercent = gUsage,
                            CoreClockMHz = gClock,
                            PowerWatts = gPower,
                            FanSpeedRpm = FindSensorValue(gSensors, SensorType.Fan, "GPU Fan", "Fan")
                        });
                    }
                }

                _consecutiveErrors = 0;

                // CPU 데이터 정제 (Intel Core Ultra HX / AMD Ryzen AI 300·400 지원)
                cpuStatus.IsHardwareSensorAvailable = true;
                ProcessCpuSensors(cpuSensors, cpuStatus);

                // GPU 데이터 정제 (RTX 5060 ~ 5090 지원)
                if (enableGpuMonitoring && gpuCandidates.Count > 0)
                {
                    GpuCandidate selectedGpu = SelectCandidate(gpuCandidates);
                    bool isNvidia = selectedGpu.HardwareType == HardwareType.GpuNvidia;

                    float? gUsageNorm = NormalizeLoad(selectedGpu.UsagePercent);
                    float? gPowerNorm = NormalizeGpuPowerWatts(selectedGpu.PowerWatts);
                    float? gClockNorm = NormalizeClockMHz(selectedGpu.CoreClockMHz);

                    if (isNvidia && (gUsageNorm ?? 0f) <= 0.5f && (gPowerNorm ?? 0f) <= 0.5f) gClockNorm = 0f;

                    gpuStatus = new GpuStatus
                    {
                        IsMonitoringEnabled = true,
                        IsHardwareAvailable = true,
                        IsDiscrete = isNvidia,
                        Name = selectedGpu.Name,
                        TemperatureCelsius = NormalizeTemperature(selectedGpu.TemperatureCelsius),
                        UsagePercent = gUsageNorm,
                        CoreClockMHz = gClockNorm,
                        PowerWatts = gPowerNorm,
                        FanSpeedRpm = NormalizeFanRpm(selectedGpu.FanSpeedRpm),
                        Source = "LibreHardwareMonitor · " + (isNvidia ? "dGPU" : "iGPU")
                    };
                }

                return (cpuStatus, gpuStatus);
            }
            catch (Exception ex)
            {
                _consecutiveErrors++;
                if (_consecutiveErrors < MaxConsecutiveErrorsBeforeReset)
                {
                    cpuStatus.IsHardwareSensorAvailable = true;
                    cpuStatus.UsagePercent = _lastValidUsage;
                    cpuStatus.TemperatureCelsius = _lastValidTemp > 0 ? _lastValidTemp : null;
                    cpuStatus.ClockMHz = _lastValidClock > 0 ? _lastValidClock : null;
                    cpuStatus.PowerWatts = _lastValidPower > 0 ? _lastValidPower : null;
                    cpuStatus.VoltageVolts = _lastValidVoltage > 0 ? _lastValidVoltage : null;
                    return (cpuStatus, gpuStatus);
                }

                ResetSensorSession();
                cpuStatus.IsHardwareSensorAvailable = false;
                cpuStatus.ErrorMessage = $"통합 모니터링 오류: {ex.Message}";
                return (cpuStatus, gpuStatus);
            }
        }
    }

    private void EnsureSensorInitialized()
    {
        if (_sensorInitialized && _computer is not null) return;
        try
        {
            _computer?.Close();
            _computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true };
            _computer.Open();
            _sensorInitialized = true;
            _sensorInitializationError = string.Empty;
            _consecutiveErrors = 0;
        }
        catch (Exception ex)
        {
            _sensorInitialized = false;
            _sensorInitializationError = ex.Message;
            _computer = null;
        }
    }

    private void EnsurePdhInitialized()
    {
        if (_pdhInitialized || _pdhFailed) return;

        IntPtr query = IntPtr.Zero;
        try
        {
            if (PdhOpenQueryW(IntPtr.Zero, IntPtr.Zero, out query) != 0 || query == IntPtr.Zero)
            {
                _pdhFailed = true;
                return;
            }

            if (PdhAddEnglishCounterW(query, @"\Processor Information(_Total)\% Processor Performance", IntPtr.Zero, out IntPtr perfCounter) != 0 ||
                PdhAddEnglishCounterW(query, @"\Processor Information(_Total)\Processor Frequency", IntPtr.Zero, out IntPtr freqCounter) != 0)
            {
                PdhCloseQuery(query);
                _pdhFailed = true;
                return;
            }

            PdhCollectQueryData(query);

            _pdhQuery = query;
            _pdhPerfCounter = perfCounter;
            _pdhFreqCounter = freqCounter;
            _pdhInitialized = true;
        }
        catch
        {
            if (query != IntPtr.Zero)
            {
                try { PdhCloseQuery(query); } catch { }
            }
            _pdhFailed = true;
        }
    }

    private float? ReadPdhEffectiveClockMHz()
    {
        EnsurePdhInitialized();
        if (!_pdhInitialized || _pdhQuery == IntPtr.Zero) return null;

        try
        {
            if (PdhCollectQueryData(_pdhQuery) != 0) return null;

            uint s1 = PdhGetFormattedCounterValue(_pdhPerfCounter, PdhFmtDouble, out _, out PdhFmtCounterValue perfVal);
            uint s2 = PdhGetFormattedCounterValue(_pdhFreqCounter, PdhFmtDouble, out _, out PdhFmtCounterValue freqVal);

            if (s1 != 0 || s2 != 0) return null;
            if (perfVal.CStatus is not (PdhCstatusValidData or PdhCstatusNewData)) return null;
            if (freqVal.CStatus is not (PdhCstatusValidData or PdhCstatusNewData)) return null;

            double baseMhz = freqVal.DoubleValue;
            double perfPct = perfVal.DoubleValue;
            if (baseMhz <= 100.0 || perfPct <= 1.0) return null;

            double effectiveMhz = baseMhz * (perfPct / 100.0);
            return effectiveMhz is > 100.0 and <= 6500.0 ? (float)effectiveMhz : null;
        }
        catch
        {
            return null;
        }
    }

    private void ClosePdhQuery()
    {
        if (_pdhQuery != IntPtr.Zero)
        {
            try { PdhCloseQuery(_pdhQuery); } catch { }
            _pdhQuery = IntPtr.Zero;
        }
        _pdhPerfCounter = IntPtr.Zero;
        _pdhFreqCounter = IntPtr.Zero;
        _pdhInitialized = false;
        _pdhFailed = false;
    }

    private void ResetSensorSession()
    {
        try { _computer?.Close(); }
        catch { }
        finally
        {
            _computer = null;
            _sensorInitialized = false;
            _consecutiveErrors = 0;
            ClosePdhQuery();
        }
    }

    private void ProcessCpuSensors(List<ISensor> cpuSensors, CpuStatus status)
    {
        bool isAmd = HardwarePlatformDetector.Current.IsAmd;
        AmdCpuTuningService.OmenAmdCpuTelemetry bridgeTelemetry = default;
        bool hasBridge = isAmd && AmdCpuTuningService.TryReadBridgeTelemetry(out bridgeTelemetry);

        // 1. CPU 사용량
        float? rawUsage = Math.Clamp(
            cpuSensors.FirstOrDefault(v => v.SensorType == SensorType.Load && v.Name.Contains("CPU Total", StringComparison.OrdinalIgnoreCase))?.Value ?? 0f,
            0f, 100f);
        if (rawUsage > 0f || _usageGlitchCount >= 3) { _lastValidUsage = rawUsage ?? 0f; _usageGlitchCount = 0; } else _usageGlitchCount++;
        status.UsagePercent = _usageGlitchCount <= MaxGlitchTolerance ? _lastValidUsage : 0f;

        // 2. CPU 온도 (AMD: Tctl/Tdie 우선 / Intel: 전체 코어 평균 기반 + 활성화된 코어에 소폭 가중치 부여)
        float? rawTemp = isAmd
            ? CalculateAmdCpuTemperature(cpuSensors, hasBridge, bridgeTelemetry, out string tempSource)
            : CalculateWeightedAverageCpuTemperature(cpuSensors, out tempSource);
        status.TemperatureSource = tempSource;

        if (rawTemp > 0f) { _lastValidTemp = rawTemp.Value; _tempGlitchCount = 0; } else _tempGlitchCount++;
        status.TemperatureCelsius = _tempGlitchCount <= MaxGlitchTolerance ? _lastValidTemp : null;

        // 3. CPU 클럭 (Windows 커널 PDH APERF/MPERF 유효 클럭 우선 -> LHM 평균/최대 폴백)
        var coreClocks = cpuSensors
            .Where(v => v.SensorType == SensorType.Clock
                     && v.Name.Contains("Core", StringComparison.OrdinalIgnoreCase)
                     && !v.Name.Contains("Bus", StringComparison.OrdinalIgnoreCase)
                     && v.Value is > 100f and <= 6500f)
            .ToList();

        float? pdhEffectiveClock = ReadPdhEffectiveClockMHz();

        float? rawClock;
        if (isAmd)
        {
            rawClock = (hasBridge && bridgeTelemetry.AverageEffectiveClockMHz > 100 ? (float?)bridgeTelemetry.AverageEffectiveClockMHz : null)
                ?? pdhEffectiveClock
                ?? cpuSensors.FirstOrDefault(v => v.SensorType == SensorType.Clock && v.Name.Contains("Average", StringComparison.OrdinalIgnoreCase) && v.Value > 100f)?.Value
                ?? (coreClocks.Count > 0 ? coreClocks.Average(v => v.Value) : null);
            status.ClockSource = pdhEffectiveClock.HasValue ? "Windows PDH 유효 클럭" : "LibreHardwareMonitor";
        }
        else
        {
            rawClock = pdhEffectiveClock
                ?? cpuSensors.FirstOrDefault(v => v.SensorType == SensorType.Clock && v.Name.Contains("Average", StringComparison.OrdinalIgnoreCase) && v.Value > 100f)?.Value
                ?? (coreClocks.Count > 0 ? coreClocks.Average(v => v.Value) : null)
                ?? cpuSensors.FirstOrDefault(v => v.SensorType == SensorType.Clock && v.Name.Contains("Core Max", StringComparison.OrdinalIgnoreCase) && v.Value > 100f)?.Value;
            status.ClockSource = pdhEffectiveClock.HasValue ? "Windows PDH 유효 클럭 (APERF/MPERF)" : "LibreHardwareMonitor (폴백)";
        }

        if (rawClock > 0f) { _lastValidClock = rawClock.Value; _clockGlitchCount = 0; } else _clockGlitchCount++;
        status.ClockMHz = _clockGlitchCount <= MaxGlitchTolerance ? _lastValidClock : null;

        // 4. CPU 전력 (Intel "CPU Package" / AMD "Package", "PPT")
        float? rawPower = cpuSensors.FirstOrDefault(v =>
            v.SensorType == SensorType.Power &&
            (v.Name.Contains("CPU Package", StringComparison.OrdinalIgnoreCase) ||
             v.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
             v.Name.Contains("PPT", StringComparison.OrdinalIgnoreCase)))?.Value;

        if (rawPower is null or <= 0f)
        {
            var powerSensors = cpuSensors.Where(v => v.SensorType == SensorType.Power && v.Value is > 0f and <= 250f).ToList();
            rawPower = powerSensors.Count > 0 ? powerSensors.Max(v => v.Value) : 0f;
        }

        if ((rawPower is null or <= 0f) && hasBridge && bridgeTelemetry.PPTValueWatts > 0)
        {
            rawPower = (float)bridgeTelemetry.PPTValueWatts;
        }

        if (rawPower is > 0f and <= 250f) { _lastValidPower = rawPower.Value; _powerGlitchCount = 0; } else _powerGlitchCount++;
        status.PowerWatts = _powerGlitchCount <= MaxGlitchTolerance ? _lastValidPower : null;

        // 5. CPU 전압
        float? rawVoltage = null;

        if (isAmd)
        {
            rawVoltage = AmdCpuTuningService.LastCoreVoltage;

            if (rawVoltage is null or < 0.3f or > 1.75f)
            {
                var coreSvi2 = cpuSensors.FirstOrDefault(v =>
                    v.SensorType == SensorType.Voltage
                    && v.Name.Equals("Core (SVI2 TFN)", StringComparison.OrdinalIgnoreCase));

                var perCoreVid = cpuSensors
                    .Where(v => v.SensorType == SensorType.Voltage
                             && Regex.IsMatch(v.Name, @"^Core\s*#\s*\d+\s*VID$", RegexOptions.IgnoreCase)
                             && (v.Value ?? 0f) > 0.05f)
                    .ToList();

                if (coreSvi2?.Value != null && perCoreVid.Count > 0)
                {
                    float diff = coreSvi2.Value.Value - perCoreVid.Average(v => v.Value ?? 0f);
                    rawVoltage = diff is >= 0.3f and <= 1.75f ? diff : coreSvi2.Value.Value;
                }
                else if (coreSvi2?.Value is >= 0.3f and <= 1.75f)
                {
                    rawVoltage = coreSvi2.Value.Value;
                }
            }

            if ((rawVoltage is null or < 0.3f or > 1.75f) && hasBridge && bridgeTelemetry.AverageCoreVoltageVolts is >= 0.3 and <= 1.75)
            {
                rawVoltage = (float)bridgeTelemetry.AverageCoreVoltageVolts;
            }
        }
        else
        {
            // Intel Core Ultra 9 275HX: 공유 IA Vcore 레일은 최고 활성 VID 를 따르므로 C-State 0.6V 딥 희석 방지
            rawVoltage = cpuSensors
                .FirstOrDefault(v => v.SensorType == SensorType.Voltage
                                  && (v.Name.Equals("CPU Core", StringComparison.OrdinalIgnoreCase) ||
                                      v.Name.Equals("Vcore", StringComparison.OrdinalIgnoreCase) ||
                                      v.Name.Equals("Core Max", StringComparison.OrdinalIgnoreCase))
                                  && v.Value is >= 0.3f and <= 1.75f)?.Value;

            if (rawVoltage is null)
            {
                var vidSensors = cpuSensors
                    .Where(v => v.SensorType == SensorType.Voltage
                             && Regex.IsMatch(v.Name, @"(Core\s*#?\s*\d+\s*VID|VID\s*#?\s*\d+)", RegexOptions.IgnoreCase)
                             && v.Value is >= 0.3f and <= 1.75f)
                    .ToList();

                if (vidSensors.Count > 0)
                {
                    // 유휴 C-State 코어(0.6V대)가 평균을 끌어내리지 않도록 활성 코어 최고 VID 사용
                    rawVoltage = vidSensors.Max(v => v.Value ?? 0f);
                }
            }

            if (rawVoltage is null)
            {
                var coreSvi2 = cpuSensors.FirstOrDefault(v =>
                    v.SensorType == SensorType.Voltage
                    && v.Name.Equals("Core (SVI2 TFN)", StringComparison.OrdinalIgnoreCase));

                var perCoreVid = cpuSensors
                    .Where(v => v.SensorType == SensorType.Voltage
                             && Regex.IsMatch(v.Name, @"^Core\s*#\s*\d+\s*VID$", RegexOptions.IgnoreCase)
                             && (v.Value ?? 0f) > 0.05f)
                    .ToList();

                if (coreSvi2?.Value != null && perCoreVid.Count > 0)
                {
                    rawVoltage = coreSvi2.Value.Value - perCoreVid.Average(v => v.Value ?? 0f);
                }
            }
        }

        if (rawVoltage is >= 0.3f and <= 1.75f)
        {
            bool isCpuActive = (status.ClockMHz ?? 0f) > 1500f || (status.UsagePercent ?? 0f) > 1.5f || (status.PowerWatts ?? 0f) > 10f;
            bool isTransientLowDip = !isAmd && rawVoltage.Value < 0.85f && _lastValidVoltage >= 0.95f && isCpuActive;

            if (isTransientLowDip && _voltageDipCount < MaxVoltageDipHold)
            {
                _voltageDipCount++;
            }
            else
            {
                _lastValidVoltage = rawVoltage.Value;
                _voltageGlitchCount = 0;
                _voltageDipCount = 0;
            }
        }
        else
        {
            _voltageGlitchCount++;
        }
        status.VoltageVolts = _voltageGlitchCount <= MaxGlitchTolerance ? _lastValidVoltage : null;
    }

    private static float? CalculateAmdCpuTemperature(
        List<ISensor> cpuSensors,
        bool hasBridge,
        AmdCpuTuningService.OmenAmdCpuTelemetry bridge,
        out string source)
    {
        var validTempSensors = cpuSensors
            .Where(v => v.SensorType == SensorType.Temperature
                     && !v.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase)
                     && !v.Name.Contains("TjMax", StringComparison.OrdinalIgnoreCase)
                     && v.Value is > 0f and <= 125f)
            .ToList();

        float? tctl = validTempSensors
            .FirstOrDefault(v => v.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) ||
                                 v.Name.Contains("Tdie", StringComparison.OrdinalIgnoreCase))?.Value;
        if (tctl.HasValue)
        {
            source = "AMD Tctl/Tdie";
            return tctl.Value;
        }

        if (validTempSensors.Count > 0)
        {
            source = "AMD 최대 온도 센서";
            return validTempSensors.Max(v => v.Value!.Value);
        }

        if (hasBridge && bridge.TemperatureCelsius is > 0 and <= 125)
        {
            source = "Ryzen Master SDK";
            return (float)bridge.TemperatureCelsius;
        }

        source = "CPU 온도 센서 없음";
        return null;
    }

    /// <summary>
    /// 전체 CPU 코어의 온도를 평균으로 계산하되, 현재 부하(또는 부스트 클럭)가 걸려 활성화된 코어에
    /// 소폭 가중치(1.0 ~ 1.5)를 부여합니다. 단일 코어 스파이크(Package/Max)에 의한 과장된 온도 튐을 방지합니다.
    /// </summary>
    private static float? CalculateWeightedAverageCpuTemperature(List<ISensor> cpuSensors, out string source)
    {
        var validTempSensors = cpuSensors
            .Where(v => v.SensorType == SensorType.Temperature
                     && !v.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase)
                     && !v.Name.Contains("TjMax", StringComparison.OrdinalIgnoreCase)
                     && v.Value is > 0f and <= 125f)
            .ToList();

        if (validTempSensors.Count == 0)
        {
            source = "CPU 온도 센서 없음";
            return null;
        }

        // 개별 코어 온도 센서 추출 ("CPU Core #1", "Core #2", "P-Core #1", "E-Core #1" 등)
        var perCoreTemps = new List<(int CoreIndex, float Temp)>();
        foreach (ISensor sensor in validTempSensors)
        {
            Match m = CoreTempIndexRegex.Match(sensor.Name.Trim());
            if (m.Success && int.TryParse(m.Groups[1].Value, out int coreIdx))
            {
                perCoreTemps.Add((coreIdx, sensor.Value!.Value));
            }
        }

        if (perCoreTemps.Count > 0)
        {
            // 코어별 사용률(%) 수집 (HT 스레드가 있는 경우 물리 코어 기준 최대 부하 사용)
            var coreLoads = new Dictionary<int, float>();
            foreach (ISensor sensor in cpuSensors)
            {
                if (sensor.SensorType != SensorType.Load || sensor.Value is not (>= 0f and <= 100f))
                    continue;

                Match m = CoreLoadIndexRegex.Match(sensor.Name.Trim());
                if (m.Success && int.TryParse(m.Groups[1].Value, out int coreIdx))
                {
                    float load = sensor.Value.Value;
                    if (!coreLoads.TryGetValue(coreIdx, out float existing) || load > existing)
                    {
                        coreLoads[coreIdx] = load;
                    }
                }
            }

            // 부하 센서가 없는 코어를 위한 코어별 클럭(MHz) 보조 수집
            var coreClocks = new Dictionary<int, float>();
            if (coreLoads.Count == 0)
            {
                foreach (ISensor sensor in cpuSensors)
                {
                    if (sensor.SensorType != SensorType.Clock || sensor.Value is not (> 100f and <= 6500f))
                        continue;

                    Match m = CoreTempIndexRegex.Match(sensor.Name.Trim());
                    if (m.Success && int.TryParse(m.Groups[1].Value, out int coreIdx))
                    {
                        coreClocks[coreIdx] = sensor.Value.Value;
                    }
                }
            }

            double weightedSum = 0d;
            double weightTotal = 0d;

            foreach (var (coreIdx, temp) in perCoreTemps)
            {
                // 기본 가중치 1.0 (전체 코어 균등 평균 기반)
                float weight = 1.0f;

                if (coreLoads.TryGetValue(coreIdx, out float load))
                {
                    if (load > CoreActiveLoadThreshold)
                    {
                        float activeRatio = Math.Clamp((load - CoreActiveLoadThreshold) / (100f - CoreActiveLoadThreshold), 0f, 1f);
                        weight += ActiveCoreMaxBonusWeight * activeRatio;
                    }
                }
                else if (coreClocks.TryGetValue(coreIdx, out float clockMhz) && clockMhz > 2000f)
                {
                    float activeRatio = Math.Clamp((clockMhz - 2000f) / 2500f, 0f, 1f);
                    weight += (ActiveCoreMaxBonusWeight * 0.8f) * activeRatio;
                }

                weightedSum += temp * weight;
                weightTotal += weight;
            }

            if (weightTotal > 0d)
            {
                source = "전체 코어 평균 (활성 코어 가중치)";
                return (float)(weightedSum / weightTotal);
            }
        }

        // 개별 코어 센서가 노출되지 않는 환경 폴백: Core Average -> 비-패키지 센서 평균 -> CPU Package
        float? coreAvg = validTempSensors
            .FirstOrDefault(v => v.Name.Contains("Core Average", StringComparison.OrdinalIgnoreCase))?.Value;
        if (coreAvg.HasValue)
        {
            source = "Core Average";
            return coreAvg.Value;
        }

        var nonPackageSensors = validTempSensors
            .Where(v => !v.Name.Contains("Package", StringComparison.OrdinalIgnoreCase)
                     && !v.Name.Contains("Max", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (nonPackageSensors.Count > 0)
        {
            source = "센서 전체 평균";
            return nonPackageSensors.Average(v => v.Value!.Value);
        }

        source = "CPU Package (폴백)";
        return validTempSensors.Average(v => v.Value!.Value);
    }

    private static void CollectSensors(IHardware hardware, ICollection<ISensor> destination)
    {
        foreach (ISensor sensor in hardware.Sensors) destination.Add(sensor);
        foreach (IHardware child in hardware.SubHardware) CollectSensors(child, destination);
    }

    private static bool IsGpuHardware(IHardware hw) => hw.HardwareType == HardwareType.GpuNvidia || hw.HardwareType == HardwareType.GpuAmd || hw.HardwareType == HardwareType.GpuIntel;

    private static GpuCandidate SelectCandidate(IReadOnlyCollection<GpuCandidate> candidates)
    {
        return candidates.FirstOrDefault(c => c.HardwareType == HardwareType.GpuNvidia && (c.UsagePercent >= 1.0f || c.CoreClockMHz >= 150f))
            ?? candidates.FirstOrDefault(c => c.HardwareType == HardwareType.GpuNvidia)
            ?? candidates.First();
    }

    private static float? FindSensorValue(IEnumerable<ISensor> sensors, SensorType type, params string[] names)
    {
        foreach (string name in names)
        {
            var s = sensors.FirstOrDefault(x => x.SensorType == type && x.Value.HasValue && x.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (s != null) return s.Value;
        }
        return sensors.FirstOrDefault(x => x.SensorType == type && x.Value.HasValue)?.Value;
    }

    private static float? NormalizeTemperature(float? value) => value is >= -20f and <= 130f ? value : null;
    private static float? NormalizeLoad(float? value) => value is >= 0f and <= 100f ? value : null;
    private static float? NormalizeClockMHz(float? raw) { if (!raw.HasValue) return null; float v = raw.Value > 100000f ? raw.Value / 1000000f : raw.Value; return v <= 5000f ? v : null; }
    // RTX 5060 ~ 5090 (115~175W + 피크 스파이크) 허용 상한 250W
    private static float? NormalizeGpuPowerWatts(float? raw) { if (!raw.HasValue) return null; float v = raw.Value > 100000f ? raw.Value / 1000000f : raw.Value; return v <= 250f ? v : null; }
    private static float? NormalizeFanRpm(float? value) => value is >= 0f and <= 20000f ? value : null;

    public void Dispose() { lock (_syncRoot) { ResetSensorSession(); } }

    private sealed class GpuCandidate
    {
        public HardwareType HardwareType { get; set; }
        public string Name { get; set; } = "GPU";
        public float? TemperatureCelsius { get; set; }
        public float? UsagePercent { get; set; }
        public float? CoreClockMHz { get; set; }
        public float? PowerWatts { get; set; }
        public float? FanSpeedRpm { get; set; }
    }

    private sealed class CombinedUpdateVisitor : IVisitor
    {
        private readonly bool _enableGpu;
        public CombinedUpdateVisitor(bool enableGpu) => _enableGpu = enableGpu;
        public void VisitComputer(IComputer computer) => computer.Traverse(this);
        public void VisitHardware(IHardware hardware)
        {
            try
            {
                if (hardware.HardwareType == HardwareType.Cpu) hardware.Update();
                else if (_enableGpu && (hardware.HardwareType == HardwareType.GpuAmd || hardware.HardwareType == HardwareType.GpuIntel)) hardware.Update();
                else if (_enableGpu && hardware.HardwareType == HardwareType.GpuNvidia)
                {
                    bool isAc = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
                    bool isAwake = hardware.Sensors.Any(s => s.Value > 0.001f);
                    if (isAc || isAwake) hardware.Update();
                }
            }
            catch { }
            foreach (IHardware child in hardware.SubHardware) child.Accept(this);
        }
        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }
}