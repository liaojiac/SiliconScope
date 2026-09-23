"""verify_structure.py — C# 静态结构校验
说明：沙盒无 .NET SDK，无法 dotnet build，故做结构性 preflight，
     逐文件检查新增类型/方法/字段是否齐全、Program.cs 调用是否匹配签名。
通过标准：全部断言通过。
"""
import os

ROOT = os.path.dirname(os.path.abspath(__file__))
def read(p): return open(os.path.join(ROOT, p), encoding="utf-8").read()

files = {
    "Collector.cs":  read("src/Collector/Collector.cs"),
    "Logger.cs":     read("src/Collector/Logger.cs"),
    "Diagnostic.cs": read("src/Collector/Diagnostic.cs"),
    "Program.cs":    read("Program.cs"),
    "Exporter.cs":   read(os.path.join(ROOT, "..", "评分引擎", "src", "ScoringEngine", "Exporter.cs")),
}

P, F = 0, 0
def check(name, cond):
    global P, F
    if cond: P += 1; print(f"  ✅ {name}")
    else:    F += 1; print(f"  ❌ {name}")

c = files["Collector.cs"]; l = files["Logger.cs"]
d = files["Diagnostic.cs"]; p = files["Program.cs"]; e = files["Exporter.cs"]

print("[采集层 C# 结构校验]")
check("Logger.cs: Logger 类 + Instance 单例",  "class Logger" in l and "Instance" in l)
check("Logger: 双输出 Console + File",         "ConsoleEnabled" in l and "FileEnabled" in l)
check("Logger: LogLevel 枚举",                 "enum LogLevel" in l)
check("Logger: 滚动文件（按天 yyyyMMdd）",      "yyyyMMdd" in l)
check("Logger: 结构化上下文 dictionary",         "ctxDict" in l or "Context" in l)
check("Logger: 写盘失败不影响主流程",            "LOG-FAIL" in l)
check("Diagnostic: DiagnosticBuilder",           "class DiagnosticBuilder" in d)
check("Diagnostic: FieldResolution 字段解析",     "FieldResolution" in d)
check("Diagnostic: 自动推导缺失字段",            "missing" in d)
check("Diagnostic: RetainCount = 10",            "RetainCount = 10" in d)
check("Diagnostic: PackDiagnosticBundle 打包",    "PackDiagnosticBundle" in d)
check("Diagnostic: 打包含 logs/reports/cache",   "logs" in d and "sensor_cache" in d)
check("Collector: 编排器注入 Logger",            "Logger.Instance" in c)
check("Collector: 注入 DiagnosticBuilder",        "DiagnosticBuilder" in c)
check("Collector: finally 落盘诊断报告",          "finally" in c and "DiagnosticStore.Save" in c)
check("Collector: ★新增 RationalityCheck",        "RationalityCheck" in c)
check("Collector: 合理性校验只看数值（Between）",  "Between(f.VCore" in c)
check("Collector: LhmCollector.LastDump",         "LastDump" in c and "private set" in c)
check("Collector: 构造器接受 baselineTag",        "baselineTag" in c)
check("Program: Configure 初始化日志",            "Logger.Instance.Configure" in p)
check("Program: PackDiagnosticBundle 打包",       "PackDiagnosticBundle" in p)
check("Program: Exporter.ExportBoth",             "Exporter.ExportBoth" in p)
check("Program: 诊断报告落盘 + RetainLatest",     "DiagnosticStore.Save" in p and "RetainLatest" in p)

print("\n[评分引擎 C# 结构校验]")
check("Exporter: ToJson",                        "ToJson" in e)
check("Exporter: ToMarkdown（人类可读）",          "ToMarkdown" in e)
check("Exporter: ExportBoth（JSON+MD 双格式）",    "ExportBoth" in e)
check("Exporter: Markdown 核心分布可视化",         "█" in e or "Bar(" in e)
check("Exporter: Build 构造报告（含每核明细）",    "CoreRow" in e and "Build(" in e)
check("Exporter: 评级/置信度/最佳最差核心字段",    "BestCore" in e and "WorstCore" in e)

# —— 新增结构校验（真机修正项）——
se = read(os.path.join(ROOT, "..", "评分引擎", "src", "ScoringEngine", "ScoringEngine.cs"))
csproj_col = read("src/Collector/Collector.csproj")
csproj_se  = read(os.path.join(ROOT, "..", "评分引擎", "src", "ScoringEngine", "ScoringEngine.csproj"))
baseline   = read("src/Collector/baseline.json")

print("\n[真机修正 · 采集层]")
compat = read("src/Collector/Compat.cs")   # ★ 匹配规则在 Compat.cs，非 Collector.cs
check("★ VCore 弹性匹配含 VDDCR（SVI3 真机命名）", '"VDDCR"' in compat)
check("★ VCore 排除 SoC/Misc/LDO/SMU（防误采）",
      all(k in compat for k in ["SoC", "Misc", "LDO", "SMU"]))
check("★ NameRule 含 Exclude 字段（支持排除）", "Exclude" in compat)
check("★ AutoMatch 应用排除规则", "IsExcluded(e.Name)" in compat)
check("★ HardwareFeatures 含 FullLoadFreqMHz", "FullLoadFreqMHz" in c)
check("★ 满载判定用 FullLoadFreqMHz（非基础频率）",
      "main.FullLoadFreqMHz * CollectorOptions.FullLoadThreshold" in c)
check("★ 满载阈值 = 0.90", "FullLoadThreshold { get; set; } = 0.90" in c)
check("★ ToScoreInput（串起主链路）", "ToScoreInput()" in c)
check("多轮采样配置 SampleRounds/SampleIntervalMs",
      "SampleRounds" in c and "SampleIntervalMs" in c)

print("\n[真机修正 · 评分引擎]")
check("★ AnchorVoltage（V/F 线性折算）", "AnchorVoltage" in se)
check("★ CalcSpAnchored（折算后 SP）", "CalcSpAnchored" in se)
check("★ 折算是线性而非等比乘法", "(slopeMvPerMhz / 1000.0) *" in se)
check("★ BaselineResolver（三级参数解析）", "class BaselineResolver" in se)
check("★ ZenProfile（代际默认斜率）", "class ZenProfile" in se)
check("★ VfFit（最小二乘自标定）", "class VfFit" in se)
check("★ Calibration 标定等级", "Calibrated" in se and "Estimated" in se)
check("Baseline 含 MaxBoost/Anchor/Slope/CalibrationLevel",
      all(k in se for k in ["MaxBoostMHz", "AnchorFreqMHz", "SlopeMvPerMhz", "CalibrationLevel"]))
check("置信度随标定等级变化（0.6 / 0.4）", "? 0.6 : 0.4" in se)
check("百分位用标准正态 CDF（非错误线性）", "NormCdf" in se)

print("\n[工程与量表]")
check("★ 评分引擎有自己的 csproj（此前缺失致源码不参与编译）",
      '<TargetFramework>net8.0</TargetFramework>' in csproj_se)
check("★ Collector.csproj 引用评分引擎", "ScoringEngine.csproj" in csproj_col)
check("★ 7800X3D 为 Calibrated", '"CalibrationLevel": "Calibrated"' in baseline)
check("★ 7800X3D VRef = 1.070（GamersNexus 校准）", '"VRef": 1.070' in baseline)
check("★ 7800X3D 斜率 0.41 / 锚点 4800",
      '"SlopeMvPerMhz": 0.41' in baseline and '"AnchorFreqMHz": 4800' in baseline)
check("★ 7800X3D MaxBoost = 5000", '"MaxBoostMHz": 5000' in baseline)
check("Exporter 柱状图用本次 min/max 归一化（三参签名）",
      "Bar(double value, double min, double max)" in e and "Bar(c.VID.Value, lo, hi)" in e)
check("Exporter 标注每核 VID 为 CPPC 请求值", "CPPC" in e)
check("Exporter 含 Estimated 水印", "估计值" in e)

print(f"\n=== {P} passed, {F} failed ===")
os._exit(0 if F == 0 else 1)
