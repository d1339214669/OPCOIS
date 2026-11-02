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
