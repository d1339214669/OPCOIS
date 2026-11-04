using OIS.DicomScp;
using OIS.DicomScp.Index;
using OIS.DicomScp.Scu;
using OIS.DicomScp.Wpf.ViewModels;
using Xunit;

namespace OIS.DicomScp.Wpf.Tests;

/// <summary>
/// 转发 + 历史 ViewModel 逻辑测试（AC-05 / AC-07，headless）：
/// 加载目标/实例、C-ECHO 成功/失败/未预期异常、C-STORE 成功/失败、转发后刷新历史、历史查询失败可见。
/// </summary>
public sealed class ForwardingViewModelTests
{
    private static readonly DateTime T0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static InstanceRow Row(string uid, DateTime receivedAt)
        => new()
        {
            InternalId = new string('c', 32),
            SopInstanceUid = uid,
            PatientId = "P",
            Modality = "CT",
            FilePath = "c/" + new string('c', 32),
            ReceivedAt = receivedAt,
        };

    private static (ForwardingViewModel Vm, FakeConfigStore Cfg, FakeScu Scu, FakeInstanceQuery Inst, FakeForwardLogQuery Log) CreateVm()
    {
        var cfg = new AppConfig();
        cfg.Scu.RemoteScps = new List<RemoteScpTarget>
        {
            new() { Name = "PACS-A", Host = "10.0.0.1", Port = 11113, CalledAeTitle = "PACS", Protocol = "DICOM" },
            new() { Name = "PACS-B", Host = "10.0.0.2", Port = 11114, CalledAeTitle = "PACS", Protocol = "DICOM" },
        };
        var store = new FakeConfigStore(cfg);
        var scu = new FakeScu();
        var inst = new FakeInstanceQuery();
        inst.Rows.Add(Row("uid-1", T0.AddMinutes(1)));
        inst.Rows.Add(Row("uid-2", T0.AddMinutes(2)));
        var log = new FakeForwardLogQuery();
        return (new ForwardingViewModel(store, scu, inst, log), store, scu, inst, log);
    }

    [Fact]
    public async Task LoadAll_Populates_Targets_First_Selected_And_Instances()
    {
        var (vm, _, _, _, _) = CreateVm();

        await vm.LoadAllAsync();

        Assert.Equal(2, vm.Targets.Count);
        Assert.Equal("PACS-A", vm.SelectedTarget!.Name);
        Assert.Equal(2, vm.Instances.Count);
        Assert.Null(vm.SelectedInstance);
        Assert.Contains("已加载 2", vm.StatusText);
        Assert.Null(vm.ErrorText);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task LoadAll_InstanceQueryFailure_Is_Visible()
    {
        var (vm, _, _, inst, _) = CreateVm();
        inst.QueryException = new Exception("PG 超时");

        await vm.LoadAllAsync();

        Assert.Equal("加载失败", vm.StatusText);
        Assert.NotNull(vm.ErrorText);
        Assert.Contains("PG 超时", vm.ErrorText);
        // 目标仍已加载（失败前完成）。
        Assert.Equal(2, vm.Targets.Count);
    }

    [Fact]
    public async Task LoadAll_NoTargets_Allowed_With_Count_Text()
    {
        var (vm, store, _, _, _) = CreateVm();
        var empty = new AppConfig();
        store.Current.Scu.RemoteScps = new List<RemoteScpTarget>(); // 0 个目标合法（AC-01 0..n）

        await vm.LoadAllAsync();

        Assert.Empty(vm.Targets);
        Assert.Null(vm.SelectedTarget);
        Assert.False(vm.EchoCommand.CanExecute());
        Assert.False(vm.StoreCommand.CanExecute());
        Assert.Contains("已加载 0", vm.StatusText);
    }

    [Fact]
    public async Task Echo_Success_Is_Visible()
    {
        var (vm, _, scu, _, _) = CreateVm();
        await vm.LoadAllAsync();

        await vm.EchoAsync();

        Assert.Equal(1, scu.EchoCount);
        Assert.Same(vm.SelectedTarget, scu.LastEchoTarget);
        Assert.Contains("成功", vm.EchoStatusText);
        Assert.Contains("PACS-A", vm.EchoStatusText);
        Assert.Null(vm.EchoReasonText);
        Assert.Null(vm.ErrorText);
    }

    [Fact]
    public async Task Echo_Failed_Reason_Visible_And_Can_Retest_Other_Target()
    {
        var (vm, _, scu, _, _) = CreateVm();
        scu.EchoResult = new EchoResult(EchoStatus.Failed, "由于目标计算机积极拒绝，无法连接");
        await vm.LoadAllAsync();

        await vm.EchoAsync();

        Assert.Contains("失败", vm.EchoStatusText);
        Assert.Equal("由于目标计算机积极拒绝，无法连接", vm.EchoReasonText);
        Assert.False(vm.IsBusy);

        // AC-05：失败后可继续测下一目标（不崩溃、状态可复位）。
        vm.SelectedTarget = vm.Targets[1];
        await vm.EchoAsync();
        Assert.Equal(2, scu.EchoCount);
        Assert.Contains("PACS-B", vm.EchoStatusText);
    }

    [Fact]
    public async Task Echo_Unexpected_Exception_Is_Visible_Not_Crash()
    {
        var (vm, _, scu, _, _) = CreateVm();
        scu.EchoException = new InvalidOperationException("socket 未初始化（未预期）");
        await vm.LoadAllAsync();

        await vm.EchoAsync(); // 不得抛异常

        Assert.NotNull(vm.EchoReasonText);
        Assert.Contains("socket 未初始化", vm.EchoReasonText);
        Assert.NotNull(vm.ErrorText);
        Assert.Contains("未预期异常", vm.ErrorText);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Store_Success_Updates_Status_And_Refreshes_History()
    {
        var (vm, _, scu, _, log) = CreateVm();
        log.Rows.Add(new ForwardLogRow
        {
            Id = 1,
            RemoteScpName = "PACS-A",
            InternalId = new string('c', 32),
            SopInstanceUid = "uid-1",
            Status = "success",
            ForwardedAt = T0,
        });
        await vm.LoadAllAsync();
        vm.SelectedInstance = vm.Instances.First(r => r.SopInstanceUid == "uid-1");

        await vm.StoreAsync();

        Assert.Equal(1, scu.StoreCount);
        var (target, sopUid) = scu.LastStore!.Value;
        Assert.Equal("PACS-A", target!.Name);
        Assert.Equal("uid-1", sopUid);
        Assert.Contains("转发成功", vm.StoreStatusText);
        Assert.Null(vm.StoreReasonText);
        // 转发后历史自动刷新（AC-07）。
        Assert.Single(vm.History);
        Assert.Equal(50, log.LastTake);
    }

    [Fact]
    public async Task Store_Failed_Reason_Visible_And_Can_Retry()
    {
        var (vm, _, scu, _, _) = CreateVm();
        scu.StoreResult = new ForwardResult(StoreStatus.Failed, "未找到该 SOPInstanceUID 的实例");
        await vm.LoadAllAsync();
        vm.SelectedInstance = vm.Instances[0];

        await vm.StoreAsync();

        Assert.Contains("转发失败", vm.StoreStatusText);
        Assert.Equal("未找到该 SOPInstanceUID 的实例", vm.StoreReasonText);
        Assert.False(vm.IsBusy);

        // 失败后可重试（AC-07 不崩溃）。
        scu.StoreResult = new ForwardResult(StoreStatus.Success, null);
        await vm.StoreAsync();
        Assert.Contains("转发成功", vm.StoreStatusText);
        Assert.Equal(2, scu.StoreCount);
    }

    [Fact]
    public async Task Store_Without_Selection_Is_NoOp()
    {
        var (vm, _, scu, _, _) = CreateVm();

        // 未加载：无目标无实例。
        vm.StoreCommand.Execute();
        Assert.Equal(0, scu.StoreCount);

        await vm.LoadAllAsync();
        vm.SelectedInstance = null;
        Assert.False(vm.StoreCommand.CanExecute());
        vm.StoreCommand.Execute();
        Assert.Equal(0, scu.StoreCount);
    }

    [Fact]
    public async Task RefreshHistory_Failure_Is_Visible_With_Empty_History()
    {
        var (vm, _, _, _, log) = CreateVm();
        log.Exception = new Exception("PG 连接池耗尽");

        await vm.RefreshHistoryAsync();

        Assert.Empty(vm.History);
        Assert.NotNull(vm.ErrorText);
        Assert.Contains("转发历史查询失败", vm.ErrorText);
        Assert.Contains("连接池耗尽", vm.ErrorText);
    }

    [Fact]
    public async Task RefreshHistory_Success_Populates_Desc()
    {
        var (vm, _, _, _, log) = CreateVm();
        var t = T0;
        for (var i = 1; i <= 3; i++)
        {
            log.Rows.Add(new ForwardLogRow
            {
                Id = i,
                RemoteScpName = "PACS-A",
                InternalId = new string('d', 32),
                Status = i == 2 ? "failed" : "success",
                Detail = i == 2 ? "AET 拒绝" : null,
                ForwardedAt = t,
            });
            t = t.AddMinutes(1);
        }

        await vm.RefreshHistoryAsync();

        Assert.Equal(3, vm.History.Count);
        Assert.Equal(3, vm.History[0].Id); // id 倒序
        Assert.Equal("failed", vm.History[1].Status);
        Assert.Equal("AET 拒绝", vm.History[1].Detail);
    }
}
