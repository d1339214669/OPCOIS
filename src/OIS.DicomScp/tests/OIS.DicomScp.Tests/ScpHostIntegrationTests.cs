using System.Net.Sockets;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using OIS.DicomScp;
using Xunit;

namespace OIS.DicomScp.Tests;

/// <summary>
/// task-003 AC-08 集成测试：IScpHost（本地 DicomServer 封装）启停/幂等/重启（真实端口 + fo-dicom 对端 C-ECHO + 真实 PG 建表）。
/// 属于串行集合（与 TestScpHost 测试共享 fo-dicom 静态运行时，必须串行）。
/// </summary>
[Collection("ScuScpIntegration")]
public sealed class ScpHostIntegrationTests : IAsyncLifetime
{
    private const string ConnString = "Host=localhost;Port=5432;Username=postgres;Database=dicom";
    private readonly List<string> _tempDirs = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var dir in _tempDirs)
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        await Task.CompletedTask;
    }

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ois-scp-host-tests", Guid.NewGuid().ToString("N"));
        _tempDirs.Add(dir);
        return dir;
    }

    /// <summary>AC-08：启动后 C-ECHO=Success；重复启动 → ScpAlreadyRunningException（明确「已运行」，不双监听）；
    /// 停止后端口释放（连接拒绝）；停止后可再次启动（可重启）。</summary>
    [Fact]
    public async Task ScpHost_Start_Echo_DoubleStartThrows_Stop_Restart()
    {
        var port = GetFreePort();
        var host = new ScpHost(NewTempDir(), ConnString, port, "OIS", new[] { "*" }, new TestLogger());
        Assert.False(host.IsRunning);
        Assert.Equal(port, host.Port);

        await host.StartAsync();
        Assert.True(host.IsRunning);

        // 启动后对 127.0.0.1:port C-ECHO = Success。
        var client = DicomClientFactory.Create("127.0.0.1", port, false, "TEST", "OIS");
        await client.AddRequestAsync(new DicomCEchoRequest());
        await client.SendAsync();

        // 幂等：已运行时再启动 → 明确抛 ScpAlreadyRunningException（不崩、不双监听）。
        await Assert.ThrowsAsync<ScpAlreadyRunningException>(() => host.StartAsync());

        // 停止：监听释放（连接被拒 → SocketException 10061；注意 Assert.Throws 要求精确类型，不能用基类 IOException）。
        await host.StopAsync();
        Assert.False(host.IsRunning);
        await Assert.ThrowsAsync<SocketException>(async () =>
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", port);
            tcp.Close();
        });

        // 停止后可再次启动（可重启），再次 C-ECHO 仍 Success。
        await host.StartAsync();
        Assert.True(host.IsRunning);
        var client2 = DicomClientFactory.Create("127.0.0.1", port, false, "TEST", "OIS");
        await client2.AddRequestAsync(new DicomCEchoRequest());
        await client2.SendAsync();

        await host.StopAsync();
        await host.DisposeAsync();
    }

    /// <summary>AC-08/AC-09：未运行时停止 → 明确报错（失败必须可见）。</summary>
    [Fact]
    public async Task ScpHost_Stop_When_Not_Running_Throws()
    {
        var host = new ScpHost(NewTempDir(), ConnString, GetFreePort(), "OIS", new[] { "*" }, new TestLogger());
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StopAsync());
        await host.DisposeAsync();
    }

    private static int GetFreePort()
    {
        var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
