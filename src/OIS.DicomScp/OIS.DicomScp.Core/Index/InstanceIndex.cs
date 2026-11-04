using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace OIS.DicomScp.Index;

/// <summary>
/// PostgreSQL 索引访问层：负责 <c>dicom_instance</c> 表的建表（幂等）与 C-STORE 后元数据写入。
/// </summary>
/// <remarks>
/// 建表 DDL 见 <c>Index/Schema.sql</c>，用 <c>CREATE TABLE IF NOT EXISTS</c> 保证幂等（AC-02），
/// 重复启动不报错。<c>sop_instance_uid</c> 带 <c>UNIQUE</c> 约束，配合 <c>ON CONFLICT</c> 处理同实例去重（AC-05）。
/// </remarks>
public sealed class InstanceIndex : IInstanceQuery
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
        try
        {
            await cmd.ExecuteNonQueryAsync();
        }
        catch (PostgresException ex) when (ex.SqlState == "42P07")
        {
            // 42P07 = duplicate_table/index：并发启动/多实例同时 `CREATE TABLE/INDEX IF NOT EXISTS` 时,
            // PostgreSQL 目录可能竞态报"关系已存在"。此时 DDL 已由对端实例完成，属幂等预期内，忽略即可（AC-02）。
            _logger.LogDebug("建表/建索引并发竞态（已由其他实例创建），忽略: {Message}", ex.Message);
        }

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

    /// <summary>
    /// 按 SOPInstanceUID 查找实例的元数据（含 <c>internal_id</c> 与 <c>file_path</c>，AC-06）。
    /// 仅返回库中该 UID 的一行；不存在时返回 <c>null</c>（不抛未处理异常，AC-06）。
    /// </summary>
    /// <returns>匹配的 <see cref="InstanceMetadata"/>（至少含 InternalId/FilePath），未找到返回 null。</returns>
    public async Task<InstanceMetadata?> FindBySopInstanceUidAsync(string sopInstanceUid, CancellationToken ct = default)
    {
        const string sql = @"
SELECT internal_id, sop_instance_uid, sop_class_uid, patient_id, patient_name,
       study_instance_uid, series_instance_uid, modality, study_date, study_time,
       study_description, series_number, instance_number, file_path, file_size_bytes
FROM dicom_instance
WHERE sop_instance_uid = @uid
LIMIT 1";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("uid", sopInstanceUid);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var meta = new InstanceMetadata
        {
            InternalId = reader.GetString(0),
            SopInstanceUid = reader.GetString(1),
            SopClassUid = GetOrNull(reader, 2),
            PatientId = GetOrNull(reader, 3),
            PatientName = GetOrNull(reader, 4),
            StudyInstanceUid = GetOrNull(reader, 5),
            SeriesInstanceUid = GetOrNull(reader, 6),
            Modality = GetOrNull(reader, 7),
            StudyDate = GetOrNull(reader, 8),
            StudyTime = GetOrNull(reader, 9),
            StudyDescription = GetOrNull(reader, 10),
            SeriesNumber = GetOrNull(reader, 11),
            InstanceNumber = GetOrNull(reader, 12),
            FilePath = reader.GetString(13),
            FileSizeBytes = reader.IsDBNull(14) ? null : reader.GetInt64(14),
        };
        return meta;
    }

    private static string? GetOrNull(NpgsqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    /// <summary>
    /// 查询 dicom_instance 行（AC-02 实例浏览）：
    /// 按 PatientID / StudyInstanceUID / Modality / received_at 时间范围过滤（参数为 null 则不过滤），
    /// 按 received_at 倒序，最多返回 <paramref name="take"/> 行；过滤无结果返回空集合（合法，非错误）。
    /// 返回字段全部为库中原样字符串（UID/StudyDate 不做时区/格式/大小写/trim 转换，AC-09）。
    /// </summary>
    /// <param name="patientId">PatientID 过滤（null 不过滤）。</param>
    /// <param name="studyInstanceUid">StudyInstanceUID 过滤（null 不过滤）。</param>
    /// <param name="modality">Modality 过滤（null 不过滤）。</param>
    /// <param name="fromUtc">received_at 下界（含，UTC；null 不过滤）。</param>
    /// <param name="toUtc">received_at 上界（含，UTC；null 不过滤）。</param>
    /// <param name="take">最多返回行数（必须 &gt;= 1）。</param>
    public async Task<IReadOnlyList<InstanceRow>> QueryInstancesAsync(
        string? patientId,
        string? studyInstanceUid,
        string? modality,
        DateTime? fromUtc,
        DateTime? toUtc,
        int take,
        CancellationToken ct = default)
    {
        if (take <= 0)
            throw new ArgumentOutOfRangeException(nameof(take), take, "take 必须 >= 1");

        const string sql = @"
SELECT internal_id, sop_instance_uid, sop_class_uid, patient_id, patient_name,
       study_instance_uid, series_instance_uid, modality, study_date, study_time,
       study_description, series_number, instance_number, file_path, file_size_bytes,
       received_at
FROM dicom_instance
WHERE (@patient_id IS NULL OR patient_id = @patient_id)
  AND (@study_uid IS NULL OR study_instance_uid = @study_uid)
  AND (@modality IS NULL OR modality = @modality)
  AND (@from_utc IS NULL OR received_at >= @from_utc)
  AND (@to_utc IS NULL OR received_at <= @to_utc)
ORDER BY received_at DESC
LIMIT @take";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        // 显式声明参数类型：过滤参数为 DBNull 时 PostgreSQL 无法从 `@p IS NULL` 推断类型（42P08），
        // 必须带类型声明（Varchar / TimestampTz），否则查询直接失败。
        cmd.Parameters.Add(new NpgsqlParameter("patient_id", NpgsqlDbType.Varchar) { Value = (object?)patientId ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("study_uid", NpgsqlDbType.Varchar) { Value = (object?)studyInstanceUid ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("modality", NpgsqlDbType.Varchar) { Value = (object?)modality ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("from_utc", NpgsqlDbType.TimestampTz) { Value = (object?)fromUtc ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("to_utc", NpgsqlDbType.TimestampTz) { Value = (object?)toUtc ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("take", NpgsqlDbType.Integer) { Value = take });

        var rows = new List<InstanceRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new InstanceRow
            {
                InternalId = reader.GetString(0),
                SopInstanceUid = reader.GetString(1),
                SopClassUid = GetOrNull(reader, 2),
                PatientId = GetOrNull(reader, 3),
                PatientName = GetOrNull(reader, 4),
                StudyInstanceUid = GetOrNull(reader, 5),
                SeriesInstanceUid = GetOrNull(reader, 6),
                Modality = GetOrNull(reader, 7),
                StudyDate = GetOrNull(reader, 8),
                StudyTime = GetOrNull(reader, 9),
                StudyDescription = GetOrNull(reader, 10),
                SeriesNumber = GetOrNull(reader, 11),
                InstanceNumber = GetOrNull(reader, 12),
                FilePath = reader.GetString(13),
                FileSizeBytes = reader.IsDBNull(14) ? null : reader.GetInt64(14),
                ReceivedAt = reader.GetDateTime(15),
            });
        }
        return rows;
    }

    /// <inheritdoc />
    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        const string sql = "SELECT COUNT(*) FROM dicom_instance";
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null ? 0 : Convert.ToInt32(result);
    }

    /// <summary>spec §6.2 <see cref="IInstanceQuery.QueryAsync"/> 的显式实现：委托给 <see cref="QueryInstancesAsync"/>（spec §6.1 命名）。</summary>
    Task<IReadOnlyList<InstanceRow>> IInstanceQuery.QueryAsync(
        string? patientId, string? studyInstanceUid, string? modality,
        DateTime? fromUtc, DateTime? toUtc, int take, CancellationToken ct)
        => QueryInstancesAsync(patientId, studyInstanceUid, modality, fromUtc, toUtc, take, ct);
}
