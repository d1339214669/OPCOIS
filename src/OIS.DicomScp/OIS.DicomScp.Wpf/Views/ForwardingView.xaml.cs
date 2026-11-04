using OIS.DicomScp.Wpf.Commands;
using OIS.DicomScp.Wpf.ViewModels;

namespace OIS.DicomScp.Wpf.Views;

/// <summary>
/// 转发 + 历史页签（AC-05 / AC-07）：目标/实例下拉 + C-ECHO/C-STORE 结果区 + 转发历史表。
/// View 层只负责绑定与命令接线，业务全部在 ViewModel（AC-10）。
/// </summary>
public partial class ForwardingView : System.Windows.Controls.UserControl
{
    /// <param name="vm">转发 + 历史 ViewModel（由 <see cref="Composition.ConsoleServices"/> 组装注入）。</param>
    public ForwardingView(ForwardingViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        BtnEcho.Command = new AppCommandBridge(vm.EchoCommand);
        BtnStore.Command = new AppCommandBridge(vm.StoreCommand);
        BtnHistory.Command = new AppCommandBridge(vm.RefreshHistoryCommand);
    }
}
