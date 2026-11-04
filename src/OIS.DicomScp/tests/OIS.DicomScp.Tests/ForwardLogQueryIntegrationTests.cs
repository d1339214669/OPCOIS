using Npgsql;
using OIS.DicomScp.Scu;
using Xunit;

namespace OIS.DicomScp.Tests;

/// <summary>
/// task-003 AC-07 集成测试：IForwardLogQuery / ForwardLog.ForwardLogQueryAsync（真实 PostgreSQL forward_log 表）。
/// 断言「最近 N 条倒序 + 字段与实际值一致（含失败可读原因）」。属于串行集合（共享 PG 库）。
/// </summary>
[Collection("ScuScpIntegration")]
public sealed class ForwardLogQueryIntegrationTests
{
    private const string ConnString = "Host=localhost;Port=5432;Username=postgres;Database=dicom";
    private const string Target = "T003-FL";
    private const string SopUid = "1.2.999.task003.fl.uid";

    [Fact]
    public async Task ForwardLogQuery_Recent_Returns_Last_N_Desc_With_Fields()
    {
        await using var conn = new NpgsqlConnection(ConnString);
        await conn.OpenAsync();

        var fwd = new ForwardLog(ConnString, new TestLogger());

        // 写 3 条（成功/失败/成功），失败必须带可读原因（AC-09 失败可见）。
        await fwd.WriteAsync(new ForwardLogEntry
        {
            RemoteScpName = Target,
            InternalId = "fl00000000000000000000000000000001",
            SopInstanceUid = SopUid,
            SopClassUid = "1.2.840.10008.5.1.4.1.1.2",
            Status = "success",
            Detail = "Success",
        });
        await fwd.WriteAsync(new ForwardLogEntry
        {
            RemoteScpName = Target,
            InternalId = "fl00000000000000000000000000000002",
            SopInstanceUid = SopUid,
            Status = "failed",
            Detail = "连接被拒绝（可读原因）",
        });
        await fwd.WriteAsync(new ForwardLogEntry
        {
            RemoteScpName = Target,
            InternalId = "fl00000000000000000000000000000003",
            SopInstanceUid = SopUid,
            SopClassUid = "1.2.840.10008.5.1.4.1.1.2",
            Status = "success",
            Detail = "Success",
        });

        try
        {
            // 最近 2 条：倒序（最新在前）——第 3 条 success、第 2 条 failed。
            var recent = await fwd.ForwardLogQueryAsync(2);
            Assert.Equal(2, recent.Count);
            Assert.Equal("success", recent[0].Status);
            Assert.Equal("failed", recent[1].Status);
            Assert.Contains("连接被拒绝", recent[1].Detail);
            Assert.True(recent[0].Id > recent[1].Id, "应按记录 id 倒序（时间倒序）");

            // 字段与 forward_log 实际值一致（AC-07）。
            Assert.Equal(Target, recent[0].RemoteScpName);
            Assert.Equal(SopUid, recent[0].SopInstanceUid);
            Assert.Equal("fl00000000000000000000000000000003", recent[0].InternalId);
            Assert.Equal("1.2.840.10008.5.1.4.1.1.2", recent[0].SopClassUid);

            // 最近 10 条应包含刚写的 3 条（本集合串行，此 3 条为全局最新）。
            var all = await fwd.ForwardLogQueryAsync(10);
            Assert.Equal(3, all.Count(r => r.RemoteScpName == Target && r.SopInstanceUid == SopUid));

            // 经 IForwardLogQuery 接口调用等价。
            var viaInterface = await ((IForwardLogQuery)fwd).RecentAsync(1, CancellationToken.None);
            Assert.Single(viaInterface);
            Assert.Equal("success", viaInterface[0].Status);

            // take <= 0 → 明确报错（失败可见，AC-09）。
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fwd.ForwardLogQueryAsync(0));
        }
        finally
        {
            await using var cmd = new NpgsqlCommand(
                "DELETE FROM forward_log WHERE remote_scp_name = @r AND sop_instance_uid = @u", conn);
            cmd.Parameters.AddWithValue("r", Target);
            cmd.Parameters.AddWithValue("u", SopUid);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
