using System.Windows.Input;

namespace OIS.DicomScp.Wpf.Commands;

/// <summary>
/// 把 headless 的 <see cref="IAppCommand"/> 适配为 WPF 的 <see cref="System.Windows.Input.ICommand"/>，
/// 供 View 层按钮 Command 属性绑定使用（纯接线，无业务逻辑；业务全部在 ViewModel）。
/// </summary>
public sealed class AppCommandBridge : ICommand
{
    private readonly IAppCommand _inner;
    private EventHandler? _handlers;

    /// <param name="inner">headless 命令（来自 ViewModel）。</param>
    public AppCommandBridge(IAppCommand inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _inner.CanExecuteChanged += OnInnerCanExecuteChanged;
    }

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => _inner.CanExecute();

    /// <inheritdoc />
    public void Execute(object? parameter) => _inner.Execute();

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged
    {
        add => _handlers += value;
        remove => _handlers -= value;
    }

    private void OnInnerCanExecuteChanged(object? sender, EventArgs e)
        => _handlers?.Invoke(this, e);
}
