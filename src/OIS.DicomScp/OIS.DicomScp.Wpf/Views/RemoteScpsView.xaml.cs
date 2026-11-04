using OIS.DicomScp.Wpf.Commands;
using OIS.DicomScp.Wpf.ViewModels;

namespace OIS.DicomScp.Wpf.Views;

/// <summary>
/// 远程 SCP 配置页签（AC-04）：目标列表 + 编辑表单 + 保存/加载 + 校验/读写错误区。
/// View 层只负责绑定与命令接线，业务全部在 ViewModel（AC-10）。
/// </summary>
public partial class RemoteScpsView : System.Windows.Controls.UserControl
{
    /// <param name="vm">远程 SCP 配置 ViewModel（由 <see cref="Composition.ConsoleServices"/> 组装注入）。</param>
    public RemoteScpsView(RemoteScpsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        BtnAdd.Command = new AppCommandBridge(vm.AddCommand);
        BtnDelete.Command = new AppCommandBridge(vm.DeleteCommand);
        BtnSave.Command = new AppCommandBridge(vm.SaveCommand);
        BtnReload.Command = new AppCommandBridge(vm.LoadCommand);
    }
}
