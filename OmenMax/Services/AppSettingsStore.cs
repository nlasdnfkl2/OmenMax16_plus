#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace OmenMax16.Services;

/// <summary>
/// HP OMEN MAX 16 하드웨어 모델(Intel / AMD + RTX 50 시리즈)별 전력 제한 설정 저장소.
/// 초기 생성 및 정규화 시 HardwarePlatformDetector.Current 프로파일의 하한/상한/기본값을 적용합니다.
/// </summary>
public sealed class AppSettings
{
    // 하위 호환용 기본 상수
    public const int MinCpuWatts = 15;
    public const int MaxCpuWatts = 171;
    public const int DefaultPl1Watts = 85;
    public const int DefaultPl2Watts = 125;
    public const int DefaultTauSeconds = 32;
    public const int DefaultTppCpuWatts = 80;

    // AMD Ryzen (RyzenAdj) 기준 상수
    public const int MinAmdWatts = 5;
    public const int MaxAmdWatts = 100;
    public const int DefaultAmdStapmWatts = 45;
    public const int DefaultAmdFastWatts = 55;
    public const int DefaultAmdSlowWatts = 45;
    public const int MinAmdUvOffset = -40;
    public const int MaxAmdUvOffset = 0;

    // ── 통합 CPU 튜닝 설정 (Intel: PL1/PL2/Tau, AMD: STAPM·Slow/Fast) ──
    public int CpuPl1Watts { get; set; } = DefaultPl1Watts;
    public int CpuPl2Watts { get; set; } = DefaultPl2Watts;
    public int CpuTauSeconds { get; set; } = DefaultTauSeconds;

    public bool Tpp250AutoBalance { get; set; } = true;
    public int TppCpuSimultaneousWatts { get; set; } = DefaultTppCpuWatts;

    // ── AMD 전용 보조 설정 ───────────────────────────────────────────
    public int AmdUvOffset { get; set; } = 0;
    public int AmdStapmWatts { get; set; } = DefaultAmdStapmWatts;
    public int AmdFastWatts { get; set; } = DefaultAmdFastWatts;
    public int AmdSlowWatts { get; set; } = DefaultAmdSlowWatts;

    // ── 공통 GPU / 세션 설정 ─────────────────────────────────────────
    public int GpuClockCapMhz { get; set; } = 0;
    public bool GpuCtgpEnabled { get; set; } = true;
    public bool GpuDynamicBoostEnabled { get; set; } = true;

    public bool SessionActive { get; set; }
    public DateTime LastActiveUtc { get; set; } = DateTime.MinValue;

    public AppSettings()
    {
        ResetTuning();
    }

    public bool HasCustomCpuTuning
    {
        get
        {
            var cpu = HardwarePlatformDetector.Current.CpuProfile;
            if (HardwarePlatformDetector.Current.IsAmd)
            {
                return AmdUvOffset != 0 ||
                       CpuPl1Watts != cpu.DefaultPl1Watts ||
                       CpuPl2Watts != cpu.DefaultPl2Watts;
            }

            return CpuPl1Watts != cpu.DefaultPl1Watts ||
                   CpuPl2Watts != cpu.DefaultPl2Watts ||
                   CpuTauSeconds != cpu.DefaultTauSeconds;
        }
    }

    public void ResetTuning()
    {
        var platform = HardwarePlatformDetector.Current;
        var cpu = platform.CpuProfile;

        CpuPl1Watts = cpu.DefaultPl1Watts;
        CpuPl2Watts = cpu.DefaultPl2Watts;
        CpuTauSeconds = cpu.DefaultTauSeconds;
        Tpp250AutoBalance = true;
        TppCpuSimultaneousWatts = cpu.DefaultTppCpuWatts;

        AmdUvOffset = 0;
        AmdStapmWatts = platform.IsAmd ? cpu.DefaultPl1Watts : DefaultAmdStapmWatts;
        AmdSlowWatts = platform.IsAmd ? cpu.DefaultPl1Watts : DefaultAmdSlowWatts;
        AmdFastWatts = platform.IsAmd ? cpu.DefaultPl2Watts : DefaultAmdFastWatts;

        GpuClockCapMhz = 0;
        GpuCtgpEnabled = true;
        GpuDynamicBoostEnabled = true;
    }

    public void Normalize()
    {
        var platform = HardwarePlatformDetector.Current;
        var cpu = platform.CpuProfile;

        CpuPl1Watts = Math.Clamp(CpuPl1Watts, cpu.MinWatts, cpu.MaxPl1Watts);
        CpuPl2Watts = Math.Clamp(CpuPl2Watts, cpu.MinWatts, cpu.MaxPl2Watts);
        if (CpuPl2Watts < CpuPl1Watts) CpuPl2Watts = CpuPl1Watts;

        CpuTauSeconds = Math.Clamp(CpuTauSeconds, 1, 56);
        TppCpuSimultaneousWatts = Math.Clamp(
            TppCpuSimultaneousWatts,
            cpu.TppCpuMinWatts,
            platform.EffectiveTppCpuMaxWatts);

        AmdUvOffset = Math.Clamp(AmdUvOffset, MinAmdUvOffset, MaxAmdUvOffset);

        if (platform.IsAmd)
        {
            AmdStapmWatts = CpuPl1Watts;
            AmdSlowWatts = CpuPl1Watts;
            AmdFastWatts = CpuPl2Watts;
        }
        else
        {
            AmdStapmWatts = Math.Clamp(AmdStapmWatts, MinAmdWatts, MaxAmdWatts);
            AmdFastWatts = Math.Clamp(AmdFastWatts, MinAmdWatts, MaxAmdWatts);
            AmdSlowWatts = Math.Clamp(AmdSlowWatts, MinAmdWatts, MaxAmdWatts);
        }

        if (GpuClockCapMhz != 0) GpuClockCapMhz = Math.Clamp(GpuClockCapMhz, 1200, 2800);
    }
}

public static class AppSettingsStore
{
    private static readonly object Sync = new();
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OmenMax16");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");
    private static readonly string LogPath = Path.Combine(Dir, "settings.log");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        lock (Sync)
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options);
                    if (s is not null)
                    {
                        s.Normalize();
                        return s;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"설정 읽기 실패: {ex.Message}");
            }
            var fresh = new AppSettings();
            fresh.Normalize();
            return fresh;
        }
    }

    public static void Save(AppSettings s)
    {
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(s, Options));
                File.Move(temp, FilePath, true);
            }
            catch (Exception ex)
            {
                Log($"설정 저장 실패: {ex.Message}");
            }
        }
    }

    public static void Log(string msg)
    {
        Debug.WriteLine($"[Settings] {msg}");
        try
        {
            Directory.CreateDirectory(Dir);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}{Environment.NewLine}");
        }
        catch { }
    }
}