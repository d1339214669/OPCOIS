using OIS.DicomScp.Wpf.Commands;
using OIS.DicomScp.Wpf.ViewModels;

namespace OIS.DicomScp.Wpf.Views;

/// <summary>
/// 详情核对页签（AC-03）：文件↔库逐项核对报告 + 错误区。
/// View 层只负责绑定与命令接线，业务全部在 ViewModel（AC-10）。
/// </summary>
public partial class InstanceDetailView : System.Windows.Controls.UserControl
{
    /// <param name="vm">详情核对 ViewModel（由 <see cref="Composition.ConsoleServices"/> 组装注入）。</param>
    public InstanceDetailView(InstanceDetailViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        BtnLoad.Command = new AppCommandBridge(vm.LoadCommand);
    }
}
