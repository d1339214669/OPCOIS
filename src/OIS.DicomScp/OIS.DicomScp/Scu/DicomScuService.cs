using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using Microsoft.Extensions.Logging;
using OIS.DicomScp.Index;
using OIS.DicomScp.Storage;

namespace OIS.DicomScp.Scu;

/// <summary>
/// fo-dicom 5.2.6 的 SCU 实现：C-ECHO（连通性验证）+ C-STORE（转发本地已存实例）。
///
/// <para>要点：
/// <list type="bullet">
/// <item>成功与否<b>必须看响应 <c>response.Status</c></b>，不能把「SendAsync 不抛异常」当成功（AC-02/AC-04/AC-07）。
/// 通过 <c>OnResponseReceived</c> 回调拿到 <c>DicomResponse</c> 后读其 <c>Status</c>。</item>
/// <item>转发内容 = 本地存储文件的<b>原字节/原标签</b>（<c>DicomCStoreRequest(File)</c> 原样发送，不重编码、不改 UID，AC-04/AC-08 无损闭环）。</item>
/// <item>任何异常（连接失败/超时/AET 拒绝/读文件失败）都捕获并转为 <c>Failed</c> 结果 + <c>forward_log</c> 写 <c>status='failed'</c>（AC-05/AC-07「失败必须可见」）。</item>
/// </list>
/// </para>
/// </summary>
public sealed class DicomScuService : IDicomScuService
{
    private readonly FileStorage _fileStorage;
    private readonly InstanceIndex _index;
    private readonly IForwardLog _forwardLog;
    private readonly ILogger _logger;
    private readonly string _fallbackCallingAe;

    /// <summary>
    /// 构造 SCU 服务。
    /// </summary>
    /// <param name="fileStorage">本地文件存储（解析绝对路径读文件）。</param>
    /// <param name="index">索引访问（按 SOPInstanceUID 定位内部 ID + file_path，AC-06）。</param>
    /// <param name="forwardLog">转发记录写入器（AC-05）。</param>
    /// <param name="fallbackCallingAe">SCU 侧 fallback calling AE（Scu.CallingAeTitle 或 Scp.AeTitle，可为空）。</param>
    /// <param name="logger">日志器。</param>
    public DicomScuService(FileStorage fileStorage, InstanceIndex index, IForwardLog forwardLog, string? fallbackCallingAe, ILogger logger)
    {
        _fileStorage = fileStorage ?? throw new ArgumentNullException(nameof(fileStorage));
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _forwardLog = forwardLog ?? throw new ArgumentNullException(nameof(forwardLog));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _fallbackCallingAe = string.IsNullOrWhiteSpace(fallbackCallingAe) ? string.Empty : fallbackCallingAe.Trim();
    }

    /// <inheritdoc />
    public async Task<EchoResult> EchoAsync(RemoteScpTarget target, CancellationToken ct = default)
    {
        try
        {
            var callingAe = ResolveCallingAe(target);
            _logger.LogInformation("C-ECHO -> {Name} ({CallingAe}@{Host}:{Port} called={CalledAe})",
                target.Name, callingAe, target.Host, target.Port, target.CalledAeTitle);

            var responseState = await SendEchoAsync(target, callingAe, ct);
            if (responseState == DicomStatus.Success)
            {
                _logger.LogInformation("C-ECHO 成功: {Name}", target.Name);
                return new EchoResult(EchoStatus.Success, null);
            }

            // 响应状态非 Success（如 AET 拒绝、SOP 不支持等）——失败可见。
            var reason = $"C-ECHO 响应非 Success: {DicomStatusToString(responseState)}";
            _logger.LogWarning(reason);
            return new EchoResult(EchoStatus.Failed, reason);
        }
        catch (Exception ex)
        {
            // 异常隔离（AC-03/AC-07）：连接拒绝/超时/异常都不让异常升级到调用方，失败可见。
            _logger.LogError(ex, "C-ECHO 失败: {Name} ({Message})", target.Name, ex.Message);
            return new EchoResult(EchoStatus.Failed, ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<ForwardResult> StoreByInternalIdAsync(RemoteScpTarget target, string internalId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(internalId))
        {
            await WriteFailedLogAsync(target, "", "", null, null, null, "内部 ID 为空");
            return new ForwardResult(StoreStatus.Failed, "内部 ID 为空");
        }

        // 解析绝对路径读文件（AC-04/AC-08：原字节/原标签，绝不重编码）。
        var absolutePath = _fileStorage.AbsolutePathOf(internalId.Trim());
        return await ForwardFileAsync(target, internalId.Trim(), absolutePath, ct);
    }

    /// <inheritdoc />
    public async Task<ForwardResult> StoreBySopInstanceUidAsync(RemoteScpTarget target, string sopInstanceUid, CancellationToken ct = default)
    {
        // AC-06：按 SOPInstanceUID 定位内部 ID + file_path；不存在 → Failed，且无转发副作用。
        InstanceMetadata? meta;
        try
        {
            meta = await _index.FindBySopInstanceUidAsync(sopInstanceUid, ct);
        }
        catch (Exception ex)
        {
            var msg = $"SOPInstanceUID 定位查询失败: {ex.Message}";
            _logger.LogError(ex, "{Msg}", msg);
            await WriteFailedLogAsync(target, "", sopInstanceUid, null, null, null, msg);
            return new ForwardResult(StoreStatus.Failed, msg);
        }

        if (meta is null)
        {
            // 未找到——明确返回失败（AC-06），不产生 C-STORE 发送、不产生伪造成功。
            _logger.LogWarning("SOPInstanceUID 未找到: {Uid}", sopInstanceUid);
            await WriteFailedLogAsync(target, "", sopInstanceUid, null, null, null, "未找到该 SOPInstanceUID 的实例");
            return new ForwardResult(StoreStatus.Failed, "未找到该 SOPInstanceUID 的实例");
        }

        // meta.FilePath 是相对存储目录路径，与存储根目录组合解析绝对路径（AC-06）。
        var relative = meta.FilePath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        var absolutePath = Path.GetFullPath(Path.Combine(_fileStorage.Root, relative));
        return await ForwardFileAsync(target, meta.InternalId, absolutePath, ct,
            meta.SopInstanceUid, meta.SopClassUid, meta.StudyInstanceUid, meta.SeriesInstanceUid);
    }

    /// <summary>读文件并 C-STORE 转发，写 forwarding 记录（成功/失败各一行）。</summary>
    private async Task<ForwardResult> ForwardFileAsync(
        RemoteScpTarget target, string internalId, string absolutePath, CancellationToken ct,
        string? sopInstanceUid = null, string? sopClassUid = null, string? studyUid = null, string? seriesUid = null)
    {
        try
        {
            // 1) 读文件（AC-07：文件不存在 → Failed，不伪造 Success）。
            if (!File.Exists(absolutePath))
            {
                var notFound = $"本地存储文件缺失: {absolutePath}";
                _logger.LogWarning("{Msg}", notFound);
                await WriteFailedLogAsync(target, internalId, sopInstanceUid, sopClassUid, studyUid, seriesUid, notFound);
                return new ForwardResult(StoreStatus.Failed, notFound);
            }

            DicomFile file;
            try
            {
                file = DicomFile.Open(absolutePath);
            }
            catch (Exception ex)
            {
                var msg = $"本地存储文件不可读: {absolutePath} ({ex.Message})";
                _logger.LogError(ex, "{Msg}", msg);
                await WriteFailedLogAsync(target, internalId, sopInstanceUid, sopClassUid, studyUid, seriesUid, msg);
                return new ForwardResult(StoreStatus.Failed, msg);
            }

            // 从文件取业务 UID（若调用方未给），用于转发记录。
            sopInstanceUid ??= GetString(file, DicomTag.SOPInstanceUID);
            sopClassUid ??= GetString(file, DicomTag.SOPClassUID);
            studyUid ??= GetString(file, DicomTag.StudyInstanceUID);
            seriesUid ??= GetString(file, DicomTag.SeriesInstanceUID);

            // 2) C-STORE 推送（SDC：DicomCStoreRequest(file) 原样发送，ac-04/08 无损）。
            var callingAe = ResolveCallingAe(target);
            _logger.LogInformation("C-STORE -> {Name} ({CallingAe}@{Host}:{Port} called={CalledAe}) internal={InternalId}",
                target.Name, callingAe, target.Host, target.Port, target.CalledAeTitle, internalId);

            var status = await SendStoreAsync(file, target, callingAe, ct);
            if (status == DicomStatus.Success)
            {
                _logger.LogInformation("C-STORE 转发成功: {InternalId} -> {Name}", internalId, target.Name);
                await _forwardLog.WriteAsync(new ForwardLogEntry
                {
                    RemoteScpName = target.Name,
                    InternalId = internalId,
                    SopInstanceUid = sopInstanceUid,
                    SopClassUid = sopClassUid,
                    StudyInstanceUid = studyUid,
                    SeriesInstanceUid = seriesUid,
                    Status = "success",
                    Detail = "Success",
                }, ct);
                return new ForwardResult(StoreStatus.Success, null);
            }

            // 响应非 Success（C-STORE 失败）——写 failed 记录，失败可见。
            var reason = $"C-STORE 响应非 Success: {DicomStatusToString(status)}";
            _logger.LogWarning(reason);
            await WriteFailedLogAsync(target, internalId, sopInstanceUid, sopClassUid, studyUid, seriesUid, reason);
            return new ForwardResult(StoreStatus.Failed, reason);
        }
        catch (Exception ex)
        {
            // 异常隔离（AC-03/AC-07）：任何异常 → 失败 + failed 记录，不崩。
            _logger.LogError(ex, "C-STORE 转发异常: {InternalId} -> {Name}", internalId, target.Name);
            await WriteFailedLogAsync(target, internalId, sopInstanceUid, sopClassUid, studyUid, seriesUid, ex.Message);
            return new ForwardResult(StoreStatus.Failed, ex.Message);
        }
    }

    /// <summary>发起 C-ECHO 并返回响应状态。</summary>
    private static async Task<DicomStatus> SendEchoAsync(RemoteScpTarget target, string callingAe, CancellationToken ct)
    {
        var client = DicomClientFactory.Create(target.Host, target.Port, false, callingAe, target.CalledAeTitle);
        var echo = new DicomCEchoRequest();
        var tcs = new TaskCompletionSource<DicomStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        echo.OnResponseReceived += (req, res) => tcs.TrySetResult(res.Status);
        await client.AddRequestAsync(echo);
        await client.SendAsync(ct);
        // 若关联被拒绝，SendAsync 抛异常 → 上层 catch。否则等待响应状态。
        return await tcs.Task;
    }

    /// <summary>发起 C-STORE 并返回响应状态（原文件字节原样发送）。</summary>
    private static async Task<DicomStatus> SendStoreAsync(DicomFile file, RemoteScpTarget target, string callingAe, CancellationToken ct)
    {
        var client = DicomClientFactory.Create(target.Host, target.Port, false, callingAe, target.CalledAeTitle);
        var store = new DicomCStoreRequest(file);
        var tcs = new TaskCompletionSource<DicomStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.OnResponseReceived += (req, res) => tcs.TrySetResult(res.Status);
        await client.AddRequestAsync(store);
        await client.SendAsync(ct);
        return await tcs.Task;
    }

    /// <summary>写一条 status='failed' 的转发记录（AC-05 失败必须可见）。</summary>
    private async Task WriteFailedLogAsync(RemoteScpTarget target, string internalId, string? sopUid,
        string? sopClassUid, string? studyUid, string? seriesUid, string detail)
    {
        try
        {
            await _forwardLog.WriteAsync(new ForwardLogEntry
            {
                RemoteScpName = target.Name,
                InternalId = internalId,
                SopInstanceUid = sopUid,
                SopClassUid = sopClassUid,
                StudyInstanceUid = studyUid,
                SeriesInstanceUid = seriesUid,
                Status = "failed",
                Detail = detail,
            });
        }
        catch (Exception logEx)
        {
            // 转发记录写失败本身也应可见（AC-05），但不让它掩盖原始失败原因。
            _logger.LogError(logEx, "写 forward_log 失败记录时出错: {Msg}", logEx.Message);
        }
    }

    /// <summary>
    /// calling AE fallback 链（AC-08 写明）：
    /// 目标.CallingAeTitle → Scu.CallingAeTitle（构造传入 fallback）→ Scp.AeTitle（构造传入）→ 空串（fo-dicom 默认生成）。
    /// </summary>
    private string ResolveCallingAe(RemoteScpTarget target)
    {
        if (!string.IsNullOrWhiteSpace(target.CallingAeTitle))
            return target.CallingAeTitle.Trim();
        return _fallbackCallingAe;
    }

    private static string? GetString(DicomFile file, DicomTag tag)
        => file.Dataset.TryGetString(tag, out var v) && v.Length > 0 ? v : null;

    private static string DicomStatusToString(DicomStatus status)
        => $"{status.Code:X4} {status.Description}";
}
