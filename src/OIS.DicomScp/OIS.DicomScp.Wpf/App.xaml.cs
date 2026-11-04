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
            _services = ConsoleServices.Create();
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
