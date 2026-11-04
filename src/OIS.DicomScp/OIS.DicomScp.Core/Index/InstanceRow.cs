namespace OIS.DicomScp.Index;

/// <summary>
/// <c>dicom_instance</c> 表一行的只读投影（AC-02 实例浏览 / AC-03 详情核对）。
/// UID / StudyDate / StudyTime 一律数据库原样字符串，绝不做时区/日期/大小写/trim 转换（AC-09 领域纪律）。
/// 缺失字段为 null（显示为空，不臆造默认值）。
/// </summary>
public sealed record InstanceRow
{
    /// <summary>内部唯一 ID（GUID N 格式 32 位小写十六进制），即落盘文件名。</summary>
    public required string InternalId { get; init; }

    /// <summary>SOP Instance UID（业务 UID，原样）。</summary>
    public required string SopInstanceUid { get; init; }

    public string? SopClassUid { get; init; }
    public string? PatientId { get; init; }
    public string? PatientName { get; init; }
    public string? StudyInstanceUid { get; init; }
    public string? SeriesInstanceUid { get; init; }
    public string? Modality { get; init; }

    /// <summary>DICOM YYYYMMDD 原样（无时区/格式转换，AC-09）。</summary>
    public string? StudyDate { get; init; }

    /// <summary>DICOM HHMMSS.FFFFFF 原样（可选）。</summary>
    public string? StudyTime { get; init; }

    public string? StudyDescription { get; init; }
    public string? SeriesNumber { get; init; }
    public string? InstanceNumber { get; init; }

    /// <summary>相对存储目录路径（如 <c>&lt;id[0..2]&gt;/&lt;id&gt;</c>，无扩展名）。</summary>
    public required string FilePath { get; init; }

    public long? FileSizeBytes { get; init; }

    /// <summary>入库时间（TIMESTAMPTZ → UTC）。</summary>
    public DateTime ReceivedAt { get; init; }
}
