using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OIS.DicomScp;

/// <summary>
/// appsettings.json 读取 + 写回（task-003 spec §6.1/§6.2，AC-04）。
/// 读：文件缺失时返回默认 <see cref="AppConfig"/>（与 CLI 既有行为一致）；
/// 写：原子写（同目录临时文件 + 重命名替换），写失败/中途崩溃不损坏原文件。
/// 保持其他配置节（Scp/Storage/Postgres）不丢失由调用方「读-改-写」保证（见 AC-04）。
/// </summary>
public interface IConfigStore
{
    /// <summary>读取 appsettings.json 为 <see cref="AppConfig"/>；文件缺失返回默认配置（不抛错）。</summary>
    Task<AppConfig> LoadAsync(CancellationToken ct = default);

    /// <summary>把 <see cref="AppConfig"/> 原子写回 appsettings.json（保持既有 JSON 字段名/嵌套结构）。</summary>
    Task SaveAsync(AppConfig cfg, CancellationToken ct = default);
}

/// <summary><see cref="IConfigStore"/> 的文件系统实现。</summary>
public sealed class ConfigStore : IConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly ILogger _logger;
    private readonly string _path;

    /// <summary>
    /// 创建配置读写器。
    /// </summary>
    /// <param name="path">appsettings.json 路径（相对/绝对均可；WPF 与 CLI 默认同目录约定，可配）。</param>
    /// <param name="logger">日志器。</param>
    public ConfigStore(string path, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("配置文件路径不能为空", nameof(path));
        if (logger is null)
            throw new ArgumentNullException(nameof(logger));
        _path = System.IO.Path.GetFullPath(path);
        _logger = logger;
    }

    /// <summary>配置文件绝对路径。</summary>
    public string Path => _path;

    /// <inheritdoc />
    public async Task<AppConfig> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path))
        {
            _logger.LogDebug("配置文件不存在，使用默认配置: {Path}", _path);
            return new AppConfig();
        }

        var json = await File.ReadAllTextAsync(_path, ct).ConfigureAwait(false);
        var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
        if (cfg is null)
            throw new InvalidDataException($"配置文件反序列化失败（空内容或非法 JSON）: {_path}");
        _logger.LogDebug("已读取配置: {Path}", _path);
        return cfg;
    }

    /// <inheritdoc />
    public async Task SaveAsync(AppConfig cfg, CancellationToken ct = default)
    {
        if (cfg is null)
            throw new ArgumentNullException(nameof(cfg));

        var json = JsonSerializer.Serialize(cfg, JsonOptions);
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // 原子写（AC-04）：先写同目录临时文件，再用重命名替换目标文件；
        // 即使中途崩溃/失败，原 appsettings.json 也不会被写坏。
        var tmp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
            File.Move(tmp, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try
                {
                    File.Delete(tmp);
                }
                catch (IOException ex)
                {
                    // 临时文件清理失败不影响主流程，但必须可见（AC-09 禁止静默吞）。
                    _logger.LogError(ex, "配置临时文件清理失败: {Path}", tmp);
                }
            }
        }

        _logger.LogInformation("配置已写回: {Path}", _path);
    }
}
