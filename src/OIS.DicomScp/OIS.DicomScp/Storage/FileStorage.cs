using Microsoft.Extensions.Logging;

namespace OIS.DicomScp.Storage;

/// <summary>
/// Orthanc 式文件存储层：把 DICOM 实例以<b>内部生成的唯一 ID</b> 落盘，
/// 文件名<b>绝不</b>使用 SOPInstanceUID / StudyInstanceUID / PatientID 等业务标识（AC-04 匿名化硬性要求）。
/// 分片：内部 ID 前 2 位构成一层子目录（<c>&lt;id[0..2]&gt;/&lt;id&gt;</c>，无扩展名）。
/// </summary>
/// <remarks>
/// 一致性策略（与 Index 配合，二选一并记录于此）：
///   <b>先写文件，后写数据库索引</b>。
/// 理由：文件是 DICOM 实例的权威副本（无损），若索引写入失败，文件仍保留可重建索引；
/// 反之若先入库后写文件，入库成功而写文件失败会留下指向不存在文件的悬空索引，更难恢复。
/// 任何失败均按 DOMAIN.md「失败必须可见」记录错误并向上抛出，不回滚成静默成功。
/// 重复接收同一 SOPInstanceUID 时由调用方（Scp 服务）负责去重：命中已有则跳过写盘。
/// </remarks>
public sealed class FileStorage
{
    private readonly string _root;
    private readonly ILogger _logger;

    /// <summary>
    /// 创建文件存储。若根目录不存在会创建（AC-07：目录不存在时自动创建）。
    /// </summary>
    /// <param name="root">存储根目录（绝对路径，来自配置 Storage:Directory）。</param>
    /// <param name="logger">日志器。</param>
    /// <exception cref="UnauthorizedAccessException">根目录不可创建 / 不可写时抛出（AC-07 权限不足输出明确错误）。</exception>
    public FileStorage(string root, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("存储目录不能为空", nameof(root));

        _root = Path.GetFullPath(root);
        _logger = logger;

        try
        {
            Directory.CreateDirectory(_root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new IOException($"无法创建/访问存储目录 '{_root}': {ex.Message}", ex);
        }
    }

    /// <summary>存储根目录（绝对路径）。</summary>
    public string Root => _root;

    /// <summary>
    /// 生成一个内部唯一 ID（GUID N 格式，全小写 [0-9a-f]，长度 32）。
    /// 与任何业务 UID 无关联可推断（匿名化要求，AC-04）。
    /// </summary>
    public static string GenerateInternalId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// 由内部 ID 计算相对存储目录的路径（分片）：<c>&lt;id[0..2]&gt;/&lt;id&gt;</c>，无扩展名。
    /// </summary>
    public static string RelativePathOf(string internalId)
    {
        if (string.IsNullOrWhiteSpace(internalId) || internalId.Length < 2)
            throw new ArgumentException("内部 ID 长度必须至少为 2", nameof(internalId));
        return Path.Combine(internalId[..2], internalId);
    }

    /// <summary>
    /// 由内部 ID 计算落盘文件的绝对路径。
    /// </summary>
    public string AbsolutePathOf(string internalId)
    {
        var relative = RelativePathOf(internalId);
        return Path.GetFullPath(Path.Combine(_root, relative));
    }

    /// <summary>
    /// 把 DICOM 实例字节写盘。
    /// </summary>
    /// <param name="internalId">内部 ID（文件名）。</param>
    /// <param name="bytes">DICOM 文件完整字节（原样写盘，无损闭环）。</param>
    /// <returns>写盘文件的字节数。</returns>
    public long Store(string internalId, byte[] bytes)
    {
        var absolute = AbsolutePathOf(internalId);
        var dir = Path.GetDirectoryName(absolute)
                  ?? throw new IOException($"无法解析文件目录: {absolute}");

        Directory.CreateDirectory(dir);
        File.WriteAllBytes(absolute, bytes);

        _logger.LogInformation("落盘实例 {InternalId} -> {Path} ({Bytes} bytes)", internalId, absolute, bytes.Length);
        return bytes.Length;
    }

    /// <summary>
    /// 检查某内部 ID 对应的文件是否已存在。
    /// </summary>
    public bool Exists(string internalId) => File.Exists(AbsolutePathOf(internalId));
}
