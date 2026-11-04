using Microsoft.Extensions.Logging;
using OIS.DicomScp;
using OIS.DicomScp.Index;
using OIS.DicomScp.Scu;
using OIS.DicomScp.Storage;
using OIS.DicomScp.Wpf.ViewModels;

namespace OIS.DicomScp.Wpf.Composition;

/// <summary>
/// WPF 管理控制台的服务组装（依赖注入手工版，AC-01）：
/// appsettings.json（<see cref="IConfigStore"/>）→ Core 服务（索引/日志/存储/SCU/SCP 宿主）→ 5 个 ViewModel。
/// 与 CLI 同一套默认值回退链（端口 11112 / AET OIS / 存储 D:\OIS\storage / 本机 PG trust），行为一致。
/// </summary>
public sealed class ConsoleServices : IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ScpHost _scpHost;

    private ConsoleServices(
        IConfigStore configStore,
        IInstanceQuery instanceQuery,
        IForwardLogQuery forwardLogQuery,
        IDicomScuService scu,
        ScpHost scpHost,
        string storageRoot,
        ILoggerFactory loggerFactory,
        InstancesViewModel instances,
        InstanceDetailViewModel detail,
        RemoteScpsViewModel remoteScps,
        ForwardingViewModel forwarding,
        ServiceStatusViewModel serviceStatus)
    {
        ConfigStore = configStore;
        InstanceQuery = instanceQuery;
        ForwardLogQuery = forwardLogQuery;
        Scu = scu;
        _scpHost = scpHost;
        StorageRoot = storageRoot;
        _loggerFactory = loggerFactory;
        Instances = instances;
        Detail = detail;
        RemoteScps = remoteScps;
        Forwarding = forwarding;
        ServiceStatus = serviceStatus;
    }

    /// <summary>配置读写（appsettings.json 原子写，AC-04）。</summary>
    public IConfigStore ConfigStore { get; }

    /// <summary>实例只读查询（dicom_instance，AC-02）。</summary>
    public IInstanceQuery InstanceQuery { get; }

    /// <summary>转发日志只读查询（forward_log，AC-07）。</summary>
    public IForwardLogQuery ForwardLogQuery { get; }

    /// <summary>出站 SCU 服务（C-ECHO / C-STORE，AC-05/AC-07）。</summary>
    public IDicomScuService Scu { get; }

    /// <summary>SCP 服务宿主（AC-08 启动/停止，幂等）。</summary>
    public IScpHost ScpHost => _scpHost;

    /// <summary>DICOM 实例存储根目录（appsettings.json Storage.Directory）。</summary>
    public string StorageRoot { get; }

    /// <summary>实例浏览 ViewModel。</summary>
    public InstancesViewModel Instances { get; }

    /// <summary>详情核对 ViewModel。</summary>
    public InstanceDetailViewModel Detail { get; }

    /// <summary>远程 SCP 配置 ViewModel。</summary>
    public RemoteScpsViewModel RemoteScps { get; }

    /// <summary>转发 + 历史 ViewModel。</summary>
    public ForwardingViewModel Forwarding { get; }

    /// <summary>服务状态 ViewModel。</summary>
    public ServiceStatusViewModel ServiceStatus { get; }

    /// <summary>
    /// 组装全部服务与 ViewModel（窗口打开前调用）。
    /// 配置缺失时用与 CLI 相同的默认回退；PostgreSQL 建表失败仅记日志（后续查询会在 UI 中可见报错，AC-09）。
    /// </summary>
    public static ConsoleServices Create()
    {
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        var logger = loggerFactory.CreateLogger("OIS.DicomScp.Wpf");

        var configPath = System.IO.Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var configStore = new ConfigStore(configPath, logger);
        var cfg = configStore.LoadAsync().GetAwaiter().GetResult();

        // 默认值回退链与 CLI 完全一致（AC-01：配置缺失不静默出错行为，而是统一默认值 + 控制台可查）。
        var port = cfg.Scp?.Port is > 0 and <= 65535 ? cfg.Scp!.Port!.Value : 11112;
        var aeTitle = string.IsNullOrWhiteSpace(cfg.Scp?.AeTitle) ? "OIS" : cfg.Scp!.AeTitle!.Trim();
        var storageDir = string.IsNullOrWhiteSpace(cfg.Storage?.Directory) ? System.IO.Path.Combine("D:", "OIS", "storage") : cfg.Storage!.Directory!;
        var connString = string.IsNullOrWhiteSpace(cfg.Postgres?.ConnectionString)
            ? "Host=localhost;Port=5432;Username=postgres;Database=dicom"
            : cfg.Postgres!.ConnectionString!;
        var whitelist = cfg.Scp?.StorageServiceSopClasses is { Count: > 0 }
            ? cfg.Scp!.StorageServiceSopClasses!
            : new List<string> { "*" };
        var fallbackCallingAe = string.IsNullOrWhiteSpace(cfg.Scu?.CallingAeTitle) ? aeTitle : cfg.Scu!.CallingAeTitle!.Trim();

        var index = new InstanceIndex(connString, logger);
        try
        {
            // 幂等建表（与 CLI 启动一致）；失败不阻止控制台启动（PostgreSQL 不可用时，
            // 实例查询/实例数会在 UI 中报可见错误，AC-09 失败必须可见）。
            index.EnsureSchemaAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "启动时 PostgreSQL 建表检查失败（控制台继续启动，实例查询将报可见错误）");
        }

        var forwardLog = new ForwardLog(connString, logger);
        var fileStorage = new FileStorage(storageDir, logger);
        var scu = new DicomScuService(fileStorage, index, forwardLog, fallbackCallingAe, logger);
        var scpHost = new ScpHost(storageDir, connString, port, aeTitle, whitelist, logger);

        return new ConsoleServices(
            configStore,
            index,
            forwardLog,
            scu,
            scpHost,
            storageDir,
            loggerFactory,
            new InstancesViewModel(index),
            new InstanceDetailViewModel(storageDir),
            new RemoteScpsViewModel(configStore),
            new ForwardingViewModel(configStore, scu, index, forwardLog),
            new ServiceStatusViewModel(configStore, scpHost, index));
    }

    /// <summary>释放 SCP 宿主（若仍在运行则先停止）与日志工厂；释放失败记日志（不静默吞）。</summary>
    public void Dispose()
    {
        try
        {
            _scpHost.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _loggerFactory
                .CreateLogger("OIS.DicomScp.Wpf")
                .LogWarning(ex, "Dispose: 停止/释放 SCP 宿主失败（进程退出中）");
        }

        _loggerFactory.Dispose();
    }
}
