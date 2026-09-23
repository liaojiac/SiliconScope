// Compat.cs — LHM 跨主板传感器命名兼容层（V1.6）
// 四层防御：① 弹性匹配 ② 合理性校验 ③ 探测自省 ④ 主板指纹缓存
// 对应：LHM跨主板兼容设计.md

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Collector;

/// <summary>传感器字段类型</summary>
public enum Field { Voltage, FreqMHz, TempC, PowerW, LoadPct }

/// <summary>命名规则：精确名（优先）+ 包含关键词（兜底）− 排除词（防误采）</summary>
public sealed record NameRule(Field Field, string[] Exact, string[] Contains,
    Regex RegexRule, string[] Exclude)
{
    public NameRule(Field field, string[] exact, string[] contains)
        : this(field, exact, contains, null!, Array.Empty<string>()) { }

    public NameRule(Field field, string[] exact, string[] contains, string[] exclude)
        : this(field, exact, contains, null!, exclude) { }

    /// <summary>该名称是否被排除</summary>
    public bool IsExcluded(string name)
        => Exclude.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));
}

public static class SensorNameRules
{
    // ★ 弹性匹配：内置名在前，变体兜底（跨主板/BIOS 自适应）
    //
    // ★ 真机修正（报告 §4.2）★★
    //   旧版写死 "Core (SVI2 TFN)"（SVI2 协议命名），但 AM5 / Zen4 主板走
    //   【SVI3 供电协议】，LHM 报告的核心电压传感器名是【VDDCR】（如 "VDDCR CPU"）。
    //   字符串精确匹配必然落空 → 真机上 VCore 恒为空。
    //   改为弹性匹配 + 排除规则，避免把 "VDDCR SoC"（片上系统供电）等
    //   同名前缀的非核心供电误当成核心电压。
    public static readonly NameRule VCore = new(Field.Voltage,
        exact:    new[] { "VDDCR CPU", "Core (SVI2 TFN)" },
        contains: new[] { "VDDCR", "SVI2", "SVI3", "Core Voltage", "CPU Core" },
        exclude:  new[] { "SoC", "Misc", "LDO", "SMU" });

    public static readonly NameRule PackageTemp = new(Field.TempC,
        exact:    new[] { "Core (Tctl/Tdie)" },        // Zen2+
        contains: new[] { "Tctl", "Tdie", "Package", "CPU Temperature" });

    public static readonly NameRule AvgFreq = new(Field.FreqMHz,
        exact:    new[] { "Cores (Average)" },
        contains: new[] { "Average", "Core (Avg)", "CPU Clock" });

    public static readonly NameRule[] PerCore = new[]
    {
        new NameRule(Field.Voltage,  Array.Empty<string>(), Array.Empty<string>(),
                     new Regex(@"^Core #\d+ VID$", RegexOptions.Compiled),
                     Array.Empty<string>()),       // 每核 VID
        new NameRule(Field.FreqMHz, Array.Empty<string>(), Array.Empty<string>(),
                     new Regex(@"^Core #\d+ \(Effective\)$", RegexOptions.Compiled),
                     Array.Empty<string>()), // 有效频率
        new NameRule(Field.FreqMHz, Array.Empty<string>(), Array.Empty<string>(),
                     new Regex(@"^Core #\d+$", RegexOptions.Compiled),
                     Array.Empty<string>()),           // 瞬时频率
    };
}

/// <summary>合理性校验：与命名无关，跨主板无条件生效的防假大雕防线</summary>
public static class Sanity
{
    public static bool IsPlausible(Field field, double v) => field switch
    {
        Field.Voltage  => v is >= 0.20 and <= 1.55,   // VCore 0.2~1.55V
        Field.FreqMHz  => v is >= 1500 and <= 6500,    // 1.5G~6.5GHz
        Field.TempC    => v is >= 20   and <= 105,     // ℃
        Field.PowerW   => v is >= 0    and <= 400,
        Field.LoadPct  => v is >= 0    and <= 100,
        _              => true
    };
}

/// <summary>探测模式：枚举全部传感器 + 按规则自动匹配</summary>
public sealed record ProbeEntry(string Hardware, string SensorType, string Name, double? Value);
public sealed record ProbeReport(DateTime GeneratedAt, List<ProbeEntry> Entries)
{
    public ProbeReport() : this(DateTime.UtcNow, new List<ProbeEntry>()) { }
}

public static class SensorProbe
{
    /// <summary>③ 探测：dump 全部传感器（模拟 LHM 遍历，真实环境用 Computer.Accept）</summary>
    public static ProbeReport Run(IEnumerable<(string hw, string type, string name, double? value)> sensors)
    {
        var report = new ProbeReport();
        foreach (var (hw, type, name, value) in sensors)
            report.Entries.Add(new ProbeEntry(hw, type, name, value));
        return report;
    }

    /// <summary>③ 自动匹配：对每个规则找"最匹配名 + 合理值"的传感器（返回首个命中）</summary>
    public static (string? matchedName, double? value) AutoMatch(ProbeReport report, NameRule rule)
    {
        bool Ok(ProbeEntry e)
            => !rule.IsExcluded(e.Name)                       // ★ 排除词优先（防误采 SoC/Misc/LDO/SMU）
               && Sanity.IsPlausible(rule.Field, e.Value ?? -1);

        // 1) 精确匹配（最高优先级），且值合理
        foreach (var e in report.Entries)
        {
            if (rule.Exact.Contains(e.Name) && Ok(e))
                return (e.Name, e.Value);
        }
        // 2) 包含关键词兜底，且值合理
        foreach (var e in report.Entries)
        {
            if (rule.Contains.Any(k => e.Name.Contains(k, StringComparison.OrdinalIgnoreCase))
                && Ok(e))
                return (e.Name, e.Value);
        }
        // 3) 正则（每核）
        if (rule.RegexRule != null)
        {
            foreach (var e in report.Entries)
            {
                if (rule.RegexRule.IsMatch(e.Name) && Ok(e))
                    return (e.Name, e.Value);
            }
        }
        return (null, null); // 未匹配 → 触发降级
    }
}

/// <summary>④ 主板指纹：主板 + BIOS + LHM 版本 → 传感器映射缓存</summary>
public sealed record BoardFingerprint(string Board, string Bios, string LhmVersion);

public static class BoardCache
{
    public static string Fingerprint(BoardFingerprint fp)
        => $"{fp.Board}_{fp.Bios}_{fp.LhmVersion}";

    /// <summary>查缓存：命中返回映射，未命中返回 null（触发探测）</summary>
    public static IReadOnlyDictionary<string, string>? Lookup(string fingerprint,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> cache)
        => cache.TryGetValue(pseudo: fingerprint, out var map) ? map : null;

    // 占位参数避免 "out variable" 歧义
    private static bool TryGetValue(this IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> dict,
        string pseudo, out IReadOnlyDictionary<string, string> map)
        => dict.TryGetValue(pseudo, out map!);
}
