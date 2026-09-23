using System;
using System.Collections.Generic;

namespace SiliconScope.UI.Models;

/// <summary>一次完整评测的结果（UI 展示用）</summary>
public sealed class EvaluationResult
{
    /// <summary>历史记录唯一 Id（时间戳格式，评测完成时生成）；仅本次会话未保存时可为空。</summary>
    public string Id { get; set; } = "";
    public string AppVersion { get; set; } = "";

    public DateTime Time { get; set; } = DateTime.Now;

    public string Model { get; set; } = "";
    public string Zen { get; set; } = "";
    public double VCore { get; set; }
    public double FreqMHz { get; set; }
    public double ReferenceFreqMHz { get; set; }   // 基础频率（仅展示）
    public double FullLoadFreqMHz { get; set; }    // 标称全核 MaxBoost（满载判定基准）
    public double TempC { get; set; }
    public double? PowerW { get; set; }
    public string LoadSource { get; set; } = "未知";
    public string DataSource { get; set; } = "LHM";

    public double SpScore { get; set; }
    public double Percentile { get; set; }
    public string Grade { get; set; } = "普通";
    public string GradeColor { get; set; } = "#58A6FF";
    public string Mode { get; set; } = "OfflineAbsolute";
    public double Confidence { get; set; }
    public string Note { get; set; } = "";

    public double VAnchor { get; set; }
    public double AnchorFreqMHz { get; set; }
    public double SlopeMvPerMhz { get; set; }
    public double VRef { get; set; }
    public string SlopeSource { get; set; } = "";
    public string AnchorSource { get; set; } = "";
    public string CalibrationLevel { get; set; } = "Estimated";
    public bool IsEstimated => CalibrationLevel == "Estimated";

    public double[]? PerCoreVIDs { get; set; }
    public int BestCoreIndex { get; set; } = -1;
    public int WorstCoreIndex { get; set; } = -1;

    public List<(double freq, double volt)> VfPoints { get; set; } = new();
    public List<(string name, bool ok, string detail)> EnvironmentChecks { get; set; } = new();
}
