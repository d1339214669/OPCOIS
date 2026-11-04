using OIS.DicomScp;
using OIS.DicomScp.Wpf.Commands;

namespace OIS.DicomScp.Wpf.ViewModels;

/// <summary>
/// 远程 SCP 配置页签 ViewModel（AC-04 / AC-09）。
/// 加载/编辑/新增/删除/保存远程 SCP 目标；保存为「读-改-写」appsettings.json，
/// 保持其他配置节（Scp/Storage/Postgres/Scu.CallingAeTitle）不丢失；
/// 校验失败 → 拒绝保存 + 可读原因（配置文件保持原样）；读写失败 → 可见错误（不静默）。
/// 纯逻辑、不引用 WPF 命名空间（headless 可测，AC-10）。
/// </summary>
public sealed class RemoteScpsViewModel : ViewModelBase
{
    private readonly IConfigStore _configStore;
    private readonly System.Collections.ObjectModel.ObservableCollection<RemoteScpEditItem> _items = new();

    private RemoteScpEditItem? _selected;
    private string _statusText = "尚未加载";
    private string? _errorText;
    private bool _isBusy;

    /// <summary>创建远程 SCP 配置 ViewModel。</summary>
    /// <param name="configStore">配置读写（Core <see cref="IConfigStore"/>，appsettings.json 原子写）。</param>
    public RemoteScpsViewModel(IConfigStore configStore)
    {
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        LoadCommand = new RelayCommand(() => _ = LoadAsync(), () => !IsBusy);
        AddCommand = new RelayCommand(Add, () => !IsBusy);
        DeleteCommand = new RelayCommand(Delete, () => Selected is not null && !IsBusy);
        SaveCommand = new RelayCommand(() => _ = SaveAsync(), () => !IsBusy);
    }

    /// <summary>加载命令（从 appsettings.json 读取）。</summary>
    public IAppCommand LoadCommand { get; }

    /// <summary>新增目标命令。</summary>
    public IAppCommand AddCommand { get; }

    /// <summary>删除选中目标命令。</summary>
    public IAppCommand DeleteCommand { get; }

    /// <summary>保存命令（校验通过后原子写回 appsettings.json）。</summary>
    public IAppCommand SaveCommand { get; }

    /// <summary>编辑行集合（ListBox 数据源）。</summary>
    public System.Collections.ObjectModel.ObservableCollection<RemoteScpEditItem> Items => _items;

    /// <summary>当前选中编辑行。</summary>
    public RemoteScpEditItem? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                DeleteCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>是否有选中行（View 层据此使能编辑表单）。</summary>
    public bool HasSelection => _selected is not null;

    /// <summary>状态文本。</summary>
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    /// <summary>错误文本（校验拒绝/读写失败时明确原因；成功为 null）。</summary>
    public string? ErrorText { get => _errorText; private set => SetProperty(ref _errorText, value); }

    /// <summary>是否正在加载/保存。</summary>
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }

    /// <summary>
    /// 从 appsettings.json 加载远程 SCP 目标（AC-04）；
    /// 配置缺失/读失败 → 可见错误（不静默）。
    /// </summary>
    public async Task LoadAsync()
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

            _items.Clear();
            foreach (var t in list)
                _items.Add(RemoteScpEditItem.FromTarget(t));

            Selected = _items.FirstOrDefault();
            StatusText = $"已加载 {list.Count} 个远程 SCP 目标（appsettings.json）";
        }
        catch (Exception ex)
        {
            StatusText = "加载失败";
            ErrorText = $"读取配置失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            RaiseBusy();
        }
    }

    /// <summary>新增一个空白目标行（保存后生效）。</summary>
    public void Add()
    {
        var item = new RemoteScpEditItem();
        _items.Add(item);
        Selected = item;
        StatusText = "已新增目标：填写 Name/Host/Port 等后点「保存」写回 appsettings.json";
    }

    /// <summary>删除选中目标行（保存后生效）。</summary>
    public void Delete()
    {
        if (Selected is null)
            return;

        var name = Selected.DisplayName;
        _items.Remove(Selected);
        Selected = _items.FirstOrDefault();
        StatusText = $"已删除「{name}」（点「保存」写回配置）";
    }

    /// <summary>
    /// 保存（AC-04）：先校验（Name 非空 + Ordinal 唯一、Host 非空、Port 1-65535、CalledAe/CallingAe 非空白-only、协议仅 DICOM），
    /// 拒绝 → 可读原因 + 配置保持原样；通过 → 读-改-写 appsettings.json（保持其他配置节）。
    /// </summary>
    public async Task SaveAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        ErrorText = null;
        RaiseBusy();

        try
        {
            var (ok, reason) = Validate();
            if (!ok)
            {
                StatusText = "保存被拒绝";
                ErrorText = $"校验失败：{reason}（appsettings.json 未改动）";
                return;
            }

            // 读-改-写：仅替换 Scu.RemoteScps，其余配置节原样保留（AC-04 保存不丢失其他配置）。
            var cfg = await _configStore.LoadAsync();
            var scu = cfg.Scu ?? new ScuSection();
            scu.RemoteScps = _items.Select(ToTarget).ToList();
            cfg.Scu = scu;

            await _configStore.SaveAsync(cfg);
            StatusText = $"已保存 {_items.Count} 个远程 SCP 目标到 appsettings.json（原子写）";
        }
        catch (Exception ex)
        {
            StatusText = "保存失败";
            ErrorText = $"写回配置失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            RaiseBusy();
        }
    }

    /// <summary>
    /// 校验（与 task-002 RemoteScpRepository 同一套规则，AC-04）：
    /// Name 非空且 Ordinal 唯一；Host 非空；Port 1-65535；CalledAe/CallingAe 已配置则不可为纯空白；协议为空默认 DICOM，非 DICOM 拒绝。
    /// </summary>
    internal (bool Ok, string? Reason) Validate()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < _items.Count; i++)
        {
            var it = _items[i];
            var name = it.Name?.Trim() ?? string.Empty;

            if (name.Length == 0)
                return (false, $"第 {i + 1} 个目标的 Name 不能为空");

            if (!seen.Add(name))
                return (false, $"远程 SCP 目标 Name 重复：'{name}'（Name 必须唯一，比较区分大小写）");

            if (string.IsNullOrWhiteSpace(it.Host))
                return (false, $"远程 SCP '{name}' 的 Host 不能为空");

            if (it.Port is < 1 or > 65535)
                return (false, $"远程 SCP '{name}' 的端口 {it.Port} 非法（须 1-65535）");

            if (IsConfiguredButBlank(it.CalledAe))
                return (false, $"远程 SCP '{name}' 的 CalledAeTitle 已配置但为纯空白（清空或填写有效值）");

            if (IsConfiguredButBlank(it.CallingAe))
                return (false, $"远程 SCP '{name}' 的 CallingAeTitle 已配置但为纯空白（清空或填写有效值）");

            var protocol = it.Protocol?.Trim();
            if (protocol is { Length: > 0 } && !string.Equals(protocol, "DICOM", StringComparison.OrdinalIgnoreCase))
                return (false, $"远程 SCP '{name}' 的协议 '{protocol}' 暂不支持（本期仅 DICOM）");
        }

        return (true, null);
    }

    private static bool IsConfiguredButBlank(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Trim().Length == 0;

    private static RemoteScpTarget ToTarget(RemoteScpEditItem it) => new()
    {
        Name = it.Name.Trim(),
        Host = it.Host.Trim(),
        Port = it.Port,
        CalledAeTitle = string.IsNullOrWhiteSpace(it.CalledAe) ? string.Empty : it.CalledAe.Trim(),
        CallingAeTitle = string.IsNullOrWhiteSpace(it.CallingAe) ? null : it.CallingAe.Trim(),
        Protocol = string.IsNullOrWhiteSpace(it.Protocol) ? "DICOM" : it.Protocol.Trim(),
    };

    private void RaiseBusy()
    {
        LoadCommand.RaiseCanExecuteChanged();
        AddCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
    }
}
