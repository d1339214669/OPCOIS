namespace OIS.DicomScp.Scu;

/// <summary>
/// 远程 SCP 目标注册表：从配置读取并校验 <c>Scu.RemoteScps</c> 列表（AC-01）。
/// 负责：0/1/多目标均可用；<c>Name</c> 唯一性校验（重复时抛明确错误，不静默产生歧义）。
/// </summary>
public sealed class RemoteScpRepository
{
    private readonly IReadOnlyDictionary<string, RemoteScpTarget> _byName;
    private readonly IReadOnlyList<RemoteScpTarget> _all;
    private readonly string _defaultProtocol = "DICOM";

    /// <summary>
    /// 用 <see cref="ScuOptions"/> 构造。会校验 Name 唯一性；重复时抛 <see cref="InvalidOperationException"/>（AC-01/AC-08「失败必须可见」）。
    /// </summary>
    /// <param name="options">SCU 配置（Scu 节）。</param>
    public RemoteScpRepository(ScuOptions options)
    {
        var targets = (options?.RemoteScps ?? new List<RemoteScpTarget>())
                      ?? new List<RemoteScpTarget>();

        // 规范化：Name/空值处理、Protocol 默认 DICOM（AC-08：不允许随机端口/默认 AET 静默错误转发）。
        var normalized = new List<RemoteScpTarget>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in targets)
        {
            var name = string.IsNullOrWhiteSpace(t.Name) ? "" : t.Name.Trim();
            if (name.Length == 0)
                throw new InvalidOperationException("RemoteScps 列表中存在空 Name（别名），无法定位目标，请配置非空 Name");
            if (!seen.Add(name))
                throw new InvalidOperationException($"RemoteScps 目标 Name 重复: '{name}'（Name 必须唯一，AC-01）");

            if (string.IsNullOrWhiteSpace(t.Host))
                throw new InvalidOperationException($"RemoteScps 目标 '{name}' 缺少 Host");
            if (t.Port is <= 0 or > 65535)
                throw new InvalidOperationException($"RemoteScps 目标 '{name}' 端口非法: {t.Port}（须 1-65535）");

            var protocol = string.IsNullOrWhiteSpace(t.Protocol) ? _defaultProtocol : t.Protocol.Trim();
            if (!string.Equals(protocol, "DICOM", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"RemoteScps 目标 '{name}' 协议 '{protocol}' 暂不支持（本期仅 DICOM）");

            normalized.Add(new RemoteScpTarget
            {
                Name = name,
                Host = t.Host.Trim(),
                Port = t.Port,
                CalledAeTitle = t.CalledAeTitle?.Trim() ?? string.Empty,
                CallingAeTitle = string.IsNullOrWhiteSpace(t.CallingAeTitle) ? null : t.CallingAeTitle.Trim(),
                Protocol = protocol,
            });
        }

        _all = normalized;
        _byName = normalized.ToDictionary(x => x.Name, x => x, StringComparer.Ordinal);
    }

    /// <summary>全部已配置目标（0..n 个）。</summary>
    public IReadOnlyList<RemoteScpTarget> All => _all;

    /// <summary>当前目标个数。</summary>
    public int Count => _all.Count;

    /// <summary>按 Name 查找目标；不存在返回 null（AC-01 无歧义定位）。</summary>
    public RemoteScpTarget? FindByName(string name)
        => !string.IsNullOrWhiteSpace(name) && _byName.TryGetValue(name.Trim(), out var t) ? t : null;
}
