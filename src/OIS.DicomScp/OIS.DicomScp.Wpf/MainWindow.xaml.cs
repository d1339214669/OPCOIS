using OIS.DicomScp.Index;
using OIS.DicomScp.Wpf.Composition;
using OIS.DicomScp.Wpf.Views;
using System.Windows;
using System.Windows.Controls;

namespace OIS.DicomScp.Wpf;

/// <summary>
/// 主窗口（task-003 ③）：5 页签 MVVM 骨架。
/// 页签内容 = 各页签 View（DataContext 由 <see cref="ConsoleServices"/> 组装的 ViewModel 注入）；
/// 跨页签联动（实例浏览「核对选中」→ 详情核对）在此接线；窗口加载后预热转发页与服务状态页。
/// </summary>
public partial class MainWindow : Window
{
    private readonly ConsoleServices _services;

    /// <param name="services">服务组装（含 5 个 ViewModel，AC-01~AC-09 业务全在 ViewModel/Core）。</param>
    public MainWindow(ConsoleServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        InitializeComponent();

        ((TabItem)Tabs.Items[0]).Content = new InstanceListView(_services.Instances);
        ((TabItem)Tabs.Items[1]).Content = new InstanceDetailView(_services.Detail);
        ((TabItem)Tabs.Items[2]).Content = new RemoteScpsView(_services.RemoteScps);
        ((TabItem)Tabs.Items[3]).Content = new ForwardingView(_services.Forwarding);
        ((TabItem)Tabs.Items[4]).Content = new ServiceStatusView(_services.ServiceStatus);

        _services.Instances.ReviewRequested += OnReviewRequested;
        Loaded += OnLoaded;
    }

    /// <summary>实例浏览「核对选中实例」→ 切到详情核对页签并执行核对（跨页签联动）。</summary>
    private void OnReviewRequested(object? sender, InstanceRow row)
    {
        _ = _services.Detail.LoadAsync(row);
        Tabs.SelectedIndex = 1;
    }

    /// <summary>窗口加载：预热转发页（目标+实例）与服务状态页（端口/AET/实例数）。</summary>
    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        await _services.Forwarding.LoadAllAsync();
        await _services.ServiceStatus.InitializeAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        _services.Instances.ReviewRequested -= OnReviewRequested;
        base.OnClosed(e);
    }
}
