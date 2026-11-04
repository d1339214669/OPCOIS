namespace OIS.DicomScp.Scu;

/// <summary>
/// forward_log 只读查询 API（task-003 spec §6.2，AC-07 转发历史）。
/// 供 WPF 管理控制台「转发 + 历史」页签使用。
/// </summary>
public interface IForwardLogQuery
{
    /// <summary>取最近 <paramref name="take"/> 条转发记录（按记录 id 倒序 = 时间倒序）。</summary>
    Task<IReadOnlyList<ForwardLogRow>> RecentAsync(int take, CancellationToken ct);
}
