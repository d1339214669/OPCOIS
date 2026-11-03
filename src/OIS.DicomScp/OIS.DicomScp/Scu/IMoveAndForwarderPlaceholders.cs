namespace OIS.DicomScp.Scu;

/// <summary>后续 task：C-MOVE 拉取结果状态（本期仅预留，不实现）。</summary>
public enum MoveStatus
{
    Success,
    Failed,
}

/// <summary>后续 task：C-MOVE 拉取结果（本期仅预留，不实现）。</summary>
public sealed record MoveResult(MoveStatus Status, string? Reason);

/// <summary>
/// 后续 task：按 Study / Series 定位所有实例并逐个转发（batched forwarding）。
/// 本期<b>仅签名占位</b>，不要求实现（spec §6.2「Abstraction 预留」）。
/// </summary>
public interface IRemoteForwarder
{
    /// <summary>转发某 Study 下所有实例（后续实现）。</summary>
    Task<ForwardResult> ForwardStudyAsync(RemoteScpTarget target, string studyInstanceUid, CancellationToken ct = default);

    /// <summary>转发某 Series 下所有实例（后续实现）。</summary>
    Task<ForwardResult> ForwardSeriesAsync(RemoteScpTarget target, string seriesInstanceUid, CancellationToken ct = default);
}

/// <summary>
/// 后续 task：C-MOVE 从远程 SCP 把实例拉回 OIS。
/// 本期<b>仅接口定义占位</b>（方法签名带 TODO），不实现传输逻辑（spec §5 Out of Scope）。
/// </summary>
public interface IMoveScu
{
    /// <summary>从远程源 SCP 拉取某实例（后续实现）。</summary>
    Task<MoveResult> MoveAsync(RemoteScpTarget source, string sopInstanceUid, CancellationToken ct = default);
}
