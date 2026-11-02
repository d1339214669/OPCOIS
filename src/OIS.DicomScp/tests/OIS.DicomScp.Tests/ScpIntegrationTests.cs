using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using Npgsql;
using OIS.DicomScp.Index;
using Xunit;

namespace OIS.DicomScp.Tests;

/// <summary>
/// task-001 集成测试：启动真实 SCP，用 fo-dicom 客户端验证 C-ECHO / C-STORE / 落盘 / 去重 / 元数据入库。
/// 需要 localhost:5432 的 PostgreSQL dicom 库（postgres 用户，trust 无密码）。
///
/// 注：fo-dicom 5.2.6 的 SCU 在 SendAsync 完成且未被拒绝即视为成功；
/// C-STORE 成功以“文件落盘 + 索引行出现”这一副作用断言（AC-04/05/06 的核心）。
/// </summary>
public class ScpIntegrationTests : IAsyncLifetime
{
    private const string ConnString = "Host=localhost;Port=5432;Username=postgres;Database=dicom";
    private TestScpHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = TestScpHost.Start(ConnString);
        await Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    /// <summary>AC-02：建表幂等（information_schema.tables 存在）。</summary>
    [Fact]
    public async Task Schema_Is_Idempotent_And_Table_Exists()
    {
        var idx = new InstanceIndex(ConnString, new TestLogger());
        await idx.EnsureSchemaAsync();
        await idx.EnsureSchemaAsync();

        await using var conn = new NpgsqlConnection(ConnString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema='public' AND table_name='dicom_instance'",
            conn);
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        Assert.Equal(1, count);
    }

    /// <summary>AC-03：C-ECHO 返回 Success（SendAsync 完成、关联未被拒绝）。</summary>
    [Fact]
    public async Task CEcho_Returns_Success()
    {
        var client = DicomClientFactory.Create("127.0.0.1", _host.Port, false, "TEST", _host.AeTitle);
        var echo = new DicomCEchoRequest();
        await client.AddRequestAsync(echo);
        // 未抛异常即表示关联被接受、C-ECHO 成功应答（AC-03）。
        await client.SendAsync();
    }

    /// <summary>
    /// AC-04：C-STORE 后落盘文件，文件名为内部 ID（非业务 UID），落在 <id[0..2]>/<id> 分片子目录，
    /// 内容与存入一致（无损闭环）。
    /// </summary>
    [Fact]
    public async Task CStore_Persists_File_With_InternalId_And_Sharding()
    {
        var sopUid = DicomUID.Generate().UID;
        var file = BuildTestFile(sopUid, "PAT-CSTORE-1", "20250901");

        var client = DicomClientFactory.Create("127.0.0.1", _host.Port, false, "TEST", _host.AeTitle);
        await client.AddRequestAsync(new DicomCStoreRequest(file));
        await client.SendAsync();

        var files = Directory.EnumerateFiles(_host.StorageDir, "*", SearchOption.AllDirectories).ToArray();
        Assert.Single(files);
        var storedFile = files[0];

        var internalId = Path.GetFileName(storedFile);
        Assert.DoesNotContain(sopUid, internalId, StringComparison.OrdinalIgnoreCase); // 文件名非业务 UID
        Assert.Matches("^[0-9a-f]{32}$", internalId);

        // 分片：位于 <id[0..2]>/<id> 子目录
        var shard = Path.GetFileName(Path.GetDirectoryName(storedFile));
        Assert.NotNull(shard);
        Assert.Equal(internalId[..2], shard);
        Assert.Matches("^[0-9a-f]{2}$", shard);

        // 无损闭环：读回 SOPInstanceUID 与原值一致
        var readBack = await DicomFile.OpenAsync(storedFile);
        Assert.Equal(sopUid, readBack.Dataset.GetSingleValue<string>(DicomTag.SOPInstanceUID));
    }

    /// <summary>AC-05：同一 SOPInstanceUID 重复 C-STORE，只落一份、索引只有一行、不抛错。</summary>
    [Fact]
    public async Task CStore_SameSopInstanceUid_Is_Deduplicated()
    {
        var sopUid = DicomUID.Generate().UID;
        var file = BuildTestFile(sopUid, "PAT-DEDUP", "20250902");

        var client = DicomClientFactory.Create("127.0.0.1", _host.Port, false, "TEST", _host.AeTitle);
        await client.AddRequestAsync(new DicomCStoreRequest(file));
        await client.AddRequestAsync(new DicomCStoreRequest(file));
        await client.SendAsync();

        var idx = new InstanceIndex(ConnString, new TestLogger());
        Assert.Equal(1, await idx.CountBySopInstanceUidAsync(sopUid));

        var files = Directory.EnumerateFiles(_host.StorageDir, "*", SearchOption.AllDirectories).ToArray();
        Assert.Single(files);
    }

    /// <summary>AC-06：C-STORE 后元数据入库，UID/日期原样，缺失字段为 NULL。</summary>
    [Fact]
    public async Task CStore_Metadata_Is_Indexed()
    {
        var sopUid = DicomUID.Generate().UID;
        var studyUid = DicomUID.Generate().UID;
        var seriesUid = DicomUID.Generate().UID;
        var file = BuildTestFile(sopUid, "PAT-META", "20250903", studyUid, seriesUid);

        var client = DicomClientFactory.Create("127.0.0.1", _host.Port, false, "TEST", _host.AeTitle);
        await client.AddRequestAsync(new DicomCStoreRequest(file));
        await client.SendAsync();

        await using var conn = new NpgsqlConnection(ConnString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT patient_id, study_instance_uid, series_instance_uid, sop_instance_uid, modality, study_date, instance_number, internal_id, file_path, sop_class_uid, file_size_bytes FROM dicom_instance WHERE sop_instance_uid=@u",
            conn);
        cmd.Parameters.AddWithValue("u", sopUid);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        Assert.Equal("PAT-META", reader.GetString(0));
        Assert.Equal(studyUid, reader.GetString(1));
        Assert.Equal(seriesUid, reader.GetString(2));
        Assert.Equal(sopUid, reader.GetString(3));
        Assert.Equal("CT", reader.GetString(4));
        Assert.Equal("20250903", reader.GetString(5)); // 日期原样
        Assert.Equal("7", reader.IsDBNull(6) ? string.Empty : reader.GetString(6));

        var internalId = reader.GetString(7);
        var filePath = reader.GetString(8);
        var sopClass = reader.GetString(9);
        var fileSize = reader.GetInt64(10);
        await reader.CloseAsync();

        Assert.Matches("^[0-9a-f]{2}/[0-9a-f]{32}$", filePath);
        var absolute = Path.Combine(_host.StorageDir, filePath.Replace("/", Path.DirectorySeparatorChar.ToString()));
        Assert.True(File.Exists(absolute), "file_path 应指向实际文件");
        Assert.Equal(Path.GetFileName(absolute), internalId);
        Assert.Equal(new FileInfo(absolute).Length, fileSize);
        Assert.Equal(DicomUID.CTImageStorage.UID, sopClass);
    }

    /// <summary>AC-07：损坏/非 DICOM 数据连接不会崩溃 SCP；后续 C-ECHO 仍正常。</summary>
    [Fact]
    public async Task BadData_DoesNot_Crash_And_Service_Still_Responds()
    {
        using (var tcp = new System.Net.Sockets.TcpClient("127.0.0.1", _host.Port))
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes("NOT A DICOM PDU AT ALL");
            await tcp.GetStream().WriteAsync(bytes);
            await Task.Delay(200);
        }

        var client = DicomClientFactory.Create("127.0.0.1", _host.Port, false, "TEST", _host.AeTitle);
        await client.AddRequestAsync(new DicomCEchoRequest());
        await client.SendAsync(); // 服务仍响应
    }

    private static DicomFile BuildTestFile(string sopUid, string patientId, string studyDate, string? studyUid = null, string? seriesUid = null)
    {
        var ds = new DicomDataset();
        ds.AddOrUpdate(DicomTag.SOPClassUID, DicomUID.CTImageStorage.UID);
        ds.AddOrUpdate(DicomTag.SOPInstanceUID, sopUid);
        ds.AddOrUpdate(DicomTag.StudyInstanceUID, studyUid ?? DicomUID.Generate().UID);
        ds.AddOrUpdate(DicomTag.SeriesInstanceUID, seriesUid ?? DicomUID.Generate().UID);
        ds.AddOrUpdate(DicomTag.PatientID, patientId);
        ds.AddOrUpdate(DicomTag.Modality, "CT");
        ds.AddOrUpdate(DicomTag.StudyDate, studyDate);
        ds.AddOrUpdate(DicomTag.InstanceNumber, "7");
        return new DicomFile(ds);
    }

    private sealed class TestLogger : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
