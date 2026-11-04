using Microsoft.Extensions.Logging;
using Npgsql;

namespace OIS.DicomScp.Scu;

/// <summary>
/// 转发记录写入器：把 <see cref="ForwardLogEntry"/> 写入 PostgreSQL <c>forward_log</c> 表（AC-05）。
/// 追加日志表，不做 <c>ON CONFLICT</c> 去重；同实例重复转发的去重策略由调用方（DicomScuService）决定。
/// </summary>
public sealed class ForwardLog : IForwardLog
{
    private readonly string _connectionString;
    private readonly ILogger _logger;

    public ForwardLog(string connectionString, ILogger logger)
    {
        _connectionString = connectionString
                            ?? throw new ArgumentNullException(nameof(connectionString));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task WriteAsync(ForwardLogEntry entry, CancellationToken ct = default)
    {
        const string sql = @"
INSERT INTO forward_log (
    remote_scp_name, internal_id, sop_instance_uid, sop_class_uid,
    study_instance_uid, series_instance_uid, status, detail
) VALUES (
    @remote_scp_name, @internal_id, @sop_instance_uid, @sop_class_uid,
    @study_instance_uid, @series_instance_uid, @status, @detail
)";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("remote_scp_name", entry.RemoteScpName);
        cmd.Parameters.AddWithValue("internal_id", entry.InternalId);
        cmd.Parameters.AddWithValue("sop_instance_uid", (object?)entry.SopInstanceUid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("sop_class_uid", (object?)entry.SopClassUid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("study_instance_uid", (object?)entry.StudyInstanceUid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("series_instance_uid", (object?)entry.SeriesInstanceUid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("status", entry.Status);
        cmd.Parameters.AddWithValue("detail", (object?)entry.Detail ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation(
            "转发记录 {Status}: {Remote} <- {InternalId} ({SopUid})",
            entry.Status, entry.RemoteScpName, entry.InternalId, entry.SopInstanceUid);
    }

    /// <inheritdoc />
    public async Task<long> CountByInstanceAndTargetAsync(string remoteScpName, string sopInstanceUid, CancellationToken ct = default)
    {
        const string sql = @"
SELECT COUNT(*) FROM forward_log
WHERE remote_scp_name = @remote AND sop_instance_uid = @uid";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("remote", remoteScpName);
        cmd.Parameters.AddWithValue("uid", sopInstanceUid);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null ? 0 : Convert.ToInt64(result);
    }
}
