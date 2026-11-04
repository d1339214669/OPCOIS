using OIS.DicomScp.Index;
using OIS.DicomScp.Wpf.ViewModels;
using Xunit;

namespace OIS.DicomScp.Wpf.Tests;

/// <summary>
/// 实例浏览 ViewModel 逻辑测试（AC-02 / AC-09，headless）：
/// 查询成功/无结果/PG 失败分支、过滤条件透传（空白→null）、核对联动事件、清空过滤。
/// </summary>
public sealed class InstancesViewModelTests
{
    private static InstanceRow Row(string uid, string? patient, string? modality, string? studyDate, DateTime receivedAt)
        => new()
        {
            InternalId = new string('a', 32),
            SopInstanceUid = uid,
            PatientId = patient,
            Modality = modality,
            StudyDate = studyDate,
            FilePath = "a/" + new string('a', 32),
            ReceivedAt = receivedAt,
        };

    private static readonly DateTime T0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Query_Success_Populates_Results_Status_And_No_Error()
    {
        var fake = new FakeInstanceQuery();
        fake.Rows.Add(Row("uid-1", "P1", "CT", "20250701", T0.AddMinutes(1)));
        fake.Rows.Add(Row("uid-2", "P2", "MR", null, T0.AddMinutes(2)));
        var vm = new InstancesViewModel(fake);

        await vm.QueryAsync();

        Assert.Equal(2, vm.Results.Count);
        Assert.True(vm.Results[0].SopInstanceUid == "uid-2", "received_at 倒序：uid-2 在前");
        Assert.Contains("查询完成", vm.StatusText);
        Assert.Contains("2", vm.StatusText);
        Assert.Null(vm.ErrorText);
        Assert.False(vm.IsBusy);
        // 空白过滤字段 → null（不过滤）；take 固定 200。
        var q = fake.LastQuery!.Value;
        Assert.Null(q.Patient);
        Assert.Null(q.Study);
        Assert.Null(q.Modality);
        Assert.Null(q.From);
        Assert.Null(q.To);
        Assert.Equal(200, q.Take);
    }

    [Fact]
    public async Task Query_Passes_NonBlank_Filters_Trims_And_Nulls_Blanks()
    {
        var fake = new FakeInstanceQuery();
        var vm = new InstancesViewModel(fake);
        vm.PatientId = "  P9  ";
        vm.StudyInstanceUid = "   "; // 纯空白 → 不过滤
        vm.Modality = "CT";
        vm.FromUtc = T0;
        vm.ToUtc = T0.AddDays(1);

        await vm.QueryAsync();

        var q = fake.LastQuery!.Value;
        Assert.Equal("P9", q.Patient);
        Assert.Null(q.Study);
        Assert.Equal("CT", q.Modality);
        Assert.Equal(T0, q.From);
        Assert.Equal(T0.AddDays(1), q.To);
    }

    [Fact]
    public async Task Query_NoResults_Is_Empty_Set_Not_Error()
    {
        var vm = new InstancesViewModel(new FakeInstanceQuery());

        await vm.QueryAsync();

        // AC-02：过滤无结果 → 空集合（合法），非错误。
        Assert.Empty(vm.Results);
        Assert.Contains("0 行", vm.StatusText);
        Assert.Null(vm.ErrorText);
    }

    [Fact]
    public async Task Query_PgFailure_Is_Visible_Error_Not_Silent_Empty()
    {
        var fake = new FakeInstanceQuery();
        fake.QueryException = new InvalidOperationException("PostgreSQL 不可用：连接被拒绝");
        var vm = new InstancesViewModel(fake);

        await vm.QueryAsync();

        // AC-02/AC-09：失败必须可见——明确错误 + 空结果，绝不静默空列表。
        Assert.Empty(vm.Results);
        Assert.Equal("查询失败", vm.StatusText);
        Assert.NotNull(vm.ErrorText);
        Assert.Contains("实例查询失败", vm.ErrorText);
        Assert.Contains("PostgreSQL 不可用", vm.ErrorText);
        Assert.False(vm.IsBusy);
        // 失败后可再次查询（不锁死）。
        fake.QueryException = null;
        await vm.QueryAsync();
        Assert.Null(vm.ErrorText);
    }

    [Fact]
    public void Review_Only_When_Selected_Raises_ReviewRequested_With_Row()
    {
        var vm = new InstancesViewModel(new FakeInstanceQuery());
        Assert.False(vm.ReviewCommand.CanExecute());

        InstanceRow? seen = null;
        vm.ReviewRequested += (_, row) => seen = row;

        var row = Row("uid-x", "P", "CT", null, T0);
        vm.Selected = row;
        Assert.True(vm.ReviewCommand.CanExecute());

        vm.ReviewCommand.Execute();

        Assert.Same(row, seen);

        // 取消选中后命令不可用，Execute 无副作用。
        vm.Selected = null;
        Assert.False(vm.ReviewCommand.CanExecute());
        vm.ReviewCommand.Execute();
        Assert.Same(row, seen); // 未再触发
    }

    [Fact]
    public void ClearFilters_Resets_All_Fields()
    {
        var vm = new InstancesViewModel(new FakeInstanceQuery());
        vm.PatientId = "P";
        vm.StudyInstanceUid = "S";
        vm.Modality = "CT";
        vm.FromUtc = T0;
        vm.ToUtc = T0.AddDays(1);

        vm.ClearFiltersCommand.Execute();

        Assert.Equal(string.Empty, vm.PatientId);
        Assert.Equal(string.Empty, vm.StudyInstanceUid);
        Assert.Equal(string.Empty, vm.Modality);
        Assert.Null(vm.FromUtc);
        Assert.Null(vm.ToUtc);
        Assert.Contains("已清空", vm.StatusText);
    }

    [Fact]
    public async Task Query_Repeated_Calls_All_Complete_Without_Stuck_Busy()
    {
        var fake = new FakeInstanceQuery();
        var vm = new InstancesViewModel(fake);

        await vm.QueryAsync();
        await vm.QueryAsync();
        await vm.QueryAsync();

        Assert.Equal(3, fake.QueryCount);
        Assert.False(vm.IsBusy);
    }
}
