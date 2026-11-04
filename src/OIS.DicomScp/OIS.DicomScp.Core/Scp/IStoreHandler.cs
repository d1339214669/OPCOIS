using FellowOakDicom;

namespace OIS.DicomScp.Scp;

/// <summary>
/// 落盘 + 入库的编排接口，由 <see cref="DicomScpService"/> 调用。
/// 解耦 SCP 网络层与存储/索引逻辑，便于测试与替换。
/// </summary>
public interface IStoreHandler
{
    /// <summary>
    /// 处理一次 C-STORE：落盘 + 入库，返回是否成功入库（false 表示同 SOPInstanceUID 去重命中已有）。
    /// </summary>
    Task<bool> HandleAsync(DicomDataset dataset, string sopInstanceUid, CancellationToken ct = default);
}
