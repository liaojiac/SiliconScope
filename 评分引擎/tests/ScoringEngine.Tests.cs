// ScoringEngine.Tests.cs — 评分引擎单元测试
// 覆盖：《评分算法详细设计》§4-§5：SP 单调性 / 跨代归一化 / 三级降级 / 评级双轨
// 运行：dotnet run --project tests

using ScoringEngine;
using System;
using System.Linq;

public static class ScoringEngineTests
{
    public static int RunAll()
    {
        int pass = 0, fail = 0;
        void Check(string name, bool cond)
        {
            if (cond) { pass++; Console.WriteLine($"  ✅ {name}"); }
            else { fail++; Console.WriteLine($"  ❌ {name}"); }
        }

        // 内置量表（对应 baseline.json）
        var store = new InMemoryBaseline();
        var engine = new ScoreEngine(store, cloud: null);

        // ===== 1. SP 单调性（电压越低 → SP 越高）=====
        Console.WriteLine("\n[Test] SP 单调性");
        double[] sps = new[]{ 1.12, 1.08, 1.05, 1.02, 0.99 }
            .Select(v => SpScaler.CalcSp(v, 1.050)).ToArray();
        for (int i = 0; i < sps.Length - 1; i++)
            Check($"单调性：{sps[i]:F1} < {sps[i+1]:F1}（电压越低SP越高）", sps[i] < sps[i+1]);
        Check("最差(1.12V) SP<100", sps[0] < 100);
        Check("最佳(0.99V) SP>100", sps[^1] > 100);

        // ===== 2. 评级双轨 =====
        Console.WriteLine("\n[Test] 评级双轨");
        Check("SP≥112 → 大雕", SpScaler.GradeBySp(112) == "大雕");
        Check("SP 104-112 → 小雕", SpScaler.GradeBySp(108) == "小雕");
        Check("SP 96-104 → 普通", SpScaler.GradeBySp(100) == "普通");
        Check("SP 88-96 → 小雷", SpScaler.GradeBySp(92) == "小雷");
        Check("SP<88 → 大雷", SpScaler.GradeBySp(80) == "大雷");
        Check("百分制≥90 → 大雕", SpScaler.GradeByPercent(90) == "大雕");
        Check("百分制 70-90 → 小雕", SpScaler.GradeByPercent(75) == "小雕");
        Check("百分制 30-70 → 普通", SpScaler.GradeByPercent(50) == "普通");
        Check("百分制 10-30 → 小雷", SpScaler.GradeByPercent(20) == "小雷");
        Check("百分制<10 → 大雷", SpScaler.GradeByPercent(5) == "大雷");
        Check("评级→展示色不为空", SpScaler.GradeColor("大雕") == "#FFB020");

        // ===== 3. ★ V/F 线性折算（真机修正）=====
        // 旧版"频率不参与评分"被真机推翻：同一颗 7800X3D 仅因负载不同差 44 分。
        Console.WriteLine("\n[Test] ★ V/F 折算：真机双点收敛");
        const double anchor = 4800, slope = 0.41, vref = 1.070;
        double spRawOcct = SpScaler.CalcSp(0.975, vref);   // OCCT  4526MHz@0.975V
        double spRawCpuz = SpScaler.CalcSp(1.127, vref);   // CPU-Z 4896MHz@1.127V
        Console.WriteLine($"    不折算：{spRawOcct:F1} vs {spRawCpuz:F1}（差 {Math.Abs(spRawOcct-spRawCpuz):F1}）");
        Check("不折算时两点发散 > 30 分（证明旧算法不成立）",
              Math.Abs(spRawOcct - spRawCpuz) > 30);
        double spAOcct = SpScaler.CalcSpAnchored(0.975, 4526, vref, anchor, slope);
        double spACpuz = SpScaler.CalcSpAnchored(1.127, 4896, vref, anchor, slope);
        Console.WriteLine($"    折算后：{spAOcct:F1} vs {spACpuz:F1}（差 {Math.Abs(spAOcct-spACpuz):F2}）");
        Check("折算后两点收敛 < 2 分（可重复）", Math.Abs(spAOcct - spACpuz) < 2);
        Check("折算后 SP 落在 93–97", spAOcct >= 93 && spAOcct <= 97 && spACpuz >= 93 && spACpuz <= 97);
        Check("旧算法误报大雕 / 新算法为普通", spRawOcct >= 112 && spAOcct < 96);
        Check("slope=0 → 退回不折算（向后兼容）",
              Math.Abs(SpScaler.CalcSpAnchored(1.0, 5000, vref, anchor, 0)
                       - SpScaler.CalcSp(1.0, vref)) < 1e-9);

        // ===== 4. 三级降级 =====
        Console.WriteLine("\n[Test] 三级降级");
        // 离线模式（无云端）→ OfflineAbsolute + 仅供参考
        var offline = new ScoreEngine(store, null)
            .Score(MakeInput(store, "7800X3D", 1.05, 4200));
        Check("离线模式 → OfflineAbsolute", offline.Mode == "OfflineAbsolute");
        Check("离线标注仅供参考", offline.ReferenceNote.Contains("仅供参考"));

        // CurveOnly：VRef=0 的量表 → 不给分
        var noRef = new ScoreEngine(new EmptyBaseline(), null)
            .Score(MakeInput(new EmptyBaseline(), "Unknown", 1.05, 4000));
        Check("无量表 → CurveOnly", noRef.Mode == "CurveOnly");

        // ===== 5. 核心体质分布 =====
        Console.WriteLine("\n[Test] 核心体质分布");
        var withCores = MakeInput(store, "7800X3D", 1.05, 4200,
            perCoreVIDs: new[] { 1.10, 0.98, 1.05, 1.02, 0.99, 1.06, 1.01, 1.03 });
        var r3 = engine.Score(withCores);
        Check("识别出最佳核心(Core#1=0.98V)", r3.BestCore == "Core#1");
        Check("识别出最差核心(Core#0=1.10V)", r3.WorstCore == "Core#0");

        // ===== 6. 通用性：未标定型号也能出分且诚实标注 =====
        Console.WriteLine("\n[Test] 通用性（三级参数解析 + 自标定）");
        Check("Zen4 默认斜率 0.41", Math.Abs(ZenProfile.DefaultSlope("Zen4") - 0.41) < 1e-9);
        Check("Zen3 默认斜率 0.35", Math.Abs(ZenProfile.DefaultSlope("Zen3") - 0.35) < 1e-9);
        Check("Zen5 默认斜率 0.45", Math.Abs(ZenProfile.DefaultSlope("Zen5") - 0.45) < 1e-9);
        Check("未知代际 → 全局默认 0.40", Math.Abs(ZenProfile.DefaultSlope("Unknown") - 0.40) < 1e-9);

        var rb = BaselineResolver.Resolve(
            new Baseline("7950X", "Zen4", 1.060, 4500, 2, MaxBoostMHz: 5700, AnchorFreqMHz: 5200));
        Check("斜率缺省 → 回退 Zen4 代际 0.41", Math.Abs(rb.SlopeMvPerMhz - 0.41) < 1e-9);
        Check("斜率来源标记为代际默认", rb.SlopeSource == "代际默认(Zen4)");
        Check("锚点保留型号精确值 5200", Math.Abs(rb.AnchorFreqMHz - 5200) < 1e-9);

        var rb2 = BaselineResolver.Resolve(new Baseline("5800X3D", "Zen3", 1.120, 3400, 5, MaxBoostMHz: 4500));
        Check("锚点缺省 → Boost×0.94 推算 (4500→4225)", Math.Abs(rb2.AnchorFreqMHz - 4225) < 1e-9);
        Check("锚点来源标记为规格推算", rb2.AnchorSource == "规格推算(Boost×0.94)");
        Check("Zen3 斜率缺省 → 0.35", Math.Abs(rb2.SlopeMvPerMhz - 0.35) < 1e-9);

        var fitPts = new[]
        {
            (3000.0, 0.975 + 0.00041 * (3000.0 - 4526.0)),
            (3500.0, 0.975 + 0.00041 * (3500.0 - 4526.0)),
            (4000.0, 0.975 + 0.00041 * (4000.0 - 4526.0)),
            (4500.0, 0.975 + 0.00041 * (4500.0 - 4526.0)),
            (5000.0, 0.975 + 0.00041 * (5000.0 - 4526.0)),
            (5200.0, 0.975 + 0.00041 * (5200.0 - 4526.0)),
        };
        var fit = VfFit.Fit(fitPts);
        Check("自标定返回拟合结果（6 点）", fit is not null);
        if (fit is not null)
        {
            Console.WriteLine($"    拟合斜率={fit.SlopeMvPerMhz:F4} mV/MHz, R²={fit.RSquared:F4}");
            Check("最小二乘精确还原 0.41", Math.Abs(fit.SlopeMvPerMhz - 0.41) < 1e-6);
            Check("线性度 R² ≈ 1.0", Math.Abs(fit.RSquared - 1.0) < 1e-6);
        }
        Check("样本不足(<3点) → null",
              VfFit.Fit(new[] { (3000.0, 0.9), (4000.0, 1.0) }) is null);
        Check("频率无方差 → null",
              VfFit.Fit(new[] { (4000.0, 0.9), (4000.0, 0.95), (4000.0, 1.0) }) is null);

        // ===== 7. 每核 VID 质量判定（真机 SVI3 占位值 0.43V 必须判无效）=====
        Console.WriteLine("\n[Test] 每核 VID 质量判定");
        // 真机坏数据：7800X3D 满载 VDDCR=1.135V，而 8 核 VID 全停在 0.43~0.45V 最低档
        var badVids = new[] { 0.4375, 0.4375, 0.4375, 0.43125, 0.43125, 0.45, 0.4375, 0.4375 };
        Check("真机占位值(全0.43V,VDDCR1.135) → 无效", PerCoreVidQuality.IsPlausible(badVids, 1.135) == false);
        Check("坏数据 Sanitize → null（不输出假排名）", PerCoreVidQuality.Sanitize(badVids, 1.135) is null);
        Check("null → 无效", PerCoreVidQuality.IsPlausible(null, 1.1) == false);
        Check("空数组 → 无效", PerCoreVidQuality.IsPlausible(Array.Empty<double>(), 1.1) == false);
        // 正常满载：各核 VID 在 0.98~1.10V 且有差异
        var goodVids = new[] { 1.10, 0.98, 1.05, 1.02, 0.99, 1.06, 1.01, 1.03 };
        Check("正常满载 VID(0.98~1.10) → 有效", PerCoreVidQuality.IsPlausible(goodVids, 1.05) == true);
        Check("正常数据 Sanitize 原样保留", ReferenceEquals(PerCoreVidQuality.Sanitize(goodVids, 1.05), goodVids));
        // VID 与 VDDCR 严重脱节（max 0.6 而 VDDCR 1.1）→ 无效
        Check("VID 与 VDDCR 脱节 >0.25V → 无效", PerCoreVidQuality.IsPlausible(new[] { 0.60, 0.61 }, 1.10) == false);
        // 全相等但处于满载合理电压：不因"相等"误杀（是否有区分度与是否坏占位是两回事）
        Check("全相等但电压合理(1.05) → 仍有效", PerCoreVidQuality.IsPlausible(new[] { 1.05, 1.05 }, 1.05) == true);

        Console.WriteLine($"\n=== {pass} passed, {fail} failed ===");
        return fail == 0 ? 0 : 1;
    }

    public static void Main() => Environment.Exit(RunAll());

    private static ScoreInput MakeInput(IBaselineStore store, string model, double vcore, double freq,
        double[]? perCoreVIDs = null)
    {
        var b = store.Get(model) ?? new Baseline(model, "Zen4", 1.05, 4000, 99);
        return new ScoreInput(model, b.ZenGeneration, vcore, freq, b.ReferenceFreqMHz, 65,
            perCoreVIDs, null);
    }
}

// ---- 测试用量表（与 baseline.json 对齐）----
public class InMemoryBaseline : IBaselineStore
{
    private readonly System.Collections.Generic.List<Baseline> _list = new()
    {
        // 7800X3D 为唯一已标定型号（GamersNexus 实测 VRef + 真机双点反推斜率）
        new Baseline("7800X3D", "Zen4", 1.070, 4200, 1,
            MaxBoostMHz: 5000, AnchorFreqMHz: 4800, SlopeMvPerMhz: 0.41,
            CalibrationLevel: Calibration.Calibrated),
        new Baseline("5800X3D", "Zen3", 1.120, 3400, 5),
        new Baseline("9950X",   "Zen5", 1.030, 4300, 7),
    };
    public Baseline? Get(string model) =>
        _list.FirstOrDefault(b => b.Model == model || model.Contains(b.Model));
}

public class EmptyBaseline : IBaselineStore
{
    // CurveOnly 语义是「型号在表内但 VRef=0」；返回 null 会被引擎当作未收录型号直接抛异常
    public Baseline? Get(string model) => new Baseline(model, "Zen4", 0.0, 4000, 99);
}
