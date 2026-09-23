// Logger.cs — 结构化日志记录（供真机排查问题）
// 设计决策：A/A → 程序同级 logs/ 目录；每次采集自动生成诊断报告。
//
// 设计要点：
//   1) 结构化：每条日志 = 时间戳 + 级别 + 上下文(context) + 消息 + 异常。
//      context 用 dictionary 携带（model/zen/source/missing 等），便于按字段检索，
//      而非把变量塞进一句话里（那叫"拼接日志"，排查时无法过滤）。
//   2) 双输出：Console（实时观察）+ 滚动文件（持久排查）。
//   3) 分级开关：Release 可关 Verbose，避免采样循环刷盘。
//   4) 线程安全：采集采样可能在多线程，内部用 lock 串行化写盘。
//   5) 生命周期：进程级单例（Logger.Instance），任意模块可写。
//
// ★ 用法：
//     Logger.Instance.Info("采集开始", new { model = "7800X3D" });
//     Logger.Instance.Warn("字段缺失", new { missing = new[] { "VCore" } });
//     Logger.Instance.Error("采集失败", ex, new { source = "LHM" });

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Collector;

/// <summary>日志级别（严重程度递增）</summary>
public enum LogLevel
{
    Verbose = 0,  // 采样级细节（每核逐点），默认关闭
    Info    = 1,  // 正常流程节点
    Warn    = 2,  // 可恢复异常（字段缺失、降级）
    Error   = 3,  // 不可恢复（采集失败、质控拒绝）
}

/// <summary>结构化日志条目（可序列化为 JSON 行）</summary>
public sealed record LogEntry(
    DateTime TimestampUtc,
    string Level,
    string Message,
    IReadOnlyDictionary<string, object?>? Context,
    string? ExceptionType,
    string? ExceptionMessage,
    string? StackTrace);

/// <summary>日志记录器（进程级单例）</summary>
public sealed class Logger
{
    private static readonly Lazy<Logger> _lazy = new(() => new Logger());
    public static Logger Instance => _lazy.Value;

    // —— 配置（可由 JSON 配置覆盖，见 CollectorOptions）——
    public LogLevel MinLevel { get; set; } = LogLevel.Info;
    public bool ConsoleEnabled { get; set; } = true;
    public bool FileEnabled { get; set; } = true;

    private readonly object _lock = new();
    private readonly StringBuilder _buffer = new(); // 内存缓冲（供导出诊断包）
    private string? _logDir;
    private string? _currentFile;

    private Logger() { }

    /// <summary>初始化日志目录（程序启动时调用一次）</summary>
    public void Configure(string? baseDir = null, LogLevel minLevel = LogLevel.Info)
    {
        MinLevel = minLevel;
        // ★ 决策 Q1=A：程序同级目录 logs/（便携优先，单机自用）
        //   可移植场景（U 盘/免安装）必须用相对路径，不能用 %LocalAppData%。
        var root = string.IsNullOrWhiteSpace(baseDir)
            ? AppContext.BaseDirectory
            : baseDir;
        _logDir = Path.Combine(root, "logs");
        Directory.CreateDirectory(_logDir);

        // 滚动文件：每天一个（YYYYMMDD.log），天然按天切分，无需大小判断
        var date = DateTime.Now.ToString("yyyyMMdd");
        _currentFile = Path.Combine(_logDir, $"{date}.log");
    }

    // ★ Write 签名统一为 (level, msg, context, exception)，所有公开方法保持一致，
    //   避免 Error(msg, ex, ctx) 因参数顺序错位而把异常当上下文（这是验证脚本曾踩过的坑）。
    public void Verbose(string msg, object? context = null) => Write(LogLevel.Verbose, msg, context, null);
    public void Info   (string msg, object? context = null) => Write(LogLevel.Info,    msg, context, null);
    public void Warn   (string msg, object? context = null) => Write(LogLevel.Warn,    msg, context, null);

    public void Error(string msg, Exception? exception = null, object? context = null)
        => Write(LogLevel.Error, msg, context, exception);

    private void Write(LogLevel level, string msg, object? context, Exception? ex)
    {
        if (level < MinLevel) return;

        var ctxDict = context switch
        {
            null => null,
            // 匿名对象 → dictionary（便于 JSON 序列化 + 按 key 检索）
            _ => context.GetType().GetProperties()
                  .ToDictionary(p => p.Name, p => p.GetValue(context) as object)
        };

        var entry = new LogEntry(
            TimestampUtc: DateTime.UtcNow,
            Level: level.ToString().ToUpperInvariant(),
            Message: msg,
            Context: ctxDict,
            ExceptionType: ex?.GetType().FullName,
            ExceptionMessage: ex?.Message,
            StackTrace: ex?.StackTrace);

        var line = FormatLine(entry);

        lock (_lock)
        {
            _buffer.AppendLine(line);
            if (ConsoleEnabled) Console.WriteLine(line);
            if (FileEnabled && _logDir is not null) AppendToFile(line);
        }
    }

    /// <summary>格式化单行（人类可读；同时保留 JSON 便于程序解析）</summary>
    private static string FormatLine(LogEntry e)
    {
        var sb = new StringBuilder();
        sb.Append($"[{e.TimestampUtc:yyyy-MM-ddTHH:mm:ss.fffZ}] [{e.Level}] {e.Message}");
        if (e.Context is { Count: > 0 })
            sb.Append($" {JsonSerializer.Serialize(e.Context)}");
        if (e.ExceptionType is not null)
            sb.Append($" | EX={e.ExceptionType}: {e.ExceptionMessage}");
        return sb.ToString();
    }

    private void AppendToFile(string line)
    {
        try
        {
            // ★ 滚动切换：跨天后自动切到新文件（无需外部定时器）
            var expected = Path.Combine(_logDir!, $"{DateTime.Now:yyyyMMdd}.log");
            if (_currentFile != expected)
            {
                _currentFile = expected;
            }
            File.AppendAllText(_currentFile, line + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // ★ 日志写盘失败绝不能影响主流程（这是护栏，不是业务）
            // 仅回退到控制台，不做二次抛异常
            if (ConsoleEnabled) Console.WriteLine($"[LOG-FAIL] {line}");
        }
    }

    /// <summary>导出内存缓冲（供诊断包打包）</summary>
    internal string GetBuffer() { lock (_lock) return _buffer.ToString(); }

    /// <summary>最近 N 行（UI 实时显示用，避免直接暴露 buffer）</summary>
    public string[] Tail(int n = 50)
    {
        lock (_lock)
        {
            return _buffer.ToString()
                .Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries)
                .TakeLast(n)
                .ToArray();
        }
    }
}
