namespace OIS.DicomScp.Wpf.Commands;

/// <summary>
/// 轻量命令抽象（headless，不依赖 WPF 的 System.Windows.Input.ICommand）。
/// ViewModel 只暴露本接口（AC-10：ViewModel 源码不出现 System.Windows）；
/// View 层经 <see cref="AppCommandBridge"/> 适配到 WPF Command 绑定。
/// </summary>
public interface IAppCommand
{
    /// <summary>当前是否可执行。</summary>
    bool CanExecute();

    /// <summary>执行命令。</summary>
    void Execute();

    /// <summary>CanExecute 状态可能变化时触发（View 据此刷新按钮可用性）。</summary>
    event EventHandler? CanExecuteChanged;

    /// <summary>CanExecute 可能变化时由 ViewModel 调用，触发 <see cref="CanExecuteChanged"/>。</summary>
    void RaiseCanExecuteChanged();
}
