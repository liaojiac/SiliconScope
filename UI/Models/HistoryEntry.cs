using System;
using System.Collections.Generic;

namespace SiliconScope.UI.Models;

/// <summary>历史成绩持久化 DTO（JSON 友好：用 double[][] / 普通 record 替代值元组）。</summary>
public sealed class HistoryEntry
{
    public string Id { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public DateTime Time { get; set; }

    public string Model { get; set; } = "";
    public string Zen { get; set; } = "";
    public double VCore { get; set; }
    public double FreqMHz { get; set; }
    public double ReferenceFreqMHz { get; set; }
    public double FullLoadFreqMHz { get; set; }
    public double TempC { get; set; }
    public string DataSource { get; set; } = "";

    public double SpScore { get; set; }
    public double Percentile { get; set; }
    public string Grade { get; set; } = "";
    public string GradeColor { get; set; } = "#58A6FF";
    public string Mode { get; set; } = "";
    public double Confidence { get; set; }
    public string Note { get; set; } = "";

    public double VAnchor { get; set; }
    public double AnchorFreqMHz { get; set; }
    public double SlopeMvPerMhz { get; set; }
    public double VRef { get; set; }
    public string SlopeSource { get; set; } = "";
    public string AnchorSource { get; set; } = "";
    public string CalibrationLevel { get; set; } = "Estimated";

    public double[]? PerCoreVIDs { get; set; }
    public int BestCoreIndex { get; set; } = -1;
    public int WorstCoreIndex { get; set; } = -1;

    /// <summary>每个元素 [频率MHz, 电压V]。</summary>
    public double[][]? VfPoints { get; set; }
    public List<EnvCheckDto>? EnvChecks { get; set; }
}

public sealed record EnvCheckDto(string Name, bool Ok, string Detail);
