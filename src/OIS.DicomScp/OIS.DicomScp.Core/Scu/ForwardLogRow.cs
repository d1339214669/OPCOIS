namespace OIS.DicomScp.Scu;

/// <summary>
/// 一次 C-STORE 转发记录的只读投影，对应 <c>forward_log</c> 表一行（AC-07 转发历史）。
/// status / detail 为数据库原样值；缺失 UID 字段为 null（不臆造）。
/// </summary>
public sealed record ForwardLogRow
{
    public long Id { get; init; }

    /// <summary>目标别名（RemoteScps[].Name）。</summary>
    public required string RemoteScpName { get; init; }

    /// <summary>转发实例的内部 ID（文件名）。</summary>
    public required string InternalId { get; init; }

    public string? SopInstanceUid { get; init; }
    public string? SopClassUid { get; init; }
    public string? StudyInstanceUid { get; init; }
    public string? SeriesInstanceUid { get; init; }

    /// <summary>转发结果：'success' | 'failed'（原样）。</summary>
    public required string Status { get; init; }

    /// <summary>成功: 状态码/空；失败: 可读原因。</summary>
    public string? Detail { get; init; }

    /// <summary>转发时间（TIMESTAMPTZ → UTC）。</summary>
    public DateTime ForwardedAt { get; init; }
}
