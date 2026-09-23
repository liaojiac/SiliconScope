using System;
using System.Globalization;
using System.Windows;
using Collector;

namespace SiliconScope.UI.ViewModels;

/// <summary>
/// 设置 VM：数据源 / 评分 / 日志隐私。
/// ★ 每项都带说明文字 —— 普通用户看不懂"探测模式"这四个字。
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly MainViewModel _main;

    public bool EnableHwinfo
    { get => _main.Settings.EnableHwinfo; set { _main.Settings.EnableHwinfo = value; Save(); } }

    public string ProbeMode
    { get => _main.Settings.ProbeMode; set { _main.Settings.ProbeMode = value; Save(); } }

    public bool MultiSample
    { get => _main.Settings.MultiSample; set { _main.Settings.MultiSample = value; Save(); } }

    public double FullLoadThreshold
    { get => _main.Settings.FullLoadThreshold; set { _main.Settings.FullLoadThreshold = value; Save(); } }

    public bool SelfFit
    { get => _main.Settings.SelfFit; set { _main.Settings.SelfFit = value; Save(); } }

    public string LogLevel
    { get => _main.Settings.LogLevel; set { _main.Settings.LogLevel = value; Save(); } }

    public int RetainReports
    { get => _main.Settings.RetainReports; set { _main.Settings.RetainReports = value; Save(); } }

    // ★ ComboBox 的 Tag 在 XAML 里是字符串，直接绑 double/int 会因类型不同导致初始不回显选中项，
    //   用字符串包装属性做稳定双向转换（InvariantCulture，避免小数点受区域设置影响）。
    public string FullLoadThresholdText
    {
        get => FullLoadThreshold.ToString("0.00", CultureInfo.InvariantCulture);
        set
        {
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                && d is > 0.5 and < 1.0)
                FullLoadThreshold = d;
        }
    }

    public string RetainReportsText
    {
        get => RetainReports.ToString(CultureInfo.InvariantCulture);
        set
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                && n is > 0 and <= 200)
                RetainReports = n;
        }
    }

    public RelayCommand OpenLogCmd { get; }
    public RelayCommand OpenReportCmd { get; }

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        OpenLogCmd = new RelayCommand(() => OpenFolder("logs"));
        OpenReportCmd = new RelayCommand(() => OpenFolder("reports"));
    }

    private void Save()
    {
        _main.Settings.Apply();       // ★ UI 改动 → 核心生效的唯一通道
        _main.Settings.Save();
    }

    private static void OpenFolder(string sub)
    {
        var dir = System.IO.Path.Combine(AppContext.BaseDirectory, sub);
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start("explorer.exe", dir);
        }
        catch (Exception ex)
        {
            Logger.Instance.Error("打开目录失败", ex);
        }
    }
}
