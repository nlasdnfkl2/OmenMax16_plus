#nullable enable
using System;
using System.Management;
using System.Text.RegularExpressions;

namespace OmenMax16.Services;

public enum CpuPlatform
{
    Unknown,
    Intel,
    Amd
}

public enum CpuModelTier
{
    Intel2026Refresh,
    Intel2025ArrowLakeHx,
    IntelLegacyHx,
    AmdRyzenAiHx,
    AmdRyzenStandard
}

public enum GpuModelTier
{
    Rtx5090_5080,
    Rtx5070Ti,
    Rtx5070_5060,
    Rtx4090_4080,
    Rtx4070_4060,
    GenericDiscrete
}

public sealed record CpuPowerProfile(
    CpuModelTier Tier,
    int MinWatts,
    int MaxPl1Watts,
    int MaxPl2Watts,
    int DefaultPl1Watts,
    int DefaultPl2Watts,
    int DefaultTauSeconds,
    int TppCpuMinWatts,
    int TppCpuMaxWatts,
    int DefaultTppCpuWatts);

public sealed record GpuPowerProfile(
    GpuModelTier Tier,
    int MinWatts,
    int BaseNoCtgpWatts,
    int BaseWatts,
    int DynamicBoostWatts,
    int MaxWatts);

public sealed record HardwarePlatformInfo(
    CpuPlatform Platform,
    string SystemModelName,
    string BaseboardProduct,
    string PlatformFamily,
    string CpuFullName,
    string CpuShortName,
    CpuPowerProfile CpuProfile,
    string DiscreteGpuFullName,
    string DiscreteGpuShortName,
    GpuPowerProfile GpuProfile,
    bool HasDiscreteGpu,
    int MaxTppWatts)
{
    public bool IsIntel => Platform == CpuPlatform.Intel;
    public bool IsAmd => Platform == CpuPlatform.Amd;
    public string PlatformKey => Platform switch
    {
        CpuPlatform.Intel => "intel",
        CpuPlatform.Amd => "amd",
        _ => "intel"
    };

    public int EffectiveTppCpuMaxWatts => Math.Min(CpuProfile.TppCpuMaxWatts, CpuProfile.MaxPl2Watts);

    public int CalculateGpuTargetWatts(bool ctgp, bool dynamicBoost)
    {
        if (ctgp && dynamicBoost) return GpuProfile.MaxWatts;
        if (ctgp) return GpuProfile.BaseWatts;
        if (dynamicBoost) return Math.Min(GpuProfile.MaxWatts, GpuProfile.BaseNoCtgpWatts + GpuProfile.DynamicBoostWatts);
        return GpuProfile.BaseNoCtgpWatts;
    }
}

/// <summary>
/// 실행 머신의 노트북 모델, CPU 아키텍처/세대(Intel vs AMD), 외장 GPU(NVIDIA) 모델명을 자동 감지하고
/// 모델별 전력 하한·상한·기본값 및 최대 TPP 프로파일을 산출합니다.
/// </summary>
public static class HardwarePlatformDetector
{
    private static readonly Lazy<HardwarePlatformInfo> CachedInfo = new(DetectInternal);

    public static HardwarePlatformInfo Current => CachedInfo.Value;

    private static HardwarePlatformInfo DetectInternal()
    {
        string systemModel = string.Empty;
        string baseboardProduct = string.Empty;

        try
        {
            using var csSearcher = new ManagementObjectSearcher("SELECT Model FROM Win32_ComputerSystem");
            foreach (ManagementObject mo in csSearcher.Get())
            {
                systemModel = (mo["Model"] as string ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(systemModel)) break;
            }
        }
        catch { }

        try
        {
            using var bbSearcher = new ManagementObjectSearcher("SELECT Product FROM Win32_BaseBoard");
            foreach (ManagementObject mo in bbSearcher.Get())
            {
                baseboardProduct = (mo["Product"] as string ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(baseboardProduct)) break;
            }
        }
        catch { }

        string platformFamily = ResolvePlatformFamily(systemModel, baseboardProduct);

        CpuPlatform platform = CpuPlatform.Unknown;
        string cpuFull = string.Empty;

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, Manufacturer FROM Win32_Processor");
            foreach (ManagementObject mo in searcher.Get())
            {
                cpuFull = (mo["Name"] as string ?? string.Empty).Trim();
                string mfg = (mo["Manufacturer"] as string ?? string.Empty).Trim();

                if (mfg.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
                    cpuFull.Contains("Intel", StringComparison.OrdinalIgnoreCase))
                {
                    platform = CpuPlatform.Intel;
                }
                else if (mfg.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                         cpuFull.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                         cpuFull.Contains("Ryzen", StringComparison.OrdinalIgnoreCase))
                {
                    platform = CpuPlatform.Amd;
                }
                break;
            }
        }
        catch { }

        if (platform == CpuPlatform.Unknown)
        {
            string envId = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? string.Empty;
            if (envId.Contains("Intel", StringComparison.OrdinalIgnoreCase)) platform = CpuPlatform.Intel;
            else if (envId.Contains("AMD", StringComparison.OrdinalIgnoreCase)) platform = CpuPlatform.Amd;
        }

        if (string.IsNullOrWhiteSpace(cpuFull))
        {
            cpuFull = platform == CpuPlatform.Amd ? "AMD Ryzen Processor" : "Intel Core Processor";
        }

        string cpuShort = FormatCpuShortName(cpuFull, platform);
        CpuPowerProfile cpuProfile = BuildCpuProfile(cpuFull, platform);

        string gpuFull = string.Empty;
        bool hasDiscrete = false;

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterCompatibility FROM Win32_VideoController");
            foreach (ManagementObject mo in searcher.Get())
            {
                string name = (mo["Name"] as string ?? string.Empty).Trim();
                string compat = (mo["AdapterCompatibility"] as string ?? string.Empty).Trim();
                if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                    compat.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                {
                    gpuFull = name;
                    hasDiscrete = true;
                    break;
                }
            }
        }
        catch { }

        string gpuShort = FormatGpuShortName(gpuFull);
        GpuPowerProfile gpuProfile = BuildGpuProfile(gpuFull);
        int maxTppWatts = ResolveMaxTppWatts(cpuProfile, gpuProfile);

        return new HardwarePlatformInfo(
            Platform: platform,
            SystemModelName: systemModel,
            BaseboardProduct: baseboardProduct,
            PlatformFamily: platformFamily,
            CpuFullName: cpuFull,
            CpuShortName: cpuShort,
            CpuProfile: cpuProfile,
            DiscreteGpuFullName: gpuFull,
            DiscreteGpuShortName: gpuShort,
            GpuProfile: gpuProfile,
            HasDiscreteGpu: hasDiscrete,
            MaxTppWatts: maxTppWatts);
    }

    private static string ResolvePlatformFamily(string systemModel, string baseboardProduct)
    {
        string combined = $"{systemModel} {baseboardProduct}";
        if (combined.Contains("OMEN MAX", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("MAX 16", StringComparison.OrdinalIgnoreCase) ||
            baseboardProduct.Equals("8D87", StringComparison.OrdinalIgnoreCase) ||
            baseboardProduct.Equals("8D88", StringComparison.OrdinalIgnoreCase))
        {
            return "OMEN MAX 16";
        }

        if (combined.Contains("OMEN 16", StringComparison.OrdinalIgnoreCase))
            return "OMEN 16";

        if (combined.Contains("OMEN", StringComparison.OrdinalIgnoreCase))
            return "OMEN";

        return "OMEN MAX 16";
    }

    private static CpuPowerProfile BuildCpuProfile(string cpuFull, CpuPlatform platform)
    {
        if (platform == CpuPlatform.Amd)
        {
            bool isHxHighTier = Regex.IsMatch(cpuFull, @"(?:HX\s*(?:370|375|470|475)|Ryzen\s+AI\s+9\s+HX)", RegexOptions.IgnoreCase);
            if (isHxHighTier)
            {
                return new CpuPowerProfile(
                    Tier: CpuModelTier.AmdRyzenAiHx,
                    MinWatts: 15,
                    MaxPl1Watts: 65,
                    MaxPl2Watts: 80,
                    DefaultPl1Watts: 45,
                    DefaultPl2Watts: 55,
                    DefaultTauSeconds: 32,
                    TppCpuMinWatts: 25,
                    TppCpuMaxWatts: 70,
                    DefaultTppCpuWatts: 50);
            }

            return new CpuPowerProfile(
                Tier: CpuModelTier.AmdRyzenStandard,
                MinWatts: 15,
                MaxPl1Watts: 60,
                MaxPl2Watts: 75,
                DefaultPl1Watts: 45,
                DefaultPl2Watts: 54,
                DefaultTauSeconds: 32,
                TppCpuMinWatts: 25,
                TppCpuMaxWatts: 65,
                DefaultTppCpuWatts: 45);
        }

        // Intel 2026 Arrow Lake-HX Refresh (Core Ultra 9 290HX / Ultra 7 270HX / Plus)
        if (Regex.IsMatch(cpuFull, @"(?:290HX|270HX|Plus)", RegexOptions.IgnoreCase))
        {
            bool is290Hx = cpuFull.Contains("290HX", StringComparison.OrdinalIgnoreCase);
            return new CpuPowerProfile(
                Tier: CpuModelTier.Intel2026Refresh,
                MinWatts: 20,
                MaxPl1Watts: 150,
                MaxPl2Watts: is290Hx ? 171 : 160,
                DefaultPl1Watts: 95,
                DefaultPl2Watts: 135,
                DefaultTauSeconds: 32,
                TppCpuMinWatts: 55,
                TppCpuMaxWatts: 130,
                DefaultTppCpuWatts: 100);
        }

        // Intel 2025 Arrow Lake-HX (Core Ultra 9 275HX / 285HX / Ultra 7 255HX / 265HX)
        if (Regex.IsMatch(cpuFull, @"(?:285HX|275HX|265HX|255HX|245HX|Ultra\s+[579]\s+2\d{2}HX)", RegexOptions.IgnoreCase))
        {
            return new CpuPowerProfile(
                Tier: CpuModelTier.Intel2025ArrowLakeHx,
                MinWatts: 20,
                MaxPl1Watts: 140,
                MaxPl2Watts: 160,
                DefaultPl1Watts: 85,
                DefaultPl2Watts: 125,
                DefaultTauSeconds: 32,
                TppCpuMinWatts: 55,
                TppCpuMaxWatts: 115,
                DefaultTppCpuWatts: 80);
        }

        // Intel 13th/14th Gen HX 및 기타 Intel 고성능 프로세서
        return new CpuPowerProfile(
            Tier: CpuModelTier.IntelLegacyHx,
            MinWatts: 20,
            MaxPl1Watts: 140,
            MaxPl2Watts: 157,
            DefaultPl1Watts: 80,
            DefaultPl2Watts: 120,
            DefaultTauSeconds: 28,
            TppCpuMinWatts: 50,
            TppCpuMaxWatts: 110,
            DefaultTppCpuWatts: 75);
    }

    private static GpuPowerProfile BuildGpuProfile(string gpuFull)
    {
        if (string.IsNullOrWhiteSpace(gpuFull))
        {
            return new GpuPowerProfile(
                Tier: GpuModelTier.GenericDiscrete,
                MinWatts: 50,
                BaseNoCtgpWatts: 100,
                BaseWatts: 115,
                DynamicBoostWatts: 25,
                MaxWatts: 140);
        }

        // RTX 5070 Ti Laptop GPU (115W cTGP + 25W PPAB = 140W) — 반드시 5070 보다 먼저 검사
        if (Regex.IsMatch(gpuFull, @"5070\s*Ti", RegexOptions.IgnoreCase))
        {
            return new GpuPowerProfile(
                Tier: GpuModelTier.Rtx5070Ti,
                MinWatts: 50,
                BaseNoCtgpWatts: 100,
                BaseWatts: 115,
                DynamicBoostWatts: 25,
                MaxWatts: 140);
        }

        // RTX 5080 / 5090 Laptop GPU (150W cTGP + 25W PPAB = 175W)
        if (Regex.IsMatch(gpuFull, @"50(?:80|90)", RegexOptions.IgnoreCase))
        {
            return new GpuPowerProfile(
                Tier: GpuModelTier.Rtx5090_5080,
                MinWatts: 55,
                BaseNoCtgpWatts: 130,
                BaseWatts: 150,
                DynamicBoostWatts: 25,
                MaxWatts: 175);
        }

        // RTX 5060 / 5070 Laptop GPU (100W cTGP + 15W PPAB = 115W)
        if (Regex.IsMatch(gpuFull, @"50(?:60|70)", RegexOptions.IgnoreCase))
        {
            return new GpuPowerProfile(
                Tier: GpuModelTier.Rtx5070_5060,
                MinWatts: 45,
                BaseNoCtgpWatts: 85,
                BaseWatts: 100,
                DynamicBoostWatts: 15,
                MaxWatts: 115);
        }

        // RTX 4080 / 4090 Laptop GPU (150W + 25W = 175W)
        if (Regex.IsMatch(gpuFull, @"40(?:80|90)", RegexOptions.IgnoreCase))
        {
            return new GpuPowerProfile(
                Tier: GpuModelTier.Rtx4090_4080,
                MinWatts: 55,
                BaseNoCtgpWatts: 130,
                BaseWatts: 150,
                DynamicBoostWatts: 25,
                MaxWatts: 175);
        }

        // RTX 4050 / 4060 / 4070 Laptop GPU (115W + 25W = 140W)
        if (Regex.IsMatch(gpuFull, @"40(?:50|60|70)", RegexOptions.IgnoreCase))
        {
            return new GpuPowerProfile(
                Tier: GpuModelTier.Rtx4070_4060,
                MinWatts: 45,
                BaseNoCtgpWatts: 100,
                BaseWatts: 115,
                DynamicBoostWatts: 25,
                MaxWatts: 140);
        }

        return new GpuPowerProfile(
            Tier: GpuModelTier.GenericDiscrete,
            MinWatts: 50,
            BaseNoCtgpWatts: 100,
            BaseWatts: 115,
            DynamicBoostWatts: 25,
            MaxWatts: 140);
    }

    private static int ResolveMaxTppWatts(CpuPowerProfile cpu, GpuPowerProfile gpu)
    {
        if (cpu.Tier == CpuModelTier.Intel2026Refresh)
        {
            if (gpu.MaxWatts >= 175) return 300;
            if (gpu.MaxWatts >= 140) return 265;
            return 250;
        }

        return 250;
    }

    private static string FormatCpuShortName(string fullName, CpuPlatform platform)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            return platform == CpuPlatform.Amd ? "AMD Ryzen" : "Intel Core";

        var ultraMatch = Regex.Match(fullName, @"(Ultra\s+\d+\s+[A-Za-z0-9\-]+(?:\s+Plus)?)", RegexOptions.IgnoreCase);
        if (ultraMatch.Success)
            return Regex.Replace(ultraMatch.Groups[1].Value.Trim(), @"\s+", " ");

        var coreIMatch = Regex.Match(fullName, @"(i[3579]\-[A-Za-z0-9]+)", RegexOptions.IgnoreCase);
        if (coreIMatch.Success)
            return "Core " + coreIMatch.Groups[1].Value.Trim();

        var ryzenMatch = Regex.Match(fullName, @"(Ryzen(?:\s+AI)?\s+\d+\s+[A-Za-z0-9\s\-]+?)(?:\s+w/|\s+with|\s+@|$)", RegexOptions.IgnoreCase);
        if (ryzenMatch.Success)
            return Regex.Replace(ryzenMatch.Groups[1].Value.Trim(), @"\s+", " ");

        string cleaned = fullName
            .Replace("(R)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("(TM)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Intel", "", StringComparison.OrdinalIgnoreCase)
            .Replace("AMD", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Processor", "", StringComparison.OrdinalIgnoreCase)
            .Trim();

        cleaned = Regex.Replace(cleaned, @"\s+", " ");
        return cleaned.Length > 26 ? cleaned[..26].Trim() : cleaned;
    }

    private static string FormatGpuShortName(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            return "dGPU";

        var rtxMatch = Regex.Match(fullName, @"((?:RTX|GTX)\s+[0-9]{3,4}(?:\s+(?:Ti|SUPER|Ada))?)", RegexOptions.IgnoreCase);
        if (rtxMatch.Success)
            return Regex.Replace(rtxMatch.Groups[1].Value.Trim(), @"\s+", " ");

        string cleaned = fullName
            .Replace("NVIDIA", "", StringComparison.OrdinalIgnoreCase)
            .Replace("GeForce", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Laptop GPU", "", StringComparison.OrdinalIgnoreCase)
            .Trim();

        cleaned = Regex.Replace(cleaned, @"\s+", " ");
        return string.IsNullOrWhiteSpace(cleaned) ? "NVIDIA GPU" : cleaned;
    }
}