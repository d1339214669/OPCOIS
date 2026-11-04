using OIS.DicomScp;
using OIS.DicomScp.Index;
using OIS.DicomScp.Wpf.Commands;

namespace OIS.DicomScp.Wpf.ViewModels;

/// <summary>
/// 服务状态页签 ViewModel（AC-08 / AC-09）。
/// 显示当前 SCP 服务端口 / AET / 运行状态 / 实例总数；启动（幂等：已在运行 → 明确提示而非报错，AC-08）、
/// 停止（未运行 → 明确提示）、刷新实例数（PostgreSQL 失败 → 可见错误，实例数显示「—」，不臆造 0，AC-09）。
/// 纯逻辑、不引用 WPF 命名空间（headless 可测，AC-10）。
/// </summary>
public sealed class ServiceStatusViewModel : ViewModelBase
{
    private readonly IConfigStore _configStore;
    private readonly IScpHost _host;
    private readonly IInstanceQuery _query;

    private int _port;
    private string _aeTitle = string.Empty;
    private bool _isRunning;
    private int? _instanceCount;
    private string _statusText = "尚未加载";
    private string? _errorText;
    private bool _isBusy;

    /// <summary>
    /// 创建服务状态 ViewModel。
    /// </summary>
    /// <param name="configStore">配置读写（读取端口 / AET 显示）。</param>
    /// <param name="host">SCP 服务宿主（Core <see cref="IScpHost"/>：StartAsync 幂等 / StopAsync / IsRunning）。</param>
    /// <param name="query">实例只读查询（实例总数，AC-09）。</param>
    public ServiceStatusViewModel(IConfigStore configStore, IScpHost host, IInstanceQuery query)
    {
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _query = query ?? throw new ArgumentNullException(nameof(query));

        StartCommand = new RelayCommand(() => _ = StartAsync(), () => !IsBusy);
        StopCommand = new RelayCommand(() => _ = StopAsync(), () => !IsBusy);
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => !IsBusy);
    }

    /// <summary>启动命令（AC-08：幂等，已在运行 → 明确提示而非报错）。</summary>
    public IAppCommand StartCommand { get; }

    /// <summary>停止命令（未运行 → 明确提示而非报错）。</summary>
    public IAppCommand StopCommand { get; }

    /// <summary>刷新实例数命令。</summary>
    public IAppCommand RefreshCommand { get; }

    /// <summary>监听端口（appsettings.json Scp.Port，非法时回退默认 11112）。</summary>
    public int Port { get => _port; private set => SetProperty(ref _port, value); }

    /// <summary>对外 AET（appsettings.json Scp.AeTitle，空时回退默认 "OIS"）。</summary>
    public string AeTitle { get => _aeTitle; private set => SetProperty(ref _aeTitle, value); }

    /// <summary>服务是否正在运行。</summary>
    public bool IsRunning { get => _isRunning; private set => SetProperty(ref _isRunning, value); }

    /// <summary>实例总数（PostgreSQL 查询失败为 null → UI 显示「—」，不臆造 0，AC-09）。</summary>
    public int? InstanceCount { get => _instanceCount; private set => SetProperty(ref _instanceCount, value); }

    /// <summary>实例数显示文本（null → 「—」）。</summary>
    public string InstanceCountDisplay => _instanceCount?.ToString() ?? "—";

    /// <summary>状态文本。</summary>
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    /// <summary>错误文本（启动/停止/刷新失败时明确原因；成功为 null）。</summary>
    public string? ErrorText { get => _errorText; private set => SetProperty(ref _errorText, value); }

    /// <summary>是否正在执行启停/刷新。</summary>
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }

    /// <summary>
    /// 初始化显示（窗口加载时调用）：从配置读端口/AET、同步运行状态、刷新实例数。
    /// </summary>
    public async Task InitializeAsync()
    {
        var cfg = await _configStore.LoadAsync();
        Port = cfg.Scp?.Port is > 0 and <= 65535 ? cfg.Scp!.Port!.Value : 11112;
        AeTitle = string.IsNullOrWhiteSpace(cfg.Scp?.AeTitle) ? "OIS" : cfg.Scp!.AeTitle!.Trim();
        IsRunning = _host.IsRunning;
        await RefreshAsync();
    }

    /// <summary>
    /// 启动服务（AC-08）：幂等——已在运行 → 「已在运行」明确提示（非错误、非崩溃）；
    /// 启动失败（端口占用等）→ 可见错误。
    /// </summary>
    public async Task StartAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        ErrorText = null;
        RaiseBusy();

        try
        {
            try
            {
                await _host.StartAsync();
            }
            catch (ScpAlreadyRunningException)
            {
                // AC-08 幂等：重复启动 → 明确提示，不算错误。
                IsRunning = true;
                StatusText = "服务已在运行（无需重复启动）";
                return;
            }

            IsRunning = _host.IsRunning;
            StatusText = $"服务已启动（监听端口 {Port}，AET={AeTitle}）";
        }
        catch (Exception ex)
        {
            IsRunning = _host.IsRunning;
            StatusText = "启动失败";
            ErrorText = $"启动失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            RaiseBusy();
        }
    }

    /// <summary>
    /// 停止服务：未运行 → 明确提示（非错误）；停止失败 → 可见错误。
    /// </summary>
    public async Task StopAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        ErrorText = null;
        RaiseBusy();

        try
        {
            try
            {
                await _host.StopAsync();
            }
            catch (InvalidOperationException)
            {
                // 未运行 → 明确提示，不算错误。
                IsRunning = false;
                StatusText = "服务未运行（无需停止）";
                return;
            }

            IsRunning = false;
            StatusText = "服务已停止";
        }
        catch (Exception ex)
        {
            IsRunning = _host.IsRunning;
            StatusText = "停止失败";
            ErrorText = $"停止失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            RaiseBusy();
        }
    }

    /// <summary>
    /// 刷新实例数与运行状态（AC-09）：PostgreSQL 失败 → 可见错误 + 实例数 null（显示「—」，不臆造 0）。
    /// </summary>
    public async Task RefreshAsync()
    {
        try
        {
            InstanceCount = await _query.CountAsync(CancellationToken.None);
            IsRunning = _host.IsRunning;
            ErrorText = null; // 成功刷新 → 清除此前的错误（状态复位，AC-09）
            StatusText = $"实例总数 {InstanceCount}（PostgreSQL）";
        }
        catch (Exception ex)
        {
            InstanceCount = null;
            IsRunning = _host.IsRunning;
            ErrorText = $"实例数查询失败（PostgreSQL 不可用？）：{ex.Message}";
        }
    }

    private void RaiseBusy()
    {
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        RefreshCommand.RaiseCanExecuteChanged();
    }
}
