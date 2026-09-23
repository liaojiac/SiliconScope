// BaselineAdapter.cs — 采集层与评分引擎两套同名模型的桥接层
//
// 背景：采集层（Collector 命名空间）与评分引擎（ScoringEngine 命名空间）各自定义了
//   Baseline / IBaselineStore，字段同构但类型不同。早期 UI 直接把采集层量表
//   喂给评分引擎，导致 CS1503 编译错误。此处集中做显式转换，UI 只依赖这一个适配器，
//   不再直接混用两套类型（与早期 BaselineAdapter 同一思路）。

using Collector;

namespace SiliconScope.UI.Services;

internal static class BaselineAdapter
{
    /// <summary>采集层 Baseline → 评分引擎 Baseline（逐字段拷贝）。</summary>
    public static ScoringEngine.Baseline ToEngine(Collector.Baseline b) => new(
        b.Model,
        b.ZenGeneration,
        b.VRef,
        b.ReferenceFreqMHz,
        b.Priority,
        b.MaxBoostMHz,
        b.AnchorFreqMHz,
        b.SlopeMvPerMhz,
        b.CalibrationLevel);
}

/// <summary>
/// 把采集层量表包装成评分引擎所需的 <see cref="ScoringEngine.IBaselineStore"/>。
/// 引擎的 Get 只接收 CPU 全名（如 "AMD Ryzen 7 7800X3D"），而量表键是短型号（"7800X3D"），
/// 故内部做"精确匹配 → 关键字模糊匹配"，保证全名也能命中。
/// </summary>
internal sealed class EngineBaselineStore : ScoringEngine.IBaselineStore
{
    private readonly Collector.IBaselineStore _inner;

    public EngineBaselineStore(Collector.IBaselineStore inner) => _inner = inner;

    public ScoringEngine.Baseline? Get(string model)
    {
        var b = _inner.Get(model) ?? _inner.GetByKeyword(model);
        return b is null ? null : BaselineAdapter.ToEngine(b);
    }
}
