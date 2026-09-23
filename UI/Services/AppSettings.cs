using System;
using System.IO;
using System.Text.Json;
using Collector;

namespace SiliconScope.UI.Services;

/// <summary>
/// 应用设置（持久化到 settings.json，程序同级目录）。
/// ★ 界面可选项全部集中在此，便于与采集层 CollectorOptions 同步。
/// </summary>
public sealed class AppSettings
{
    public bool EnableHwinfo { get; set; } = true;       // HWiNFO 增强默认开：自动探测共享内存，没有则静默回退 LHM）
    public string ProbeMode { get; set; } = "AutoOnFirstRun";
    public bool MultiSample { get; set; } = true;        // 多轮采样（约 10s 稳态窗口）
    public double FullLoadThreshold { get; set; } = 0.90; // 满载阈值
    public bool SelfFit { get; set; } = false;           // 自标定 V/F 拟合
    public string LogLevel { get; set; } = "Info";
    public int RetainReports { get; set; } = 10;
    public int RetainHistory { get; set; } = 100;        // 本机历史成绩保留份数

    private static string Path => System.IO.Path.Combine(
        AppContext.BaseDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(Path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path)) ?? new()
                : new();
        }
        catch (Exception ex)
        {
            Logger.Instance.Warn("设置读取失败，使用默认值", new { msg = ex.Message });
            return new();
        }
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(Path, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Logger.Instance.Error("设置保存失败", ex);
        }
    }

    /// <summary>把设置应用到采集层（UI 改动 → 核心生效的唯一通道）</summary>
    public void Apply()
    {
        // 采样窗口约 10s（500ms × 20 轮）；关闭多轮采样时只采 1 轮。
        CollectorOptions.SampleIntervalMs = 500;
        CollectorOptions.SampleDurationMs = 10_000;
        CollectorOptions.SampleRounds = MultiSample ? 0 : 1;   // 0 = 由 时长/间隔 自动推导
        CollectorOptions.FullLoadThreshold = FullLoadThreshold;
        CollectorOptions.DiagnosticRetainCount = RetainReports;
        CollectorOptions.LogMinLevel = Enum.TryParse<Collector.LogLevel>(LogLevel, out var lv)
            ? lv : Collector.LogLevel.Info;
    }
}
