#nullable enable
using System;

namespace OmenMax16.Services;

/// <summary>
/// 팬 RPM 추정기 (OmenMon 원본 소스 검증 반영).
///
/// OmenMon Hardware/EcData.cs 의 EC 레지스터 주석:
///   SRP1 = 0x34, // L Fan Set Speed [krpm]
///   SRP2 = 0x35, // R Fan Set Speed [krpm]
///   RPM1/RPM2 = 0xB0/0xB1, // L Fan Get Speed [rpm] (word, 2바이트)
///   RPM3/RPM4 = 0xB2/0xB3, // R Fan Get Speed [rpm] (word, 2바이트)
///
/// 이는 WMI 의 raw 레벨 값이 "krpm(1000rpm 단위) 근사치"에 대응한다는 걸 뒷받침합니다.
/// 보드 8D87 실측(OmenCore ModelCapabilityDatabase): raw 최댓값 60 이 6000rpm 에 대응하고
/// (60% 명령 → 3600rpm 측정, 100% 명령[raw 60] → 6000rpm 측정) 이는 정확히
/// "raw 값 × 100 = rpm" 관계와 일치합니다 (raw 60 × 100 = 6000, raw 36 × 100 = 3600).
///
/// 따라서 EC 직접 접근 없이도, WMI 로 읽은 raw 레벨(0~60)에 100을 곱하는 것만으로
/// 상당히 정확한 RPM 추정이 가능합니다 - 별도의 위험한 EC 포트 I/O가 필요 없습니다.
/// (이 보드는 memory-mapped EC 라 legacy EC 오프셋 직접 접근은 패닉 위험이 있어 피합니다.)
/// </summary>
public static class FanRpmEstimator
{
    // 보드 8D87 실측: raw 레벨 1 당 약 100rpm (OmenCore 실측: raw 60=6000rpm, raw 36=3600rpm)
    private const int RpmPerRawLevel = 100;
    private const int RawLevelMax = 60;
    private const int EstimatedMaxRpm = RawLevelMax * RpmPerRawLevel; // 6000 rpm

    // 팬이 완전히 멈춘 것으로 볼 최소 raw 값
    private const int MinSpinRawLevel = 1;

    /// <summary>
    /// raw 레벨(0~60, WMI GetFanLevel 로부터)로부터 RPM을 추정합니다.
    /// EC 직접 읽기가 불가능하거나 위험한 이 보드에서 사용하는 1차 추정 방식입니다.
    /// </summary>
    public static int? CalculateRpmFromRawLevel(int? rawLevel)
    {
        if (!rawLevel.HasValue) return null;
        int level = Math.Abs(rawLevel.Value);
        if (level < MinSpinRawLevel) return 0;
        return Math.Clamp(level * RpmPerRawLevel, 0, EstimatedMaxRpm);
    }

    /// <summary>
    /// 퍼센트(0~100, UI 표시용)로부터 RPM을 추정합니다.
    /// raw 레벨을 직접 알 수 없을 때(예: MaxFan 켜짐 상태) 사용하는 2차 추정 방식입니다.
    /// </summary>
    public static int? CalculateRpm(int? percent)
    {
        if (!percent.HasValue) return null;
        if (percent.Value <= 0) return 0;
        double rawEquivalent = percent.Value / 100d * RawLevelMax;
        return Math.Clamp((int)Math.Round(rawEquivalent * RpmPerRawLevel), 0, EstimatedMaxRpm);
    }
}