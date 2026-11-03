namespace OIS.DicomScp.Scu;

/// <summary>
/// 一次 C-STORE 转发记录的描述，对应 <c>forward_log</c> 表的行（AC-05）。
/// 转发成功/失败各写一行；内部 ID 与业务 UID 并存，便于追溯。
/// </summary>
public sealed record ForwardLogEntry
{
    /// <summary>目标别名（RemoteScps[].Name）。</summary>
    public required string RemoteScpName { get; init; }

    /// <summary>转发实例的内部 ID（文件名）。</summary>
    public required string InternalId { get; init; }

    /// <summary>转发的实例业务 UID（DICOM 全局唯一）。</summary>
    public string? SopInstanceUid { get; init; }

    public string? SopClassUid { get; init; }
    public string? StudyInstanceUid { get; init; }
    public string? SeriesInstanceUid { get; init; }

    /// <summary>转发结果：'success' | 'failed'（对应 forward_log.status）。</summary>
    public required string Status { get; init; }

    /// <summary>成功: Success 状态码/空；失败: 可读原因。</summary>
    public string? Detail { get; init; }
}
