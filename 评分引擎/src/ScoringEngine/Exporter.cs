// Exporter.cs — 评分解导出（JSON + 人类可读 Markdown）
// 设计决策：导出=给用户看的成品，与"过程日志"解耦。
//   格式双轨：
//     · JSON  —— 供程序回读 / 云端上报预留（结构化，字段稳定）
//     · Markdown —— 供人直接阅读 / 论坛分享（含核心分布可视化）
//   一份 ScoreReport 同时可导出两种格式，避免两份数据不同步。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ScoringEngine;

/// <summary>可导出的完整评分报告（在 ScoreResult 基础上补充输入/环境上下文）</summary>
public sealed record ScoreReport(
    Guid ReportId,
    DateTime GeneratedAt,
    string AppVersion,
    ScoreInput Input,
    Baseline UsedBaseline,
    ScoreResult Result,
    IReadOnlyList<CoreRow> PerCoreRows,   // 每核明细（VID/频率/相对偏差）
    string ExportFormatVersion)
{
    public ScoreReport() : this(
        Guid.NewGuid(), DateTime.UtcNow, "dev",
        new ScoreInput("", "", 0, 0, 0, 0, null, null),
        new Baseline("", "", 0, 0, 0),
        new ScoreResult("", 0, 0, "", 0, "", null, null),
        new List<CoreRow>(), "1.0") { }
}

public sealed record CoreRow(int CoreIndex, double? VID, double? ClockMHz, double? RelativeVID);

/// <summary>评分解导出器（JSON + Markdown）</summary>
public static class Exporter
{
    public const string FormatVersion = "1.0";

    // —— JSON ——

    public static string ToJson(ScoreReport report)
    {
        // 保持字段顺序稳定（利于 diff/云端解析）
        var opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        return JsonSerializer.Serialize(report, opts);
    }

    public static void ExportJson(ScoreReport report, string path)
    {
        File.WriteAllText(path, ToJson(report));
    }

    // —— Markdown（人类可读）——
    //
    // ★ 注意导出内容与渲染分离：这里只生成 Markdown 文本，
    //   是否转 HTML/PDF 由上层决定（避免核心逻辑依赖文档渲染库）。

    public static string ToMarkdown(ScoreReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# CPU 体质评分报告");
        sb.AppendLine();
        sb.AppendLine($"- **生成时间**：{r.GeneratedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- **程序版本**：{r.AppVersion}");
        sb.AppendLine($"- **报告 ID**：`{r.ReportId:N}`");
        sb.AppendLine();
        sb.AppendLine("## 硬件信息");
        sb.AppendLine();
        sb.AppendLine($"| 项目 | 值 |");
        sb.AppendLine($"|------|-----|");
        sb.AppendLine($"| 型号 | {r.Input.Model} |");
        sb.AppendLine($"| Zen 代际 | {r.Input.ZenGeneration} |");
        sb.AppendLine($"| 核心电压 (稳态) | {r.Input.VCore:F4} V |");
        sb.AppendLine($"| 当前频率 | {r.Input.FrequencyMHz:F0} MHz |");
        sb.AppendLine($"| 额定频率 | {r.Input.ReferenceFreqMHz:F0} MHz |");
        sb.AppendLine($"| 温度 | {r.Input.TemperatureC:F1} ℃ |");
        sb.AppendLine();
        sb.AppendLine("## 评分结果");
        sb.AppendLine();
        sb.AppendLine($"| 项目 | 值 |");
        sb.AppendLine($"|------|-----|");
        sb.AppendLine($"| 模式 | {r.Result.Mode} |");
        sb.AppendLine($"| **SP 分** | **{r.Result.SpScore:F1}** |");
        sb.AppendLine($"| 百分位 | {r.Result.Percentile:F1} |");
        sb.AppendLine($"| **评级** | **{r.Result.Grade}** |");
        sb.AppendLine($"| 置信度 | {r.Result.Confidence:P0} |");
        if (!string.IsNullOrEmpty(r.Result.ReferenceNote))
            sb.AppendLine($"| 备注 | {r.Result.ReferenceNote} |");
        if (r.Result.BestCore is not null)
            sb.AppendLine($"| 最佳核心 | {r.Result.BestCore} |");
        if (r.Result.WorstCore is not null)
            sb.AppendLine($"| 最差核心 | {r.Result.WorstCore} |");
        sb.AppendLine();
        sb.AppendLine("## 核心体质分布");
        sb.AppendLine();

        if (r.PerCoreRows.Any(c => c.VID is not null))
        {
            // ★ 用字符柱状图做轻量可视化（无需图形库，纯文本即可看分布）
            var vids = r.PerCoreRows.Where(c => c.VID is not null).Select(c => c.VID!.Value).ToList();
            var lo = vids.Min();
            var hi = vids.Max();
            sb.AppendLine("```");
            foreach (var c in r.PerCoreRows)
            {
                var label = $"Core#{c.CoreIndex}".PadRight(8);
                var bar = c.VID is null ? "" : Bar(c.VID.Value, lo, hi);
                sb.AppendLine($"{label} {c.VID?.ToString("F4") ?? "  -  "}V {bar}");
            }
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("> 柱状图：VID 越低表示该核心体质越好（越低越长），"
                + $"按本次采样区间 {lo:F4}–{hi:F4}V 归一化。");
            sb.AppendLine();
            // ★ 必须诚实说明的物理事实（真机验证得出，见排查报告 §4.4）
            sb.AppendLine("> ⚠️ **每核 VID 是 CPPC 请求值，不是真实每核电压。**  \n"
                + "> Zen3/4/5 核心为**统一供电（VDDCR）**，物理上不存在每核独立电压；  \n"
                + "> LHM 暴露的 `Core #N VID` 只是各核向供电系统发出的请求值。  \n"
                + "> 因此**核间排名仅供参考**，主分以整颗 CPU 的 VDDCR 折算值为准。  \n"
                + "> 每核 VID 是各核的请求电压，并非工厂熔丝（factory fuses）中的出厂体质分，后者在软件层面读不到。");
        }
        else
        {
            sb.AppendLine("> 无有效的每核 VID 数据：本机 LHM 的 `Core #N VID` 在满载时仍停在最低档占位值（约 0.43V）且彼此相同，");
            sb.AppendLine("> 不能反映核间体质差异，已判为无效而不输出排名（避免“最佳/最差核心相同”的假结论）。主分以整颗 CPU 的 VDDCR 折算值为准。");
        }

        sb.AppendLine("## 量表信息与折算参数");
        sb.AppendLine();
        sb.AppendLine($"- 参考电压 V_ref = {r.UsedBaseline.VRef:F4} V");
        sb.AppendLine($"- 标定等级 = **{r.UsedBaseline.CalibrationLevel}**");
        sb.AppendLine($"- 数据来源：{(r.Result.Mode == "OnlineRelative" ? "云端分位表" : "本地基准量表")}");
        var rb = ScoringEngine.BaselineResolver.Resolve(r.UsedBaseline);
        sb.AppendLine($"- 锚定频率 = {rb.AnchorFreqMHz:F0} MHz（{rb.AnchorSource}）");
        sb.AppendLine($"- V/F 斜率 = {rb.SlopeMvPerMhz:F3} mV/MHz（{rb.SlopeSource}）");
        sb.AppendLine();
        sb.AppendLine("> 评分采用 **V/F 线性折算**：把实测工作点沿 V/F 直线归一化到锚定频率后再比较，");
        sb.AppendLine("> 使不同负载强度（如 OCCT 重负载 vs CPU-Z 轻负载）下的结果可重复。");
        sb.AppendLine();
        // ★ Estimated 型号显著水印（报告 §7.3）
        if (r.UsedBaseline.CalibrationLevel == Calibration.Estimated)
        {
            sb.AppendLine("> ⚠️ **本型号参考参数为估计值**，SP 绝对分仅供"
                + "**同型号 CPU 之间的横向比较**，不与任何厂商内置分数对标。  ");
            sb.AppendLine("> 待该型号积累足够实测/众包样本后，参数将自动收敛为标定值。");
            sb.AppendLine();
        }
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("> 本评分基于 V/F 折算的自研 CPU 体质评分算法（《评分算法详细设计》）。");
        sb.AppendLine("> 结果为可复现、可校验的自研评分，不依赖、也不对标任何厂商的闭源内置分数。");
        sb.AppendLine();
        sb.AppendLine("> **口径提示**：参考电压取自公开评测的 VID 口径，而实测为 VDDCR 供电读数口径，");
        sb.AppendLine("> 两者存在约 ±10mV 系统差，对应 SP 约 ±3~5 分，离线绝对分天然带有此误差。");

        return sb.ToString();
    }

    public static void ExportMarkdown(ScoreReport report, string path)
    {
        File.WriteAllText(path, ToMarkdown(report), Encoding.UTF8);
    }

    /// <summary>同时导出 JSON + Markdown（推荐：一份给程序，一份给人）</summary>
    public static (string json, string md) ExportBoth(ScoreReport report, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var stem = $"score_{report.GeneratedAt.ToLocalTime():yyyyMMdd_HHmmss}";
        var json = Path.Combine(outDir, $"{stem}.json");
        var md = Path.Combine(outDir, $"{stem}.md");
        ExportJson(report, json);
        ExportMarkdown(report, md);
        return (json, md);
    }

    // —— 辅助 ——

    /// <summary>
    /// 修正（报告 §4.5）：原固定上界 1.20V 归一化，
    ///   而核间 VID 差异只有几十 mV → 被压平成几乎等长，看不出体质差异。
    ///   改为按【本次采样自身的 min/max】归一化：最低 VID 满格、最高 VID 保底 6 格，
    ///   真实的核间离散度可见，同时不夸大量级。
    /// </summary>
    private static string Bar(double value, double min, double max)
    {
        var span = max - min;
        if (span < 1e-9) return new string('█', 20);   // 无差异 → 全满格
        var norm = (value - min) / span;               // 0 = 最低VID，1 = 最高VID
        norm = Math.Max(0, Math.Min(1, norm));
        var len = (int)((1 - norm) * 14) + 6;          // 反转：值越小越长；保底 6 格
        return new string('█', len);
    }

    /// <summary>由 ScoreInput + ScoreResult 构造报告（自动补每核明细）</summary>
    public static ScoreReport Build(ScoreInput input, Baseline baseline, ScoreResult result,
        string appVersion = "dev")
    {
        var rows = new List<CoreRow>();
        if (input.PerCoreVIDs is { Length: > 0 })
        {
            var min = input.PerCoreVIDs.Min();
            for (int i = 0; i < input.PerCoreVIDs.Length; i++)
            {
                rows.Add(new CoreRow(i, input.PerCoreVIDs[i],
                    (input.PerCoreClocks is not null && input.PerCoreClocks.Length > i) ? input.PerCoreClocks[i] : null,
                    RelativeVID: input.PerCoreVIDs[i] - min)); // 相对偏差：负值=更优
            }
        }
        return new ScoreReport(
            ReportId: Guid.NewGuid(),
            GeneratedAt: DateTime.UtcNow,
            AppVersion: string.IsNullOrWhiteSpace(appVersion) ? "dev" : appVersion,
            Input: input,
            UsedBaseline: baseline,
            Result: result,
            PerCoreRows: rows,
            ExportFormatVersion: FormatVersion);
    }
}
