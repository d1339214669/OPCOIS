using FellowOakDicom;
using OIS.DicomScp.Index;
using OIS.DicomScp.Wpf.Commands;

namespace OIS.DicomScp.Wpf.ViewModels;

/// <summary>
/// 详情核对页签 ViewModel（AC-03 / AC-09）。
/// 用 fo-dicom 打开 &lt;StorageDirectory&gt;/&lt;file_path&gt; 原始文件，逐项对比关键标签与库记录，
/// 全部一致 = Consistent，任一不一致 = Inconsistent；文件缺失/读失败 → 明确错误状态，不崩溃（AC-03/AC-09 失败必须可见）。
/// 对比按原样字符串严格相等（null == null 为一致），不做时区/格式/大小写/trim 转换（AC-09）。
/// 纯逻辑、不引用 WPF 命名空间（headless 可测，AC-10）。
/// </summary>
public sealed class InstanceDetailViewModel : ViewModelBase
{
    private readonly string _storageRoot;

    private InstanceRow? _selected;
    private IReadOnlyList<FieldCheck> _report = Array.Empty<FieldCheck>();
    private string _statusText = "请选择实例并执行核对";
    private string? _errorText;
    private bool _isBusy;
    private bool _isConsistent;

    /// <summary>
    /// 创建详情核对 ViewModel。
    /// </summary>
    /// <param name="storageRoot">存储根目录（appsettings.json Storage.Directory；库中 file_path 为相对此目录的相对路径）。</param>
    public InstanceDetailViewModel(string storageRoot)
    {
        if (string.IsNullOrWhiteSpace(storageRoot))
            throw new ArgumentException("存储根目录不能为空", nameof(storageRoot));

        _storageRoot = System.IO.Path.GetFullPath(storageRoot);
        LoadCommand = new RelayCommand(() => _ = LoadAsync(Selected), () => Selected is not null && !IsBusy);
    }

    /// <summary>核对命令（对 <see cref="Selected"/> 实例执行文件↔库核对）。</summary>
    public IAppCommand LoadCommand { get; }

    /// <summary>待核对实例（由实例浏览页「核对」事件设置）。</summary>
    public InstanceRow? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                OnPropertyChanged(nameof(SelectedSummary));
                LoadCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>选中实例摘要（InternalId / SOPInstanceUID 原样）。</summary>
    public string? SelectedSummary
        => _selected is null ? null : $"{_selected.InternalId} / {_selected.SopInstanceUid}";

    /// <summary>逐项核对报告（5 个关键标签）。</summary>
    public IReadOnlyList<FieldCheck> Report { get => _report; private set => SetProperty(ref _report, value); }

    /// <summary>整体结论文本（Consistent / Inconsistent / 失败说明）。</summary>
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    /// <summary>是否全部一致（仅核对成功时有意义）。</summary>
    public bool IsConsistent { get => _isConsistent; private set => SetProperty(ref _isConsistent, value); }

    /// <summary>错误文本（文件缺失/读失败时明确原因；成功为 null）。</summary>
    public string? ErrorText { get => _errorText; private set => SetProperty(ref _errorText, value); }

    /// <summary>是否正在核对。</summary>
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }

    /// <summary>
    /// 执行核对（AC-03）：fo-dicom 打开 &lt;storageRoot&gt;/&lt;FilePath&gt;，
    /// 对比 SOPInstanceUID / PatientID / StudyInstanceUID / Modality / StudyDate 与库记录；
    /// 文件缺失或读取失败 → 可见错误状态（不静默、不崩溃）。
    /// </summary>
    public async Task LoadAsync(InstanceRow? row)
    {
        if (row is null || IsBusy)
            return;

        Selected = row;
        IsBusy = true;
        ErrorText = null;
        Report = Array.Empty<FieldCheck>();
        IsConsistent = false;
        LoadCommand.RaiseCanExecuteChanged();

        try
        {
            var filePath = System.IO.Path.Combine(_storageRoot, row.FilePath);
            if (!System.IO.File.Exists(filePath))
            {
                StatusText = "核对失败：存储文件缺失";
                ErrorText = $"存储文件缺失：{filePath}（库中记录 file_path={row.FilePath}）";
                return;
            }

            var file = await DicomFile.OpenAsync(filePath);
            var ds = file.Dataset;

            Report = new[]
            {
                Check(DicomTag.SOPInstanceUID, ds, row.SopInstanceUid),
                Check(DicomTag.PatientID, ds, row.PatientId),
                Check(DicomTag.StudyInstanceUID, ds, row.StudyInstanceUid),
                Check(DicomTag.Modality, ds, row.Modality),
                Check(DicomTag.StudyDate, ds, row.StudyDate),
            };

            IsConsistent = Report.All(c => c.IsConsistent);
            StatusText = IsConsistent
                ? "核对结果：一致（Consistent）"
                : "核对结果：存在不一致字段（Inconsistent）";
        }
        catch (Exception ex)
        {
            // AC-03/AC-09 失败必须可见：读失败（非 DICOM/损坏/权限）→ 明确错误状态，不静默。
            Report = Array.Empty<FieldCheck>();
            IsConsistent = false;
            StatusText = "核对失败：文件读取错误";
            ErrorText = $"文件读取失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            LoadCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// 标签 → 报告列显示名（fo-dicom 5.x 的 <c>DicomTag.ToString()</c> 返回「(GGGG,TTTT)」而非常量名，
    /// 报告列用常量名可读性更好；未知标签回退为标签码）。
    /// </summary>
    private static readonly Dictionary<DicomTag, string> TagNames = new()
    {
        [DicomTag.SOPInstanceUID] = "SOPInstanceUID",
        [DicomTag.PatientID] = "PatientID",
        [DicomTag.StudyInstanceUID] = "StudyInstanceUID",
        [DicomTag.Modality] = "Modality",
        [DicomTag.StudyDate] = "StudyDate",
    };

    /// <summary>单标签对比：文件值 vs 库值，严格字符串相等（null 对 null 一致；缺失为 null，不臆造，AC-09）。</summary>
    private static FieldCheck Check(DicomTag tag, DicomDataset ds, string? dbValue)
    {
        string? fileValue = ds.TryGetString(tag, out var v) && v.Length > 0 ? v : null;
        return new FieldCheck(
            TagNames.TryGetValue(tag, out var name) ? name : tag.ToString(),
            fileValue,
            dbValue,
            string.Equals(fileValue, dbValue, StringComparison.Ordinal));
    }
}
