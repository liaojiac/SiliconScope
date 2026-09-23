using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Collector;

namespace SiliconScope.UI;

/// <summary>
/// 应用入口。
/// 启动可靠性：
///   1) 静态构造在 WPF 入口（App.g.cs: new App() → InitializeComponent 加载 App.xaml/Styles.xaml）
///      【之前】就初始化日志并挂 AppDomain 致命异常兜底——XAML 资源加载期崩溃也能弹窗/写日志，
///      彻底消除"双击 exe 一闪而过、无日志无弹窗"。
///   2) OnStartup 中先挂 UI 线程/后台 Task 异常处理，再手动创建主窗口（不使用 StartupUri）。
///   3) 程序集名改为纯 ASCII（SiliconScope），避免非 UTF-8 区域下 apphost 定位入口程序集失败。
/// </summary>
public partial class App : Application
{
    static App()
    {
        // 最早可用的兜底点：此时 Application/窗口都还没创建，Logger 只依赖文件 IO，可安全初始化。
        try
        {
            Logger.Instance.Configure(
                baseDir: AppContext.BaseDirectory,
                minLevel: CollectorOptions.LogMinLevel);
        }
        catch { /* 日志初始化失败也不阻止后续弹窗 */ }

        // 致命未处理异常（含 XAML 资源加载、native 交互抛出且未被 UI 处理器接住的异常）：
        // 尽量写日志并用 Win32 消息框提示（MessageBox 不依赖 Application.Current）。
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            TryLog($"致命未处理异常(IsTerminating={e.IsTerminating})",
                e.ExceptionObject is Exception ex ? ex.ToString() : "未知致命错误");
            TryAlert($"程序遇到致命错误无法继续：\n\n" +
                     $"{(e.ExceptionObject is Exception x ? x.Message : "未知错误")}\n\n" +
                     "详情已尝试写入程序目录 logs/，也可运行「诊断启动.bat」抓取启动日志。");
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            TryLog("后台任务未观察异常", e.Exception.ToString());
            e.SetObserved();
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        Logger.Instance.Info("应用启动", new { version = AppInfo.Version });

        // UI 线程未处理异常：记录 + 弹窗，进程尽量继续
        DispatcherUnhandledException += OnDispatcherUnhandled;

        try
        {
            base.OnStartup(e);
        }
        catch (Exception ex)
        {
            Fatal("基类启动失败", ex);
            return;
        }

        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            Fatal("主窗口创建失败", ex);
        }
    }

    private void Fatal(string stage, Exception ex)
    {
        TryLog(stage, ex.ToString());
        TryAlert($"程序启动失败：{ex.Message}\n\n阶段：{stage}\n详情已写入程序目录 logs/，" +
                 "也可运行「诊断启动.bat」抓取启动日志。");
        Shutdown(1);
    }

    private void OnDispatcherUnhandled(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        TryLog("未处理异常(UI线程)", e.Exception.ToString());
        TryAlert($"程序发生异常：{e.Exception.Message}\n\n详细信息已写入 logs/ 目录。");
        e.Handled = true;
    }

    private static void TryLog(string title, string detail)
    {
        try { Logger.Instance.Error(title, context: new { detail }); } catch { }
    }

    private static void TryAlert(string text)
    {
        try
        {
            MessageBox.Show(text, "SiliconScope", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
    }
}
