using FellowOakDicom;
using Microsoft.Extensions.Logging;
using OIS.DicomScp.Index;
using OIS.DicomScp.Storage;

namespace OIS.DicomScp.Scp;

/// <summary>
/// C-STORE 编排实现：落盘（先写文件）→ 入库（后写数据库索引）。
/// </summary>
/// <remarks>
/// 顺序：<b>先写文件，后写索引</b>（见 <see cref="FileStorage"/> 说明）。
/// 同 SOPInstanceUID 去重：先查索引，命中则跳过写盘与入库（AC-05，同一 ID 覆盖或跳过，本实现为跳过）。
/// 缺省/空 UID 域置 null 入库，绝不臆造（AC-06/AC-08）。
/// </remarks>
public sealed class StoreHandler : IStoreHandler
{
    private readonly FileStorage _fileStorage;
    private readonly InstanceIndex _index;
    private readonly ILogger _logger;

    public StoreHandler(FileStorage fileStorage, InstanceIndex index, ILogger logger)
    {
        _fileStorage = fileStorage ?? throw new ArgumentNullException(nameof(fileStorage));
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<bool> HandleAsync(DicomDataset dataset, string sopInstanceUid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sopInstanceUid))
        {
            // AC-07：必填 UID 缺失。不静默吞错：记录并抛错，由上层返回失败状态。
            throw new InvalidOperationException("SOPInstanceUID 缺失，无法存储，拒绝该实例");
        }

        // AC-05 去重：同 SOPInstanceUID 已存在则跳过（不产生第二个文件/第二行）。
        if (await _index.ExistsBySopInstanceUidAsync(sopInstanceUid, ct))
        {
            _logger.LogInformation("去重：SOPInstanceUID {Uid} 已存在，跳过本次存储", sopInstanceUid);
            return false;
        }

        // 1) 生成内部 ID，并以 DICOM 文件格式序列化为字节（无损闭环）。
        var internalId = FileStorage.GenerateInternalId();
        byte[] bytes = await SerializeFileAsync(dataset, ct);

        // 2) 先写文件。
        var fileSize = _fileStorage.Store(internalId, bytes);
        var relativePath = FileStorage.RelativePathOf(internalId);

        // 3) 后写索引。
        var meta = ToMetadata(dataset, internalId, relativePath, fileSize);
        var inserted = await _index.InsertAsync(meta, ct);

        return inserted;
    }

    /// <summary>把 C-STORE 收到的 DicomDataset 序列化为标准 DICOM 文件字节（无损闭环，AC-04）。</summary>
    private static async Task<byte[]> SerializeFileAsync(DicomDataset dataset, CancellationToken ct)
    {
        // 保留原 transfer syntax / 编码，避免重编码改变数据。
        var file = new DicomFile(dataset);
        await using var ms = new MemoryStream();
        await file.SaveAsync(ms);
        return ms.ToArray();
    }

    /// <summary>从 DicomDataset 抽取入库元数据（AC-06/AC-08），缺失字段置 null。</summary>
    private static InstanceMetadata ToMetadata(DicomDataset ds, string internalId, string relativePath, long fileSize)
    {
        return new InstanceMetadata
        {
            InternalId = internalId,
            SopInstanceUid = GetUid(ds, DicomTag.SOPInstanceUID) ?? string.Empty,
            SopClassUid = GetUid(ds, DicomTag.SOPClassUID),
            PatientId = GetString(ds, DicomTag.PatientID),
            PatientName = GetString(ds, DicomTag.PatientName),
            StudyInstanceUid = GetUid(ds, DicomTag.StudyInstanceUID),
            SeriesInstanceUid = GetUid(ds, DicomTag.SeriesInstanceUID),
            Modality = GetString(ds, DicomTag.Modality),
            StudyDate = GetString(ds, DicomTag.StudyDate),
            StudyTime = GetString(ds, DicomTag.StudyTime),
            StudyDescription = GetString(ds, DicomTag.StudyDescription),
            SeriesNumber = GetString(ds, DicomTag.SeriesNumber),
            InstanceNumber = GetString(ds, DicomTag.InstanceNumber),
            FilePath = relativePath.Replace('\\', '/'),
            FileSizeBytes = fileSize,
        };
    }

    /// <summary>读取 UID 类型标签，返回原文（不修剪/篡改，AC-08），缺失或空返回 null。</summary>
    private static string? GetUid(DicomDataset ds, DicomTag tag)
        => TryGet(ds, tag, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    /// <summary>读取字符串标签原值，缺失/空返回 null（不臆造，AC-06）。</summary>
    private static string? GetString(DicomDataset ds, DicomTag tag)
        => TryGet(ds, tag, out var v) && v.Length > 0 ? v : null;

    private static bool TryGet(DicomDataset ds, DicomTag tag, out string value)
    {
        if (ds.TryGetString(tag, out var v))
        {
            value = v;
            return true;
        }
        value = string.Empty;
        return false;
    }
}
