// Program.cs — 采集层演示 + 主链路串联
//
// 修正要点（真机数据驱动，见 docs/archive/真机修正说明_V1.6.6.md）：
//   1) 传感器命名：AM5/Zen4 走 SVI3，核心电压名为【VDDCR CPU】而非旧代码写死的
//      "Core (SVI2 TFN)" —— 后者在真机上永远匹配不到，VCore 恒为空。
//   2) 满载判定基准改用【全核 MaxBoost】，阈值 0.95 → 0.90。
//   3) 评分改用【V/F 线性折算】到锚定频率（旧版不折算被真机双点推翻：差 44 分）。
//   4) 主链路真正串起来：采集 → 质控 → 评分 → 导出（此前 SP 被硬编码成 101.1）。
//
// 真机集成要点：
//   1) Logger.Instance.Configure();              // 程序启动时一次
//   2) var features = orchestrator.Collect();     // 内部自动写日志 + 诊断报告
//   3) var result   = engine.Score(features.ToScoreInput());
//   4) var report   = Exporter.Build(features.ToScoreInput(), baseline, result);
//      Exporter.ExportBoth(report, "导出目录");
//   5) 出问题时：DiagnosticStore.PackDiagnosticBundle();  // 打包发开发者

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Collector;
using ScoringEngine;   // 注意：Baseline 两个命名空间都有，故 Baseline 一律完全限定

var baseDir = AppContext.BaseDirectory;

// —— 0) 初始化日志（程序同级 logs/，便携优先）——
Logger.Instance.Configure(baseDir: baseDir, minLevel: CollectorOptions.LogMinLevel);

// —— 1) 模拟 LHM dump（★ 用真机实际命名：SVI3 / VDDCR）——
var sensors = FakeLhm().ToList();
Logger.Instance.Info("程序启动，准备采集", new { sensorCount = sensors.Count });

// —— 2) 探测模式：首次自动探测 + 字段匹配（AutoOnFirstRun）——
var state = new ProbeState();
var probeReport = SensorProbe.Run(sensors);
var probeResult = ProbeSession.Execute(probeReport);
state = ProbeSession.Advance(state, probeResult);

Console.WriteLine($"映射字段：{probeResult.FieldToName.Count}");
Console.WriteLine($"缺失：{(probeResult.MissingFields.Count == 0 ? "无" : string.Join(",", probeResult.MissingFields))}");

var builder = new DiagnosticBuilder();
builder.AddSensors(sensors.Select(s => new SensorDump(s.Item1, s.Item2, s.Item3, s.Item4)));
foreach (var kv in probeResult.FieldToName)
    builder.ResolveField(kv.Key, kv.Value, value: null, rule: "ProbeMode");
foreach (var missing in probeResult.MissingFields)
    builder.ResolveField(missing, matchedName: null, value: null, rule: "ProbeMode");

// —— 3) 构造采集结果（★ 补 FullLoadFreqMHz = MaxBoost 5000）——
var features = new HardwareFeatures(
    Model: "7800X3D", ZenGeneration: "Zen4",
    VCore: 1.046, FrequencyMHz: 5050,
    ReferenceFreqMHz: 4200,      // 基础频率（仅展示）
    FullLoadFreqMHz: 5000,       // ★ MaxBoost —— 满载判定的正确基准
    TemperatureC: 68,
    PerCoreVIDs: new[] { 1.10, 0.98, 1.05, 1.02, 0.99, 1.06, 1.01, 1.03 },
    PerCoreClocks: null, Source: "LhmOnly", EnvironmentOk: true);

var ok = RationalityCheck.Pass(features, out var rationalMsg);
Console.WriteLine($"\n合理性校验：{(ok ? "通过" : "拒绝")}{(ok ? "" : " → " + rationalMsg)}");

// 满载判定演示（基准 = MaxBoost，阈值 0.90）
var fullLoadTarget = features.FullLoadFreqMHz * CollectorOptions.FullLoadThreshold;
Console.WriteLine($"满载判定：{features.FrequencyMHz:F0}MHz vs 门槛 {fullLoadTarget:F0}MHz " +
                  $"→ {(features.FrequencyMHz >= fullLoadTarget ? "通过" : "未达满载")}");

builder.WithValidation(new ValidationResults(
    EnvironmentOk: true, RationalityOk: ok,
    FullLoadOk: features.FrequencyMHz >= fullLoadTarget, QualityGateOk: true,
    RejectReason: ok ? null : rationalMsg));
var diagPath = DiagnosticStore.Save(builder.Build());
Console.WriteLine($"诊断报告：{diagPath}");
DiagnosticStore.RetainLatest();

// —— 4) ★ 真实评分不再硬编码，走 V/F 折算）——
// 7800X3D 已标定参数（GamersNexus 实测 VRef + 真机双点反推斜率）
var scoringBaseline = new ScoringEngine.Baseline(
    "7800X3D", "Zen4", VRef: 1.070, ReferenceFreqMHz: 4200, Priority: 1,
    MaxBoostMHz: 5000, AnchorFreqMHz: 4800, SlopeMvPerMhz: 0.41,
    CalibrationLevel: Calibration.Calibrated);

var engine = new ScoreEngine(new SingleBaselineStore(scoringBaseline));
var scoreInput = features.ToScoreInput();
var scoreResult = engine.Score(scoreInput);

Console.WriteLine($"\n★ 评分结果：SP={scoreResult.SpScore:F1} 评级={scoreResult.Grade} " +
                  $"（模式={scoreResult.Mode}，置信度={scoreResult.Confidence:P0}）");
if (scoreResult.BestCore is not null)
    Console.WriteLine($"   最佳核心={scoreResult.BestCore}，最差核心={scoreResult.WorstCore}");

// —— 5) 折算效果演示：真机两个工作点（OCCT 重负载 / CPU-Z 轻负载）——
Console.WriteLine("\n[折算验证] 同一颗 7800X3D 的两种负载工作点：");
var pts = new[]
{
    ("OCCT  重负载", 4526.0, 0.975),
    ("CPU-Z 轻负载", 4896.0, 1.127),
};
foreach (var (tag, f, v) in pts)
{
    var raw = SpScaler.CalcSp(v, scoringBaseline.VRef);
    var anchored = SpScaler.CalcSpAnchored(v, f, scoringBaseline.VRef, 4800, 0.41);
    Console.WriteLine($"  {tag}: {f:F0}MHz @{v:F3}V → 不折算 SP={raw:F1} / 折算后 SP={anchored:F1}");
}
Console.WriteLine("  → 不折算时差约 44 分（不可重复）；折算后收敛到 <2 分。");

// —— 6) 导出（JSON + Markdown）——
var scoreReport = Exporter.Build(scoreInput, scoringBaseline, scoreResult);
var outDir = Path.Combine(baseDir, "导出");
var (jsonPath, mdPath) = Exporter.ExportBoth(scoreReport, outDir);
Console.WriteLine($"\n评分解(JSON)：{jsonPath}");
Console.WriteLine($"评分解(Markdown)：{mdPath}");

// —— 7) 一键打包诊断包 ——
var bundle = DiagnosticStore.PackDiagnosticBundle(baseDir);
Console.WriteLine($"\n📦 诊断包已打包：{bundle}");

Console.WriteLine("\n[OK] 主链路演示完成（采集 → 质控 → 评分 → 导出 → 诊断包）");

// —— 模拟 LHM dump（★ 真机 SVI3 命名；含同名干扰项以验证排除规则）——
static IEnumerable<(string hw, string type, string name, double? value)> FakeLhm()
{
    yield return ("CPU", "Voltage",     "VDDCR CPU",           1.046); // ★ 真机核心电压（SVI3）
    yield return ("CPU", "Voltage",     "VDDCR SoC",           0.912); // 应被排除（SoC 非核心）
    yield return ("CPU", "Voltage",     "VDD Misc",            1.100); // 应被排除（Misc）
    yield return ("CPU", "Temperature", "Core (Tctl/Tdie)",    68.0);
    yield return ("CPU", "Clock",       "Cores (Average)",     5050.0);
    for (int i = 0; i < 8; i++)
        yield return ("CPU", "Voltage", $"Core #{i} VID", 1.04 + i * 0.001);
    for (int i = 0; i < 8; i++)
        yield return ("CPU", "Clock",   $"Core #{i} (Effective)", 5020.0);
}

// 单型号量表（演示用；真实项目从 baseline.json 经 JsonBaselineStore 读取）
internal sealed class SingleBaselineStore : ScoringEngine.IBaselineStore
{
    private readonly ScoringEngine.Baseline _b;
    public SingleBaselineStore(ScoringEngine.Baseline b) => _b = b;
    public ScoringEngine.Baseline? Get(string model) => _b;
}
