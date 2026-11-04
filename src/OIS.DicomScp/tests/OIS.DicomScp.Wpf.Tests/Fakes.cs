using OIS.DicomScp;
using OIS.DicomScp.Index;
using OIS.DicomScp.Scu;

namespace OIS.DicomScp.Wpf.Tests;

// ============ headless 测试替身（fake）：驱动 ViewModel 逻辑分支，不依赖 PG/DICOM 网络/文件系统服务 ============

/// <summary>
/// <see cref="IInstanceQuery"/> 替身：内存行 + 可配置异常；
/// 过滤/排序/take 行为与真实 InstanceIndex 契约一致（供断言「VM 把过滤条件传对了」）。
/// </summary>
public sealed class FakeInstanceQuery : IInstanceQuery
{
    /// <summary>数据源行。</summary>
    public List<InstanceRow> Rows { get; } = new();

    /// <summary>QueryAsync 抛出异常（模拟 PostgreSQL 不可用等）。</summary>
    public Exception? QueryException { get; set; }

    /// <summary>CountAsync 抛出异常（模拟 PostgreSQL 不可用）。</summary>
    public Exception? CountException { get; set; }

    /// <summary>实例总数（CountAsync）。</summary>
    public int Count { get; set; }

    /// <summary>QueryAsync 调用次数。</summary>
    public int QueryCount { get; private set; }

    /// <summary>最近一次 QueryAsync 收到的参数（null = 不过滤）。</summary>
    public (string? Patient, string? Study, string? Modality, DateTime? From, DateTime? To, int Take)? LastQuery { get; private set; }

    /// <inheritdoc />
    public Task<IReadOnlyList<InstanceRow>> QueryAsync(
        string? patientId,
        string? studyInstanceUid,
        string? modality,
        DateTime? fromUtc,
        DateTime? toUtc,
        int take,
        CancellationToken ct = default)
    {
        QueryCount++;
        LastQuery = (patientId, studyInstanceUid, modality, fromUtc, toUtc, take);
        if (QueryException is not null)
            throw QueryException;

        IEnumerable<InstanceRow> rows = Rows;
        if (patientId is not null)
            rows = rows.Where(r => r.PatientId == patientId);
        if (studyInstanceUid is not null)
            rows = rows.Where(r => r.StudyInstanceUid == studyInstanceUid);
        if (modality is not null)
            rows = rows.Where(r => r.Modality == modality);
        if (fromUtc is not null)
            rows = rows.Where(r => r.ReceivedAt >= fromUtc.Value);
        if (toUtc is not null)
            rows = rows.Where(r => r.ReceivedAt <= toUtc.Value);
        return Task.FromResult<IReadOnlyList<InstanceRow>>(
            rows.OrderByDescending(r => r.ReceivedAt).Take(take).ToList());
    }

    /// <inheritdoc />
    public Task<int> CountAsync(CancellationToken ct = default)
    {
        if (CountException is not null)
            throw CountException;
        return Task.FromResult(Count);
    }
}

/// <summary>
/// <see cref="IForwardLogQuery"/> 替身：内存行 + 可配置异常。
/// </summary>
public sealed class FakeForwardLogQuery : IForwardLogQuery
{
    /// <summary>数据源行。</summary>
    public List<ForwardLogRow> Rows { get; } = new();

    /// <summary>RecentAsync 抛出异常。</summary>
    public Exception? Exception { get; set; }

    /// <summary>最近一次 take 参数。</summary>
    public int LastTake { get; private set; }

    /// <inheritdoc />
    public Task<IReadOnlyList<ForwardLogRow>> RecentAsync(int take, CancellationToken ct = default)
    {
        LastTake = take;
        if (Exception is not null)
            throw Exception;
        return Task.FromResult<IReadOnlyList<ForwardLogRow>>(
            Rows.OrderByDescending(r => r.Id).Take(take).ToList());
    }
}

/// <summary>
/// <see cref="IDicomScuService"/> 替身：可配置 C-ECHO / C-STORE 结果或异常。
/// </summary>
public sealed class FakeScu : IDicomScuService
{
    /// <summary>C-ECHO 返回结果。</summary>
    public EchoResult EchoResult { get; set; } = new(EchoStatus.Success, null);

    /// <summary>C-STORE 返回结果。</summary>
    public ForwardResult StoreResult { get; set; } = new(StoreStatus.Success, null);

    /// <summary>_echoAsync 抛出异常（模拟未预期异常路径）。</summary>
    public Exception? EchoException { get; set; }

    /// <summary>StoreBySopInstanceUidAsync 抛出异常。</summary>
    public Exception? StoreException { get; set; }

    /// <summary>C-ECHO 调用次数。</summary>
    public int EchoCount { get; private set; }

    /// <summary>C-STORE 调用次数。</summary>
    public int StoreCount { get; private set; }

    /// <summary>最近一次 C-ECHO 目标。</summary>
    public RemoteScpTarget? LastEchoTarget { get; private set; }

    /// <summary>最近一次 C-STORE（目标, SOPInstanceUID）。</summary>
    public (RemoteScpTarget? Target, string? SopUid)? LastStore { get; private set; }

    /// <inheritdoc />
    public Task<EchoResult> EchoAsync(RemoteScpTarget target, CancellationToken ct = default)
    {
        EchoCount++;
        LastEchoTarget = target;
        if (EchoException is not null)
            throw EchoException;
        return Task.FromResult(EchoResult);
    }

    /// <inheritdoc />
    public Task<ForwardResult> StoreByInternalIdAsync(RemoteScpTarget target, string internalId, CancellationToken ct = default)
        => throw new NotSupportedException("ViewModel 不使用 StoreByInternalIdAsync");

    /// <inheritdoc />
    public Task<ForwardResult> StoreBySopInstanceUidAsync(RemoteScpTarget target, string sopInstanceUid, CancellationToken ct = default)
    {
        StoreCount++;
        LastStore = (target, sopInstanceUid);
        if (StoreException is not null)
            throw StoreException;
        return Task.FromResult(StoreResult);
    }
}

/// <summary>
/// <see cref="IConfigStore"/> 替身：内存 AppConfig + 保存记录（不碰文件系统）。
/// </summary>
public sealed class FakeConfigStore : IConfigStore
{
    private AppConfig _current;

    /// <summary>LoadAsync 抛出异常。</summary>
    public Exception? LoadException { get; set; }

    /// <summary>SaveAsync 抛出异常。</summary>
    public Exception? SaveException { get; set; }

    /// <summary>保存次数（AC-04：校验拒绝时不得调用保存）。</summary>
    public int SaveCount { get; private set; }

    /// <summary>最近一次保存的配置快照。</summary>
    public AppConfig? LastSaved { get; private set; }

    /// <param name="initial">初始配置（模拟 appsettings.json 内容）。</param>
    public FakeConfigStore(AppConfig initial)
    {
        _current = initial ?? throw new ArgumentNullException(nameof(initial));
    }

    /// <summary>当前配置（保存后更新）。</summary>
    public AppConfig Current => _current;

    /// <inheritdoc />
    public Task<AppConfig> LoadAsync(CancellationToken ct = default)
    {
        if (LoadException is not null)
            throw LoadException;
        return Task.FromResult(_current);
    }

    /// <inheritdoc />
    public Task SaveAsync(AppConfig cfg, CancellationToken ct = default)
    {
        if (SaveException is not null)
            throw SaveException;
        SaveCount++;
        LastSaved = cfg;
        _current = cfg;
        return Task.CompletedTask;
    }
}

/// <summary>
/// <see cref="IScpHost"/> 替身：可控制运行状态与异常路径（AC-08 幂等语义与 AC-09 失败可见）。
/// </summary>
public sealed class FakeScpHost : IScpHost
{
    /// <summary>StartAsync 前强制抛出的异常（模拟端口占用等启动失败）。</summary>
    public Exception? StartException { get; set; }

    /// <summary>StopAsync 时抛出异常（模拟停止失败）。</summary>
    public bool FailStop { get; set; }

    /// <summary>StartAsync 调用次数。</summary>
    public int StartCount { get; private set; }

    /// <summary>StopAsync 调用次数。</summary>
    public int StopCount { get; private set; }

    /// <summary>端口（构造指定）。</summary>
    public int Port { get; }

    /// <inheritdoc />
    public bool IsRunning { get; private set; }

    /// <summary>构造替身宿主；<paramref name="startRunning"/> = true 时初始即为运行中（验证幂等提示路径）。</summary>
    public FakeScpHost(int port = 11112, bool startRunning = false)
    {
        Port = port;
        IsRunning = startRunning;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken ct = default)
    {
        StartCount++;
        if (IsRunning)
            throw new ScpAlreadyRunningException("fake: 已在运行");
        if (StartException is not null)
            throw StartException;
        IsRunning = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync()
    {
        StopCount++;
        if (!IsRunning)
            throw new InvalidOperationException("fake: 未运行");
        if (FailStop)
            throw new Exception("fake: 停止失败");
        IsRunning = false;
        return Task.CompletedTask;
    }
}
