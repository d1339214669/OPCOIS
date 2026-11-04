using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OIS.DicomScp.Wpf.ViewModels;

/// <summary>
/// ViewModel 基类：INotifyPropertyChanged + SetProperty 辅助。
/// 纯 .NET 基础设施，不引用 WPF 命名空间（headless 可测，AC-10）。
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>触发属性变化通知。</summary>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>字段赋值 + 变化通知；值未变化时不触发。</summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
