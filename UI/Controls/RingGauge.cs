using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SiliconScope.UI.Controls;

/// <summary>
/// SP 环形仪表：外圈进度（0-150）+ 中心大数字 + 评级。
/// ★ 纯 WPF 绘制，零第三方图表依赖。
/// </summary>
public sealed class RingGauge : Control
{
    public static readonly DependencyProperty ScoreProperty =
        DependencyProperty.Register(nameof(Score), typeof(double), typeof(RingGauge),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GradeProperty =
        DependencyProperty.Register(nameof(Grade), typeof(string), typeof(RingGauge),
            new FrameworkPropertyMetadata("普通", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GradeColorProperty =
        DependencyProperty.Register(nameof(GradeColor), typeof(string), typeof(RingGauge),
            new FrameworkPropertyMetadata("#58A6FF", FrameworkPropertyMetadataOptions.AffectsRender));

    public double Score { get => (double)GetValue(ScoreProperty); set => SetValue(ScoreProperty, value); }
    public string Grade { get => (string)GetValue(GradeProperty); set => SetValue(GradeProperty, value); }
    public string GradeColor { get => (string)GetValue(GradeColorProperty); set => SetValue(GradeColorProperty, value); }

    private const double Max = 150d;

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        double size = Math.Min(w, h);
        double r = size / 2 - 12;
        var center = new Point(w / 2, h / 2);

        // 底环
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromRgb(0x2A, 0x34, 0x41)), 10),
            center, r, r);

        // 进度环（从顶部顺时针）
        double ratio = Math.Max(0, Math.Min(1, Score / Max));
        if (ratio > 0)
        {
            var brush = new SolidColorBrush(SafeColor(GradeColor, Color.FromRgb(0x58, 0xA6, 0xFF)));
            double start = -90;                      // 12 点方向
            double sweep = 360 * ratio;
            var geo = ArcGeometry(center, r, start, sweep);
            dc.DrawGeometry(null, new Pen(brush, 10) { StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round }, geo);
        }

        // 中心数值
        var ft = new FormattedText(Score.ToString("F1", CultureInfo.InvariantCulture),
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size * 0.28,
            new SolidColorBrush(Color.FromRgb(0xE6, 0xED, 0xF3)), 1.0);
        dc.DrawText(ft, new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2 - size * 0.05));

        // 评级
        var fg = new FormattedText(Grade,
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size * 0.11,
            new SolidColorBrush(SafeColor(GradeColor, Color.FromRgb(0x58, 0xA6, 0xFF))), 1.0);
        dc.DrawText(fg, new Point(center.X - fg.Width / 2, center.Y + size * 0.13));

        // 底部 SP SCORE
        var fu = new FormattedText("SP / 150",
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size * 0.07,
            new SolidColorBrush(Color.FromRgb(0x6E, 0x76, 0x81)), 1.0);
        dc.DrawText(fu, new Point(center.X - fu.Width / 2, center.Y + size * 0.27));
    }

    /// <summary>安全解析十六进制颜色；绑定值为 null/空/非法时回退，绝不让 OnRender 因坏色值抛异常。</summary>
    private static Color SafeColor(string? hex, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex) &&
                ColorConverter.ConvertFromString(hex) is Color c) return c;
        }
        catch (FormatException) { /* 非法色值，走回退 */ }
        return fallback;
    }

    private static PathGeometry ArcGeometry(Point c, double r, double startDeg, double sweepDeg)
    {
        double start = startDeg * Math.PI / 180;
        double end = (startDeg + sweepDeg) * Math.PI / 180;
        var p0 = new Point(c.X + r * Math.Cos(start), c.Y + r * Math.Sin(start));
        var p1 = new Point(c.X + r * Math.Cos(end), c.Y + r * Math.Sin(end));
        bool large = sweepDeg > 180;
        var seg = new ArcSegment(p1, new Size(r, r), 0, large, SweepDirection.Clockwise, true);
        var pf = new PathFigure { StartPoint = p0, IsClosed = false };
        pf.Segments.Add(seg);
        return new PathGeometry(new[] { pf });
    }
}
