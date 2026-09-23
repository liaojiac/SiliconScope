using System;
using System.Collections.ObjectModel;
using System.Linq;
using Collector;
using SiliconScope.UI.Models;
using SiliconScope.UI.Services;

namespace SiliconScope.UI.ViewModels;

/// <summary>历史列表中的一条（展示包装）。</summary>
public sealed class HistoryItem
{
    private readonly HistoryEntry _e;
    public HistoryItem(HistoryEntry e) => _e = e;
    public HistoryEntry Entry => _e;
    public string Id => _e.Id;
    public string TimeText => _e.Time.ToString("yyyy-MM-dd HH:mm:ss");
    public string Model => string.IsNullOrWhiteSpace(_e.Model) ? "未知型号" : _e.Model;
    public string Zen => _e.Zen;
    public string Sub => $"{_e.Zen} · {_e.FreqMHz:F0} MHz @ {_e.VCore:F3} V · {_e.TempC:F0} ℃";
    public string SpText => _e.SpScore.ToString("F1");
    public string Grade => _e.Grade;
    public string GradeColor => string.IsNullOrWhiteSpace(_e.GradeColor) ? "#58A6FF" : _e.GradeColor;
    public string SourceText => $"数据源 {_e.DataSource} · v{_e.AppVersion}";
}

/// <summary>本机历史成绩 VM：列表、查看详情、删除、打开目录。</summary>
public sealed class HistoryViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private readonly HistoryStore _store;

    public ObservableCollection<HistoryItem> Items { get; } = new();

    private HistoryItem? _selected;
    public HistoryItem? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                OpenCmd.Notify();
                DeleteCmd.Notify();
            }
        }
    }

    private bool _isEmpty = true;
    public bool IsEmpty { get => _isEmpty; set { if (Set(ref _isEmpty, value)) { Raise(nameof(HasItems)); } } }
    public bool HasItems => !IsEmpty;

    private string _hint = "";
    public string Hint { get => _hint; set => Set(ref _hint, value); }

    public RelayCommand RefreshCmd { get; }
    public RelayCommand OpenCmd { get; }
    public RelayCommand DeleteCmd { get; }
    public RelayCommand OpenFolderCmd { get; }

    public HistoryViewModel(MainViewModel main)
    {
        _main = main;
        _store = new HistoryStore(main.Settings.RetainHistory);
        RefreshCmd = new RelayCommand(Refresh);
        OpenCmd = new RelayCommand(OpenSelected, () => Selected is not null);
        DeleteCmd = new RelayCommand(DeleteSelected, () => Selected is not null);
        OpenFolderCmd = new RelayCommand(() => _store.OpenFolder());
    }

    public void Refresh()
    {
        try
        {
            var all = _store.LoadAll();
            Items.Clear();
            foreach (var e in all) Items.Add(new HistoryItem(e));
            IsEmpty = Items.Count == 0;
            Hint = IsEmpty
                ? "还没有历史成绩。完成一次满载评测后会自动保存在这里（仅本机，不上传）。"
                : $"共 {Items.Count} 条记录，最新在上；双击或选中后点「查看详情」。";
        }
        catch (Exception ex)
        {
            Logger.Instance.Error("历史成绩加载失败", ex);
            IsEmpty = true;
            Hint = $"历史成绩加载失败：{ex.Message}";
        }
    }

    private void OpenSelected()
    {
        if (Selected is null) return;
        try
        {
            EvaluationResult r = HistoryStore.ToResult(Selected.Entry);
            _main.Result.Load(r);
            _main.Go(NavPage.Result);
        }
        catch (Exception ex)
        {
            Logger.Instance.Error("打开历史详情失败", ex);
            System.Windows.MessageBox.Show($"打开失败：{ex.Message}", "历史成绩",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    private void DeleteSelected()
    {
        if (Selected is null) return;
        var item = Selected;
        var ok = System.Windows.MessageBox.Show(
            $"确定删除 {item.TimeText} 的成绩（SP {item.SpText}）吗？", "删除历史",
            System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question);
        if (ok != System.Windows.MessageBoxResult.OK) return;

        _store.Delete(item.Id);
        Refresh();
    }
}
