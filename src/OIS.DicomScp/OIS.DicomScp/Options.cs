namespace OIS.DicomScp;

/// <summary>
/// SCP 配置项，绑定 <c>appsettings.json</c> 的 <c>Scp</c> 节。
/// 端口 / AET / 存储目录 / PG 连接串全部来自配置，不硬编码（AC-01）。
/// </summary>
public sealed class ScpOptions
{
    /// <summary>监听 TCP 端口，默认 11112。值为 null 或非正数时使用默认值。</summary>
    public int? Port { get; set; }

    /// <summary>对外 AET（Application Entity Title），默认 OIS。</summary>
    public string AeTitle { get; set; } = "OIS";

    /// <summary>允许接收的 Storage SOP Class UID 白名单；"*" 表示全部 Storage SOP Class。</summary>
    public List<string> StorageServiceSopClasses { get; set; } = new() { "*" };

    /// <summary>实际端口（回退到默认 11112）。</summary>
    public int EffectivePort => Port is > 0 and <= 65535 ? Port.Value : 11112;
}

/// <summary>
/// SCU（出站连接）配置，绑定 <c>appsettings.json</c> 的 <c>Scu</c> 节。
/// 提供 SCU 侧默认 calling AE 与远程 SCP 目标列表（AC-01）。
/// </summary>
public sealed class ScuOptions
{
    /// <summary>
    /// SCU 侧默认 calling（local）AE Title。可为空——此时按 fallback 链
    /// 目标.CallingAeTitle → Scu.CallingAeTitle → Scp.AeTitle → fo-dicom 默认。
    /// </summary>
    public string? CallingAeTitle { get; set; }

    /// <summary>远程 SCP 目标列表，可 0..n 个（AC-01）。Name 需唯一。</summary>
    public List<RemoteScpTarget> RemoteScps { get; set; } = new();
}

/// <summary>
/// 一个已配置的远程 SCP 目标（绑定 <c>Scu.RemoteScps</c> 数组项，AC-01）。
/// 所有出站操作（C-ECHO / C-STORE）都指向该目标。
/// </summary>
public sealed record RemoteScpTarget
{
    /// <summary>别名，唯一（AC-01），用于命令行/接口定位目标。</summary>
    public string Name { get; init; } = "";

    /// <summary>主机名或 IP。</summary>
    public string Host { get; init; } = "";

    /// <summary>目标端口，默认 11112。</summary>
    public int Port { get; init; } = 11112;

    /// <summary>called/remote AE（远端 SCP 期望收到的 AE Title，用于路由/鉴权）。</summary>
    public string CalledAeTitle { get; init; } = "";

    /// <summary>local/calling AE。为空时 fallback 到 Scu.CallingAeTitle → Scp.AeTitle → fo-dicom 默认。</summary>
    public string? CallingAeTitle { get; init; }

    /// <summary>协议，默认 "DICOM"（预留；本期仅支持 DICOM）。</summary>
    public string Protocol { get; init; } = "DICOM";
}

public sealed class StorageOptions
{
    /// <summary>存储根目录，默认 D:\OIS\storage。</summary>
    public string Directory { get; set; } = Path.Combine("D:", "OIS", "storage");
}

public sealed class PostgresOptions
{
    /// <summary>Npgsql 连接串，默认本机 trust 认证连接 dicom 库。</summary>
    public string ConnectionString { get; set; } =
        "Host=localhost;Port=5432;Username=postgres;Database=dicom";
}
