namespace OIS.DicomScp;

/// <summary>
/// appsettings.json 完整配置（Scp / Scu / Storage / Postgres 四节），
/// JSON 字段名与既有 appsettings.json 完全一致（AC-04 格式兼容：
/// task-002 的 <c>RemoteScpRepository</c> 必须能继续读取保存后的文件）。
/// </summary>
public sealed class AppConfig
{
    public ScpSection Scp { get; set; } = new();
    public ScuSection Scu { get; set; } = new();
    public StorageSection Storage { get; set; } = new();
    public PostgresSection Postgres { get; set; } = new();
}

/// <summary><c>Scp</c> 节：本地 DICOM SCP 服务配置。</summary>
public sealed class ScpSection
{
    /// <summary>监听 TCP 端口（null 或非正数时由使用方回退默认 11112）。</summary>
    public int? Port { get; set; }

    /// <summary>对外 AET（null/空时由使用方回退默认 "OIS"）。</summary>
    public string? AeTitle { get; set; }

    /// <summary>允许接收的 Storage SOP Class 白名单；"*" 表示全部 Storage SOP Class。</summary>
    public List<string>? StorageServiceSopClasses { get; set; }
}

/// <summary><c>Scu</c> 节：出站 SCU 配置（task-002）。</summary>
public sealed class ScuSection
{
    /// <summary>SCU 侧默认 calling AE（fallback 链：目标.CallingAeTitle → 本项 → Scp.AeTitle → fo-dicom 默认）。</summary>
    public string? CallingAeTitle { get; set; }

    /// <summary>远程 SCP 目标列表（Name 须唯一），字段名与 task-002 一致。</summary>
    public List<RemoteScpTarget>? RemoteScps { get; set; } = new();
}

/// <summary><c>Storage</c> 节：DICOM 实例落盘根目录。</summary>
public sealed class StorageSection
{
    public string? Directory { get; set; }
}

/// <summary><c>Postgres</c> 节：索引库连接串。</summary>
public sealed class PostgresSection
{
    public string? ConnectionString { get; set; }
}
