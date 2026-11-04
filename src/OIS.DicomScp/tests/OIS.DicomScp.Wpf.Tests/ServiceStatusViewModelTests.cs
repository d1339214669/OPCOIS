using OIS.DicomScp;
using OIS.DicomScp.Wpf.ViewModels;
using Xunit;

namespace OIS.DicomScp.Wpf.Tests;

/// <summary>
/// 服务状态 ViewModel 逻辑测试（AC-08 / AC-09，headless）：
/// 初始化读端口/AET（含默认回退）、启动成功/幂等「已运行」提示/启动失败、
/// 停止成功/未运行提示/停止失败、实例数刷新成功/PG 失败显示「—」不臆造。
/// </summary>
public sealed class ServiceStatusViewModelTests
{
    private static (ServiceStatusViewModel Vm, FakeConfigStore Cfg, FakeScpHost Host, FakeInstanceQuery Query) CreateVm(int? port = 12345, string? aeTitle = "OISX")
    {
        var cfg = new AppConfig();
        if (port is not null)
            cfg.Scp.Port = port.Value;
        if (aeTitle is not null)
            cfg.Scp.AeTitle = aeTitle;
        var store = new FakeConfigStore(cfg);
        var host = new FakeScpHost(12345);
        var query = new FakeInstanceQuery { Count = 42 };
        return (new ServiceStatusViewModel(store, host, query), store, host, query);
    }

    [Fact]
    public async Task Initialize_Reads_Port_AeTitle_And_Refreshes_Count()
    {
        var (vm, _, host, query) = CreateVm(port: 12345, aeTitle: "OISX");
        query.Count = 42;

        await vm.InitializeAsync();

        Assert.Equal(12345, vm.Port);
        Assert.Equal("OISX", vm.AeTitle);
        Assert.False(vm.IsRunning);
        Assert.Equal(42, vm.InstanceCount);
        Assert.Equal("42", vm.InstanceCountDisplay);
        Assert.Null(vm.ErrorText);
    }

    [Fact]
    public async Task Initialize_Falls_Back_To_Defaults_When_Config_Blank()
    {
        var (vm, _, _, _) = CreateVm(port: null, aeTitle: null);

        await vm.InitializeAsync();

        // AC-01 默认回退链（与 CLI 一致）：端口 11112、AET "OIS"。
        Assert.Equal(11112, vm.Port);
        Assert.Equal("OIS", vm.AeTitle);
    }

    [Fact]
    public async Task Start_Succeeds_Running_True_And_Port_Shown()
    {
        var (vm, _, host, _) = CreateVm();
        await vm.InitializeAsync();

        await vm.StartAsync();

        Assert.True(vm.IsRunning);
        Assert.Equal(1, host.StartCount);
        Assert.Contains("服务已启动", vm.StatusText);
        Assert.Contains("12345", vm.StatusText);
        Assert.Null(vm.ErrorText);
    }

    [Fact]
    public async Task Start_AlreadyRunning_Is_Idempotent_Message_Not_Error()
    {
        var cfg = new AppConfig
        {
            Scp = new ScpSection { Port = 12345, AeTitle = "OISX" },
        };
        var store = new FakeConfigStore(cfg);
        var host = new FakeScpHost(12345, startRunning: true); // 宿主已在运行：StartAsync 抛 ScpAlreadyRunningException
        var query = new FakeInstanceQuery();
        var vm = new ServiceStatusViewModel(store, host, query);

        await vm.StartAsync();

        // AC-08：重复启动 → 明确「已运行」提示，非错误、非崩溃。
        Assert.True(vm.IsRunning);
        Assert.Equal("服务已在运行（无需重复启动）", vm.StatusText);
        Assert.Null(vm.ErrorText);
        Assert.False(vm.IsBusy);
        Assert.Equal(1, host.StartCount);
    }

    [Fact]
    public async Task Start_Failure_Is_Visible()
    {
        var (vm, _, host, _) = CreateVm();
        host.StartException = new Exception("端口 12345 已被占用");

        await vm.StartAsync();

        Assert.False(vm.IsRunning);
        Assert.Equal("启动失败", vm.StatusText);
        Assert.NotNull(vm.ErrorText);
        Assert.Contains("端口 12345 已被占用", vm.ErrorText);
    }

    [Fact]
    public async Task Stop_Succeeds_Running_False()
    {
        var (vm, _, host, _) = CreateVm();
        await vm.InitializeAsync();
        await vm.StartAsync();
        Assert.True(vm.IsRunning);

        await vm.StopAsync();

        Assert.False(vm.IsRunning);
        Assert.Equal(1, host.StopCount);
        Assert.Contains("服务已停止", vm.StatusText);
        Assert.Null(vm.ErrorText);
    }

    [Fact]
    public async Task Stop_NotRunning_Is_Message_Not_Error()
    {
        var (vm, _, host, _) = CreateVm();
        await vm.InitializeAsync(); // 未启动

        await vm.StopAsync();

        // 未运行 → 明确提示，非错误。
        Assert.False(vm.IsRunning);
        Assert.Contains("服务未运行", vm.StatusText);
        Assert.Null(vm.ErrorText);
    }

    [Fact]
    public async Task Stop_Failure_Is_Visible()
    {
        var (vm, _, host, _) = CreateVm();
        await vm.InitializeAsync();
        await vm.StartAsync();
        host.FailStop = true;

        await vm.StopAsync();

        Assert.Equal("停止失败", vm.StatusText);
        Assert.NotNull(vm.ErrorText);
        Assert.Contains("fake: 停止失败", vm.ErrorText);
    }

    [Fact]
    public async Task Refresh_CountFailure_Is_Visible_With_Dash_Not_Fabricated_Zero()
    {
        var (vm, _, _, query) = CreateVm();
        query.CountException = new Exception("PostgreSQL 连接被拒绝");

        await vm.RefreshAsync();

        // AC-09：PG 失败 → 可见错误；实例数 null（显示「—」），不臆造 0。
        Assert.Null(vm.InstanceCount);
        Assert.Equal("—", vm.InstanceCountDisplay);
        Assert.NotNull(vm.ErrorText);
        Assert.Contains("实例数查询失败", vm.ErrorText);
        Assert.Contains("PostgreSQL 连接被拒绝", vm.ErrorText);
    }

    [Fact]
    public async Task Refresh_Success_Clears_Previous_Error()
    {
        var (vm, _, _, query) = CreateVm();
        query.CountException = new Exception("PG 暂时不可用");
        await vm.RefreshAsync();
        Assert.NotNull(vm.ErrorText);

        query.CountException = null;
        query.Count = 7;
        await vm.RefreshAsync();

        Assert.Equal(7, vm.InstanceCount);
        Assert.Equal("7", vm.InstanceCountDisplay);
        Assert.Null(vm.ErrorText);
    }

    [Fact]
    public async Task Initialize_Reflects_PreRunning_Host_State()
    {
        var cfg = new AppConfig
        {
            Scp = new ScpSection { Port = 12345, AeTitle = "OISX" },
        };
        var store = new FakeConfigStore(cfg);
        var host = new FakeScpHost(12345, startRunning: true);
        var query = new FakeInstanceQuery { Count = 5 };
        var vm = new ServiceStatusViewModel(store, host, query);

        await vm.InitializeAsync();

        Assert.True(vm.IsRunning); // 初始化时同步宿主运行状态
        Assert.Equal(5, vm.InstanceCount);
    }
}
