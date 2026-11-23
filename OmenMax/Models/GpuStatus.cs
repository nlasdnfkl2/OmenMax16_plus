namespace OmenMax16.Models;

public sealed class GpuStatus
{
    public bool IsMonitoringEnabled { get; set; }
    public bool IsHardwareAvailable { get; set; }
    // True when the detected GPU is a discrete device (dGPU)
    public bool IsDiscrete { get; set; }
    public string Name { get; set; } = "GPU 모니터링 꺼짐";
    public float? TemperatureCelsius { get; set; }
    public float? UsagePercent { get; set; }
    public float? CoreClockMHz { get; set; }
    public float? PowerWatts { get; set; }
    public float? FanSpeedRpm { get; set; }
    public float? MemoryUsedMegaBytes { get; set; }
    public float? MemoryTotalMegaBytes { get; set; }
    public string Source { get; set; } = "GPU 절전 보호";
    public string ErrorMessage { get; set; } = string.Empty;
}
