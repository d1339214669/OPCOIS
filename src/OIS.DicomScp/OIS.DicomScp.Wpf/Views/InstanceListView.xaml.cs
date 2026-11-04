using OIS.DicomScp.Wpf.Commands;
using OIS.DicomScp.Wpf.ViewModels;

namespace OIS.DicomScp.Wpf.Views;

/// <summary>
/// 实例浏览页签（AC-02）：过滤条件 + 查询 + 结果表格 + 错误区。
/// View 层只负责绑定与命令接线（<see cref="AppCommandBridge"/> 适配），业务全部在 ViewModel（AC-10）。
/// </summary>
public partial class InstanceListView : System.Windows.Controls.UserControl
{
    /// <param name="vm">实例浏览 ViewModel（由 <see cref="Composition.ConsoleServices"/> 组装注入）。</param>
    public InstanceListView(InstancesViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        // 命令接线：IAppCommand（headless）→ WPF ICommand（纯适配，无业务逻辑）
        BtnQuery.Command = new AppCommandBridge(vm.QueryCommand);
        BtnReview.Command = new AppCommandBridge(vm.ReviewCommand);
        BtnClear.Command = new AppCommandBridge(vm.ClearFiltersCommand);
    }
}
