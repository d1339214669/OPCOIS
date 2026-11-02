using System.Text.Json;
using FellowOakDicom;
using FellowOakDicom.Network;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OIS.DicomScp.Index;
using OIS.DicomScp.Scp;
using OIS.DicomScp.Storage;

namespace OIS.DicomScp;

/// <summary>
/// DICOM Storage SCP 核心存储层入口。
/// 读取配置 → 幂等建表 → 启动 fo-dicom SCP → 等待 Ctrl+C。
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        var logger = loggerFactory.CreateLogger("OIS.DicomScp");

        try
        {
            // 1) 读取配置（appsettings.json，AC-01）。手动读 JSON，避免额外依赖版本冲突。
            var cfg = LoadConfig();
            var port = cfg.Scp.Port is > 0 and <= 65535 ? cfg.Scp.Port!.Value : 11112;
            var aeTitle = string.IsNullOrWhiteSpace(cfg.Scp.AeTitle) ? "OIS" : cfg.Scp.AeTitle.Trim();
            var storageDir = string.IsNullOrWhiteSpace(cfg.Storage.Directory)
                ? Path.Combine(@"D:", "OIS", "storage")
                : cfg.Storage.Directory;
            var connString = string.IsNullOrWhiteSpace(cfg.Postgres.ConnectionString)
                ? "Host=localhost;Port=5432;Username=postgres;Database=dicom"
                : cfg.Postgres.ConnectionString;

            logger.LogInformation(
                "配置: Port={Port}, AeTitle={AeTitle}；存储目录={Storage}；Storage SOP 白名单=[{Whitelist}]",
                port, aeTitle, Path.GetFullPath(storageDir), string.Join(", ", cfg.Scp.StorageServiceSopClasses ?? new List<string> { "*" }));

            // 2) 幂等建表（AC-02）。
            var index = new InstanceIndex(connString, logger);
            await index.EnsureSchemaAsync();
            logger.LogInformation("PostgreSQL 表 dicom_instance 就绪");

            // 3) 组装存储 + 编排 + 白名单。
            var fileStorage = new FileStorage(storageDir, logger);
            var storeHandler = new StoreHandler(fileStorage, index, logger);
            StorageSopClassRegistry.Configured = cfg.Scp.StorageServiceSopClasses ?? new List<string> { "*" };
            ScpServiceRuntime.StoreHandler = storeHandler;

            // 4) 初始化 fo-dicom 服务容器并启动 SCP。
            var setup = new DicomSetupBuilder();
            setup.RegisterServices(s => s.AddFellowOakDicom());
            setup.Build();

            using var cts = new CancellationTokenSource();
            using var server = DicomServerFactory.Create<DicomScpService>(port);

            logger.LogInformation("DICOM Storage SCP 已启动，监听 0.0.0.0:{Port} (AET={AeTitle})", port, aeTitle);

            // 5) 等待退出信号。
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                logger.LogInformation("收到退出信号，正在停止...");
                cts.Cancel();
            };

            await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);
            return 0;
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("服务已停止");
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DICOM SCP 启动失败: {Message}", ex.Message);
            return 1;
        }
    }

    private static ConfigRoot LoadConfig()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path))
        {
            // 配置文件缺失时使用默认配置（端口 11112、AET OIS、D:\OIS\storage、本机 PG）。
            return new ConfigRoot();
        }
        return JsonSerializer.Deserialize<ConfigRoot>(File.ReadAllText(path)) ?? new ConfigRoot();
    }

    private sealed class ConfigRoot
    {
        public ScpSection Scp { get; set; } = new();
        public StorageSection Storage { get; set; } = new();
        public PostgresSection Postgres { get; set; } = new();
    }

    private sealed class ScpSection
    {
        public int? Port { get; set; }
        public string? AeTitle { get; set; }
        public List<string>? StorageServiceSopClasses { get; set; }
    }

    private sealed class StorageSection
    {
        public string? Directory { get; set; }
    }

    private sealed class PostgresSection
    {
        public string? ConnectionString { get; set; }
    }
}
