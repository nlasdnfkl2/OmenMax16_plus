#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace OmenMax16.Services;

/// <summary>
/// HP OMEN MAX 16 (Intel Core Ultra 9 275HX 15~160W + NVIDIA RTX 5070 Ti 140~175W, TPP 250W = 75W+175W)
/// 게이밍 최적화 전력 제한 설정 저장소.
/// </summary>
public sealed class AppSettings
{
    public const int MinCpuWatts = 15;
    public const int MaxCpuWatts = 160;
    public const int DefaultPl1Watts = 85;
    public const int DefaultPl2Watts = 125;
    public const int DefaultTauSeconds = 32;
    public const int DefaultTppCpuWatts = 75;   // CPU+GPU 동시 고부하 시 TPP 250W (CPU 75W + GPU 175W) 게이밍 권장 기준

    public int CpuPl1Watts { get; set; } = DefaultPl1Watts;     // 지속 전력 제한 PL1 (15 ~ 160 W)
    public int CpuPl2Watts { get; set; } = DefaultPl2Watts;     // 단기 부스트 전력 제한 PL2 (15 ~ 160 W)
    public int CpuTauSeconds { get; set; } = DefaultTauSeconds; // 부스트 유지 시간 Tau (1 ~ 56 초)

    public bool Tpp250AutoBalance { get; set; } = true;         // GPU 고부하 시 CPU 자동 제한 (TPP 250W 균형 배분)
    public int TppCpuSimultaneousWatts { get; set; } = DefaultTppCpuWatts;

    public int GpuClockCapMhz { get; set; } = 0;                // 0 = 제한 없음, 1200 ~ 2800 MHz
    public bool GpuCtgpEnabled { get; set; } = true;            // cTGP ON (140W -> 최대 175W TGP 허용)
    public bool GpuDynamicBoostEnabled { get; set; } = true;    // PPAB ON (+35W Dynamic Boost)

    public bool SessionActive { get; set; }
    public DateTime LastActiveUtc { get; set; } = DateTime.MinValue;

    public bool HasCustomCpu =>
        CpuPl1Watts != DefaultPl1Watts ||
        CpuPl2Watts != DefaultPl2Watts ||
        CpuTauSeconds != DefaultTauSeconds;

    public void ResetTuning()
    {
        CpuPl1Watts = DefaultPl1Watts;
        CpuPl2Watts = DefaultPl2Watts;
        CpuTauSeconds = DefaultTauSeconds;
        Tpp250AutoBalance = true;
        TppCpuSimultaneousWatts = DefaultTppCpuWatts;
        GpuClockCapMhz = 0;
        GpuCtgpEnabled = true;
        GpuDynamicBoostEnabled = true;
    }

    public void Normalize()
    {
        CpuPl1Watts = Math.Clamp(CpuPl1Watts, MinCpuWatts, MaxCpuWatts);
        CpuPl2Watts = Math.Clamp(CpuPl2Watts, MinCpuWatts, MaxCpuWatts);
        if (CpuPl2Watts < CpuPl1Watts) CpuPl2Watts = CpuPl1Watts;
        CpuTauSeconds = Math.Clamp(CpuTauSeconds, 1, 56);
        TppCpuSimultaneousWatts = Math.Clamp(TppCpuSimultaneousWatts, 50, 110);
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
            return new AppSettings();
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
