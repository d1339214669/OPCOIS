namespace OIS.DicomScp.Index;

/// <summary>
/// 一个 DICOM 实例在存储层中的元数据描述。
/// 与 <c>dicom_instance</c> 表字段一一对应（见 <c>Schema.sql</c>）。
/// 缺省/空字符串的 UID 域保持 <c>null</c>，绝不臆造默认值（DOMAIN.md 纪律）。
/// </summary>
public sealed record InstanceMetadata
{
    /// <summary>内部生成的唯一 ID（GUID N 格式，全小写 [0-9a-f]），即落盘文件名。非业务 UID（AC-04 匿名化）。</summary>
    public required string InternalId { get; init; }

    /// <summary>SOP Instance UID（业务 UID，DICOM 全局唯一）。</summary>
    public required string SopInstanceUid { get; init; }

    public string? SopClassUid { get; init; }
    public string? PatientId { get; init; }
    public string? PatientName { get; init; }
    public string? StudyInstanceUid { get; init; }
    public string? SeriesInstanceUid { get; init; }
    public string? Modality { get; init; }

    /// <summary>DICOM YYYYMMDD 原样保留，不做时区/格式转换（AC-08）。</summary>
    public string? StudyDate { get; init; }

    /// <summary>DICOM HHMMSS.FFFFFF 原样（可选）。</summary>
    public string? StudyTime { get; init; }
    public string? StudyDescription { get; init; }
    public string? SeriesNumber { get; init; }
    public string? InstanceNumber { get; init; }

    /// <summary>相对存储目录路径，如 <c>&lt;id[0..2]&gt;/&lt;id&gt;</c>（无扩展名）。</summary>
    public required string FilePath { get; init; }

    /// <summary>落盘文件字节数。</summary>
    public long? FileSizeBytes { get; init; }
}
