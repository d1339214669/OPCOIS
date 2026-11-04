using FellowOakDicom;
using FellowOakDicom.Network;
using Microsoft.Extensions.Logging;
using OIS.DicomScp.Index;
using OIS.DicomScp.Scp;
using OIS.DicomScp.Storage;

namespace OIS.DicomScp;

/// <summary>
/// 本地 DICOM SCP 服务宿主封装（task-003 spec §6.1/§6.2，AC-08）。
/// 供 WPF 管理控制台「服务状态」页签与 CLI 复用：启动/停止本地 DicomServer（fo-dicom），
/// 内部完成幂等建表、文件存储、存储编排器注入与 fo-dicom 服务容器初始化（与 CLI 同一套组装逻辑）。
/// 线程/异常安全：DICOM/DB 调用在后台 Task，StartAsync/StopAsync 用信号量串行化，避免双监听（AC-08 幂等）。
/// </summary>
public interface IScpHost
{
    /// <summary>
    /// 启动本地 DICOM SCP（监听 <see cref="Port"/>）。
    /// 幂等：已运行时抛 <see cref="ScpAlreadyRunningException"/>（AC-08：明确提示「已运行」，不重复监听、不崩）。
    /// </summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// 停止本地 DICOM SCP 并释放监听端口。
    /// 未运行时抛 <see cref="InvalidOperationException"/>（失败必须可见，AC-09）。
    /// </summary>
    Task StopAsync();

    /// <summary>是否正在运行。</summary>
    bool IsRunning { get; }

    /// <summary>监听 TCP 端口（来自配置）。</summary>
    int Port { get; }
}

/// <summary>
/// <see cref="ScpHost"/> 实现：封装 <see cref="DicomServer"/> 的启动/停止生命周期。
/// 注意：存储编排器经 <see cref="ScpServiceRuntime"/>（fo-dicom 静态注入）共享，
/// 同一进程内应只维护一个运行中的 <see cref="ScpHost"/>（CLI 与 WPF 各自独立进程，天然满足）。
/// </summary>
public sealed class ScpHost : IScpHost, IAsyncDisposable
{
    private readonly string _storageDirectory;
    private readonly string _connectionString;
    private readonly string _aeTitle;
    private readonly IReadOnlyCollection<string> _storageSopClasses;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IDicomServer? _server;

    /// <inheritdoc />
    public int Port { get; }

    /// <inheritdoc />
    public bool IsRunning => _server is not null;

    /// <summary>
    /// 创建 SCP 宿主（尚未启动）。
    /// </summary>
    /// <param name="storageDirectory">DICOM 实例存储根目录（来自配置 Storage:Directory）。</param>
    /// <param name="connectionString">PostgreSQL 连接串（来自配置 Postgres:ConnectionString）。</param>
    /// <param name="port">监听 TCP 端口（1-65535，来自配置 Scp:Port）。</param>
    /// <param name="aeTitle">SCP 对外 AET（来自配置 Scp:AeTitle）。</param>
    /// <param name="storageSopClasses">Storage SOP Class 白名单（"*" 表示全部，来自配置 Scp:StorageServiceSopClasses）。</param>
    /// <param name="logger">日志器。</param>
    public ScpHost(
        string storageDirectory,
        string connectionString,
        int port,
        string aeTitle,
        IReadOnlyCollection<string> storageSopClasses,
        ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(storageDirectory))
            throw new ArgumentException("存储目录不能为空", nameof(storageDirectory));
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("PostgreSQL 连接串不能为空", nameof(connectionString));
        if (string.IsNullOrWhiteSpace(aeTitle))
            throw new ArgumentException("AET 不能为空", nameof(aeTitle));
        if (storageSopClasses is null || storageSopClasses.Count == 0)
            throw new ArgumentException("Storage SOP Class 白名单不能为空", nameof(storageSopClasses));
        if (port is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), port, "端口须为 1-65535");
        if (logger is null)
            throw new ArgumentNullException(nameof(logger));

        _storageDirectory = storageDirectory;
        _connectionString = connectionString;
        _aeTitle = aeTitle;
        _storageSopClasses = storageSopClasses;
        _logger = logger;
        Port = port;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsRunning)
                throw new ScpAlreadyRunningException($"DICOM SCP 已在运行（端口 {Port}），请勿重复启动");

            // 幂等建表（与 CLI 启动路径一致，AC-02 建表幂等）。
            var index = new InstanceIndex(_connectionString, _logger);
            await index.EnsureSchemaAsync().ConfigureAwait(false);

            // 组装存储 + 编排 + fo-dicom 静态运行时（与 CLI 同一套组装逻辑，行为一致）。
            var fileStorage = new FileStorage(_storageDirectory, _logger);
            var storeHandler = new StoreHandler(fileStorage, index, _logger);
            StorageSopClassRegistry.Configured = _storageSopClasses;
            ScpServiceRuntime.StoreHandler = storeHandler;

            // 初始化 fo-dicom 服务容器（同进程内重复 Build() 安全，既有测试已多进程内多次调用验证）。
            var setup = new DicomSetupBuilder();
            setup.RegisterServices(s => s.AddFellowOakDicom());
            setup.Build();

            _server = DicomServerFactory.Create<DicomScpService>(Port);
            _logger.LogInformation("DICOM SCP (ScpHost) 已启动，监听 0.0.0.0:{Port} (AET={AeTitle})", Port, _aeTitle);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsRunning)
                throw new InvalidOperationException("DICOM SCP 未运行，无法停止");

            _server?.Dispose();
            _server = null;
            _logger.LogInformation("DICOM SCP (ScpHost) 已停止，端口 {Port} 已释放", Port);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>释放资源：若仍在运行则先停止。</summary>
    public async ValueTask DisposeAsync()
    {
        if (IsRunning)
            await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}

/// <summary>
/// 重复启动本地 DICOM SCP 时抛出（AC-08 幂等语义：明确提示「已运行」，调用方据此显示可读状态而非崩溃）。
/// </summary>
public sealed class ScpAlreadyRunningException : Exception
{
    public ScpAlreadyRunningException(string message) : base(message)
    {
    }
}
