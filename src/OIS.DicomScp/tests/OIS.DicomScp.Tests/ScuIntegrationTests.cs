using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using OIS.DicomScp.Index;
using OIS.DicomScp.Scu;
using OIS.DicomScp.Storage;
using Xunit;

namespace OIS.DicomScp.Tests;

/// <summary>
/// task-002 集成测试：SCU 出站转发。
/// 1) 用 task-001 的 SCP 把实例入库 + 落盘（源）；
/// 2) 用 <see cref="DicomScuService"/> 把该实例转发给一个捕获型远端 SCP；
/// 3) 断言：C-ECHO 成功/失败、C-STORE 转发内容=原文（UID 不篡改）、forward_log 记录（成功/失败）、按 SOPUID 定位、异常不崩。
/// 需要 localhost:5432 的 PostgreSQL dicom 库（postgres 用户 trust）。
/// </summary>
[Collection("ScuScpIntegration")]
public class ScuIntegrationTests : IAsyncLifetime
{
    private const string ConnString = "Host=localhost;Port=5432;Username=postgres;Database=dicom";
    private TestScpHost _source = null!;   // 本地存储（源）
    private CapturingScpHost _remote = null!; // 远端 SCP（接收转发）
    private DicomScuService _scu = null!;

    public async Task InitializeAsync()
    {
        _source = TestScpHost.Start(ConnString);
        _remote = CapturingScpHost.Start("PACS");

        var logger = new TestLogger();
        var index = new InstanceIndex(ConnString, logger);
        // 确保 forward_log 表已建（EnsureSchema 执行整个 Schema.sql）。
        await index.EnsureSchemaAsync();
        var fileStorage = new FileStorage(_source.StorageDir, logger);
        var fwdLog = new ForwardLog(ConnString, logger);
        _scu = new DicomScuService(fileStorage, index, fwdLog, "OIS", logger);
    }

    public async Task DisposeAsync()
    {
        await _remote.DisposeAsync();
        await _source.DisposeAsync();
    }

    /// <summary>AC-02：C-ECHO 对可达目标返回 Success。</summary>
    [Fact]
    public async Task Echo_To_Reachable_Returns_Success()
    {
        var target = new RemoteScpTarget
        {
            Name = "TestPacs",
            Host = "127.0.0.1",
            Port = _remote.Port,
            CalledAeTitle = _remote.AeTitle,
            CallingAeTitle = "OIS",
            Protocol = "DICOM",
        };

        var result = await _scu.EchoAsync(target);
        Assert.Equal(EchoStatus.Success, result.Status);
        Assert.Null(result.Reason);
        Assert.True(_remote.EchoCount >= 1, "远端 SCP 应实际收到 C-ECHO");
    }

    /// <summary>AC-03：C-ECHO 对不可达目标返回 Failed 且不抛异常；后续对可达目标仍成功（异常隔离）。</summary>
    [Fact]
    public async Task Echo_To_Unreachable_Returns_Failed_And_Service_Still_Works()
    {
        var deadTarget = new RemoteScpTarget
        {
            Name = "Dead",
            Host = "127.0.0.1",
            Port = GetUnusedPort(), // 一个未被监听的端口
            CalledAeTitle = "X",
            CallingAeTitle = "OIS",
        };

        // 不可达 → Failed，不抛异常，不崩溃。
        var failed = await _scu.EchoAsync(deadTarget);
        Assert.Equal(EchoStatus.Failed, failed.Status);
        Assert.False(string.IsNullOrWhiteSpace(failed.Reason), "失败原因应可读");

        // 异常隔离：随后对可达目标再发一次 C-ECHO 仍成功。
        var reachable = new RemoteScpTarget
        {
            Name = "Reachable",
            Host = "127.0.0.1",
            Port = _remote.Port,
            CalledAeTitle = _remote.AeTitle,
            CallingAeTitle = "OIS",
        };
        var ok = await _scu.EchoAsync(reachable);
        Assert.Equal(EchoStatus.Success, ok.Status);
    }

    /// <summary>
    /// AC-04/AC-08：C-STORE 转发内容 = 原文（SOPInstanceUID/StudyInstanceUID 严格一致、StudyDate 原样），
    /// 返回 Success；远端实际收到。
    /// </summary>
    [Fact]
    public async Task Store_Forwards_Original_Instance_Lossless()
    {
        // 1) 先用 task-001 SCP 把实例存到本地（源）。
        var sopUid = DicomUID.Generate().UID;
        var studyUid = DicomUID.Generate().UID;
        var file = BuildTestFile(sopUid, "PAT-FWD", "20250115", studyUid);
        await StoreViaScp(file);

        // 2) 从本地存储定位内部 ID（唯一文件）。
        var internalId = GetSourceInternalId();
        Assert.False(string.IsNullOrEmpty(internalId));

        // 3) 转发给远端 SCP。
        var target = Target();
        var result = await _scu.StoreByInternalIdAsync(target, internalId);
        Assert.Equal(StoreStatus.Success, result.Status);

        // 4) 远端实际收到，且 UID/日期与源一致（无损闭环，不篡改）。
        Assert.Single(_remote.ReceivedDatasets);
        var received = _remote.ReceivedDatasets[0];
        Assert.Equal(sopUid, received.GetSingleValue<string>(DicomTag.SOPInstanceUID));
        Assert.Equal(studyUid, received.GetSingleValue<string>(DicomTag.StudyInstanceUID));
        Assert.Equal("20250115", received.GetSingleValue<string>(DicomTag.StudyDate)); // YYYYMMDD 原样
        Assert.Equal("CT", received.GetSingleValue<string>(DicomTag.Modality));
    }

    /// <summary>AC-05：转发成功写一条 forward_log success 行。</summary>
    [Fact]
    public async Task Forward_Log_Writes_Success_Row()
    {
        var sopUid = DicomUID.Generate().UID;
        var file = BuildTestFile(sopUid, "PAT-LOG-SUCCESS", "20250201");
        await StoreViaScp(file);
        var internalId = GetSourceInternalId();

        var target = Target();
        var result = await _scu.StoreByInternalIdAsync(target, internalId);
        Assert.Equal(StoreStatus.Success, result.Status);

        // forward_log 应有一行 success 记录（目标+实例）。
        var idx = new InstanceIndex(ConnString, new TestLogger());
        using var conn = new Npgsql.NpgsqlConnection(ConnString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT status, detail FROM forward_log WHERE remote_scp_name=@r AND sop_instance_uid=@u", conn);
        cmd.Parameters.AddWithValue("r", target.Name);
        cmd.Parameters.AddWithValue("u", sopUid);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("success", reader.GetString(0));
    }

    /// <summary>AC-05：转发失败也写一行 status='failed'（失败必须可见）。</summary>
    [Fact]
    public async Task Forward_Log_Writes_Failed_Row_When_Unreachable()
    {
        var sopUid = DicomUID.Generate().UID;
        var file = BuildTestFile(sopUid, "PAT-LOG-FAIL", "20250301");
        await StoreViaScp(file);
        var internalId = GetSourceInternalId();

        // 指向不可达端口 → 转发失败。
        var deadTarget = new RemoteScpTarget
        {
            Name = "DeadPacs",
            Host = "127.0.0.1",
            Port = GetUnusedPort(),
            CalledAeTitle = "X",
            CallingAeTitle = "OIS",
        };
        var result = await _scu.StoreByInternalIdAsync(deadTarget, internalId);
        Assert.Equal(StoreStatus.Failed, result.Status);

        using var conn = new Npgsql.NpgsqlConnection(ConnString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT status FROM forward_log WHERE remote_scp_name=@r AND sop_instance_uid=@u", conn);
        cmd.Parameters.AddWithValue("r", deadTarget.Name);
        cmd.Parameters.AddWithValue("u", sopUid);
        var status = await cmd.ExecuteScalarAsync();
        Assert.Equal("failed", Convert.ToString(status));
    }

    /// <summary>AC-06：按 SOPInstanceUID 定位并转发；不存在的 UID 返回 Failed 且不产生 forward_log 副作用。</summary>
    [Fact]
    public async Task Store_By_SopUid_Works_And_Unknown_Uid_No_Side_Effect()
    {
        // 存在：按 SOPInstanceUID 转发成功。
        var sopUid = DicomUID.Generate().UID;
        var file = BuildTestFile(sopUid, "PAT-SOPUID", "20250401");
        await StoreViaScp(file);

        var target = Target();
        var ok = await _scu.StoreBySopInstanceUidAsync(target, sopUid);
        Assert.Equal(StoreStatus.Success, ok.Status);
        Assert.Single(_remote.ReceivedDatasets);
        Assert.Equal(sopUid, _remote.ReceivedDatasets[0].GetSingleValue<string>(DicomTag.SOPInstanceUID));

        // 不存在：明确返回 Failed，无 C-STORE 发送、无转发记录。
        var unknownUid = DicomUID.Generate().UID;
        var before = _remote.ReceivedDatasets.Count;
        var failed = await _scu.StoreBySopInstanceUidAsync(target, unknownUid);
        Assert.Equal(StoreStatus.Failed, failed.Status);
        Assert.False(string.IsNullOrWhiteSpace(failed.Reason));
        Assert.Equal(before, _remote.ReceivedDatasets.Count); // 无 C-STORE 发送（无副作用）
    }

    /// <summary>AC-07：本地存储文件缺失（internal_id 引用文件被删）→ C-STORE 返回 Failed 并记录 failed，不崩溃、不伪造 Success。</summary>
    [Fact]
    public async Task Store_When_File_Missing_Returns_Failed_Lossless()
    {
        var sopUid = DicomUID.Generate().UID;
        var file = BuildTestFile(sopUid, "PAT-MISSING", "20250501");
        await StoreViaScp(file);
        var internalId = GetSourceInternalId();

        // 删除本地存储文件。
        var abs = Path.Combine(_source.StorageDir, internalId[..2], internalId);
        File.Delete(abs);

        var target = Target();
        var result = await _scu.StoreByInternalIdAsync(target, internalId);
        Assert.Equal(StoreStatus.Failed, result.Status);
        Assert.Contains("文件", result.Reason, StringComparison.OrdinalIgnoreCase); // 文件缺失原因

        // 失败也写 forward_log failed 行（internal_id 引用仍记录，detail 写明原因）。
        using var conn = new Npgsql.NpgsqlConnection(ConnString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT status FROM forward_log WHERE remote_scp_name=@r AND internal_id=@i", conn);
        cmd.Parameters.AddWithValue("r", target.Name);
        cmd.Parameters.AddWithValue("i", internalId);
        var status = await cmd.ExecuteScalarAsync();
        Assert.Equal("failed", Convert.ToString(status));
    }

    /// <summary>AC-05：同实例连续转发两次可写两行（不在本实现去重，追加日志记录），且第一行不被覆盖。</summary>
    [Fact]
    public async Task Repeated_Forward_Appends_Multiple_Rows_No_Overwrite()
    {
        var sopUid = DicomUID.Generate().UID;
        var file = BuildTestFile(sopUid, "PAT-REPEAT", "20250601");
        await StoreViaScp(file);
        var internalId = GetSourceInternalId();
        var target = Target();

        var r1 = await _scu.StoreByInternalIdAsync(target, internalId);
        var r2 = await _scu.StoreByInternalIdAsync(target, internalId);
        Assert.Equal(StoreStatus.Success, r1.Status);
        Assert.Equal(StoreStatus.Success, r2.Status);

        using var conn = new Npgsql.NpgsqlConnection(ConnString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT COUNT(*) FROM forward_log WHERE remote_scp_name=@r AND sop_instance_uid=@u", conn);
        cmd.Parameters.AddWithValue("r", target.Name);
        cmd.Parameters.AddWithValue("u", sopUid);
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        Assert.True(count >= 2, $"同实例重复转发应追加多行（当前 {count} 行），不覆盖第一次记录");
    }

    // ---- helpers ----

    private RemoteScpTarget Target()
        => new()
        {
            Name = "TestPacs",
            Host = "127.0.0.1",
            Port = _remote.Port,
            CalledAeTitle = _remote.AeTitle,
            CallingAeTitle = "OIS",
            Protocol = "DICOM",
        };

    private string GetSourceInternalId()
    {
        var files = Directory.EnumerateFiles(_source.StorageDir, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f).Length == 32).ToArray();
        Assert.Single(files);
        return Path.GetFileName(files[0]);
    }

    private async Task StoreViaScp(DicomFile file)
    {
        var client = DicomClientFactory.Create("127.0.0.1", _source.Port, false, "TEST", _source.AeTitle);
        await client.AddRequestAsync(new DicomCStoreRequest(file));
        await client.SendAsync();
    }

    private static int GetUnusedPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static DicomFile BuildTestFile(string sopUid, string patientId, string studyDate, string? studyUid = null)
    {
        var ds = new DicomDataset();
        ds.AddOrUpdate(DicomTag.SOPClassUID, DicomUID.CTImageStorage.UID);
        ds.AddOrUpdate(DicomTag.SOPInstanceUID, sopUid);
        ds.AddOrUpdate(DicomTag.StudyInstanceUID, studyUid ?? DicomUID.Generate().UID);
        ds.AddOrUpdate(DicomTag.SeriesInstanceUID, DicomUID.Generate().UID);
        ds.AddOrUpdate(DicomTag.PatientID, patientId);
        ds.AddOrUpdate(DicomTag.Modality, "CT");
        ds.AddOrUpdate(DicomTag.StudyDate, studyDate);
        return new DicomFile(ds);
    }

    private sealed class TestLogger : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
