namespace OIS.DicomScp.Scp;

/// <summary>
/// 运行时共享状态：让 fo-dicom 默认构造的 <see cref="DicomScpService"/> 能拿到注入的存储编排器。
/// fo-dicom 的 <c>DicomServer.Create&lt;TService&gt;</c> 使用标准构造函数创建服务实例，
/// 通过静态运行时把依赖传入，规避 fo-dicom 内部 DI 差异。
/// </summary>
public static class ScpServiceRuntime
{
    /// <summary>当前生效的存储编排器（C-STORE 处理逻辑）。</summary>
    public static IStoreHandler? StoreHandler { get; set; }
}
