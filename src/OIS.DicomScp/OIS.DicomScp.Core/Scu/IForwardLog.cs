namespace OIS.DicomScp.Scu;

/// <summary>
/// 转发记录写入接口：往 <c>forward_log</c> 表写一行（AC-05），并支持按目标+实例统计转发次数（用于同实例重复转发的去重策略）。
/// </summary>
public interface IForwardLog
{
    /// <summary>向 <c>forward_log</c> 写一行转发记录（成功/失败各一行，失败必须可见）。</summary>
    Task WriteAsync(ForwardLogEntry entry, CancellationToken ct = default);

    /// <summary>
    /// 统计某目标上某 SOPInstanceUID 的转发记录数（含 status='success' 的行）。
    /// 用于 AC-05 同实例重复转发的去重策略由调用方决定。
    /// </summary>
    Task<long> CountByInstanceAndTargetAsync(string remoteScpName, string sopInstanceUid, CancellationToken ct = default);
}
