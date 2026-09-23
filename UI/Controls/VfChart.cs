using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SiliconScope.UI.Controls;

/// <summary>
/// V/F 工作点图：实测散点 + 拟合直线 + 锚点竖线 + 折算后电压标记。
/// ★ 把抽象的 V/F 折算变成看得见的直线 —— 这是让用户信任分数的关键。
/// </summary>
public sealed class VfChart : Control
{
    public static readonly DependencyProperty PointsProperty =
        DependencyProperty.Register(nameof(Points), typeof(IEnumerable<(double freq, double volt)>),
            typeof(VfChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AnchorFreqProperty =
        DependencyProperty.Register(nameof(AnchorFreq), typeof(double), typeof(VfChart),
            new FrameworkPropertyMetadata(4800d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SlopeProperty =
        DependencyProperty.Register(nameof(Slope), typeof(double), typeof(VfChart),
            new FrameworkPropertyMetadata(0.41, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable<(double freq, double volt)> Points
    { get => (IEnumerable<(double freq, double volt)>)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }

    public double AnchorFreq { get => (double)GetValue(AnchorFreqProperty); set => SetValue(AnchorFreqProperty, value); }
    public double Slope { get => (double)GetValue(SlopeProperty); set => SetValue(SlopeProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var pts = Points?.ToList();
        double W = ActualWidth, H = ActualHeight;
        if (W <= 10 || H <= 10) return;

        const double PL = 48, PR = 14, PT = 16, PB = 30;
        double f0 = 4200, f1 = 5000, v0 = 0.92, v1 = 1.15;

        // 自适应频率范围（点可能超出默认区间）
        if (pts is { Count: > 0 })
        {
            f0 = Math.Min(f0, pts.Min(p => p.freq) - 60);
            f1 = Math.Max(f1, pts.Max(p => p.freq) + 60);
            v0 = Math.Min(v0, pts.Min(p => p.volt) - 0.01);
            v1 = Math.Max(v1, pts.Max(p => p.volt) + 0.01);
        }

        Func<double, double> X = f => PL + (f - f0) / (f1 - f0) * (W - PL - PR);
        Func<double, double> Y = v => H - PB - (v - v0) / (v1 - v0) * (H - PT - PB);

        var axis = new Pen(new SolidColorBrush(Color.FromRgb(0x2A, 0x34, 0x41)), 1);
        var grid = new Pen(new SolidColorBrush(Color.FromRgb(0x20, 0x28, 0x34)), 1);
        var lbl = new SolidColorBrush(Color.FromRgb(0x6E, 0x76, 0x81));

        // 轴
        dc.DrawLine(axis, new Point(PL, H - PB), new Point(W - PR, H - PB));
        dc.DrawLine(axis, new Point(PL, PT), new Point(PL, H - PB));

        // 横向网格 + 电压刻度
        for (double v = Math.Ceiling(v0 * 100) / 100; v <= v1; v += 0.05)
        {
            dc.DrawLine(grid, new Point(PL, Y(v)), new Point(W - PR, Y(v)));
            var t = Txt(v.ToString("F2"), 9.5, lbl);
            dc.DrawText(t, new Point(PL - 6 - t.Width, Y(v) - t.Height / 2));
        }

        // 频率刻度
        for (double f = Math.Ceiling(f0 / 200) * 200; f <= f1; f += 200)
        {
            var t = Txt(f.ToString("F0"), 9.5, lbl);
            dc.DrawText(t, new Point(X(f) - t.Width / 2, H - PB + 4));
        }

        // 拟合直线
        if (Slope > 0 && pts != null && pts.Count > 0)
        {
            double k = Slope / 1000.0;
            var p = pts[0];
            double b = p.volt - k * p.freq;
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x58, 0xA6, 0xFF)), 1.6)
            { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
            dc.DrawLine(pen, new Point(X(f0), Y(k * f0 + b)), new Point(X(f1), Y(k * f1 + b)));

            // 锚点竖线
            var apen = new Pen(new SolidColorBrush(Color.FromRgb(0xBC, 0x8C, 0xFF)), 1.4)
            { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
            dc.DrawLine(apen, new Point(X(AnchorFreq), PT), new Point(X(AnchorFreq), H - PB));

            double va = k * AnchorFreq + b;
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xBC, 0x8C, 0xFF)), null,
                new Point(X(AnchorFreq), Y(va)), 5, 5);
            var vt = Txt($"{va:F4}V", 10, new SolidColorBrush(Color.FromRgb(0xBC, 0x8C, 0xFF)));
            dc.DrawText(vt, new Point(X(AnchorFreq) + 8, Y(va) - 12));
        }

        // 实测散点
        if (pts != null)
        {
            foreach (var p in pts)
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x35)), null,
                    new Point(X(p.freq), Y(p.volt)), 4.5, 4.5);
        }

        // 轴标题
        var xt = Txt("频率 (MHz)", 10, lbl);
        dc.DrawText(xt, new Point((PL + W - PR) / 2 - xt.Width / 2, H - 12));
    }

    private static FormattedText Txt(string s, double size, Brush brush)
        => new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush, 1.0);
}
