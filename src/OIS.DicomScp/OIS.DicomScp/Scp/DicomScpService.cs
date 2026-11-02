using System.Text;
using FellowOakDicom;
using FellowOakDicom.Network;
using Microsoft.Extensions.Logging;

namespace OIS.DicomScp.Scp;

/// <summary>
/// fo-dicom 的 DICOM Storage SCP 服务，监听 TCP 端口，正确处理 C-ECHO 与 C-STORE。
/// 网络异常/断连不崩溃：所有异常在 C-STORE handler 内捕获并记录，返回失败状态而非抛栈。
/// </summary>
public sealed class DicomScpService : DicomService, IDicomServiceProvider, IDicomCEchoProvider, IDicomCStoreProvider
{
    private static readonly ILogger<DicomScpService> ScpLogger =
        Microsoft.Extensions.Logging.Abstractions.NullLogger<DicomScpService>.Instance;

    /// <summary>fo-dicom 使用的标准构造函数（由 DicomServer 反射调用）。</summary>
    public DicomScpService(
        INetworkStream stream,
        Encoding fallbackEncoding,
        ILogger logger,
        DicomServiceDependencies dependencies)
        : base(stream, fallbackEncoding, logger, dependencies)
    {
    }

    /// <inheritdoc />
    public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request)
    {
        ScpLogger.LogInformation("收到 C-ECHO");
        return Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));
    }

    /// <inheritdoc />
    public async Task<DicomCStoreResponse> OnCStoreRequestAsync(DicomCStoreRequest request)
    {
        var handler = ScpServiceRuntime.StoreHandler;
        var sopInstanceUid = request.SOPInstanceUID?.UID ?? string.Empty;
        try
        {
            if (handler is null)
                throw new InvalidOperationException("C-STORE 处理逻辑未注入（ScpServiceRuntime.StoreHandler 为空）");

            await handler.HandleAsync(request.Dataset, sopInstanceUid);
            // 无论新入库还是去重命中，都返回 Success（AC-05：重复 C-STORE 不报错）。
            return new DicomCStoreResponse(request, DicomStatus.Success);
        }
        catch (Exception ex)
        {
            // DOMAIN.md「失败必须可见」：记录错误并返回失败状态，不静默吞掉，也不让异常升级到连接层。
            ScpLogger.LogError(ex, "C-STORE 处理失败: {Uid}", sopInstanceUid);
            return new DicomCStoreResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    /// <inheritdoc />
    public Task OnCStoreRequestExceptionAsync(string tempFileName, Exception e)
    {
        ScpLogger.LogError(e, "C-STORE 请求异常（临时文件 {Temp}）: {Message}", tempFileName, e.Message);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnConnectionClosedAsync(Exception? exception)
    {
        if (exception is not null)
            ScpLogger.LogDebug(exception, "连接关闭：{Message}", exception.Message);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        foreach (var pc in association.PresentationContexts)
        {
            // 只接受 Verification（C-ECHO）与 Storage SOP Class（C-STORE，受配置白名单过滤）。
            var isVerification = pc.AbstractSyntax == DicomUID.Verification;
            var isAllowedStorage =
                StorageSopClassRegistry.IsAllowed(pc.AbstractSyntax?.UID, StorageSopClassRegistry.Configured);

            if (isVerification || isAllowedStorage)
            {
                pc.AcceptTransferSyntaxes(pc.GetTransferSyntaxes().ToArray());
            }
            else
            {
                pc.SetResult(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
            }
        }

        await SendAssociationAcceptAsync(association);
    }

    /// <inheritdoc />
    public Task OnReceiveAssociationReleaseRequestAsync() => SendAssociationReleaseResponseAsync();

    /// <inheritdoc />
    public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
        => ScpLogger.LogWarning("收到 ABORT: {Source}/{Reason}", source, reason);

    /// <inheritdoc />
    public void OnConnectionClosed(Exception exception)
    {
        if (exception is not null)
            ScpLogger.LogDebug(exception, "连接关闭（IDicomService）");
    }
}
