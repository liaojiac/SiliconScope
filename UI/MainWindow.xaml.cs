using System.Windows;
using System.Windows.Controls;
using SiliconScope.UI.ViewModels;

namespace SiliconScope.UI;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    // InitializeComponent 期间，导航 ListBox 因 XAML 里的 SelectedIndex="0"
    //   会在 EndInit 阶段就触发 SelectionChanged；而此刻同在 XAML 中、排在它后面创建的
    //   TitleText 等命名控件【尚未完成实例化】，事件里访问 TitleText 必然 NullReferenceException，
    //   导致整个主窗口在 InitializeComponent 阶段创建失败（真机日志已证实）。
    //   用 _ready 把初始化期间的选择事件整体忽略，等组件树建好后再同步一次标题/导航。
    private bool _ready;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        _ready = true;
        SyncNav();   // 组件树已就绪，安全地同步一次标题与导航状态
        // 窗口显示后再在后台探测传感器（含加载 PawnIO 驱动），
        //   驱动被拦截等情况只会让环境自检标红，不会导致启动闪退。
        Loaded += (_, _) => _vm.StartBackgroundProbe();
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 初始化未完成：直接忽略（初始页由构造函数末尾 SyncNav 设定）
        if (!_ready) return;
        SyncNav();
    }

    /// <summary>按导航当前选中项同步页面与标题；对所有命名控件做空值保护，任何阶段调用都安全。</summary>
    private void SyncNav()
    {
        if (Nav?.SelectedItem is not ListBoxItem item)
        {
            UpdateTitle(0);
            return;
        }

        string tag = item.Tag?.ToString() ?? "Run";
        _vm.Go(tag switch
        {
            "Result" => NavPage.Result,
            "History" => NavPage.History,
            "Settings" => NavPage.Settings,
            _ => NavPage.Run,
        });
        UpdateTitle(Nav.SelectedIndex);
    }

    private void UpdateTitle(int idx)
    {
        if (TitleText is null) return;   // 双保险：初始化早期被调用也不崩
        TitleText.Text = idx switch
        {
            1 => "评分详情",
            2 => "本机历史成绩",
            3 => "设置",
            _ => "开始评测",
        };
    }
}
