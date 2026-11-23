#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace OmenMax16.Services;

/// <summary>
/// Intel Core Ultra HX (Arrow Lake-HX 275HX/255HX 및 2026 Refresh 290HX/270HX 등) 전용
/// 전력 제한(PL1/PL2/Tau) 및 CPU+GPU 동시 게이밍 시 플랫폼 TPP 자동 전력 배분을 제어합니다.
///
/// 제어 계층:
///  1. WinRing0x64.dll 기반 Intel RAPL MSR 0x610 (MSR_PKG_POWER_LIMIT) 직접 읽기/쓰기
///  2. Windows HVCI/VBS 환경 또는 BIOS MSR Lock(bit 63) 시 안전 폴백 및 상태 보고
/// </summary>
public static class IntelCpuTuningService
{
    private const uint MsrRaplPowerUnit = 0x606;
    private const uint MsrPkgPowerLimit = 0x610;

    private static readonly object Sync = new();

    private static int _targetPl1Watts = HardwarePlatformDetector.Current.CpuProfile.DefaultPl1Watts;
    private static int _targetPl2Watts = HardwarePlatformDetector.Current.CpuProfile.DefaultPl2Watts;
    private static int _targetTauSeconds = HardwarePlatformDetector.Current.CpuProfile.DefaultTauSeconds;

    private static int _effectivePl1Watts = HardwarePlatformDetector.Current.CpuProfile.DefaultPl1Watts;
    private static int _effectivePl2Watts = HardwarePlatformDetector.Current.CpuProfile.DefaultPl2Watts;
    private static bool _isTpp250Throttled;

    private static volatile bool _stopped;
    private static int _running;
    private static int _overCount;
    private static int _gpuHeavySamples;
    private static int _gpuIdleSamples;
    private static DateTime _lastApplyUtc = DateTime.MinValue;

    public static bool IsActive { get; private set; }
    public static bool IsTpp250Active { get { lock (Sync) { return _isTpp250Throttled; } } }
    public static int EffectivePl1Watts { get { lock (Sync) { return _effectivePl1Watts; } } }
    public static int EffectivePl2Watts { get { lock (Sync) { return _effectivePl2Watts; } } }

    private static string _statusMessage = $"{HardwarePlatformDetector.Current.CpuShortName} 전력 제어 대기 중";
    public static string StatusMessage { get { lock (Sync) { return _statusMessage; } } }

    private static bool _olsInitialized;
    private static bool _olsAttempted;

    private const float OvershootMarginWatts = 2.5f;
    private const int OvershootSamples = 5;
    private const int ReapplyCooldownSeconds = 15;

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OmenMax16", "intel-cpu-tuning.log");

    #region WinRing0x64 P/Invoke

    [DllImport("WinRing0x64.dll", EntryPoint = "InitializeOls")]
    private static extern bool InitializeOls();

    [DllImport("WinRing0x64.dll", EntryPoint = "DeinitializeOls")]
    private static extern void DeinitializeOls();

    [DllImport("WinRing0x64.dll", EntryPoint = "GetDllStatus")]
    private static extern uint GetDllStatus();

    [DllImport("WinRing0x64.dll", EntryPoint = "Rdmsr")]
    private static extern bool Rdmsr(uint index, out uint eax, out uint edx);

    [DllImport("WinRing0x64.dll", EntryPoint = "Wrmsr")]
    private static extern bool Wrmsr(uint index, uint eax, uint edx);

    #endregion

    private static bool EnsureOls()
    {
        if (_olsInitialized) return true;
        if (_olsAttempted) return false;
        _olsAttempted = true;

        try
        {
            string dllPath = Path.Combine(AppContext.BaseDirectory, "WinRing0x64.dll");
            if (!File.Exists(dllPath))
            {
                SetStatus("WinRing0x64.dll 없음 — WMI 열 정책 모드로 동작");
                return false;
            }

            if (InitializeOls() && GetDllStatus() == 0)
            {
                _olsInitialized = true;
                Log($"WinRing0x64 초기화 성공 ({HardwarePlatformDetector.Current.CpuShortName} · RAPL MSR 0x610 활성)");
                return true;
            }

            uint status = 999;
            try { status = GetDllStatus(); } catch { }
            SetStatus($"MSR 드라이버 비활성(코드 {status}, HVCI/메모리 무결성 확인) — WMI 열 정책으로 동작");
            Log($"WinRing0x64 초기화 실패: GetDllStatus={status}");
            return false;
        }
        catch (Exception ex)
        {
            SetStatus($"MSR 접근 불가({ex.Message}) — WMI 열 정책으로 동작");
            Log($"WinRing0x64 예외: {ex}");
            return false;
        }
    }

    /// <summary>
    /// 감지된 Intel CPU 모델의 허용 전력 범위 내에서 PL1 / PL2 / Tau 를 적용합니다.
    /// </summary>
    public static void ApplyTuning(
        int pl1Watts,
        int pl2Watts,
        int tauSeconds = AppSettings.DefaultTauSeconds,
        bool isGpuHeavy = false,
        bool tpp250AutoBalance = true,
        int tppCpuWatts = AppSettings.DefaultTppCpuWatts)
    {
        var cpu = HardwarePlatformDetector.Current.CpuProfile;
        lock (Sync)
        {
            _targetPl1Watts = Math.Clamp(pl1Watts, cpu.MinWatts, cpu.MaxPl1Watts);
            _targetPl2Watts = Math.Clamp(Math.Max(pl2Watts, _targetPl1Watts), cpu.MinWatts, cpu.MaxPl2Watts);
            _targetTauSeconds = Math.Clamp(tauSeconds, 1, 56);

            ComputeEffectiveLimitsLocked(isGpuHeavy, tpp250AutoBalance, tppCpuWatts);
        }

        _stopped = false;
        IsActive = true;
        _overCount = 0;
        TriggerApplyAsync();
    }

    /// <summary>
    /// 1초 모니터링 루프에서 호출합니다.
    /// 1) 외장 GPU 고부하 진입/이탈 시 모델별 TPP CPU 상한을 자동 전환합니다.
    /// 2) HP EC 펌웨어가 MSR 0x610 값을 덮어써서 실측 전력이 PL2를 연속 초과하면 재적용합니다.
    /// </summary>
    public static void CheckAndMaintain(
        float measuredCpuWatts,
        bool gpuActive,
        float gpuPowerWatts,
        float gpuUsagePercent,
        bool tpp250AutoBalance,
        int tppCpuWatts)
    {
        if (_stopped) return;

        bool rawGpuHeavy = gpuActive && (gpuPowerWatts >= 55f || gpuUsagePercent >= 40f);
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
        int effPl2;

        lock (Sync)
        {
            int prevPl1 = _effectivePl1Watts;
            int prevPl2 = _effectivePl2Watts;
            bool prevTpp = _isTpp250Throttled;

            ComputeEffectiveLimitsLocked(stableGpuHeavy, tpp250AutoBalance, tppCpuWatts);

            if (_effectivePl1Watts != prevPl1 || _effectivePl2Watts != prevPl2 || _isTpp250Throttled != prevTpp)
            {
                needApply = true;
                Log($"[TPP {maxTpp}W 전환] GPU고부하={stableGpuHeavy} → CPU PL1={_effectivePl1Watts}W, PL2={_effectivePl2Watts}W");
            }

            effPl2 = _effectivePl2Watts;
        }

        if (needApply)
        {
            _overCount = 0;
            TriggerApplyAsync();
            return;
        }

        if (!IsActive) return;

        if (measuredCpuWatts > effPl2 + OvershootMarginWatts)
            _overCount++;
        else
            _overCount = 0;

        if (_overCount >= OvershootSamples &&
            (DateTime.UtcNow - _lastApplyUtc).TotalSeconds >= ReapplyCooldownSeconds)
        {
            _overCount = 0;
            Log($"[EC 복원 감지] CPU 실측 {measuredCpuWatts:F1}W > PL2 {effPl2}W ({OvershootSamples}회 연속) → MSR 재적용");
            TriggerApplyAsync();
        }
    }

    public static (float? Pl1Watts, float? Pl2Watts) ReadCurrentPowerLimits()
    {
        lock (Sync)
        {
            if (!_olsInitialized) return (null, null);
            try
            {
                if (!Rdmsr(MsrRaplPowerUnit, out uint unitEax, out _)) return (null, null);
                int puExp = (int)(unitEax & 0x0F);
                double powerUnit = 1.0 / (1 << puExp);

                if (!Rdmsr(MsrPkgPowerLimit, out uint eax, out uint edx)) return (null, null);
                uint pl1Raw = eax & 0x7FFF;
                uint pl2Raw = edx & 0x7FFF;
                return ((float)(pl1Raw * powerUnit), (float)(pl2Raw * powerUnit));
            }
            catch
            {
                return (null, null);
            }
        }
    }

    public static void Shutdown()
    {
        _stopped = true;
        IsActive = false;
        lock (Sync)
        {
            if (_olsInitialized)
            {
                try { DeinitializeOls(); } catch { }
                _olsInitialized = false;
            }
        }
    }

    private static void ComputeEffectiveLimitsLocked(bool isGpuHeavy, bool tpp250AutoBalance, int tppCpuWatts)
    {
        var platform = HardwarePlatformDetector.Current;
        var cpu = platform.CpuProfile;
        int cappedCpu = Math.Clamp(tppCpuWatts, cpu.TppCpuMinWatts, platform.EffectiveTppCpuMaxWatts);

        if (tpp250AutoBalance && isGpuHeavy && (_targetPl1Watts > cappedCpu || _targetPl2Watts > cappedCpu))
        {
            _effectivePl1Watts = Math.Min(_targetPl1Watts, cappedCpu);
            _effectivePl2Watts = Math.Min(_targetPl2Watts, cappedCpu);
            _isTpp250Throttled = true;
        }
        else
        {
            _effectivePl1Watts = _targetPl1Watts;
            _effectivePl2Watts = _targetPl2Watts;
            _isTpp250Throttled = false;
        }
    }

    private static void TriggerApplyAsync()
    {
        if (_stopped) return;
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        _lastApplyUtc = DateTime.UtcNow;

        Task.Run(() =>
        {
            try
            {
                ApplyMsrValues();
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        });
    }

    private static void ApplyMsrValues()
    {
        int pl1, pl2, tau;
        bool tppActive;
        int maxTpp = HardwarePlatformDetector.Current.MaxTppWatts;

        lock (Sync)
        {
            pl1 = _effectivePl1Watts;
            pl2 = _effectivePl2Watts;
            tau = _targetTauSeconds;
            tppActive = _isTpp250Throttled;

            if (!EnsureOls()) return;

            try
            {
                if (!Rdmsr(MsrRaplPowerUnit, out uint unitEax, out _))
                {
                    SetStatus("MSR 0x606 읽기 실패");
                    return;
                }

                int puExp = (int)(unitEax & 0x0F);
                int tuExp = (int)((unitEax >> 16) & 0x0F);
                double powerUnit = 1.0 / (1 << puExp);
                double timeUnit = 1.0 / (1 << tuExp);

                if (!Rdmsr(MsrPkgPowerLimit, out uint curEax, out uint curEdx))
                {
                    SetStatus("MSR 0x610 읽기 실패");
                    return;
                }

                bool isLocked = (curEdx & 0x80000000u) != 0;
                string powerResult;

                if (isLocked)
                {
                    powerResult = "BIOS가 MSR 전력 제한을 잠금(Locked) — HP WMI 열 정책으로 동작";
                    Log(powerResult);
                }
                else
                {
                    uint pl1Raw = (uint)Math.Clamp(Math.Round(pl1 / powerUnit), 1, 0x7FFF);
                    uint pl2Raw = (uint)Math.Clamp(Math.Round(pl2 / powerUnit), 1, 0x7FFF);
                    uint tauEncoded = EncodeRaplTimeWindow(tau, timeUnit);

                    uint newEax = (curEax & 0xFF000000u) | pl1Raw | (1u << 15) | (1u << 16) | (tauEncoded << 17);
                    uint newEdx = (curEdx & 0x7FFE0000u) | pl2Raw | (1u << 15) | (1u << 16);

                    if (Wrmsr(MsrPkgPowerLimit, newEax, newEdx) &&
                        Rdmsr(MsrPkgPowerLimit, out uint verEax, out uint verEdx))
                    {
                        double appliedPl1 = (verEax & 0x7FFF) * powerUnit;
                        double appliedPl2 = (verEdx & 0x7FFF) * powerUnit;
                        string tppTag = tppActive ? $" [TPP {maxTpp}W 배분]" : "";
                        powerResult = $"PL1 {appliedPl1:0}W / PL2 {appliedPl2:0}W / {tau}s 적용 완료{tppTag}";
                        Log($"MSR 0x610 쓰기 성공: {powerResult}");
                    }
                    else
                    {
                        powerResult = "MSR 0x610 쓰기 거부됨";
                        Log(powerResult);
                    }
                }

                SetStatus(powerResult);
            }
            catch (Exception ex)
            {
                SetStatus($"Intel MSR 적용 예외: {ex.Message}");
                Log($"ApplyMsrValues 예외: {ex}");
            }
        }
    }

    private static uint EncodeRaplTimeWindow(int targetSeconds, double timeUnitSeconds)
    {
        uint bestCode = 0;
        double bestDiff = double.MaxValue;

        for (uint y = 0; y < 32; y++)
        {
            for (uint z = 0; z < 4; z++)
            {
                double seconds = (1L << (int)y) * (1.0 + z / 4.0) * timeUnitSeconds;
                double diff = Math.Abs(seconds - targetSeconds);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    bestCode = (z << 5) | y;
                }
            }
        }

        return bestCode;
    }

    private static void SetStatus(string msg)
    {
        lock (Sync) { _statusMessage = msg; }
    }

    private static void Log(string msg)
    {
        Debug.WriteLine($"[IntelCpuTuning] {msg}");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}{Environment.NewLine}");
        }
        catch { }
    }
}