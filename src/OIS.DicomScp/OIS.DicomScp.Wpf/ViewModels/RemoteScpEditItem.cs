namespace OIS.DicomScp.Wpf.ViewModels;

/// <summary>
/// 远程 SCP 配置编辑行（AC-04）：可编辑 POCO + INPC，View 层直接绑定。
/// 保存前由 <see cref="RemoteScpsViewModel"/> 统一校验；本类不做业务校验。
/// </summary>
public sealed class RemoteScpEditItem : ViewModelBase
{
    private string _name = string.Empty;
    private string _host = string.Empty;
    private int _port = 11112;
    private string _calledAe = string.Empty;
    private string _callingAe = string.Empty;
    private string _protocol = "DICOM";

    /// <summary>名称（唯一标识，AC-04 校验：非空 + Ordinal 唯一）。</summary>
    public string Name { get => _name; set => SetProperty(ref _name, value); }

    /// <summary>主机（AC-04 校验：非空）。</summary>
    public string Host { get => _host; set => SetProperty(ref _host, value); }

    /// <summary>端口（AC-04 校验：1-65535）。</summary>
    public int Port { get => _port; set => SetProperty(ref _port, value); }

    /// <summary>被叫 AETitle（可空；空白保存为 ""）。</summary>
    public string CalledAe { get => _calledAe; set => SetProperty(ref _calledAe, value); }

    /// <summary>主叫 AETitle（可空；空白保存为 null → 用全局 Scu.CallingAeTitle 回退）。</summary>
    public string CallingAe { get => _callingAe; set => SetProperty(ref _callingAe, value); }

    /// <summary>协议（本期仅 DICOM）。</summary>
    public string Protocol { get => _protocol; set => SetProperty(ref _protocol, value); }

    /// <summary>列表显示名（未命名占位）。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "（未命名目标）" : Name.Trim();

    /// <summary>由 <see cref="OIS.DicomScp.RemoteScpTarget"/> 构造编辑行（空白归一为 ""/null 显示）。</summary>
    public static RemoteScpEditItem FromTarget(OIS.DicomScp.RemoteScpTarget t) => new()
    {
        Name = t.Name,
        Host = t.Host,
        Port = t.Port,
        CalledAe = t.CalledAeTitle ?? string.Empty,
        CallingAe = t.CallingAeTitle ?? string.Empty,
        Protocol = string.IsNullOrWhiteSpace(t.Protocol) ? "DICOM" : t.Protocol,
    };
}
