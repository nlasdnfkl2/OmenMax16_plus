#nullable enable

using System.Drawing;
using System;
using System.Windows.Forms;

namespace OmenMax16.UI;

public static class AppTheme
{
    /*
     * OMEN MAX 16 White Theme
     *
     * Background : Pure White
     * Border     : Strong Purple
     * Accent     : Deep Orange
     * Text       : Near Black
     */

    public static readonly Color WindowBackground =
        Color.FromArgb(
            255,
            255,
            255);

    /*
     * 모든 카드와 구역은 순백색으로 유지한다.
     *
     * 구분은 배경색이 아니라 보라색 테두리와
     * 진한 주황색 세로 포인트로 만든다.
     */
    public static readonly Color CardBackground =
        Color.FromArgb(
            255,
            255,
            255);

    public static readonly Color CardBackgroundHover =
        Color.FromArgb(
            255,
            247,
            242);

    /*
     * OMEN 스타일에 맞는 진한 보라색.
     */
    public static readonly Color BorderPurple =
        Color.FromArgb(
            103,
            58,
            183);

    public static readonly Color BorderPurpleLight =
        Color.FromArgb(
            191,
            170,
            224);

    /*
     * 진한 주황색 포인트.
     *
     * 기존 245,124,0보다 더 짙고 대비가 높다.
     */
    public static readonly Color AccentOrange =
        Color.FromArgb(
            230,
            81,
            0);

    public static readonly Color AccentOrangeLight =
        Color.FromArgb(
            255,
            235,
            224);

    public static readonly Color TextPrimary =
        Color.FromArgb(
            30,
            30,
            34);

    public static readonly Color TextSecondary =
        Color.FromArgb(
            100,
            100,
            112);

    public static readonly Color SuccessGreen =
        Color.FromArgb(
            32,
            136,
            77);

    public static readonly Color WarningYellow =
        Color.FromArgb(
            194,
            123,
            0);

    public static readonly Color DangerRed =
        Color.FromArgb(
            196,
            54,
            54);

    public static readonly Color DisabledGray =
        Color.FromArgb(
            145,
            145,
            155);

    /*
     * Segoe UI는 한국어와 영문, 숫자, 기호를
     * Windows 환경에서 안정적으로 표시한다.
     */
    public const string FontFamilyName =
        "Segoe UI";

    private static Font CreateFont(
        float size,
        FontStyle style)
    {
        return new Font(
            FontFamilyName,
            size,
            style,
            GraphicsUnit.Point);
    }

    /*
     * 메인 창 제목.
     */
    public static Font CreateTitleFont()
    {
        return CreateFont(
            18.0f,
            FontStyle.Bold);
    }

    /*
     * CPU 모델명, GPU 이름 등 보조 제목.
     */
    public static Font CreateSubtitleFont()
    {
        return CreateFont(
            9.5f,
            FontStyle.Regular);
    }

    /*
     * 구역 제목.
     */
    public static Font CreateSectionFont()
    {
        return CreateFont(
            10.0f,
            FontStyle.Bold);
    }

    /*
     * 큰 수치 카드가 필요한 경우만 사용한다.
     *
     * 현재의 작은 모니터링 UI에서는
     * CreateDetailFont()를 주로 사용한다.
     */
    public static Font CreateMetricValueFont()
    {
        return CreateFont(
            18.0f,
            FontStyle.Bold);
    }

    public static Font CreateMetricTitleFont()
    {
        return CreateFont(
            9.0f,
            FontStyle.Bold);
    }

    public static Font CreateMetricDescriptionFont()
    {
        return CreateFont(
            8.0f,
            FontStyle.Regular);
    }

    /*
     * 상단 MONITORING 상태 표시.
     */
    public static Font CreateStatusFont()
    {
        return CreateFont(
            8.5f,
            FontStyle.Bold);
    }

    /*
     * CPU / GPU / MEMORY / BATTERY / FAN 행.
     */
    public static Font CreateDetailFont()
    {
        return CreateFont(
            9.5f,
            FontStyle.Regular);
    }

    /*
     * 3 × 3 제어 버튼용 폰트.
     */
    public static Font CreateButtonFont()
    {
        return CreateFont(
            8.5f,
            FontStyle.Bold);
    }

    /*
     * 기존 코드와의 호환성을 위해 남겨둔 메서드.
     *
     * AutoScaleMode.Dpi가 DPI 처리를 맡으므로
     * 여기서는 별도 수동 배율 계산을 하지 않는다.
     */
    public static Font CreateScaledFontFor(
        Control control,
        float baseSize,
        FontStyle style)
    {
        return CreateFont(
            baseSize,
            style);
    }
}