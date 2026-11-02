using System.Net;
using FellowOakDicom;
using FellowOakDicom.Network;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OIS.DicomScp.Index;
using OIS.DicomScp.Scp;
using OIS.DicomScp.Storage;

namespace OIS.DicomScp.Tests;

/// <summary>
/// 集成测试宿主：在测试进程内启动一个 fo-dicom SCP（DicomScpService），
/// 使用临时存储目录 + 真实 PostgreSQL 索引，供 C-ECHO / C-STORE 集成测试使用。
/// </summary>
public sealed class TestScpHost : IAsyncDisposable
{
    private readonly IDicomServer _server;
    private readonly string _storageDir;

    public int Port { get; }
    public string StorageDir => _storageDir;
    public string ConnectionString { get; }
    public string AeTitle { get; }

    private TestScpHost(int port, string aeTitle, string storageDir, string connectionString)
    {
        Port = port;
        AeTitle = aeTitle;
        _storageDir = storageDir;
        ConnectionString = connectionString;

        // 清理旧目录
        if (Directory.Exists(_storageDir))
            Directory.Delete(_storageDir, recursive: true);
        Directory.CreateDirectory(_storageDir);

        var services = new ServiceCollection();
        new DicomSetupBuilder().RegisterServices(s => s.AddFellowOakDicom()).Build();

        var logger = NullLogger.Instance;
        var index = new InstanceIndex(connectionString, logger);
        index.EnsureSchemaAsync().GetAwaiter().GetResult();

        var fileStorage = new FileStorage(_storageDir, logger);
        var storeHandler = new StoreHandler(fileStorage, index, logger);
        ScpServiceRuntime.StoreHandler = storeHandler;
        StorageSopClassRegistry.Configured = new[] { "*" };

        _server = DicomServerFactory.Create<DicomScpService>(port);
    }

    /// <summary>创建并启动一个测试 SCP 宿主（随机可用端口）。</summary>
    public static TestScpHost Start(string connectionString, string? aeTitle = null)
    {
        var port = GetFreePort();
        var dir = Path.Combine(Path.GetTempPath(), "ois-dicomscp-tests", Guid.NewGuid().ToString("N"));
        return new TestScpHost(port, aeTitle ?? "OIS", dir, connectionString);
    }

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        _server?.Dispose();
        try
        {
            if (Directory.Exists(_storageDir))
                Directory.Delete(_storageDir, recursive: true);
        }
        catch (IOException)
        {
            // 忽略清理失败
        }
        await Task.CompletedTask;
    }
}
