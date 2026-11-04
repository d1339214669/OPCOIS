using Npgsql;
using NpgsqlTypes;
using OIS.DicomScp.Index;
using Xunit;

namespace OIS.DicomScp.Tests;

/// <summary>
/// task-003 AC-02 集成测试：IInstanceQuery / InstanceIndex.QueryInstancesAsync（真实 PostgreSQL dicom_instance 表）。
/// 预置 5 行（固定 UID，received_at 显式错峰），断言各过滤条件、倒序、take、空结果与 AC-09 原样字符串。
/// 属于串行集合（共享 fo-dicom 静态运行时与 PG 库）。
/// </summary>
[Collection("ScuScpIntegration")]
public sealed class InstanceQueryIntegrationTests
{
    private const string ConnString = "Host=localhost;Port=5432;Username=postgres;Database=dicom";

    private static readonly string[] SopUids =
    {
        "1.2.999.task003.iq.uid1",
        "1.2.999.task003.iq.uid2",
        "1.2.999.task003.iq.uid3",
        "1.2.999.task003.iq.uid4",
        "1.2.999.task003.iq.uid5",
    };

    // (Patient, Study, Modality, StudyDate, 相对 baseTime 的分钟偏移)
    // Patient/Study/Modality 值对本测试独占（T003-* 前缀 + 1.2.999.*），与库中既有数据（task-001/002 测试行）无碰撞；
    // received_at 落在 2020-01-01 时间窗（baseTime 起 0~8 分钟），既有行 received_at 均为「入库当时 now()」（近期），互不重叠。
    private static readonly (string Patient, string Study, string? Modality, string? StudyDate, int Minutes)[] Seed =
    {
        ("T003-QA", "1.2.999.task003.iq.study.a", "T003A", "20250701", 0),
        ("T003-QA", "1.2.999.task003.iq.study.a", "T003B", "20250702", 2),
        ("T003-QB", "1.2.999.task003.iq.study.b", "T003A", "20250703", 4),
        ("T003-QC", "1.2.999.task003.iq.study.c", null, null, 6),
        ("T003-QB", "1.2.999.task003.iq.study.b", "T003A", "20250705", 8),
    };

    /// <summary>预置时间窗基点（2020-01-01 01:00 UTC）：保证与库中既有行（received_at=入库时刻）完全分离。</summary>
    private static DateTime BaseTime() => new DateTime(2020, 1, 1, 1, 0, 0, DateTimeKind.Utc);

    private static string InternalIdOf(int index) => "9000000000000000000000000000" + (index + 1);
    private static string FilePathOf(int index) => "task003/iq/row" + (index + 1);

    [Fact]
    public async Task Query_Filters_By_Patient_Study_Modality_TimeRange_Take_And_Order()
    {
        var baseTime = BaseTime();
        await SeedAsync(baseTime);
        try
        {
            var query = (IInstanceQuery)new InstanceIndex(ConnString, new TestLogger());

            // 0) 全表查询（无过滤）：至少包含本测试 5 行，且本 5 行相对顺序为 received_at 倒序（uid5 最新在前）。
            var all = await query.QueryAsync(null, null, null, null, null, 500, CancellationToken.None);
            Assert.True(all.Count >= 5, $"全表至少应有本测试 5 行（实际 {all.Count}）");
            var mineInAll = all.Where(r => SopUids.Contains(r.SopInstanceUid)).Select(r => r.SopInstanceUid).ToList();
            Assert.Equal(SopUids.Reverse().ToArray(), mineInAll.ToArray());

            // 1) 时间窗 [base-1m, base+9m]：恰好命中本 5 行（2020 年窗，与既有近期行无重叠），倒序。
            var window = await query.QueryAsync(null, null, null, baseTime.AddMinutes(-1), baseTime.AddMinutes(9), 100, CancellationToken.None);
            Assert.Equal(5, window.Count);
            Assert.Equal(SopUids[4], window[0].SopInstanceUid);
            Assert.Equal(SopUids[3], window[1].SopInstanceUid);
            Assert.Equal(SopUids[0], window[4].SopInstanceUid);

            // 2) PatientID 过滤。
            var byPatient = await query.QueryAsync("T003-QA", null, null, null, null, 100, CancellationToken.None);
            Assert.Equal(2, byPatient.Count);
            Assert.All(byPatient, r => Assert.Equal("T003-QA", r.PatientId));
            Assert.Equal(new HashSet<string> { SopUids[0], SopUids[1] }, new HashSet<string>(byPatient.Select(r => r.SopInstanceUid)));

            // 3) StudyInstanceUID 过滤。
            var byStudy = await query.QueryAsync(null, "1.2.999.task003.iq.study.b", null, null, null, 100, CancellationToken.None);
            Assert.Equal(2, byStudy.Count);
            Assert.Equal(new HashSet<string> { SopUids[2], SopUids[4] }, new HashSet<string>(byStudy.Select(r => r.SopInstanceUid)));

            // 4) Modality 过滤（T003A 独占本测试 3 行）。
            var byModality = await query.QueryAsync(null, null, "T003A", null, null, 100, CancellationToken.None);
            Assert.Equal(3, byModality.Count);
            Assert.Equal(new HashSet<string> { SopUids[0], SopUids[2], SopUids[4] }, new HashSet<string>(byModality.Select(r => r.SopInstanceUid)));

            // 5) received_at 时间范围（UTC，含边界）：base+3m ~ base+7m → 偏移 4/6 两行。
            var byRange = await query.QueryAsync(null, null, null, baseTime.AddMinutes(3), baseTime.AddMinutes(7), 100, CancellationToken.None);
            Assert.Equal(2, byRange.Count);
            Assert.Equal(new HashSet<string> { SopUids[2], SopUids[3] }, new HashSet<string>(byRange.Select(r => r.SopInstanceUid)));

            // 6) 仅上界（to）：base+3m 之前 → 偏移 0/2 两行（既有行 received_at 为近期 now()，被上界排除）。
            var byToOnly = await query.QueryAsync(null, null, null, null, baseTime.AddMinutes(3), 100, CancellationToken.None);
            Assert.Equal(2, byToOnly.Count);
            Assert.Equal(new HashSet<string> { SopUids[0], SopUids[1] }, new HashSet<string>(byToOnly.Select(r => r.SopInstanceUid)));

            // 7) take 截断（窗口内）：取 2 行 = 最新两行。
            var top2 = await query.QueryAsync(null, null, null, baseTime.AddMinutes(-1), baseTime.AddMinutes(9), 2, CancellationToken.None);
            Assert.Equal(2, top2.Count);
            Assert.Equal(new HashSet<string> { SopUids[3], SopUids[4] }, new HashSet<string>(top2.Select(r => r.SopInstanceUid)));

            // 8) 过滤无结果 → 空集合（合法，非错误，AC-02）。
            var none = await query.QueryAsync("T003-NOPAT", null, null, null, null, 100, CancellationToken.None);
            Assert.Empty(none);
        }
        finally
        {
            await CleanupAsync();
        }
    }

    [Fact]
    public async Task Query_Returns_Raw_Strings_As_Is_and_Missing_Fields_Null()
    {
        var baseTime = BaseTime();
        await SeedAsync(baseTime);
        try
        {
            var query = (IInstanceQuery)new InstanceIndex(ConnString, new TestLogger());

            // AC-09：StudyDate 一律 YYYYMMDD 原样（不做 "2025-07-01" 式转换）。
            var qa = await query.QueryAsync("T003-QA", null, null, null, null, 100, CancellationToken.None);
            var row1 = qa.Single(r => r.SopInstanceUid == SopUids[0]);
            Assert.Equal("20250701", row1.StudyDate);

            // AC-09：缺失 Modality/StudyDate 的实例 → null（显示为空，不臆造默认值）。
            var qc = await query.QueryAsync("T003-QC", null, null, null, null, 100, CancellationToken.None);
            Assert.Single(qc);
            Assert.Null(qc[0].Modality);
            Assert.Null(qc[0].StudyDate);
            Assert.NotNull(qc[0].InternalId); // 非空字段正常返回
        }
        finally
        {
            await CleanupAsync();
        }
    }

    [Fact]
    public async Task Query_Take_Zero_Throws_ArgumentOutOfRange()
    {
        var query = (IInstanceQuery)new InstanceIndex(ConnString, new TestLogger());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => query.QueryAsync(null, null, null, null, null, 0, CancellationToken.None));
    }

    [Fact]
    public async Task Count_Reflects_New_Rows()
    {
        var query = (IInstanceQuery)new InstanceIndex(ConnString, new TestLogger());
        var before = await query.CountAsync(CancellationToken.None);

        var baseTime = BaseTime();
        await SeedAsync(baseTime);
        try
        {
            var after = await query.CountAsync(CancellationToken.None);
            Assert.Equal(before + 5, after);
        }
        finally
        {
            await CleanupAsync();
        }
    }

    // ---- helpers ----

    private static async Task SeedAsync(DateTime baseTime)
    {
        await using var conn = new NpgsqlConnection(ConnString);
        await conn.OpenAsync();

        // 幂等预置：先清理本测试 UID（含此前运行失败时可能遗留的行），再插入。
        await using (var del = new NpgsqlCommand("DELETE FROM dicom_instance WHERE sop_instance_uid = @uid", conn))
        {
            del.Parameters.Add(new NpgsqlParameter("uid", NpgsqlDbType.Varchar));
            foreach (var uid in SopUids)
            {
                del.Parameters["uid"].Value = uid;
                await del.ExecuteNonQueryAsync();
            }
        }

        for (var i = 0; i < Seed.Length; i++)
        {
            var (patient, study, modality, studyDate, minutes) = Seed[i];
            const string sql = @"
INSERT INTO dicom_instance (
    internal_id, sop_instance_uid, sop_class_uid, patient_id, patient_name,
    study_instance_uid, series_instance_uid, modality, study_date, study_time,
    study_description, series_number, instance_number, file_path, file_size_bytes, received_at
) VALUES (
    @internal_id, @sop, @sop_class, @patient, @name, @study, @series,
    @modality, @study_date, @study_time, @study_desc, @series_no, @instance_no,
    @file_path, @file_size, @received_at
)";
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("internal_id", InternalIdOf(i));
            cmd.Parameters.AddWithValue("sop", SopUids[i]);
            cmd.Parameters.AddWithValue("sop_class", DicomUID_CT);
            cmd.Parameters.AddWithValue("patient", patient);
            cmd.Parameters.AddWithValue("name", patient + "_name");
            cmd.Parameters.AddWithValue("study", study);
            cmd.Parameters.AddWithValue("series", "1.2.999.task003.iq.series." + (i + 1));
            cmd.Parameters.AddWithValue("modality", (object?)modality ?? DBNull.Value);
            cmd.Parameters.AddWithValue("study_date", (object?)studyDate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("study_time", DBNull.Value);
            cmd.Parameters.AddWithValue("study_desc", "task003 query test row " + (i + 1));
            cmd.Parameters.AddWithValue("series_no", (i + 1).ToString());
            cmd.Parameters.AddWithValue("instance_no", "1");
            cmd.Parameters.AddWithValue("file_path", FilePathOf(i));
            cmd.Parameters.AddWithValue("file_size", 1024L + i);
            cmd.Parameters.AddWithValue("received_at", baseTime.AddMinutes(minutes));
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task CleanupAsync()
    {
        await using var conn = new NpgsqlConnection(ConnString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("DELETE FROM dicom_instance WHERE sop_instance_uid = @uid", conn);
        cmd.Parameters.Add(new NpgsqlParameter("uid", NpgsqlDbType.Varchar));
        foreach (var uid in SopUids)
        {
            cmd.Parameters["uid"].Value = uid;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private const string DicomUID_CT = "1.2.840.10008.5.1.4.1.1.2";
}
