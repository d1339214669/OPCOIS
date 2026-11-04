using FellowOakDicom;
using FellowOakDicom.Network;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OIS.DicomScp.Index;
using OIS.DicomScp.Scp;
using OIS.DicomScp.Scu;
using OIS.DicomScp.Storage;

namespace OIS.DicomScp;

/// <summary>
/// DICOM Storage SCP 核心存储层入口（task-001）+ 出站 SCU 命令（task-002）。
/// 子命令模式：
///   scp                                    默认：启动 SCP（兼容 task-001 行为）
///   echo-scu --remote &lt;Name&gt;               对 &lt;Name&gt; 远程 SCP 发 C-ECHO
///   store-scu --remote &lt;Name&gt; --id &lt;id&gt;    按内部 ID 推送实例
///   store-scu --remote &lt;Name&gt; --uid &lt;uid&gt;  按 SOPInstanceUID 推送实例
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        var logger = loggerFactory.CreateLogger("OIS.DicomScp");

        try
        {
            // 1) 读取配置（appsettings.json，AC-01/task-001 AC-01）。经 Core 的 IConfigStore 读取：
            // 配置文件缺失时返回默认配置（端口 11112、AET OIS、D:\OIS\storage、本机 PG、无远程目标），与既有行为一致。
            var cfg = await new ConfigStore(
                Path.Combine(AppContext.BaseDirectory, "appsettings.json"), logger).LoadAsync();
            var port = cfg.Scp.Port is > 0 and <= 65535 ? cfg.Scp.Port!.Value : 11112;
            var aeTitle = string.IsNullOrWhiteSpace(cfg.Scp.AeTitle) ? "OIS" : cfg.Scp.AeTitle.Trim();
            var storageDir = string.IsNullOrWhiteSpace(cfg.Storage.Directory)
                ? Path.Combine(@"D:", "OIS", "storage")
                : cfg.Storage.Directory;
            var connString = string.IsNullOrWhiteSpace(cfg.Postgres.ConnectionString)
                ? "Host=localhost;Port=5432;Username=postgres;Database=dicom"
                : cfg.Postgres.ConnectionString;

            // SCU 侧 fallback calling AE（AC-08 fallback 链：目标.CallingAeTitle → Scu.CallingAeTitle → Scp.AeTitle）。
            var scuFallbackCallingAe = string.IsNullOrWhiteSpace(cfg.Scu.CallingAeTitle)
                ? aeTitle
                : cfg.Scu.CallingAeTitle.Trim();

            // 2) 幂等建表（AC-02；含 task-002 追加的 forward_log，IF NOT EXISTS 幂等）。
            var index = new InstanceIndex(connString, logger);
            await index.EnsureSchemaAsync();
            logger.LogInformation("PostgreSQL 表 dicom_instance / forward_log 就绪");

            // 3) 组装存储 + 编排 + SCU 依赖。
            var fileStorage = new FileStorage(storageDir, logger);
            var storeHandler = new StoreHandler(fileStorage, index, logger);
            StorageSopClassRegistry.Configured = cfg.Scp.StorageServiceSopClasses ?? new List<string> { "*" };
            ScpServiceRuntime.StoreHandler = storeHandler;

            // 初始化 fo-dicom 服务容器。
            var setup = new DicomSetupBuilder();
            setup.RegisterServices(s => s.AddFellowOakDicom());
            setup.Build();

            // 4) 子命令 dispatch（task-002）。
            var command = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "scp";
            switch (command.ToLowerInvariant())
            {
                case "echo-scu":
                    return await RunEchoScuAsync(cfg, args, fileStorage, index, scuFallbackCallingAe, logger);

                case "store-scu":
                    return await RunStoreScuAsync(cfg, args, fileStorage, index, scuFallbackCallingAe, logger);

                case "scp":
                default:
                    return await RunScpAsync(port, aeTitle, cfg, logger);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("服务已停止");
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "启动失败: {Message}", ex.Message);
            return 1;
        }
    }

    /// <summary>默认：启动 SCP，兼容 task-001 行为。</summary>
    private static async Task<int> RunScpAsync(int port, string aeTitle, AppConfig cfg, ILogger logger)
    {
        logger.LogInformation(
            "配置: Port={Port}, AeTitle={AeTitle}；Storage SOP 白名单=[{Whitelist}]",
            port, aeTitle, string.Join(", ", cfg.Scp.StorageServiceSopClasses ?? new List<string> { "*" }));

        using var cts = new CancellationTokenSource();
        using var server = DicomServerFactory.Create<DicomScpService>(port);

        logger.LogInformation("DICOM Storage SCP 已启动，监听 0.0.0.0:{Port} (AET={AeTitle})", port, aeTitle);

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            logger.LogInformation("收到退出信号，正在停止...");
            cts.Cancel();
        };

        await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);
        return 0;
    }

    /// <summary>echo-scu 子命令：对某远程 SCP 发 C-ECHO。</summary>
    private static async Task<int> RunEchoScuAsync(AppConfig cfg, string[] args, FileStorage fileStorage,
        InstanceIndex index, string fallbackCallingAe, ILogger logger)
    {
        var repo = new RemoteScpRepository(ToScuOptions(cfg.Scu));
        var target = ResolveTarget(repo, args, out var name, logger);
        if (target is null)
            return 2;

        var forwardLog = new ForwardLog(
            cfg.Postgres.ConnectionString
            ?? "Host=localhost;Port=5432;Username=postgres;Database=dicom", logger);
        var scuu = new DicomScuService(fileStorage, index, forwardLog, fallbackCallingAe, logger);

        var result = await scuu.EchoAsync(target);
        Console.WriteLine($"C-ECHO[{name}] status={result.Status} reason={result.Reason ?? "-"}");
        return result.Status == EchoStatus.Success ? 0 : 1;
    }

    /// <summary>store-scu 子命令：按内部 ID 或 SOPInstanceUID 推送实例。</summary>
    private static async Task<int> RunStoreScuAsync(AppConfig cfg, string[] args, FileStorage fileStorage,
        InstanceIndex index, string fallbackCallingAe, ILogger logger)
    {
        var repo = new RemoteScpRepository(ToScuOptions(cfg.Scu));
        var target = ResolveTarget(repo, args, out var name, logger);
        if (target is null)
            return 2;

        var id = GetArg(args, "--id");
        var uid = GetArg(args, "--uid");
        if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(uid))
        {
            logger.LogError("store-scu 需要 --id &lt;internalId&gt; 或 --uid &lt;sopInstanceUid&gt;");
            return 2;
        }

        var forwardLog = new ForwardLog(
            cfg.Postgres.ConnectionString
            ?? "Host=localhost;Port=5432;Username=postgres;Database=dicom", logger);
        var scuu = new DicomScuService(fileStorage, index, forwardLog, fallbackCallingAe, logger);

        ForwardResult result = string.IsNullOrWhiteSpace(uid)
            ? await scuu.StoreByInternalIdAsync(target, id!, CancellationToken.None)
            : await scuu.StoreBySopInstanceUidAsync(target, uid!, CancellationToken.None);

        Console.WriteLine($"C-STORE[{name}] status={result.Status} reason={result.Reason ?? "-"}");
        return result.Status == StoreStatus.Success ? 0 : 1;
    }

    /// <summary>从命令参数解析 --remote &lt;Name&gt; 并定位目标；未找到返回 null 并打日志。</summary>
    private static RemoteScpTarget? ResolveTarget(RemoteScpRepository repo, string[] args, out string name, ILogger logger)
    {
        name = GetArg(args, "--remote") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            logger.LogError("缺少 --remote &lt;Name&gt; 参数（可用目标: {0}", string.Join(", ", repo.All.Select(x => x.Name)));
            return null;
        }

        var target = repo.FindByName(name);
        if (target is null)
        {
            logger.LogError("未找到名为 '{0}' 的远程 SCP 目标（可用: {1}", name, string.Join(", ", repo.All.Select(x => x.Name)));
            return null;
        }
        return target;
    }

    private static string? GetArg(string[] args, string key)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    /// <summary>把配置的 Scu 节转成 <see cref="ScuOptions"/>（供 RemoteScpRepository 校验）。</summary>
    private static ScuOptions ToScuOptions(ScuSection sc)
        => new()
        {
            CallingAeTitle = sc.CallingAeTitle,
            RemoteScps = sc.RemoteScps ?? new List<RemoteScpTarget>(),
        };
}
