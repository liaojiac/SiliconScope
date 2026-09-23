using System;
using System.IO;
using System.Linq;
using System.Windows;
using ScoringEngine;
using SiliconScope.UI.Models;
using SiliconScope.UI.Services;

namespace SiliconScope.UI.ViewModels;

/// <summary>评分详情 VM：展示 SP、V/F 折算、每核分布、参数来源，并支持导出</summary>
public sealed class ResultViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private EvaluationResult? _r;

    public bool HasResult => _r != null;
    public bool NoResult => _r == null;

    public double SpScore => _r?.SpScore ?? 0;
    public string Grade => _r?.Grade ?? "";
    public string GradeColor => _r?.GradeColor ?? "#58A6FF";
    public string Model => _r?.Model ?? "";
    public string Zen => _r?.Zen ?? "";
    public string Mode => _r?.Mode ?? "";
    public string Note => _r?.Note ?? "";
    public bool IsEstimated => _r?.IsEstimated ?? false;

    public string HardwareLine => _r is null ? "" :
        $"{_r.Zen} · 稳态 {_r.FreqMHz:F0}MHz @ {_r.VCore:F3}V · {_r.TempC:F0}℃ · 数据源 {_r.DataSource}";

    public string AnchorLine => _r is null ? "" :
        $"折算锚点 {_r.AnchorFreqMHz:F0}MHz · V/F 斜率 {_r.SlopeMvPerMhz:F2} mV/MHz · 折算后电压 {_r.VAnchor:F4}V";

    public double VAnchor => _r?.VAnchor ?? 0;
    public double AnchorFreq => _r?.AnchorFreqMHz ?? 4800;
    public double Slope => _r?.SlopeMvPerMhz ?? 0.41;
    public double VRef => _r?.VRef ?? 0;
    public string SlopeSource => _r?.SlopeSource ?? "";
    public string AnchorSource => _r?.AnchorSource ?? "";
    public string CalibrationText => IsEstimated ? "估计值 Estimated" : "已标定 Calibrated";
    public string ConfidenceText => _r is null ? "" : $"{_r.Confidence:P0}";

    public System.Collections.Generic.List<(double freq, double volt)> VfPoints
        => _r?.VfPoints ?? new();
    public double[]? Vids => _r?.PerCoreVIDs;
    public bool HasCores => _r?.PerCoreVIDs is { Length: > 0 };
    public bool NoCores => _r is not null && _r.PerCoreVIDs is not { Length: > 0 };

    public RelayCommand ExportCmd { get; }
    public RelayCommand BundleCmd { get; }

    public ResultViewModel(MainViewModel main)
    {
        _main = main;
        ExportCmd = new RelayCommand(DoExport, () => HasResult);
        BundleCmd = new RelayCommand(DoBundle, () => HasResult);
    }

    public void Load(EvaluationResult r)
    {
        _r = r;
        Raise(nameof(HasResult)); Raise(nameof(NoResult));
        Raise(nameof(SpScore)); Raise(nameof(Grade)); Raise(nameof(GradeColor));
        Raise(nameof(Model)); Raise(nameof(Zen)); Raise(nameof(Mode)); Raise(nameof(Note));
        Raise(nameof(IsEstimated)); Raise(nameof(HardwareLine)); Raise(nameof(AnchorLine));
        Raise(nameof(VAnchor)); Raise(nameof(AnchorFreq)); Raise(nameof(Slope));
        Raise(nameof(VRef)); Raise(nameof(SlopeSource)); Raise(nameof(AnchorSource));
        Raise(nameof(CalibrationText)); Raise(nameof(ConfidenceText));
        Raise(nameof(VfPoints)); Raise(nameof(Vids)); Raise(nameof(HasCores)); Raise(nameof(NoCores));
        ExportCmd.Notify(); BundleCmd.Notify();
    }

    private void DoExport()
    {
        if (_r is null) return;
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "导出");
            Directory.CreateDirectory(dir);
            // 复用评分引擎的导出器（JSON + Markdown 双格式）
            var input = new ScoreInput(_r.Model, _r.Zen, _r.VCore, _r.FreqMHz,
                _r.ReferenceFreqMHz, _r.TempC, _r.PerCoreVIDs, null);
            var baseline = new Baseline(_r.Model, _r.Zen, _r.VRef, 0, 0,
                AnchorFreqMHz: _r.AnchorFreqMHz, SlopeMvPerMhz: _r.SlopeMvPerMhz,
                CalibrationLevel: _r.CalibrationLevel);
            var sr = new ScoreResult(_r.Mode, _r.SpScore, _r.Percentile, _r.Grade,
                _r.Confidence, _r.Note,
                _r.BestCoreIndex >= 0 ? $"Core#{_r.BestCoreIndex}" : null,
                _r.WorstCoreIndex >= 0 ? $"Core#{_r.WorstCoreIndex}" : null);
            var report = Exporter.Build(input, baseline, sr,
                string.IsNullOrWhiteSpace(_r.AppVersion) ? SiliconScope.UI.AppInfo.Version : _r.AppVersion);
            var (json, md) = Exporter.ExportBoth(report, dir);
            MessageBox.Show($"已导出：\n{json}\n{md}", "导出成功",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Collector.Logger.Instance.Error("导出失败", ex);
            MessageBox.Show($"导出失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DoBundle()
    {
        try
        {
            var zip = Collector.DiagnosticStore.PackDiagnosticBundle();
            MessageBox.Show($"诊断包已生成：\n{zip}\n\n出问题时把它发给开发者即可定位。",
                "诊断包", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Collector.Logger.Instance.Error("打包诊断包失败", ex);
            MessageBox.Show($"打包失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
