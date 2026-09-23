// ProbeMode.cs — 探测模式（决策 首次自动探测 + 出错提示手动重探）
// 对应：LHM跨主板兼容设计.md §七〔待确认〕第 3 项 → 已决策
//
// 行为定义：
//   AutoOnFirstRun（默认）
//     - 首次运行（缓存无映射 / HasEverProbed=false）→ 自动探测一次 → 结果写缓存
//     - 后续启动 → 直接复用缓存，不再探测（快、零打扰）
//     - 若某次采集发现字段缺失 / 合理性校验失败 → 标记 ShouldPromptUser=true
//       → UI 层据此弹出"探测未完整，点击重新探测"提示
//   AlwaysAuto：调试用，每次启动都重探（慢但总能拿到最新映射）
//   OffUntilError：默认不探测，仅出错/字段缺失时才提示用户手动触发
//
// 关键设计原则：
//   - 探测永远"尽力而为"，宁可缺字段也不抛异常
//   - 是否弹提示由 ShouldPromptUser 决定，UI 层负责展示，核心层不耦合界面
//   - 合理性校验（Sanity）是跨主板无条件防线，与命名无关

using System;
using System.Collections.Generic;
using System.Linq;

namespace Collector;

/// <summary>探测模式行为（可配置，默认 AutoOnFirstRun）</summary>
public enum ProbeMode
{
    /// <summary>【默认】首次自动探测一次 → 缓存复用；失败/字段缺失时提示用户手动重探</summary>
    AutoOnFirstRun = 0,
    /// <summary>始终自动探测（调试用，每次启动都重探，慢但总能拿到最新映射）</summary>
    AlwaysAuto = 1,
    /// <summary>默认关闭探测，仅出错/字段缺失时才提示用户手动触发</summary>
    OffUntilError = 2,
}

/// <summary>探测会话状态（持久化到 sensor_cache.json，控制"首次/后续"行为）</summary>
public sealed record ProbeState(
    bool HasEverProbed,                 // 是否已至少探测过一次
    DateTime? LastProbedAt,             // 最近探测时间
    int FailureCount,                   // 连续失败次数
    IReadOnlyList<string> MissingFields // 上次仍缺失的字段
)
{
    public ProbeState() : this(false, null, 0, Array.Empty<string>()) { }
}

/// <summary>单个字段的探测映射结果</summary>
public sealed record FieldMapping(string LogicalName, string? SensorName, double? SampleValue, bool Resolved);

/// <summary>探测结果：命中的字段映射 + 未命中字段 + 是否建议提示用户</summary>
public sealed record ProbeResult(
    IReadOnlyDictionary<string, string> FieldToName, // 逻辑字段名 → 传感器原始名
    IReadOnlyList<string> MissingFields,
    bool ShouldPromptUser  // 是否建议 UI 弹出"探测失败，点击重新探测"
);

/// <summary>探测会话：按 ProbeMode 策略驱动"何时探测 / 何时复用缓存 / 何时提示用户"</summary>
public static class ProbeSession
{
    /// <summary>需要探测的核心字段（评分必需，缺失即视为不完整）</summary>
    private static readonly string[] RequiredFields = { "VCore", "PackageTemp", "AvgFreq" };

    /// <summary>
    /// 决定本次是否探测。
    /// 规则：
    ///   AlwaysAuto                        → 永远探测
    ///   OffUntilError                     → 仅在"强制"或"有缺失"时才探测
    ///   AutoOnFirstRun（默认）            → 未探测过 或 强制 或 有缺失 → 探测
    /// </summary>
    public static bool ShouldProbe(ProbeMode mode, ProbeState state, bool force, IReadOnlyList<string>? currentMissing)
    {
        return mode switch
        {
            ProbeMode.AlwaysAuto => true,
            ProbeMode.OffUntilError => force || (currentMissing?.Count ?? 0) > 0,
            ProbeMode.AutoOnFirstRun => !state.HasEverProbed || force || (currentMissing?.Count ?? 0) > 0,
            _ => !state.HasEverProbed,
        };
    }

    /// <summary>
    /// 执行探测 + 自动匹配，产出 ProbeResult。
    /// report：SensorProbe.Run(...) 的 dump 结果（真实环境 = Computer.Accept 遍历）
    /// 返回：字段映射 + 缺失字段 + 是否应提示用户
    /// </summary>
    public static ProbeResult Execute(ProbeReport report)
    {
        var fieldToName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();

        // 逐核心字段做自动匹配（精确名 → 包含关键词 → 正则，均需合理性校验通过）
        void Resolve(string logical, NameRule rule)
        {
            var (name, value) = SensorProbe.AutoMatch(report, rule);
            if (name is not null)
            {
                fieldToName[logical] = name;
            }
            else
            {
                missing.Add(logical);
            }
        }

        Resolve("VCore",       SensorNameRules.VCore);
        Resolve("PackageTemp", SensorNameRules.PackageTemp);
        Resolve("AvgFreq",     SensorNameRules.AvgFreq);

        // 每核字段（VID / 有效频率）：尽力而为，缺失不计入"必需失败"
        // 这里仅记录是否存在，不影响 ShouldPromptUser（避免无每核数据的老平台误报）
        foreach (var rule in SensorNameRules.PerCore)
        {
            var (name, _) = SensorProbe.AutoMatch(report, rule);
            if (name is not null && !fieldToName.ContainsKey(name))
            {
                fieldToName[name] = name; // 原始名作 key，供采集层按规则二次分组
            }
        }

        // 是否提示用户：任何必需字段缺失 → 提示手动重探
        bool shouldPrompt = RequiredFields.Any(f => !fieldToName.ContainsKey(f));

        return new ProbeResult(
            FieldToName: fieldToName,
            MissingFields: missing,
            ShouldPromptUser: shouldPrompt
        );
    }

    /// <summary>探测后的状态推进（成功/失败分别更新）</summary>
    public static ProbeState Advance(ProbeState current, ProbeResult result)
    {
        if (result.MissingFields.Count == 0)
        {
            // 成功：重置失败计数，记录时间
            return current with
            {
                HasEverProbed = true,
                LastProbedAt = DateTime.UtcNow,
                FailureCount = 0,
                MissingFields = Array.Empty<string>(),
            };
        }
        // 失败/不完整：累计失败次数，保留缺失字段
        return current with
        {
            HasEverProbed = true,
            LastProbedAt = DateTime.UtcNow,
            FailureCount = current.FailureCount + 1,
            MissingFields = result.MissingFields,
        };
    }
}
