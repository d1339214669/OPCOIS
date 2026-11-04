using FellowOakDicom;
using OIS.DicomScp.Index;
using OIS.DicomScp.Wpf.ViewModels;
using Xunit;

namespace OIS.DicomScp.Wpf.Tests;

/// <summary>
/// 详情核对 ViewModel 逻辑测试（AC-03 / AC-09，headless）：
/// 用 fo-dicom 在临时存储根写入真实 DICOM 文件，驱动文件↔库核对各分支
/// （全一致 / 单字段不一致 / 缺失字段 null 不臆造 / 双缺失一致 / 文件缺失 / 文件损坏）。
/// </summary>
public sealed class InstanceDetailViewModelTests : IDisposable
{
    private readonly string _root;

    public InstanceDetailViewModelTests()
    {
        _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ois-task003-wpftests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex)
        {
            // 临时目录清理失败不影响测试结论（OS 回收）；不静默：输出到 stderr。
            Console.Error.WriteLine($"[OIS.Wpf.Tests] 临时目录清理失败（忽略）：{ex.Message}");
        }
    }

    /// <summary>写入真实 DICOM 文件到 &lt;root&gt;/&lt;relPath&gt;；参数为 null 的标签不写入（模拟缺失）。</summary>
    private void WriteDicom(string relPath, string sopUid, string? patientId, string? studyUid, string? modality, string? studyDate)
    {
        var path = System.IO.Path.Combine(_root, relPath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

        var ds = new DicomDataset
        {
            { DicomTag.SOPInstanceUID, sopUid },
        };
        // fo-dicom 5.x：DicomFile(dataset) 构造器从 dataset 读取 0008,0016/0008,0018 生成 file meta。
        ds.Add(DicomTag.SOPClassUID, DicomUID.CTImageStorage);
        if (patientId is not null)
            ds.Add(DicomTag.PatientID, patientId);
        if (studyUid is not null)
            ds.Add(DicomTag.StudyInstanceUID, studyUid);
        if (modality is not null)
            ds.Add(DicomTag.Modality, modality);
        if (studyDate is not null)
            ds.Add(DicomTag.StudyDate, studyDate);

        var file = new DicomFile(ds);
        file.Save(path);
    }

    private static InstanceRow Row(string uid, string? patient, string? studyUid, string? modality, string? studyDate, string relPath)
        => new()
        {
            InternalId = new string('b', 32),
            SopInstanceUid = uid,
            PatientId = patient,
            StudyInstanceUid = studyUid,
            Modality = modality,
            StudyDate = studyDate,
            FilePath = relPath,
            ReceivedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

    [Fact]
    public async Task Load_AllFields_Match_Is_Consistent_With_Raw_Values()
    {
        // 注意：DICOM 文件内 UID 须为合法纯数字 UID（fo-dicom DicomUID.Parse 校验）；
        // 库中/行中同值字符串比对（AC-09 原样）。
        WriteDicom("c/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "1.2.999.3001.1.1", "PAT-1", "1.2.999.3001.2.1", "CT", "20250701");
        var vm = new InstanceDetailViewModel(_root);
        var row = Row("1.2.999.3001.1.1", "PAT-1", "1.2.999.3001.2.1", "CT", "20250701", "c/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        await vm.LoadAsync(row);

        Assert.True(vm.IsConsistent);
        Assert.Contains("Consistent", vm.StatusText);
        Assert.Null(vm.ErrorText);
        Assert.Equal(5, vm.Report.Count);
        Assert.All(vm.Report, c => Assert.True(c.IsConsistent));
        // AC-09：StudyDate 原样字符串比对（YYYYMMDD，不做 2025-07-01 式转换）。
        var dateCheck = vm.Report.Single(c => c.Field == "StudyDate");
        Assert.Equal("20250701", dateCheck.FileValue);
        Assert.Equal("20250701", dateCheck.DbValue);
    }

    [Fact]
    public async Task Load_StudyDate_Mismatch_Is_Inconsistent_And_Pointed_Out()
    {
        WriteDicom("d/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "1.2.999.3002.1.1", "PAT-2", "1.2.999.3002.2.1", "MR", "20250702");
        var vm = new InstanceDetailViewModel(_root);
        // 库中 StudyDate 与文件不一致。
        var row = Row("1.2.999.3002.1.1", "PAT-2", "1.2.999.3002.2.1", "MR", "20250701", "d/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        await vm.LoadAsync(row);

        Assert.False(vm.IsConsistent);
        Assert.Contains("Inconsistent", vm.StatusText);
        Assert.Null(vm.ErrorText);
        var dateCheck = vm.Report.Single(c => c.Field == "StudyDate");
        Assert.False(dateCheck.IsConsistent);
        Assert.Equal("20250702", dateCheck.FileValue);
        Assert.Equal("20250701", dateCheck.DbValue);
        // 其余 4 项一致。
        Assert.Equal(4, vm.Report.Count(c => c.IsConsistent));
    }

    [Fact]
    public async Task Load_File_Missing_Is_Visible_Error_Not_Crash()
    {
        var vm = new InstanceDetailViewModel(_root);
        var row = Row("1.2.999.task003.d.uid3", "PAT-3", "1.2.999.3003.2.1", "CT", null, "missing/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        await vm.LoadAsync(row); // 不得抛异常

        Assert.False(vm.IsConsistent);
        Assert.Empty(vm.Report);
        Assert.Contains("存储文件缺失", vm.StatusText);
        Assert.NotNull(vm.ErrorText);
        Assert.Contains("bbb", vm.ErrorText);
    }

    [Fact]
    public async Task Load_Corrupt_File_Is_Visible_Error_Not_Crash()
    {
        var corrupt = System.IO.Path.Combine(_root, "e/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(corrupt)!);
        System.IO.File.WriteAllBytes(corrupt, System.Text.Encoding.ASCII.GetBytes("this is not a DICOM file"));

        var vm = new InstanceDetailViewModel(_root);
        var row = Row("1.2.999.task003.d.uid4", "PAT-4", "1.2.999.3004.2.1", "CT", null, "e/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        await vm.LoadAsync(row); // 不得抛异常

        Assert.False(vm.IsConsistent);
        Assert.Empty(vm.Report);
        Assert.Contains("文件读取错误", vm.StatusText);
        Assert.NotNull(vm.ErrorText);
    }

    [Fact]
    public async Task Load_File_Field_Missing_Is_Null_Not_Invented_And_Mismatched()
    {
        // 文件里没有 Modality 标签，库里有 "CT" → 该字段 FileValue=null（不臆造）且不一致。
        WriteDicom("f/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "1.2.999.3005.1.1", "PAT-5", "1.2.999.3005.2.1", null, "20250801");
        var vm = new InstanceDetailViewModel(_root);
        var row = Row("1.2.999.3005.1.1", "PAT-5", "1.2.999.3005.2.1", "CT", "20250801", "f/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        await vm.LoadAsync(row);

        Assert.False(vm.IsConsistent);
        var modalityCheck = vm.Report.Single(c => c.Field == "Modality");
        Assert.Null(modalityCheck.FileValue); // AC-09：缺失为 null，不臆造
        Assert.Equal("CT", modalityCheck.DbValue);
        Assert.False(modalityCheck.IsConsistent);
    }

    [Fact]
    public async Task Load_Both_Missing_Is_Consistent()
    {
        // 文件与库都缺失 Modality → null == null 一致（AC-09 严格相等语义）。
        WriteDicom("g/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "1.2.999.3006.1.1", "PAT-6", "1.2.999.3006.2.1", null, null);
        var vm = new InstanceDetailViewModel(_root);
        var row = Row("1.2.999.3006.1.1", "PAT-6", "1.2.999.3006.2.1", null, null, "g/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        await vm.LoadAsync(row);

        Assert.True(vm.IsConsistent);
        var modalityCheck = vm.Report.Single(c => c.Field == "Modality");
        Assert.Null(modalityCheck.FileValue);
        Assert.Null(modalityCheck.DbValue);
        Assert.True(modalityCheck.IsConsistent);
        var dateCheck = vm.Report.Single(c => c.Field == "StudyDate");
        Assert.True(dateCheck.IsConsistent);
    }

    [Fact]
    public void LoadCommand_CanExecute_Only_When_Selected_And_NotBusy()
    {
        var vm = new InstanceDetailViewModel(_root);
        Assert.False(vm.LoadCommand.CanExecute());

        var row = Row("1.2.999.task003.d.uid7", "PAT-7", "1.2.999.3007.2.1", null, null, "h/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        vm.Selected = row;
        Assert.True(vm.LoadCommand.CanExecute());
        Assert.Equal($"{row.InternalId} / {row.SopInstanceUid}", vm.SelectedSummary);
    }

    [Fact]
    public async Task Load_Null_Row_Is_NoOp()
    {
        var vm = new InstanceDetailViewModel(_root);

        await vm.LoadAsync(null);

        Assert.Equal("请选择实例并执行核对", vm.StatusText);
        Assert.Null(vm.ErrorText);
        Assert.Empty(vm.Report);
    }
}
