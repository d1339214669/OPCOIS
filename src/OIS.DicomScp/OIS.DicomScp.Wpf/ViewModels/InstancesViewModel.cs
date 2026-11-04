using OIS.DicomScp.Index;
using OIS.DicomScp.Wpf.Commands;

namespace OIS.DicomScp.Wpf.ViewModels;

/// <summary>
/// 实例浏览页签 ViewModel（AC-02 / AC-09）。
/// 纯逻辑、不引用 WPF 命名空间（headless 可测，AC-10）。
/// 查询失败（如 PostgreSQL 不可用）时显示明确错误，不静默空列表（AC-02/AC-09 失败必须可见）；
/// 过滤无结果返回空集合（合法，非错误）；UID/StudyDate 一律原样字符串（AC-09）。
/// </summary>
public sealed class InstancesViewModel : ViewModelBase
{
    private const int DefaultTake = 200;

    private readonly IInstanceQuery _query;

    private IReadOnlyList<InstanceRow> _results = Array.Empty<InstanceRow>();
    private InstanceRow? _selected;
    private string _patientId = string.Empty;
    private string _studyInstanceUid = string.Empty;
    private string _modality = string.Empty;
    private DateTime? _fromUtc;
    private DateTime? _toUtc;
    private string _statusText = "尚未查询";
    private string? _errorText;
    private bool _isBusy;

    /// <summary>创建实例浏览 ViewModel。</summary>
    /// <param name="query">dicom_instance 只读查询（Core <see cref="IInstanceQuery"/>）。</param>
    public InstancesViewModel(IInstanceQuery query)
    {
        _query = query ?? throw new ArgumentNullException(nameof(query));
        QueryCommand = new RelayCommand(() => _ = QueryAsync(), () => !IsBusy);
        ReviewCommand = new RelayCommand(RaiseReviewRequested, () => Selected is not null && !IsBusy);
        ClearFiltersCommand = new RelayCommand(ClearFilters, () => !IsBusy);
    }

    /// <summary>查询命令（应用当前过滤条件）。</summary>
    public IAppCommand QueryCommand { get; }

    /// <summary>「核对选中实例」命令：触发 <see cref="ReviewRequested"/> 事件。</summary>
    public IAppCommand ReviewCommand { get; }

    /// <summary>清空过滤条件命令。</summary>
    public IAppCommand ClearFiltersCommand { get; }

    /// <summary>选中实例请求核对时触发（MainWindow 据此切到详情页签并执行核对）。</summary>
    public event EventHandler<InstanceRow>? ReviewRequested;

    /// <summary>查询结果集合（received_at 倒序）。</summary>
    public IReadOnlyList<InstanceRow> Results
    {
        get => _results;
        private set => SetProperty(ref _results, value);
    }

    /// <summary>当前选中实例（DataGrid 双向绑定）。</summary>
    public InstanceRow? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
                ReviewCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>过滤：PatientID（空白不过滤）。</summary>
    public string PatientId { get => _patientId; set => SetProperty(ref _patientId, value); }

    /// <summary>过滤：StudyInstanceUID（空白不过滤）。</summary>
    public string StudyInstanceUid { get => _studyInstanceUid; set => SetProperty(ref _studyInstanceUid, value); }

    /// <summary>过滤：Modality（空白不过滤）。</summary>
    public string Modality { get => _modality; set => SetProperty(ref _modality, value); }

    /// <summary>过滤：接收时间下界（UTC，可空）。</summary>
    public DateTime? FromUtc { get => _fromUtc; set => SetProperty(ref _fromUtc, value); }

    /// <summary>过滤：接收时间上界（UTC，可空）。</summary>
    public DateTime? ToUtc { get => _toUtc; set => SetProperty(ref _toUtc, value); }

    /// <summary>状态文本（查询结果概要）。</summary>
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    /// <summary>错误文本（查询失败时明确原因；成功为 null）。</summary>
    public string? ErrorText { get => _errorText; private set => SetProperty(ref _errorText, value); }

    /// <summary>是否正在查询。</summary>
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }

    /// <summary>
    /// 执行查询（AC-02）：按当前过滤条件查询，结果倒序；
    /// 失败 → <see cref="ErrorText"/> 明确原因 + 空结果（绝不静默空列表）。
    /// </summary>
    public async Task QueryAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        ErrorText = null;
        QueryCommand.RaiseCanExecuteChanged();
        ClearFiltersCommand.RaiseCanExecuteChanged();

        try
        {
            var rows = await _query.QueryAsync(
                BlankToNull(PatientId),
                BlankToNull(StudyInstanceUid),
                BlankToNull(Modality),
                FromUtc,
                ToUtc,
                DefaultTake,
                CancellationToken.None);

            Results = rows;
            Selected = null;
            StatusText = rows.Count == 0
                ? "查询完成：0 行（过滤无结果为合法空集，非错误）"
                : $"查询完成：{rows.Count} 行（按接收时间倒序，最多 {DefaultTake} 行）";
        }
        catch (Exception ex)
        {
            // AC-02/AC-09 失败必须可见：明确错误 + 空结果，不静默。
            Results = Array.Empty<InstanceRow>();
            Selected = null;
            StatusText = "查询失败";
            ErrorText = $"实例查询失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            QueryCommand.RaiseCanExecuteChanged();
            ClearFiltersCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>清空全部过滤条件。</summary>
    public void ClearFilters()
    {
        PatientId = string.Empty;
        StudyInstanceUid = string.Empty;
        Modality = string.Empty;
        FromUtc = null;
        ToUtc = null;
        StatusText = "过滤条件已清空（点「查询」执行）";
    }

    private void RaiseReviewRequested()
    {
        if (Selected is not null)
            ReviewRequested?.Invoke(this, Selected);
    }

    /// <summary>用户输入空白视为不过滤（过滤输入的用户层处理，非领域数据转换）。</summary>
    private static string? BlankToNull(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
