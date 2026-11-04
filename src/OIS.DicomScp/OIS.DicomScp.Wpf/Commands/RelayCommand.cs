namespace OIS.DicomScp.Wpf.Commands;

/// <summary>
/// <see cref="IAppCommand"/> 的默认实现（委托式命令，headless 可测）。
/// </summary>
public sealed class RelayCommand : IAppCommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    /// <summary>
    /// 创建命令。
    /// </summary>
    /// <param name="execute">执行体。</param>
    /// <param name="canExecute">可用性判定（null 恒可执行）。</param>
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute() => _canExecute is null || _canExecute();

    /// <inheritdoc />
    public void Execute() => _execute();

    /// <summary>触发 <see cref="CanExecuteChanged"/>（状态变化后由 ViewModel 调用）。</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
