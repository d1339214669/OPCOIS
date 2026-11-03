namespace OIS.DicomScp.Scu;

/// <summary>
/// 出站 SCU 服务：C-ECHO（连通性验证）+ C-STORE（转发本地已存实例）。
/// 所有方法都不抛未处理异常——异常统一转为 <c>Failed</c> 结果（AC-03/AC-07「失败必须可见」）。
/// </summary>
public interface IDicomScuService
{
    /// <summary>对某远程 SCP 目标发起 C-ECHO，验证连通性。成功须看响应 <c>Status</c>（非仅“不抛异常”）。</summary>
    Task<EchoResult> EchoAsync(RemoteScpTarget target, CancellationToken ct = default);

    /// <summary>按内部 ID 定位本地已存实例，读文件后 C-STORE 推送并记录转发日志。</summary>
    Task<ForwardResult> StoreByInternalIdAsync(RemoteScpTarget target, string internalId, CancellationToken ct = default);

    /// <summary>按 SOPInstanceUID 定位（经内部 ID 解析到文件）后 C-STORE 推送并记录转发日志。</summary>
    Task<ForwardResult> StoreBySopInstanceUidAsync(RemoteScpTarget target, string sopInstanceUid, CancellationToken ct = default);
}
