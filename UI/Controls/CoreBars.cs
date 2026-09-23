using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SiliconScope.UI.Controls;

/// <summary>
/// 每核 VID 横向柱状图。
/// ★ 按【本次采样自身的 min/max】归一化 —— 固定上界会把几十 mV 的核间差异压平成等长。
/// </summary>
public sealed class CoreBars : Control
{
    public static readonly DependencyProperty VidsProperty =
        DependencyProperty.Register(nameof(Vids), typeof(double[]), typeof(CoreBars),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double[] Vids { get => (double[])GetValue(VidsProperty); set => SetValue(VidsProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var v = Vids;
        double W = ActualWidth, H = ActualHeight;
        if (v == null || v.Length == 0 || W <= 10 || H <= 10) return;

        double lo = v.Min(), hi = v.Max();
        int n = v.Length;
        double rowH = H / n;
        double nameW = 62, valW = 62, badgeW = 46;
        double barX = nameW + 8, barW = Math.Max(20, W - nameW - valW - badgeW - 16);

        var lbl = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
        var track = new SolidColorBrush(Color.FromRgb(0x1C, 0x24, 0x2F));

        for (int i = 0; i < n; i++)
        {
            double y = i * rowH;
            double midY = y + rowH / 2;

            // 核名
            var nt = Txt($"Core #{i}", 11.5, lbl);
            dc.DrawText(nt, new Point(0, midY - nt.Height / 2));

            // 轨道
            dc.DrawRoundedRectangle(track, null,
                new Rect(barX, midY - 8, barW, 16), 3, 3);

            // ★ 归一化：本次 min → 满格；max → 保底 6 格
            double norm = hi - lo < 1e-9 ? 0 : (v[i] - lo) / (hi - lo);
            double len = (int)((1 - norm) * 14) + 6;      // 6–20 格
            double fillW = barW * (len / 20.0);

            Color c = i == Array.IndexOf(v, lo) ? Color.FromRgb(0xFF, 0xB0, 0x20)   // 最优：金
                    : i == Array.IndexOf(v, hi) ? Color.FromRgb(0x6E, 0x76, 0x81)   // 最弱：灰
                    : Color.FromRgb(0x58, 0xA6, 0xFF);
            dc.DrawRoundedRectangle(new SolidColorBrush(c), null,
                new Rect(barX, midY - 8, Math.Max(4, fillW), 16), 3, 3);

            // 电压值
            var vt = Txt($"{v[i]:F3} V", 11.5, new SolidColorBrush(Color.FromRgb(0xE6, 0xED, 0xF3)));
            dc.DrawText(vt, new Point(barX + barW + 10, midY - vt.Height / 2));

            // 标记
            string badge = i == Array.IndexOf(v, lo) ? "最优"
                         : i == Array.IndexOf(v, hi) ? "最弱" : "";
            if (badge.Length > 0)
            {
                var bt = Txt(badge, 10, new SolidColorBrush(
                    badge == "最优" ? Color.FromRgb(0xFF, 0xB0, 0x20) : Color.FromRgb(0x8B, 0x94, 0x9E)));
                dc.DrawText(bt, new Point(W - badgeW + 6, midY - bt.Height / 2));
            }
        }
    }

    private static FormattedText Txt(string s, double size, Brush brush)
        => new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush, 1.0);
}
