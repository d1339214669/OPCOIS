using OIS.DicomScp.Wpf.Composition;

namespace OIS.DicomScp.Wpf;

/// <summary>
/// WPF 管理控制台应用入口（task-003）：组装服务 → 打开主窗口。
/// 启动失败必须可见（AC-09）：弹窗说明原因并退出，不静默吞异常。
/// </summary>
public partial class App : System.Windows.Application
{
    private ConsoleServices? _services;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            // 关键：绝不在 UI 线程上直接同步等待可能跨线程完成的异步初始化。
            // 若 ConsoleServices.Create() 内的任何 await 的 continuation 被回投到
            // 当前被 GetResult() 阻塞的 Dispatcher（DispatcherSynchronizationContext），
            // 将死锁——进程活着但主窗口永不显示（bugfix 2026-11-05 修复的真实故障）。
            // Task.Run 让组装在线程池线程（无同步上下文）完成，continuation 留在线程池，无死锁。
            _services = Task.Run(ConsoleServices.Create).GetAwaiter().GetResult();
            var window = new MainWindow(_services);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            // 失败必须可见（AC-09）：明确原因 + 退出码 1。
            System.Windows.MessageBox.Show(
                "OIS DICOM 管理控制台启动失败：\n" + ex.Message,
                "启动失败",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }
}
