using System;
using System.Drawing;
using System.Windows.Forms;

namespace OmenMax16.Controls
{
    public class SmallMetricControl : Control
    {
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public float? MetricValue { get; set; }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public string DetailText { get; set; } = string.Empty;

        public SmallMetricControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            Size = new Size(220, 28);
            Font = new Font("Segoe UI", 10F);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            int diameter = 20;
            var rect = new Rectangle(2, (Height - diameter) / 2, diameter, diameter);

            // background ring
            using (var penBg = new Pen(Color.FromArgb(40, 40, 40), 4))
            {
                g.DrawEllipse(penBg, rect);
            }

            // foreground arc
            if (MetricValue.HasValue)
            {
                float pct = Math.Clamp(MetricValue.Value / 100f, 0f, 1f);
                float sweep = 360f * pct;
                using (var penArc = new Pen(Color.FromArgb(180, 80, 200), 4))
                {
                    g.DrawArc(penArc, rect, -90, sweep);
                }
            }

            // text
            using (var brush = new SolidBrush(ForeColor))
            {
                var text = string.IsNullOrEmpty(DetailText) ? (MetricValue.HasValue ? (int)Math.Round(MetricValue.Value) + "%" : "-") : DetailText;
                g.DrawString(text, Font, brush, rect.Right + 6, (Height - Font.Height) / 2);
            }
        }
    }
}
