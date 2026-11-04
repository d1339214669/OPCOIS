using OIS.DicomScp.Wpf.Commands;
using OIS.DicomScp.Wpf.ViewModels;

namespace OIS.DicomScp.Wpf.Views;

/// <summary>
/// 服务状态页签（AC-08 / AC-09）：端口/AET/运行状态/实例数 + 启动/停止/刷新 + 错误区。
/// View 层只负责绑定与命令接线，业务全部在 ViewModel（AC-10）。
/// </summary>
public partial class ServiceStatusView : System.Windows.Controls.UserControl
{
    /// <param name="vm">服务状态 ViewModel（由 <see cref="Composition.ConsoleServices"/> 组装注入）。</param>
    public ServiceStatusView(ServiceStatusViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        BtnStart.Command = new AppCommandBridge(vm.StartCommand);
        BtnStop.Command = new AppCommandBridge(vm.StopCommand);
        BtnRefresh.Command = new AppCommandBridge(vm.RefreshCommand);
    }
}
