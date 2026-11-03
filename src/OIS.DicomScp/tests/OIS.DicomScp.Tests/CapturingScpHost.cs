using System.Text;
using FellowOakDicom;
using FellowOakDicom.Network;
using Microsoft.Extensions.DependencyInjection;

namespace OIS.DicomScp.Tests;

/// <summary>
/// 测试用远端 SCP 宿主：捕获 C-ECHO / C-STORE 请求。
/// C-STORE 收到的 <see cref="DicomDataset"/> 与原始字节会被记录下来（供 AC-04/AC-08 断言"转发内容=原文、UID 不篡改"）。
/// </summary>
public sealed class CapturingScpHost : IAsyncDisposable
{
    private readonly IDicomServer _server;
    private readonly object _lock = new();

    public int Port { get; }
    public string AeTitle { get; }
    private int _echoCount;

    // C-ECHO 收到的请求数（用于 AC-02/AC-03 断言）。
    public int EchoCount => _echoCount;

    // C-STORE 收到的请求（dataset 与原始字节）。
    public List<DicomDataset> ReceivedDatasets { get; } = new();
    public List<byte[]> ReceivedRawBytes { get; } = new();

    private CapturingScpHost(int port, string aeTitle)
    {
        Port = port;
        AeTitle = aeTitle;
        var services = new ServiceCollection();
        new DicomSetupBuilder().RegisterServices(s => s.AddFellowOakDicom()).Build();
        _server = DicomServerFactory.Create<CaptureScpService>(port);
        CaptureScpService.Runtime = this;
    }

    /// <summary>创建并启动一个捕获型远端 SCP（随机可用端口）。</summary>
    public static CapturingScpHost Start(string? aeTitle = null)
    {
        var port = GetFreePort();
        return new CapturingScpHost(port, aeTitle ?? "PACS");
    }

    internal void RecordEcho() => Interlocked.Increment(ref _echoCount);

    internal void RecordStore(DicomDataset dataset, byte[] raw)
    {
        lock (_lock)
        {
            ReceivedDatasets.Add(dataset);
            ReceivedRawBytes.Add(raw);
        }
    }

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public ValueTask DisposeAsync()
    {
        _server?.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class CaptureScpService : DicomService, IDicomServiceProvider, IDicomCEchoProvider, IDicomCStoreProvider
    {
        public static CapturingScpHost? Runtime { get; set; }

        public CaptureScpService(
            INetworkStream stream,
            Encoding fallbackEncoding,
            Microsoft.Extensions.Logging.ILogger logger,
            DicomServiceDependencies dependencies)
            : base(stream, fallbackEncoding, logger, dependencies)
        {
        }

        public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request)
        {
            Runtime?.RecordEcho();
            return Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));
        }

        public Task<DicomCStoreResponse> OnCStoreRequestAsync(DicomCStoreRequest request)
        {
            // 记录原始字节（无损对照）与 dataset。
            byte[]? raw = null;
            try
            {
                using var ms = new MemoryStream();
                var file = request.File;
                if (file is not null)
                {
                    file.Save(ms);
                    raw = ms.ToArray();
                }
            }
            catch
            {
                // 记录失败不阻塞响应
            }
            Runtime?.RecordStore(request.Dataset, raw ?? Array.Empty<byte>());
            return Task.FromResult(new DicomCStoreResponse(request, DicomStatus.Success));
        }

        public Task OnCStoreRequestExceptionAsync(string tempFileName, Exception e) => Task.CompletedTask;
        public Task OnConnectionClosedAsync(Exception? exception) => Task.CompletedTask;

        public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
        {
            foreach (var pc in association.PresentationContexts)
            {
                var isVerification = pc.AbstractSyntax == DicomUID.Verification;
                var isStorage = pc.AbstractSyntax?.UID is not null && IsStorage(pc.AbstractSyntax.UID);
                if (isVerification || isStorage)
                    pc.AcceptTransferSyntaxes(pc.GetTransferSyntaxes().ToArray());
                else
                    pc.SetResult(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
            }
            return SendAssociationAcceptAsync(association);
        }

        private static bool IsStorage(string uid)
            => uid == DicomUID.CTImageStorage.UID
               || uid == DicomUID.RTStructureSetStorage.UID
               || uid == DicomUID.RTPlanStorage.UID
               || uid == DicomUID.RTDoseStorage.UID
               || uid == DicomUID.MRImageStorage.UID;

        public Task OnReceiveAssociationReleaseRequestAsync() => SendAssociationReleaseResponseAsync();
        public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason) { }
        public void OnConnectionClosed(Exception exception) { }
    }
}