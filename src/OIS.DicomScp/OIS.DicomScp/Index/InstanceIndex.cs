using Microsoft.Extensions.Logging;
using Npgsql;

namespace OIS.DicomScp.Index;

/// <summary>
/// PostgreSQL 索引访问层：负责 <c>dicom_instance</c> 表的建表（幂等）与 C-STORE 后元数据写入。
/// </summary>
/// <remarks>
/// 建表 DDL 见 <c>Index/Schema.sql</c>，用 <c>CREATE TABLE IF NOT EXISTS</c> 保证幂等（AC-02），
/// 重复启动不报错。<c>sop_instance_uid</c> 带 <c>UNIQUE</c> 约束，配合 <c>ON CONFLICT</c> 处理同实例去重（AC-05）。
/// </remarks>
public sealed class InstanceIndex
{
    private readonly string _connectionString;
    private readonly ILogger _logger;

    public InstanceIndex(string connectionString, ILogger logger)
    {
        _connectionString = connectionString
                            ?? throw new ArgumentNullException(nameof(connectionString));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 执行建表/建索引 DDL（幂等）。启动时调用一次。
    /// </summary>
    /// <param name="schemaSqlPath">Schema.sql 的绝对/相对路径。</param>
    public async Task EnsureSchemaAsync(string? schemaSqlPath = null)
    {
        var path = schemaSqlPath is not null
            ? Path.GetFullPath(schemaSqlPath)
            : Path.Combine(AppContext.BaseDirectory, "Index", "Schema.sql");

        if (!File.Exists(path))
            throw new FileNotFoundException($"找不到建表 DDL 文件: {path}", path);

        var ddl = await File.ReadAllTextAsync(path);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(ddl, conn);
        await cmd.ExecuteNonQueryAsync();

        _logger.LogInformation("已执行幂等建表（Schema.sql）");
    }

    /// <summary>
    /// 按 SOPInstanceUID 查询是否已存在。
    /// </summary>
    /// <returns>已存在返回 true（用于 AC-05 去重）。</returns>
    public async Task<bool> ExistsBySopInstanceUidAsync(string sopInstanceUid, CancellationToken ct = default)
    {
        const string sql = "SELECT 1 FROM dicom_instance WHERE sop_instance_uid = @uid LIMIT 1";
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("uid", sopInstanceUid);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is not null;
    }

    /// <summary>
    /// 插入一条实例元数据。
    /// 同 SOPInstanceUID 冲突时用 <c>ON CONFLICT (sop_instance_uid)</c> 策略处理：
    /// 存在则保留原行（不覆盖 business 元数据），仅返回 false 表示命中已有行（AC-05 去重，不产生第二行）。
    /// </summary>
    /// <returns>true=本次真正插入新行；false=已存在（冲突按去重处理，未插入）。</returns>
    public async Task<bool> InsertAsync(InstanceMetadata meta, CancellationToken ct = default)
    {
        const string sql = @"
INSERT INTO dicom_instance (
    internal_id, sop_instance_uid, sop_class_uid, patient_id, patient_name,
    study_instance_uid, series_instance_uid, modality, study_date, study_time,
    study_description, series_number, instance_number, file_path, file_size_bytes
) VALUES (
    @internal_id, @sop_instance_uid, @sop_class_uid, @patient_id, @patient_name,
    @study_instance_uid, @series_instance_uid, @modality, @study_date, @study_time,
    @study_description, @series_number, @instance_number, @file_path, @file_size_bytes
)
ON CONFLICT (sop_instance_uid) DO NOTHING";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("internal_id", meta.InternalId);
        cmd.Parameters.AddWithValue("sop_instance_uid", meta.SopInstanceUid);
        cmd.Parameters.AddWithValue("sop_class_uid", (object?)meta.SopClassUid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("patient_id", (object?)meta.PatientId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("patient_name", (object?)meta.PatientName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("study_instance_uid", (object?)meta.StudyInstanceUid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("series_instance_uid", (object?)meta.SeriesInstanceUid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("modality", (object?)meta.Modality ?? DBNull.Value);
        cmd.Parameters.AddWithValue("study_date", (object?)meta.StudyDate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("study_time", (object?)meta.StudyTime ?? DBNull.Value);
        cmd.Parameters.AddWithValue("study_description", (object?)meta.StudyDescription ?? DBNull.Value);
        cmd.Parameters.AddWithValue("series_number", (object?)meta.SeriesNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("instance_number", (object?)meta.InstanceNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("file_path", meta.FilePath);
        cmd.Parameters.AddWithValue("file_size_bytes", (object?)meta.FileSizeBytes ?? DBNull.Value);

        var affected = await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation(
            affected > 0
                ? "索引写入实例 {SopInstanceUid} -> internal_id {InternalId}"
                : "同 SOPInstanceUID 已存在，去重跳过插入 {SopInstanceUid}",
            meta.SopInstanceUid, meta.InternalId);
        return affected > 0;
    }

    /// <summary>统计某 SOPInstanceUID 在表中的行数（AC-05 去重断言用）。</summary>
    public async Task<long> CountBySopInstanceUidAsync(string sopInstanceUid, CancellationToken ct = default)
    {
        const string sql = "SELECT COUNT(*) FROM dicom_instance WHERE sop_instance_uid = @uid";
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("uid", sopInstanceUid);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null ? 0 : Convert.ToInt64(result);
    }
}
