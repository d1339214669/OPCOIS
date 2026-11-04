namespace OIS.DicomScp.Index;

/// <summary>
/// dicom_instance 只读查询 API（task-003 spec §6.2，AC-02）。
/// 供 WPF 管理控制台实例浏览使用；WPF ViewModel 仅依赖本接口（可 headless 测试，AC-10）。
/// 过滤参数为 null 表示不参与过滤；时间参数按 UTC 解释（received_at 为 TIMESTAMPTZ）。
/// </summary>
public interface IInstanceQuery
{
    /// <summary>
    /// 按 PatientID / StudyInstanceUID / Modality / received_at 时间范围过滤查询实例行，
    /// 按 received_at 倒序，最多返回 <paramref name="take"/> 行。过滤无结果返回空集合（合法，非错误，AC-02）。
    /// </summary>
    Task<IReadOnlyList<InstanceRow>> QueryAsync(
        string? patientId,
        string? studyInstanceUid,
        string? modality,
        DateTime? fromUtc,
        DateTime? toUtc,
        int take,
        CancellationToken ct);

    /// <summary>统计 dicom_instance 总行数（服务状态页「已存实例数」，AC-08）。</summary>
    Task<int> CountAsync(CancellationToken ct);
}
