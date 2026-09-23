// ScoringEngine.cs — CPU 体质评分引擎核心（稳态 V/F 体质评分）
// 设计依据：评分算法详细设计 §4-§5
//
// ★ 重大修正（真机数据驱动，见 docs/archive/真机修正说明_V1.6.6.md）★★
//
//   【背景】早期版本曾废弃一切频率折算（见 docs/archive/频率折算勘误.md），理由是旧的
//   「等比乘法折算 V×(Fref/Fmeas)」会凭空造出假大雕。方向正确，但废弃过度：
//   隐含假设「质控通过 = 大家都在同一满载工作点」，被真机数据推翻——
//     7800X3D 实测两点：
//       OCCT  重负载：4526MHz @ 0.975V  → 旧算法 SP≈123（大雕）
//       CPU-Z 轻负载：4896MHz @ 1.127V  → 旧算法 SP≈79 （小雷）
//     同一颗 CPU 仅因负载不同差 44 分 → 结果不可重复，算法不成立。
//
//   【修正】Zen 稳态 (F,V) 在固定负载线下近似落在一条【带截距的直线】上，
//   因此改用【线性折算】（不是等比乘法）：沿实测 V/F 直线把任意工作点
//   归一化到统一「锚定频率」，再与参考电压比较。
//     真机双点反推斜率 = (1.127-0.975)/(4896-4526) = 0.411 mV/MHz
//     （落在 Zen4 公开经验区间 0.35–0.45 内，反证两点确在同一物理直线上）
//   折算后两点电压分别为 1.0876 / 1.0878 V（差 0.2mV），SP 均 ≈ 95，
//   分差从 44 分收敛到 2 分以内 —— 可重复，算法成立。
//
//   【通用性】参数三级解析（型号精确 → 代际默认/规格推算 → 全局保守默认），
//   并标记来源与标定等级（Calibrated/Estimated），未标定型号也能出分且诚实标注。

namespace ScoringEngine;

using System;
using System.Collections.Generic;
using System.Linq;

public record ScoreInput(
    string Model,           // 例："AMD Ryzen 7 7800X3D"
    string ZenGeneration,   // "Zen3" / "Zen4" / "Zen5"
    double VCore,           // 核心电压（稳态均值，VDDCR 口径）
    double FrequencyMHz,    // 当前频率（稳态均值）
    double ReferenceFreqMHz,// 基础频率（仅展示，不参与满载判定）
    double TemperatureC,    // 封装温度
    double[]? PerCoreVIDs,  // 每核 VID（可为 null；★仅为 CPPC 请求值，非真实每核电压）
    double[]? PerCoreClocks // 每核频率（可为 null）
);

public record ScoreResult(
    string Mode,            // OnlineRelative / OfflineAbsolute / CurveOnly
    double SpScore,         // 绝对 SP 分（0-150）
    double Percentile,      // 相对分（0-100，仅在线模式有效）
    string Grade,           // 大雕 / 小雕 / 普通 / 小雷 / 大雷（五档）
    double Confidence,      // 置信度 0-1
    string ReferenceNote,   // "仅供参考" 等提示
    string? BestCore,       // 最佳核心编号
    string? WorstCore       // 最差核心编号
);

#region ---- 基线模型 ----

/// <summary>标定等级：参数是否有真机/公开实测出处</summary>
public static class Calibration
{
    /// <summary>已标定：参数有实测出处，绝对分可用于同型号横向比较</summary>
    public const string Calibrated = "Calibrated";
    /// <summary>估计：规格反推/经验值，绝对分仅供同型号横向比较</summary>
    public const string Estimated  = "Estimated";
}

/// <summary>基准量表条目（扩展：支持 V/F 折算与通用性兜底）</summary>
public record Baseline(
    string Model,
    string ZenGeneration,
    double VRef,
    double ReferenceFreqMHz,
    int Priority,
    double MaxBoostMHz = 0,        // 标称最大加速频率（用于推算锚点）
    double AnchorFreqMHz = 0,      // 锚定频率；0 → 按 MaxBoost×0.94 推算
    double SlopeMvPerMhz = 0,      // V/F 斜率 mV/MHz；0 → 按 Zen 代际默认
    string CalibrationLevel = Calibration.Estimated);

#endregion

#region ---- V/F 折算与 SP 计算 ----

public static class SpScaler
{
    // 评级改为五档 —— 大雷 / 小雷 / 普通 / 小雕 / 大雕
    //   SP 轨以 100（=参考电压）为中心，每档 8 分，上下对称：
    //     大雕 ≥112 → 小雕 ≥104 → 普通 ≥96 → 小雷 ≥88 → 大雷 <88
    private const double BigGoldenSp = 112;    // 大雕
    private const double SmallGoldenSp = 104;  // 小雕
    private const double NormalSp = 96;        // 普通
    private const double SmallThunderSp = 88;  // 小雷
    // 百分制轨（相对同型号群体的百分位，按正态分布的合理分档）
    private const double BigGoldenPct = 90;    // 大雕（前 10%）
    private const double SmallGoldenPct = 70;  // 小雕
    private const double NormalPct = 30;       // 普通
    private const double SmallThunderPct = 10; // 小雷

    /// <summary>五档评级（SP 绝对分轨）</summary>
    public static string GradeBySp(double sp) => sp switch
    {
        >= BigGoldenSp => "大雕",
        >= SmallGoldenSp => "小雕",
        >= NormalSp => "普通",
        >= SmallThunderSp => "小雷",
        _ => "大雷"
    };

    /// <summary>五档评级（百分位相对分轨）</summary>
    public static string GradeByPercent(double pct) => pct switch
    {
        >= BigGoldenPct => "大雕",
        >= SmallGoldenPct => "小雕",
        >= NormalPct => "普通",
        >= SmallThunderPct => "小雷",
        _ => "大雷"
    };

    /// <summary>评级 → 展示色（供 UI 直接用，避免界面层重复定义阈值）</summary>
    public static string GradeColor(string grade) => grade switch
    {
        "大雕" => "#FFB020",   // 金
        "小雕" => "#3FB950",   // 绿
        "普通" => "#58A6FF",   // 蓝
        "小雷" => "#D29922",   // 橙
        "大雷" => "#F85149",   // 红
        _ => "#8B949E"
    };

    /// <summary>
    /// 把实测工作点沿 V/F 直线归一化到锚定频率。
    ///   V_anchor = V_meas + (slope/1000) × (AnchorFreq − F_meas)
    /// ★ 线性（带截距），不是等比乘法 —— 这是与已被废弃的旧折算的本质区别。
    /// </summary>
    public static double AnchorVoltage(double vMeas, double fMeas,
        double anchorFreqMHz, double slopeMvPerMhz)
    {
        if (slopeMvPerMhz <= 0 || anchorFreqMHz <= 0) return vMeas; // 无参数 → 不折算（向后兼容）
        return vMeas + (slopeMvPerMhz / 1000.0) * (anchorFreqMHz - fMeas);
    }

    /// <summary>不折算的绝对 SP（旧口径，保留兼容）</summary>
    public static double CalcSp(double vMeas, double vRef, double scale = 300)
    {
        if (vMeas <= 0) throw new ArgumentException("电压必须为正", nameof(vMeas));
        if (vRef <= 0) throw new ArgumentException("参考电压未设置（CurveOnly 模式）", nameof(vRef));
        var sp = 100 + (vRef / vMeas - 1) * scale;
        return Math.Max(0, Math.Min(150, sp));
    }

    /// <summary>
    /// ★ 折算后的绝对 SP主口径）
    ///   先沿 V/F 直线归一化到锚点，再与 VRef 比较。
    ///   slope/anchorFreq 为 0 时自动退回不折算（保证旧量表仍可用）。
    /// </summary>
    public static double CalcSpAnchored(double vMeas, double fMeas, double vRef,
        double anchorFreqMHz, double slopeMvPerMhz, double scale = 300)
    {
        var vAnchor = AnchorVoltage(vMeas, fMeas, anchorFreqMHz, slopeMvPerMhz);
        return CalcSp(vAnchor, vRef, scale);
    }
}

#endregion

#region ---- 通用性：代际画像 / 三级解析 / 自标定 ----

/// <summary>Zen 代际工艺画像：V/F 斜率主要由工艺决定，同代内相当稳定</summary>
public static class ZenProfile
{
    /// <summary>锚定频率推算系数：全核应力频率 ≈ MaxBoost × 该系数</summary>
    public const double AnchorToBoostRatio = 0.94;

    /// <summary>代际默认 V/F 斜率 (mV/MHz)</summary>
    public static double DefaultSlope(string zenGeneration) => (zenGeneration ?? "") switch
    {
        "Zen" or "Zen+" or "Zen2" => 0.30,
        "Zen3" => 0.35,
        "Zen4" => 0.41,   // ★ 真机双点反推 0.411，与公开区间 0.35–0.45 一致
        "Zen5" => 0.45,
        _ => 0.40,        // 未知代际：全局中位，保守
    };
}

/// <summary>解析后的可用参数（含来源标记，报告里逐条展示，不做黑盒）</summary>
public sealed record ResolvedBaseline(
    double VRef,
    double AnchorFreqMHz,
    double SlopeMvPerMhz,
    string SlopeSource,        // "型号标定" / "代际默认(Zen4)" / "全局默认"
    string AnchorSource,       // "型号标定" / "规格推算(Boost×0.94)" / "全局默认"
    string CalibrationLevel);

/// <summary>三级参数解析：型号精确 → 代际默认/规格推算 → 全局保守默认</summary>
public static class BaselineResolver
{
    public static ResolvedBaseline Resolve(Baseline b)
    {
        // ① V/F 斜率
        double slope; string slopeSrc;
        if (b.SlopeMvPerMhz > 0)
        {
            slope = b.SlopeMvPerMhz;
            slopeSrc = "型号标定";
        }
        else
        {
            var zen = b.ZenGeneration ?? "";
            var known = zen is "Zen" or "Zen+" or "Zen2" or "Zen3" or "Zen4" or "Zen5";
            slope = ZenProfile.DefaultSlope(zen);
            slopeSrc = known ? $"代际默认({zen})" : "全局默认";
        }

        // ② 锚定频率
        double anchor; string anchorSrc;
        if (b.AnchorFreqMHz > 0)
        {
            anchor = b.AnchorFreqMHz;
            anchorSrc = "型号标定";
        }
        else if (b.MaxBoostMHz > 0)
        {
            // 取整到 25MHz，避免虚假精度
            anchor = Math.Round(b.MaxBoostMHz * ZenProfile.AnchorToBoostRatio / 25.0) * 25.0;
            anchorSrc = "规格推算(Boost×0.94)";
        }
        else
        {
            anchor = 0; // 无锚点 → 折算自动失效，退回不折算
            anchorSrc = "无(不折算)";
        }

        return new ResolvedBaseline(b.VRef, anchor, slope, slopeSrc, anchorSrc, b.CalibrationLevel);
    }
}

/// <summary>
/// 多点自标定：用最小二乘拟合这颗 CPU 自己的 V/F 斜率。
/// 至少 3 点；频率无方差（如全程单一烤机负载）→ 返回 null，提示回退代际默认。
/// 返回 R² 用于判断线性度（R² 低 = 工作点不可靠，不应采信）。
/// </summary>
public static class VfFit
{
    public sealed record FitResult(double SlopeMvPerMhz, double InterceptV, double RSquared, int PointCount);

    public static FitResult? Fit(IEnumerable<(double freqMHz, double volt)> points)
    {
        var pts = points?.Where(p => p.freqMHz > 0 && p.volt > 0).ToList()
                  ?? new List<(double, double)>();
        if (pts.Count < 3) return null;                       // 样本不足
        if (pts.Select(p => p.freqMHz).Distinct().Count() < 2) return null; // 频率无方差

        int n = pts.Count;
        double sx = pts.Sum(p => p.freqMHz);
        double sy = pts.Sum(p => p.volt);
            double sxx = pts.Sum(p => p.freqMHz * p.freqMHz);
        double sxy = pts.Sum(p => p.freqMHz * p.volt);

        double denom = n * sxx - sx * sx;
        if (Math.Abs(denom) < 1e-9) return null;

        double k = (n * sxy - sx * sy) / denom;   // V per MHz
        double b = (sy - k * sx) / n;

        // R²
        double yMean = sy / n;
        double ssTot = pts.Sum(p => (p.volt - yMean) * (p.volt - yMean));
        double ssRes = pts.Sum(p => (p.volt - (k * p.freqMHz + b)) * (p.volt - (k * p.freqMHz + b)));
        double r2 = ssTot < 1e-12 ? 1.0 : 1.0 - ssRes / ssTot;

        return new FitResult(k * 1000.0, b, r2, n);           // 转为 mV/MHz
    }
}

#endregion

/// <summary>
/// 评分引擎：输入 + 量表 + (可选)云端分位表 → 体质分
/// ★ 评分主口径改为【折算到锚点后的电压】
/// </summary>
public class ScoreEngine
{
    private readonly IBaselineStore _baseline;
    private readonly ICloudStats? _cloud;

    public ScoreEngine(IBaselineStore baseline, ICloudStats? cloud = null)
    {
        _baseline = baseline;
        _cloud = cloud;
    }

    public ScoreResult Score(ScoreInput input)
    {
        var baseline = _baseline.Get(input.Model)
                     ?? throw new InvalidOperationException($"未收录型号：{input.Model}");

        // ★ 三级参数解析（记录来源，供报告展示）
        var r = BaselineResolver.Resolve(baseline);

        // ★ 折算：把实测点沿 V/F 直线归一化到锚定频率
        var vAnchor = SpScaler.AnchorVoltage(input.VCore, input.FrequencyMHz,
            r.AnchorFreqMHz, r.SlopeMvPerMhz);

        double sp;
        double percentile = 0;
        string mode;
        double confidence;
        string note;

        if (_cloud is { IsAvailable: true } && _cloud.TryGetStats(input.Model, out var stats))
        {
            mode = "OnlineRelative";
            double z = (SpScaler.CalcSp(vAnchor, r.VRef) - stats.Mu) / stats.Sigma;
            percentile = Math.Max(0, Math.Min(100, NormCdf(z) * 100));
            sp = SpScaler.CalcSp(vAnchor, r.VRef);
            confidence = 0.9;
            note = "";
        }
        else if (r.VRef > 0)
        {
            mode = "OfflineAbsolute";
            sp = SpScaler.CalcSp(vAnchor, r.VRef);
            // ★ 置信度随标定等级变化：已标定 0.6 / 估计 0.4
            confidence = r.CalibrationLevel == Calibration.Calibrated ? 0.6 : 0.4;
            note = r.CalibrationLevel == Calibration.Calibrated
                ? "离线评分，仅供参考（样本库未积累）"
                : "本型号参考参数为估计值，SP 绝对分仅供同型号横向比较，不与厂商内置分数对标";
        }
        else
        {
            mode = "CurveOnly";
            return new ScoreResult(mode, 0, 0, "—", 0,
                "缺少参考电压，仅显示原始曲线", null, null);
        }

        var grade = percentile > 0 ? SpScaler.GradeByPercent(percentile) : SpScaler.GradeBySp(sp);

        // 核心体质分布（最佳/最差核心）—— ★ 仅为 CPPC 请求值排序，非真实每核电压
        string? best = null, worst = null;
        if (input.PerCoreVIDs is { Length: > 0 })
        {
            int bi = 0, wi = 0;
            for (int i = 1; i < input.PerCoreVIDs.Length; i++)
            {
                if (input.PerCoreVIDs[i] < input.PerCoreVIDs[bi]) bi = i;
                if (input.PerCoreVIDs[i] > input.PerCoreVIDs[wi]) wi = i;
            }
            best = $"Core#{bi}";
            worst = $"Core#{wi}";
        }

        return new ScoreResult(mode, Math.Round(sp, 1), Math.Round(percentile, 1),
            grade, confidence, note, best, worst);
    }

    /// <summary>标准正态 CDF（Abramowitz-Stegun 近似），替代此前错误的线性映射</summary>
    private static double NormCdf(double z)
    {
        // erf 近似
        double t = 1.0 / (1.0 + 0.2316419 * Math.Abs(z));
        double d = 0.3989422804014327 * Math.Exp(-z * z / 2.0);
        double p = d * t * (0.319381530 + t * (-0.356563782 +
                   t * (1.781477937 + t * (-1.821255978 + t * 1.330274429))));
        return z >= 0 ? 1.0 - p : p;
    }
}

// —— 以下接口由 采集SDK / 数据库 层实现，此处仅定义契约 ——

public interface IBaselineStore
{
    Baseline? Get(string model);
}

public interface ICloudStats
{
    bool IsAvailable { get; }
    bool TryGetStats(string model, out CloudStats stats);
}

public record CloudStats(double Mu, double Sigma, int SampleCount);

/// <summary>
/// 每核 VID 读数质量判定真机证据驱动）。
/// 真机（7800X3D / Zen4 / SVI3）：满载时整颗 CPU 供电 VDDCR 已 1.135V，
/// 而 LHM 报告的各核 "Core #N VID" 恒为 0.43~0.45V 最低档占位值且彼此相同，
/// 与真实供电完全脱节。用这种数据做核间排名会得出"最佳/最差核心相同"的假结论。
/// 本判定在【满载稳态采样】前提下识别此类坏数据，返回 null 让下游不输出核间分布。
/// </summary>
public static class PerCoreVidQuality
{
    /// <summary>满载时每核请求电压的合理下限（V）。Zen3/4/5 满载 P-state 通常 ≥0.9V，0.75 已留足裕量。</summary>
    public const double MinPlausibleFullLoadVid = 0.75;

    /// <summary>每核请求电压与整颗 VDDCR 的最大允许差距（V）；超过即认为读数与真实供电脱节。</summary>
    public const double DetachMargin = 0.25;

    /// <summary>仅在已确认满载（vcore 为稳态 VDDCR）时调用。</summary>
    public static bool IsPlausible(double[]? vids, double vcore)
    {
        if (vids is null || vids.Length == 0) return false;
        double mx = vids.Max();
        if (mx < MinPlausibleFullLoadVid) return false;              // 全部停在最低档占位值
        if (vcore > 0.90 && mx < vcore - DetachMargin) return false; // 与真实供电严重脱节
        return true;
    }

    /// <summary>有效则原样返回，无效返回 null（下游据此不输出核间排名/分布）。</summary>
    public static double[]? Sanitize(double[]? vids, double vcore)
        => IsPlausible(vids, vcore) ? vids : null;
}
