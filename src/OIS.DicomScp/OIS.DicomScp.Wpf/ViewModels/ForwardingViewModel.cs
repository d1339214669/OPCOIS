using OIS.DicomScp;
using OIS.DicomScp.Index;
using OIS.DicomScp.Scu;
using OIS.DicomScp.Wpf.Commands;

namespace OIS.DicomScp.Wpf.ViewModels;

/// <summary>
/// 转发 + 历史页签 ViewModel（AC-05 / AC-07 / AC-09）。
/// 目标/实例下拉（来自 appsettings.json 与 dicom_instance）→ C-ECHO 连通测试（AC-05 结果+原因可见）、
/// 按 SOPInstanceUID 转发 C-STORE（AC-07 结果+原因可见、失败不崩溃、可继续测下一目标）、转发后刷新转发历史（最近 50 条，AC-07）。
/// 纯逻辑、不引用 WPF 命名空间（headless 可测，AC-10）。
/// </summary>
public sealed class ForwardingViewModel : ViewModelBase
{
    private const int InstanceTake = 200;
    private const int HistoryTake = 50;

    private readonly IConfigStore _configStore;
    private readonly IDicomScuService _scu;
    private readonly IInstanceQuery _instances;
    private readonly IForwardLogQuery _forwardLog;

    private IReadOnlyList<RemoteScpTarget> _targets = Array.Empty<RemoteScpTarget>();
    private RemoteScpTarget? _selectedTarget;
    private IReadOnlyList<InstanceRow> _instanceList = Array.Empty<InstanceRow>();
    private InstanceRow? _selectedInstance;
    private IReadOnlyList<ForwardLogRow> _history = Array.Empty<ForwardLogRow>();
    private string _echoStatusText = string.Empty;
    private string? _echoReasonText;
    private string _storeStatusText = string.Empty;
    private string? _storeReasonText;
    private string _statusText = "尚未加载";
    private string? _errorText;
    private bool _isBusy;

    /// <summary>
    /// 创建转发 + 历史 ViewModel。
    /// </summary>
    /// <param name="configStore">配置读写（读取远程目标列表）。</param>
    /// <param name="scu">出站 SCU 服务（C-ECHO / C-STORE）。</param>
    /// <param name="instances">实例只读查询（实例下拉）。</param>
    /// <param name="forwardLog">转发日志只读查询（历史列表）。</param>
    public ForwardingViewModel(
        IConfigStore configStore,
        IDicomScuService scu,
        IInstanceQuery instances,
        IForwardLogQuery forwardLog)
    {
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _scu = scu ?? throw new ArgumentNullException(nameof(scu));
        _instances = instances ?? throw new ArgumentNullException(nameof(instances));
        _forwardLog = forwardLog ?? throw new ArgumentNullException(nameof(forwardLog));

        LoadCommand = new RelayCommand(() => _ = LoadAllAsync(), () => !IsBusy);
        EchoCommand = new RelayCommand(() => _ = EchoAsync(), () => SelectedTarget is not null && !IsBusy);
        StoreCommand = new RelayCommand(() => _ = StoreAsync(), () => SelectedTarget is not null && SelectedInstance is not null && !IsBusy);
        RefreshHistoryCommand = new RelayCommand(() => _ = RefreshHistoryAsync(), () => !IsBusy);
    }

    /// <summary>加载命令（目标 + 实例）。</summary>
    public IAppCommand LoadCommand { get; }

    /// <summary>C-ECHO 连通测试命令（AC-05）。</summary>
    public IAppCommand EchoCommand { get; }

    /// <summary>转发命令（对选中实例 C-STORE 到选中目标，AC-07）。</summary>
    public IAppCommand StoreCommand { get; }

    /// <summary>刷新转发历史命令（AC-07）。</summary>
    public IAppCommand RefreshHistoryCommand { get; }

    /// <summary>远程目标下拉数据源（appsettings.json Scu.RemoteScps）。</summary>
    public IReadOnlyList<RemoteScpTarget> Targets { get => _targets; private set => SetProperty(ref _targets, value); }

    /// <summary>选中远程目标。</summary>
    public RemoteScpTarget? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (SetProperty(ref _selectedTarget, value))
            {
                EchoCommand.RaiseCanExecuteChanged();
                StoreCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>本地实例下拉数据源（最近 200 条，received_at 倒序）。</summary>
    public IReadOnlyList<InstanceRow> Instances { get => _instanceList; private set => SetProperty(ref _instanceList, value); }

    /// <summary>选中实例。</summary>
    public InstanceRow? SelectedInstance
    {
        get => _selectedInstance;
        set
        {
            if (SetProperty(ref _selectedInstance, value))
                StoreCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>转发历史（最近 50 条，id 倒序）。</summary>
    public IReadOnlyList<ForwardLogRow> History { get => _history; private set => SetProperty(ref _history, value); }

    /// <summary>C-ECHO 结果状态文本（成功/失败，AC-05）。</summary>
    public string EchoStatusText { get => _echoStatusText; private set => SetProperty(ref _echoStatusText, value); }

    /// <summary>C-ECHO 失败原因（成功为 null；AC-05 失败必须可见）。</summary>
    public string? EchoReasonText { get => _echoReasonText; private set => SetProperty(ref _echoReasonText, value); }

    /// <summary>转发结果状态文本（成功/失败，AC-07）。</summary>
    public string StoreStatusText { get => _storeStatusText; private set => SetProperty(ref _storeStatusText, value); }

    /// <summary>转发失败原因（成功为 null；AC-07 失败必须可见）。</summary>
    public string? StoreReasonText { get => _storeReasonText; private set => SetProperty(ref _storeReasonText, value); }

    /// <summary>状态文本（加载概要等）。</summary>
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    /// <summary>错误文本（加载/未预期异常时明确原因；常规失败走 Status/Reason，不重复）。</summary>
    public string? ErrorText { get => _errorText; private set => SetProperty(ref _errorText, value); }

    /// <summary>是否正在加载/执行。</summary>
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }

    /// <summary>
    /// 加载目标（appsettings.json）+ 实例（dicom_instance 最近 200 条）。
    /// 失败 → 可见错误（AC-05 前置：无目标/无实例时操作不可用并说明原因）。
    /// </summary>
    public async Task LoadAllAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        ErrorText = null;
        RaiseBusy();

        try
        {
            var cfg = await _configStore.LoadAsync();
            var list = cfg.Scu?.RemoteScps ?? new List<RemoteScpTarget>();
            Targets = list;
            SelectedTarget = list.FirstOrDefault();

            Instances = await _instances.QueryAsync(null, null, null, null, null, InstanceTake, CancellationToken.None);
            SelectedInstance = null;

            StatusText = $"已加载 {Targets.Count} 个远程目标、{Instances.Count} 个实例（最近 {InstanceTake} 条）";
        }
        catch (Exception ex)
        {
            StatusText = "加载失败";
            ErrorText = $"加载远程目标/实例失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            RaiseBusy();
        }
    }

    /// <summary>
    /// C-ECHO 连通测试（AC-05）：结果 + 原因可见；失败不崩溃、可继续测下一目标（UI 状态复位）。
    /// </summary>
    public async Task EchoAsync()
    {
        if (IsBusy || SelectedTarget is null)
            return;

        IsBusy = true;
        ErrorText = null;
        EchoReasonText = null;
        RaiseBusy();

        try
        {
            var r = await _scu.EchoAsync(SelectedTarget);
            EchoStatusText = r.Status == EchoStatus.Success
                ? $"C-ECHO 成功（Success）：{SelectedTarget.Name}"
                : $"C-ECHO 失败（Failed）：{SelectedTarget.Name}";
            EchoReasonText = r.Reason;
        }
        catch (Exception ex)
        {
            // 契约上 IDicomScuService 不抛（异常已转 Failed）；双保险：未预期异常也可见（AC-05）。
            EchoStatusText = $"C-ECHO 异常：{SelectedTarget.Name}";
            EchoReasonText = ex.Message;
            ErrorText = $"C-ECHO 发生未预期异常：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            RaiseBusy();
        }
    }

    /// <summary>
    /// 转发（AC-07）：按 SOPInstanceUID 定位本地实例并 C-STORE 到选中目标；
    /// 结果 + 原因可见；成功后自动刷新转发历史；失败不崩溃（可继续转发其他实例/目标）。
    /// </summary>
    public async Task StoreAsync()
    {
        if (IsBusy || SelectedTarget is null || SelectedInstance is null)
            return;

        IsBusy = true;
        ErrorText = null;
        StoreReasonText = null;
        RaiseBusy();

        try
        {
            var r = await _scu.StoreBySopInstanceUidAsync(SelectedTarget, SelectedInstance.SopInstanceUid);
            StoreStatusText = r.Status == StoreStatus.Success
                ? $"转发成功（Success）：{SelectedInstance.SopInstanceUid} → {SelectedTarget.Name}"
                : $"转发失败（Failed）：{SelectedInstance.SopInstanceUid} → {SelectedTarget.Name}";
            StoreReasonText = r.Reason;

            // 转发后刷新历史（AC-07：新记录立即可见）。
            await RefreshHistoryAsync();
        }
        catch (Exception ex)
        {
            // 契约上不抛；双保险（AC-07 失败必须可见、不崩溃）。
            StoreStatusText = $"转发异常：{SelectedInstance.SopInstanceUid} → {SelectedTarget.Name}";
            StoreReasonText = ex.Message;
            ErrorText = $"转发发生未预期异常：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            RaiseBusy();
        }
    }

    /// <summary>
    /// 刷新转发历史（AC-07）：最近 <see cref="HistoryTake"/> 条，id 倒序；
    /// 查询失败 → 可见错误 + 空历史（不静默）。
    /// </summary>
    public async Task RefreshHistoryAsync()
    {
        try
        {
            History = await _forwardLog.RecentAsync(HistoryTake, CancellationToken.None);
        }
        catch (Exception ex)
        {
            History = Array.Empty<ForwardLogRow>();
            ErrorText = $"转发历史查询失败：{ex.Message}";
        }
    }

    private void RaiseBusy()
    {
        LoadCommand.RaiseCanExecuteChanged();
        EchoCommand.RaiseCanExecuteChanged();
        StoreCommand.RaiseCanExecuteChanged();
        RefreshHistoryCommand.RaiseCanExecuteChanged();
    }
}
