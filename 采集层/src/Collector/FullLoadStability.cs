// FullLoadStability.cs — 满载“持续稳定”判定
//
// 需求：检测到满载后不能立刻采样（用户可能刚启动烤机、频率/电压尚未稳定，
//       也可能只是 CPU 偶尔瞬间冲高到门槛）。要求【持续满载约 1 分钟】后才自动触发采样。
//
// 做法：维护最近一个时间窗口（默认 60s）内的“是否满载”布尔采样序列，
//       仅当窗口已基本被覆盖、且窗口内满载占比 ≥ 阈值（默认 90%，容忍极个别掉帧）才判定稳定。
//       纯时间序列逻辑，不依赖 WPF/Windows，可直接单测。

using System;
using System.Collections.Generic;
using System.Linq;

namespace Collector;

/// <summary>基于滑动窗口的满载稳定性判定（去抖，排除偶发冲高）。</summary>
public sealed class FullLoadStability
{
    private readonly Queue<(DateTime Time, bool Loaded)> _samples = new();
    private readonly TimeSpan _window;
    private readonly double _requiredRatio;

    public FullLoadStability(TimeSpan window, double requiredRatio = 0.90)
    {
        if (window.TotalSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(window));
        if (requiredRatio is <= 0 or > 1) throw new ArgumentOutOfRangeException(nameof(requiredRatio));
        _window = window;
        _requiredRatio = requiredRatio;
    }

    /// <summary>窗口目标秒数（UI 倒计时满格值）。</summary>
    public double TargetSeconds => _window.TotalSeconds;

    /// <summary>追加一个采样点并清理窗口外旧数据。</summary>
    public void Add(DateTime now, bool loaded)
    {
        _samples.Enqueue((now, loaded));
        DateTime cutoff = now - _window - TimeSpan.FromSeconds(2);
        while (_samples.Count > 0 && _samples.Peek().Time < cutoff)
            _samples.Dequeue();
    }

    public void Reset() => _samples.Clear();

    private List<(DateTime Time, bool Loaded)> InWindow(DateTime now)
        => _samples.Where(s => s.Time >= now - _window).ToList();

    /// <summary>当前窗口内满载占比（0~1）。</summary>
    public double CurrentRatio(DateTime now)
    {
        var win = InWindow(now);
        if (win.Count == 0) return 0;
        return (double)win.Count(s => s.Loaded) / win.Count;
    }

    /// <summary>
    /// 稳定进度秒数（0~TargetSeconds），用于 UI“满载稳定中 x/60s”倒计时。
    /// 持续满载时随时间线性增长；掉载会因占比下降而回退；偶发冲高占比低、进度很小。
    /// </summary>
    public double StableSeconds(DateTime now)
    {
        var win = InWindow(now);
        if (win.Count == 0) return 0;

        double observed = (now - win[0].Time).TotalSeconds;
        observed = Math.Min(observed + (_window.TotalSeconds / Math.Max(1, win.Count * 2.0)), _window.TotalSeconds);
        double ratio = (double)win.Count(s => s.Loaded) / win.Count;
        double progress = Math.Min(observed, ratio * _window.TotalSeconds);
        return Math.Clamp(progress, 0, _window.TotalSeconds);
    }

    /// <summary>窗口已基本覆盖（≥目标 98%）且满载占比达标，才算“持续满载稳定”。</summary>
    public bool IsStable(DateTime now)
    {
        var win = InWindow(now);
        if (win.Count < 2) return false;

        double span = (now - win[0].Time).TotalSeconds;
        if (span < _window.TotalSeconds * 0.98) return false;

        double ratio = (double)win.Count(s => s.Loaded) / win.Count;
        return ratio >= _requiredRatio;
    }
}
