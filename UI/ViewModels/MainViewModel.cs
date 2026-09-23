using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using Collector;
using SiliconScope.UI.Models;
using SiliconScope.UI.Services;

namespace SiliconScope.UI.ViewModels;

/// <summary>导航页</summary>
public enum NavPage { Run, Result, History, Settings }

/// <summary>
/// 主窗口 VM：负责导航 + 持有全局共享状态（评测结果、设置、服务）。
/// ★ 三个页面共用同一个 EvaluationResult，评测完成后自动跳转结果页。
/// </summary>
public sealed class MainViewModel : ViewModelBase
{
    public AppSettings Settings { get; }
    public EvaluationService Service { get; }

    public RunViewModel Run { get; }
    public ResultViewModel Result { get; }
    public HistoryViewModel History { get; }
    public SettingsViewModel Setting { get; }

    private NavPage _page = NavPage.Run;
    public NavPage Page
    {
        get => _page;
        set
        {
            if (_page == value) return;
            _page = value;
            Raise();
            Raise(nameof(IsRun)); Raise(nameof(IsResult));
            Raise(nameof(IsHistory)); Raise(nameof(IsSettings));
        }
    }

    public bool IsRun => Page == NavPage.Run;
    public bool IsResult => Page == NavPage.Result;
    public bool IsHistory => Page == NavPage.History;
    public bool IsSettings => Page == NavPage.Settings;

    private string _status = "就绪";
    public string Status { get => _status; set => Set(ref _status, value); }

    private bool _sensorReady;
    public bool SensorReady { get => _sensorReady; set => Set(ref _sensorReady, value); }

    public MainViewModel()
    {
        // 全局唯一设置实例：设置页改的就是 Service 用的那份
        Settings = AppSettings.Load();
        Service = new EvaluationService(Settings);

        Run = new RunViewModel(this);
        Result = new ResultViewModel(this);
        History = new HistoryViewModel(this);
        Setting = new SettingsViewModel(this);

        // 构造阶段【绝不】打开传感器/驱动。
        //   原先在这里同步调用 CheckEnvironment()→Collect()→Computer.Open()（加载 PawnIO 驱动），
        //   而此构造运行在主窗口创建早期、全局异常兜底尚未注册，内核隔离拦截驱动时会直接闪退
        //   （"一闪而过"、无日志无弹窗）。现改为窗口 Loaded 后在后台线程探测（见 StartBackgroundProbe）。
        SensorReady = false;
        Status = "正在检测运行环境…";
    }

    /// <summary>
    /// 窗口显示后在后台线程做一次完整环境自检（含打开 LHM/PawnIO 驱动）。
    /// 任何失败都只更新状态，绝不拖垮启动；用户点"开始评测"时还会再检一次。
    /// </summary>
    public void StartBackgroundProbe()
    {
        Task.Run(() =>
        {
            bool ready;
            try
            {
                var checks = Service.CheckEnvironment();
                ready = checks.TrueForAll(c => c.Ok);
            }
            catch (Exception ex)
            {
                Logger.Instance.Warn("启动环境探测异常", new { msg = ex.Message });
                ready = false;
            }

            Application.Current?.Dispatcher.Invoke(() =>
            {
                SensorReady = ready;
                Status = ready ? "传感器就绪" : "环境自检未通过（点「开始」查看详情）";
            });
        });
    }

    public void Go(NavPage page)
    {
        Page = page;
        if (page == NavPage.History) History.Refresh();   // 每次进入历史页都重新扫描
    }

    /// <summary>评测完成 → 写入结果、落本地历史、跳转结果页</summary>
    public void OnEvaluated(EvaluationResult r)
    {
        // 自动持久化到本机 history/（失败不影响结果展示）
        try
        {
            r.AppVersion = AppInfo.Version;
            var store = new HistoryStore(Settings.RetainHistory);
            var entry = store.Save(r);
            if (entry is not null) r.Id = entry.Id;
        }
        catch (Exception ex)
        {
            Logger.Instance.Warn("历史成绩落盘异常（不影响本次结果）", new { msg = ex.Message });
        }

        Result.Load(r);
        Page = NavPage.Result;
        Status = $"评测完成：SP {r.SpScore:F1} · {r.Grade}";
    }
}
