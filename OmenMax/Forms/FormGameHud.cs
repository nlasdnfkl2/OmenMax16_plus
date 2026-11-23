#nullable enable
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OmenMax16.Forms;

public sealed class FormGameHud : Form
{
    private readonly FormMain _owner;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private bool _isDark;

    private Label _lblCpu = null!;
    private Label _lblCpuDetail = null!;
    private Label _lblGpu = null!;
    private Label _lblGpuDetail = null!;
    private Label _lblFanCpu = null!;
    private Label _lblFanGpu = null!;
    private Label _lblBattery = null!;

    private const int CornerRadius = 22;

    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    public FormGameHud(FormMain owner, bool isDark)
    {
        _owner = owner;
        _isDark = isDark;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(300, 210);

        // 아크릴 틴트와 겹치지 않는 전용 투명 키 컬러
        BackColor = Color.FromArgb(255, 1, 254);
        TransparencyKey = BackColor;
        Font = new Font("Segoe UI", 9f);

        PositionNearTopRight();
        BuildLayout();
        ApplyRoundedRegion();
        ApplyThemeVisuals();

        Load += (_, _) => ApplyAcrylic();

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _refreshTimer.Tick += (_, _) => RefreshContent();
        _refreshTimer.Start();

        RefreshContent();
    }

    public void SetDarkTheme(bool isDark)
    {
        if (_isDark == isDark) return;
        _isDark = isDark;
        ApplyThemeVisuals();
        if (IsHandleCreated) ApplyAcrylic();
        Invalidate();
    }

    private void ApplyThemeVisuals()
    {
        Color detailColor = _isDark
            ? Color.FromArgb(235, 255, 255, 255)
            : Color.FromArgb(245, 24, 28, 38);

        _lblCpu.ForeColor = _isDark
            ? ColorTranslator.FromHtml("#4D8BFF")
            : Color.FromArgb(29, 78, 216);

        _lblGpu.ForeColor = _isDark
            ? ColorTranslator.FromHtml("#A78BFA")
            : Color.FromArgb(109, 40, 217);

        _lblCpuDetail.ForeColor = detailColor;
        _lblGpuDetail.ForeColor = detailColor;
        _lblFanCpu.ForeColor = detailColor;
        _lblFanGpu.ForeColor = detailColor;
        _lblBattery.ForeColor = detailColor;
    }

    private void PositionNearTopRight()
    {
        Rectangle wa = Screen.PrimaryScreen?.WorkingArea ?? Screen.FromControl(this).WorkingArea;
        Location = new Point(wa.Right - Width - 24, wa.Top + 56);
    }

    private void BuildLayout()
    {
        const int padding = 14;

        _lblCpu = MakeLine(padding, 14, "#4D8BFF");
        _lblCpuDetail = MakeDetailLine(padding, 40);
        _lblGpu = MakeLine(padding, 70, "#A78BFA");
        _lblGpuDetail = MakeDetailLine(padding, 96);
        _lblFanCpu = MakeDetailLine(padding, 128);
        _lblFanGpu = MakeDetailLine(padding, 150);
        _lblBattery = MakeDetailLine(padding, 176);
    }

    private Label MakeLine(int x, int y, string colorHex)
    {
        var lbl = new HudLabel
        {
            AutoSize = true,
            Location = new Point(x, y),
            ForeColor = ColorTranslator.FromHtml(colorHex),
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            BackColor = Color.Transparent
        };
        Controls.Add(lbl);
        return lbl;
    }

    private Label MakeDetailLine(int x, int y)
    {
        var lbl = new HudLabel
        {
            AutoSize = true,
            Location = new Point(x, y),
            ForeColor = Color.FromArgb(235, 255, 255, 255),
            Font = new Font("Consolas", 8.5f),
            BackColor = Color.Transparent
        };
        Controls.Add(lbl);
        return lbl;
    }

    private sealed class HudLabel : Label
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            base.OnPaint(e);
        }
    }

    private void RefreshContent()
    {
        try
        {
            HudSnapshot s = _owner.GetHudSnapshot();

            _lblCpu.Text = $"CPU   {s.CpuTemp:0}°C";
            _lblCpuDetail.Text = $"{s.CpuUsage:0}%  ·  {s.CpuClockGhz:0.00} GHz  ·  {s.CpuPower:0.0} W";

            if (s.GpuPresent)
            {
                _lblGpu.Text = s.GpuActive ? $"GPU   {s.GpuTemp:0}°C" : "GPU   OFF";
                _lblGpuDetail.Text = s.GpuActive
                    ? $"{s.GpuUsage:0}%  ·  {s.GpuClockGhz:0.00} GHz  ·  {s.GpuPower:0.0} W"
                    : "대기 중 · 활성화 시 표시";
            }
            else
            {
                _lblGpu.Text = "GPU   미검출";
                _lblGpuDetail.Text = "-";
            }

            _lblFanCpu.Text = $"CPU FAN  {(s.FanCpuRpm ?? 0),5} RPM ({s.FanCpuPercent ?? 0,3}%)";
            _lblFanGpu.Text = $"GPU FAN  {(s.FanGpuRpm ?? 0),5} RPM ({s.FanGpuPercent ?? 0,3}%)";

            string batteryLine = $"BATT  {s.BatteryPercent,3}%   {(s.BatteryCharging ? "+" : "")}{s.BatteryWatts:0.0} W";
            if (s.CurrentFps.HasValue)
            {
                batteryLine += $"  ·  {s.CurrentFps.Value:0} FPS";
            }
            _lblBattery.Text = batteryLine;
        }
        catch { }
    }

    private void ApplyRoundedRegion()
    {
        var path = new GraphicsPath();
        int r = CornerRadius;
        var rect = new Rectangle(0, 0, Width, Height);
        path.AddArc(rect.X, rect.Y, r, r, 180, 90);
        path.AddArc(rect.Right - r, rect.Y, r, r, 270, 90);
        path.AddArc(rect.Right - r, rect.Bottom - r, r, r, 0, 90);
        path.AddArc(rect.X, rect.Bottom - r, r, r, 90, 90);
        path.CloseFigure();
        Region = new Region(path);
    }

    #region Acrylic Blur

    private enum AccentState
    {
        ACCENT_DISABLED = 0,
        ACCENT_ENABLE_GRADIENT = 1,
        ACCENT_ENABLE_TRANSPARENTGRADIENT = 2,
        ACCENT_ENABLE_BLURBEHIND = 3,
        ACCENT_ENABLE_ACRYLICBLURBEHIND = 4
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    private const int WCA_ACCENT_POLICY = 19;

    private void ApplyAcrylic()
    {
        try
        {
            // 라이트 테마: 흰색 반투명 / 다크 테마: 검정 반투명 (ABGR)
            byte a = _isDark ? (byte)75 : (byte)165;
            byte r = _isDark ? (byte)10 : (byte)248;
            byte g = _isDark ? (byte)10 : (byte)250;
            byte b = _isDark ? (byte)18 : (byte)254;
            int gradientColor = (a << 24) | (b << 16) | (g << 8) | r;

            var accent = new AccentPolicy
            {
                AccentState = AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND,
                GradientColor = gradientColor
            };
            int size = Marshal.SizeOf(accent);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, ptr, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WCA_ACCENT_POLICY,
                    Data = ptr,
                    SizeOfData = size
                };
                SetWindowCompositionAttribute(Handle, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch { }
    }

    #endregion

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ApplyRoundedRegion();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _refreshTimer.Stop();
        _refreshTimer.Dispose();
        base.OnFormClosed(e);
    }
}