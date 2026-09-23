using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Collector;
using SiliconScope.UI.Models;
using SiliconScope.UI.Services;

namespace SiliconScope.UI.ViewModels;

/// <summary>环境自检项（展示用）</summary>
public sealed class EnvItem
{
    public string Name { get; set; } = "";
    public bool Ok { get; set; }
    public string Detail { get; set; } = "";
    public string Icon => Ok ? "✓" : "✕";
    public string Color => Ok ? "#3FB950" : "#F85149";
}

/// <summary>
/// 评测向导 VM：4 步（环境自检 → 等待满载 → 多点采样 → 完成）。
/// ★ 每步只做一件事，普通玩家无需理解 VDDCR 也能完成。
/// </summary>
public sealed class RunViewModel : ViewModelBase
{
    private readonly MainViewModel _main;

    public ObservableCollection<EnvItem> EnvItems { get; } = new();
    public ObservableCollection<(double freq, double volt)> LivePoints { get; } = new();

    private int _step;
    public int Step { get => _step; set { if (Set(ref _step, value)) RefreshCommands(); } }

    private string _tipTitle = "准备就绪";
    public string TipTitle { get => _tipTitle; set => Set(ref _tipTitle, value); }

    private string _tipDesc = "点击开始，程序会自动完成环境自检、满载判定与多点采样。";
    public string TipDesc { get => _tipDesc; set => Set(ref _tipDesc, value); }

    private bool _busy;
    public bool Busy { get => _busy; set { if (Set(ref _busy, value)) RefreshCommands(); } }

    // —— 实时读数（等待满载界面）——
    private double _liveFreq;
    public double LiveFreq { get => _liveFreq; set => Set(ref _liveFreq, value); }
    private double _liveVolt;
    public double LiveVolt { get => _liveVolt; set => Set(ref _liveVolt, value); }
    private double _liveTemp;
    public double LiveTemp { get => _liveTemp; set => Set(ref _liveTemp, value); }

    public string FullLoadTargetText =>
        $"门槛：全核频率 ≥ MaxBoost × {CollectorOptions.FullLoadThreshold:P0}";

    private bool _fullLoadReached;
    public bool FullLoadReached
    { get => _fullLoadReached; set { if (Set(ref _fullLoadReached, value)) RefreshCommands(); } }

    // —— 满载持续稳定确认持续满载约 1 分钟才自动采样，排除偶发冲高/刚满载）——
    private FullLoadStability? _stability;

    private double _stableSeconds;
    /// <summary>当前已稳定的满载秒数（0~StableTarget）。</summary>
    public double StableSeconds
    {
        get => _stableSeconds;
        set
        {
            if (Set(ref _stableSeconds, value))
            {
                Raise(nameof(StableRatio));
                Raise(nameof(StableText));
                Raise(nameof(StableSecondsText));
            }
        }
    }

    public double StableTarget => CollectorOptions.FullLoadHoldSeconds;
    public double StableRatio => StableTarget <= 0 ? 0 : Math.Min(1.0, StableSeconds / StableTarget);
    public string StableSecondsText => $"{StableSeconds:F0} / {StableTarget:F0} s";

    private bool _fullLoadStable;
    /// <summary>是否已持续满载达标（达标即自动采样，也才允许手动点）。</summary>
    public bool FullLoadStable
    { get => _fullLoadStable; private set { if (Set(ref _fullLoadStable, value)) RefreshCommands(); } }

    public string StableText
    {
        get
        {
            if (Busy || Step != 1) return "进入等待满载后开始稳定确认。";
            if (FullLoadStable) return "已持续满载，正在自动开始采样…";
            if (StableSeconds <= 0.01)
                return "请启动烤机并保持满载；需持续满载约 1 分钟（排除偶发冲高）后才会自动采样。";
            return $"满载稳定确认中：{StableSeconds:F0}/{StableTarget:F0} 秒（中途掉载会回退计时）。";
        }
    }

    // —— 采样进度 ——
    private int _done, _total;
    public int Done { get => _done; set => Set(ref _done, value); }
    public int Total { get => _total; set => Set(ref _total, value); }
    public string ProgressText => $"采样 {Done} / {Total} 轮";

    private DispatcherTimer? _timer;

    public RelayCommand StartCmd { get; }
    public RelayCommand ToLoadCmd { get; }
    public RelayCommand StartSampleCmd { get; }
    public RelayCommand CancelCmd { get; }
    public RelayCommand RestartCmd { get; }
    public RelayCommand ViewResultCmd { get; }

    public RunViewModel(MainViewModel main)
    {
        _main = main;
        StartCmd = new RelayCommand(DoEnvironment, () => !Busy && Step == 0);
        ToLoadCmd = new RelayCommand(() => Step = 1, () => !Busy && Step == 0);
        StartSampleCmd = new RelayCommand(DoSample,
            () => !Busy && Step == 1 && FullLoadStable);   // 必须持续满载稳定满 1 分钟才允许采样（与自动触发同一门槛）
        CancelCmd = new RelayCommand(StopLive, () => _timer != null);
        // 评测完成（Step==3）后可一键重测，不必重启软件
        RestartCmd = new RelayCommand(Restart, () => !Busy && Step == 3);
        ViewResultCmd = new RelayCommand(() => _main.Go(NavPage.Result), () => Step == 3);
    }

    private void RefreshCommands()
    {
        StartCmd.Notify(); ToLoadCmd.Notify(); StartSampleCmd.Notify(); CancelCmd.Notify();
        RestartCmd.Notify(); ViewResultCmd.Notify();
    }

    /// <summary>把向导完整复位到初始态（停定时器、清数据/稳定判定/进度），供“重新评测”使用。</summary>
    private void ResetWizard()
    {
        StopLive();
        EnvItems.Clear();
        LivePoints.Clear();
        _stability = null;
        StableSeconds = 0;
        FullLoadStable = false;
        FullLoadReached = false;
        Done = 0;
        Total = 0;
        TipTitle = "准备就绪";
        TipDesc = "点击开始，程序会自动完成环境自检、满载判定与多点采样。";
        Step = 0;
    }

    /// <summary>重新评测：复位后立即重新跑环境自检并进入等待满载，一键重测。</summary>
    private void Restart()
    {
        ResetWizard();
        DoEnvironment();
    }

    // ===== Step 1：环境自检 =====
    private void DoEnvironment()
    {
        Busy = true;
        TipTitle = "环境自检";
        TipDesc = "正在检查运行权限与驱动状态…";

        Task.Run(() =>
        {
            var checks = _main.Service.CheckEnvironment();
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                EnvItems.Clear();
                foreach (var c in checks)
                    EnvItems.Add(new EnvItem { Name = c.Name, Ok = c.Ok, Detail = c.Detail });

                bool allOk = checks.TrueForAll(c => c.Ok);
                Busy = false;
                if (allOk)
                {
                    Step = 1;
                    TipTitle = "等待满载负载";
                    TipDesc = "请运行烤机工具，让 CPU 进入满载稳态。";
                    StartLive();
                }
                else
                {
                    Step = 0;
                    TipTitle = "环境自检未通过";
                    TipDesc = "请按下方提示修复后重试；不满足条件时不会给出任何分数。";
                }
            });
        });
    }

    // ===== Step 2：等待满载（实时读数 + 持续稳定确认）=====
    private void StartLive()
    {
        StopLive();
        _stability = new FullLoadStability(
            TimeSpan.FromSeconds(CollectorOptions.FullLoadHoldSeconds),
            CollectorOptions.FullLoadHoldRatio);
        StableSeconds = 0;
        FullLoadStable = false;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _timer.Tick += (_, _) =>
        {
            var f = _main.Service.Peek();
            if (f is null) return;
            LiveFreq = f.FrequencyMHz;
            LiveVolt = f.VCore;
            LiveTemp = f.TemperatureC;

            // 瞬时满载（状态灯）与“持续满载稳定”（滑窗去抖）分开判定
            bool loaded = f.FrequencyMHz >= f.FullLoadFreqMHz * CollectorOptions.FullLoadThreshold;
            FullLoadReached = loaded;

            var now = DateTime.Now;
            _stability.Add(now, loaded);
            StableSeconds = _stability.StableSeconds(now);

            if (!Busy && Step == 1 && _stability.IsStable(now))
            {
                FullLoadStable = true;
                DoSample();   // 持续满载约 1 分钟且占比达标 → 自动开始采样
            }
            else
            {
                FullLoadStable = false;
            }
        };
        _timer.Start();
        RefreshCommands();
    }

    private void StopLive()
    {
        _timer?.Stop();
        _timer = null;
        RefreshCommands();
    }

    // ===== Step 3：多点采样 + 评分 =====
    private void DoSample()
    {
        StopLive();
        Step = 2;
        Busy = true;
        LivePoints.Clear();
        Total = Math.Max(1, CollectorOptions.SampleRounds);
        Done = 0;
        TipTitle = "多点采样中";
        TipDesc = $"正在进行约 {CollectorOptions.SampleDurationMs / 1000} 秒稳态采样（频率、电压），用于拟合这颗 CPU 的 V/F 曲线，期间请保持烤机满载…";

        var progress = new Progress<(int done, int total, double freq, double volt)>(p =>
        {
            Done = p.done; Total = p.total;
            LivePoints.Add((p.freq, p.volt));
            Raise(nameof(ProgressText));
        });

        Task.Run(() =>
        {
            var (result, error) = _main.Service.Evaluate(progress);
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                Busy = false;
                if (result is null)
                {
                    Step = 1;
                    TipTitle = "无法评分";
                    TipDesc = error ?? "未知原因";
                    StartLive();
                    return;
                }
                Step = 3;
                TipTitle = "评测完成";
                TipDesc = $"SP {result.SpScore:F1} · {result.Grade}";
                _main.OnEvaluated(result);
            });
        });
    }
}
