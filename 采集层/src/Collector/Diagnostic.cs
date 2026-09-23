// Diagnostic.cs — 单次采集诊断报告（JSON 落盘，定位命名/匹配/质控问题）
// 设计决策：日志=过程流水，诊断报告=单次采集快照，二者解耦。
//
// ★ 一份诊断报告包含排查所需的一切，可脱离程序独立分析：
//     · 元信息：时间、程序版本、环境（OS/.NET/管理员/核心隔离）
//     · 传感器原始 dump：ALL sensor (hw/type/name/value) —— 定位命名问题的关键
//     · 字段匹配结果：逻辑字段 → 实际原始名 → 采样值 → 是否命中
//     · 环境自检、合理性校验、质控门禁、满载判定结果
//     · 最终评分 + 降级原因（如有）
//
//   用户出问题时，把整个 reports/ 目录或打包后的 zip 发来即可复现，
//   无需再问"你看到了什么"——这正是探测模式 + 日志体系的价值闭环。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Collector;

/// <summary>单次采集的完整诊断快照（不可变）</summary>
public sealed record DiagnosticReport(
    Guid ReportId,
    DateTime GeneratedAt,
    string AppVersion,
    EnvironmentInfo Environment,
    IReadOnlyList<SensorDump> SensorDump,       // ★ 全部原始传感器（排查命名的核心）
    IReadOnlyDictionary<string, FieldResolution> FieldResolution, // 逻辑字段 → 原始名
    IReadOnlyList<string> MissingFields,
    ValidationResults Validation,
    ScoreSnapshot? Score,                        // 最终评分（如有）
    IReadOnlyList<string> Errors,
    double DurationMs)
{
    public DiagnosticReport() : this(
        Guid.NewGuid(), DateTime.UtcNow, "unknown",
        new EnvironmentInfo(), new List<SensorDump>(),
        new Dictionary<string, FieldResolution>(), new List<string>(),
        new ValidationResults(), null, new List<string>(), 0) { }
}

public sealed record SensorDump(string Hardware, string SensorType, string Name, double? Value);

/// <summary>单个逻辑字段的解析结果</summary>
public sealed record FieldResolution(
    string LogicalName,
    string? MatchedSensorName,   // 命中了哪个原始传感器名（null=未命中）
    double? SampleValue,
    bool Resolved,
    string? Rule);              // 使用的匹配规则（精确名/包含关键词）

public sealed record EnvironmentInfo(
    bool IsAdministrator = false,
    bool CoreIsolationOff = false,      // 核心隔离是否关闭（LHM 电压归零的根因）
    string OsDescription = "",
    string DotNetVersion = "",
    bool LhmAvailable = false);         // LHM 库是否可用

public sealed record ValidationResults(
    bool EnvironmentOk = false,         // 环境自检
    bool RationalityOk = false,        // 合理性校验（数值区间）
    bool FullLoadOk = false,           // 满载判定
    bool QualityGateOk = false,        // 质控门禁
    string? RejectReason = null);     // 首个拒绝原因（如有）

/// <summary>评分快照（供报告记录最终结果）</summary>
public sealed record ScoreSnapshot(
    string Mode, double SpScore, double Percentile, string Grade,
    double Confidence, string? BestCore, string? WorstCore, string ReferenceNote);

/// <summary>诊断报告构建器（采集流程逐步填充）</summary>
public sealed class DiagnosticBuilder
{
    private readonly List<SensorDump> _sensors = new();
    private readonly Dictionary<string, FieldResolution> _fields = new();
    private readonly List<string> _errors = new();
    private EnvironmentInfo _env = new();
    private ValidationResults _validation = new();
    private ScoreSnapshot? _score;

    public DiagnosticBuilder AddSensor(string hw, string type, string name, double? value)
    {
        _sensors.Add(new SensorDump(hw, type, name, value));
        return this;
    }

    public DiagnosticBuilder AddSensors(IEnumerable<SensorDump> sensors)
    {
        _sensors.AddRange(sensors);
        return this;
    }

    public DiagnosticBuilder ResolveField(string logical, string? matchedName, double? value, string? rule)
    {
        _fields[logical] = new FieldResolution(logical, matchedName, value,
            Resolved: matchedName is not null, Rule: rule);
        return this;
    }

    public DiagnosticBuilder WithEnvironment(EnvironmentInfo env)
    {
        _env = env;
        return this;
    }

    public DiagnosticBuilder WithValidation(ValidationResults v)
    {
        _validation = v;
        return this;
    }

    public DiagnosticBuilder WithScore(ScoreSnapshot score)
    {
        _score = score;
        return this;
    }

    public DiagnosticBuilder AddError(string msg)
    {
        _errors.Add(msg);
        return this;
    }

    public DiagnosticReport Build()
    {
        // ★ 缺失字段由"未解析的逻辑字段"自动推导（单一来源，避免两处不一致）
        var missing = _fields.Values.Where(f => !f.Resolved).Select(f => f.LogicalName).ToList();

        return new DiagnosticReport(
            ReportId: Guid.NewGuid(),
            GeneratedAt: DateTime.UtcNow,
            AppVersion: ThisAssemblyVersion(),
            Environment: _env,
            SensorDump: _sensors,
            FieldResolution: _fields,
            MissingFields: missing,
            Validation: _validation,
            Score: _score,
            Errors: _errors,
            DurationMs: 0);
    }

    private static string ThisAssemblyVersion()
    {
        // 取 entry assembly 版本，缺省则 "dev"；失败不抛
        try
        {
            var asm = System.Reflection.Assembly.GetEntryAssembly();
            return asm?.GetName()?.Version?.ToString() ?? "dev";
        }
        catch { return "dev"; }
    }
}

/// <summary>诊断报告持久化（落盘 + 保留策略 + 打包）</summary>
public static class DiagnosticStore
{
    // ★ 决策 Q2=A：每次采集自动生成，保留最近 10 份（防磁盘膨胀）
    public const int RetainCount = 10;

    public static string Save(DiagnosticReport report, string? baseDir = null)
    {
        var dir = EnsureReportDir(baseDir);
        // 文件名含时间戳 + 报告 ID 前 6 位（唯一、可按时间排序）
        var stamp = report.GeneratedAt.ToLocalTime().ToString("yyyyMMdd_HHmmss");
        var path = Path.Combine(dir, $"diagnostic_{stamp}_{report.ReportId:N[..6]}.json");

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        File.WriteAllText(path, json);
        Logger.Instance.Info("诊断报告已落盘", new { path, sensorCount = report.SensorDump.Count });
        return path;
    }

    /// <summary>保留最近 N 份，删除更旧的（自动清理）</summary>
    public static void RetainLatest(string? baseDir = null, int keep = RetainCount)
    {
        var dir = EnsureReportDir(baseDir);
        var files = new DirectoryInfo(dir)
            .GetFiles("diagnostic_*.json")
            .OrderByDescending(f => f.CreationTime) // 按时间，新→旧
            .ToList();
        foreach (var old in files.Skip(keep))
        {
            try { old.Delete(); } catch { /* 占用中则跳过，下次再试 */ }
        }
    }

    /// <summary>打包诊断包（日志 + 最新报告 + 缓存 → zip）</summary>
    public static string PackDiagnosticBundle(string? baseDir = null)
    {
        var root = string.IsNullOrWhiteSpace(baseDir) ? AppContext.BaseDirectory : baseDir;
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var bundleDir = Path.Combine(root, "诊断包");
        Directory.CreateDirectory(bundleDir);
        var target = Path.Combine(bundleDir, $"诊断包_{stamp}");
        Directory.CreateDirectory(target);

        // 1) 日志（整个 logs/ 目录）
        var logDir = Path.Combine(root, "logs");
        if (Directory.Exists(logDir))
            CopyDir(logDir, Path.Combine(target, "logs"));

        // 2) 报告（整个 reports/ 目录，含本次最新）
        var reportDir = EnsureReportDir(root);
        if (Directory.Exists(reportDir))
            CopyDir(reportDir, Path.Combine(target, "reports"));

        // 3) 缓存（传感器映射 + 探测状态）
        var cacheFile = Path.Combine(root, "sensor_cache.json");
        if (File.Exists(cacheFile))
            File.Copy(cacheFile, Path.Combine(target, "sensor_cache.json"), overwrite: true);

        // 4) 汇总说明（人类可读入口）
        File.WriteAllText(Path.Combine(target, "README.txt"),
            $"CPU体质评分 诊断包\n生成时间：{DateTime.Now}\n\n" +
            "目录结构：\n  logs/    - 运行日志（按天切分）\n" +
            "  reports/ - 单次采集诊断报告（JSON）\n" +
            "  sensor_cache.json - 传感器映射缓存\n\n" +
            "请将整个 诊断包_*/ 目录发给开发者定位问题。");

        // 打包为 zip（.NET 自带，无需第三方）
        var zip = Path.Combine(bundleDir, $"诊断包_{stamp}.zip");
        if (File.Exists(zip)) File.Delete(zip);
        System.IO.Compression.ZipFile.CreateFromDirectory(target, zip);

        // 打包后清理临时目录（保留 zip）
        try { Directory.Delete(target, recursive: true); } catch { }

        Logger.Instance.Info("诊断包已打包", new { zip });
        return zip;
    }

    private static string EnsureReportDir(string? baseDir)
    {
        var root = string.IsNullOrWhiteSpace(baseDir) ? AppContext.BaseDirectory : baseDir;
        var dir = Path.Combine(root, "reports");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void CopyDir(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), overwrite: true);
    }
}
