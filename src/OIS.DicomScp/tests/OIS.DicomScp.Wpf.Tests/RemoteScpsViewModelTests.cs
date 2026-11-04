using OIS.DicomScp;
using OIS.DicomScp.Wpf.ViewModels;
using Xunit;

namespace OIS.DicomScp.Wpf.Tests;

/// <summary>
/// 远程 SCP 配置 ViewModel 逻辑测试（AC-04，headless）：
/// 加载/新增/删除、校验拒绝（Name 空/重复、Host 空、Port 非法、协议非 DICOM）+ 配置保持原样、
/// 读-改-写保持其他配置节、读写失败可见。
/// </summary>
public sealed class RemoteScpsViewModelTests
{
    private static AppConfig CfgWithTargets(params RemoteScpTarget[] targets)
    {
        var cfg = new AppConfig();
        cfg.Scu.RemoteScps = targets.ToList();
        cfg.Scu.CallingAeTitle = "GLOBAL-CAE";
        cfg.Scp.Port = 12345;
        return cfg;
    }

    private static RemoteScpTarget T(string name, string host = "10.0.0.1", int port = 11113)
        => new() { Name = name, Host = host, Port = port, CalledAeTitle = "PACS", Protocol = "DICOM" };

    [Fact]
    public async Task Load_Populates_Items_Selected_And_Status()
    {
        var store = new FakeConfigStore(CfgWithTargets(T("A"), T("B", port: 20000)));
        var vm = new RemoteScpsViewModel(store);

        await vm.LoadAsync();

        Assert.Equal(2, vm.Items.Count);
        Assert.Equal("A", vm.Items[0].Name);
        Assert.Equal(11113, vm.Items[0].Port);
        Assert.Equal("PACS", vm.Items[0].CalledAe);
        Assert.Same(vm.Items[0], vm.Selected);
        Assert.True(vm.HasSelection);
        Assert.Contains("已加载 2", vm.StatusText);
        Assert.Null(vm.ErrorText);
    }

    [Fact]
    public async Task Load_ConfigFailure_Is_Visible_Error()
    {
        var store = new FakeConfigStore(new AppConfig());
        store.LoadException = new UnauthorizedAccessException("文件无读取权限");
        var vm = new RemoteScpsViewModel(store);

        await vm.LoadAsync();

        Assert.Equal("加载失败", vm.StatusText);
        Assert.NotNull(vm.ErrorText);
        Assert.Contains("文件无读取权限", vm.ErrorText);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Add_Creates_Blank_Selected_Item()
    {
        var vm = new RemoteScpsViewModel(new FakeConfigStore(CfgWithTargets(T("A"))));
        await vm.LoadAsync();

        vm.AddCommand.Execute();

        Assert.Equal(2, vm.Items.Count);
        Assert.Same(vm.Items[1], vm.Selected);
        Assert.Equal(string.Empty, vm.Items[1].Name);
        Assert.Equal(11112, vm.Items[1].Port);
        Assert.Equal("DICOM", vm.Items[1].Protocol);
        Assert.True(vm.HasSelection);
    }

    [Fact]
    public async Task Delete_Removes_Selected_Falls_Back_To_First()
    {
        var store = new FakeConfigStore(CfgWithTargets(T("A"), T("B")));
        var vm = new RemoteScpsViewModel(store);
        await vm.LoadAsync();
        vm.Selected = vm.Items[1];

        vm.DeleteCommand.Execute();

        Assert.Single(vm.Items);
        Assert.Equal("A", vm.Items[0].Name);
        Assert.Same(vm.Items[0], vm.Selected);
        // 删除是内存变更：未写配置（点「保存」才写回）。
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task Delete_No_Selection_Is_NoOp()
    {
        var vm = new RemoteScpsViewModel(new FakeConfigStore(new AppConfig()));
        await vm.LoadAsync();
        Assert.Null(vm.Selected);

        Assert.False(vm.DeleteCommand.CanExecute());
        vm.DeleteCommand.Execute();

        Assert.Empty(vm.Items);
    }

    [Fact]
    public async Task Save_Valid_Replaces_Targets_Preserves_Other_Sections()
    {
        var store = new FakeConfigStore(CfgWithTargets(T("A"), T("B")));
        var vm = new RemoteScpsViewModel(store);
        await vm.LoadAsync();

        vm.Items[0].Name = "A2";
        vm.Items[1].Host = "10.9.9.9";
        vm.Items[1].CallingAe = "  X-CAE  ";

        await vm.SaveAsync();

        Assert.Equal(1, store.SaveCount);
        Assert.Equal(2, store.LastSaved!.Scu!.RemoteScps!.Count);
        Assert.Equal("A2", store.LastSaved.Scu.RemoteScps[0].Name);
        Assert.Equal("10.9.9.9", store.LastSaved.Scu.RemoteScps[1].Host);
        Assert.Equal("X-CAE", store.LastSaved.Scu.RemoteScps[1].CallingAeTitle); // trim
        // AC-04：其他配置节与全局 calling AE 保持不丢失。
        Assert.Equal("GLOBAL-CAE", store.LastSaved.Scu.CallingAeTitle);
        Assert.Equal(12345, store.LastSaved.Scp.Port);
        Assert.Contains("已保存", vm.StatusText);
        Assert.Null(vm.ErrorText);
    }

    [Theory]
    [InlineData("dup", "dup")]   // Name Ordinal 重复
    [InlineData("", "A")]        // Name 空
    public async Task Save_Rejects_DuplicateOrEmpty_Name_File_Untouched(string name1, string name2)
    {
        var store = new FakeConfigStore(CfgWithTargets(T("A"), T("B")));
        var vm = new RemoteScpsViewModel(store);
        await vm.LoadAsync();
        vm.Items[0].Name = name1;
        vm.Items[1].Name = name2;

        await vm.SaveAsync();

        Assert.Equal(0, store.SaveCount); // 配置保持原样
        Assert.Equal("保存被拒绝", vm.StatusText);
        Assert.NotNull(vm.ErrorText);
        Assert.Contains("校验失败", vm.ErrorText);
    }

    [Fact]
    public async Task Save_Rejects_Invalid_Host_Port_Protocol()
    {
        var store = new FakeConfigStore(CfgWithTargets(T("A"), T("B")));
        var vm = new RemoteScpsViewModel(store);
        await vm.LoadAsync();

        // 1) Host 空
        vm.Items[0].Host = "   ";
        await vm.SaveAsync();
        Assert.Equal(0, store.SaveCount);
        Assert.Contains("Host", vm.ErrorText);

        // 2) Port 非法（0）
        vm.Items[0].Host = "10.0.0.1";
        vm.Items[0].Port = 0;
        await vm.SaveAsync();
        Assert.Equal(0, store.SaveCount);
        Assert.Contains("端口", vm.ErrorText);

        // 3) Port 非法（>65535）
        vm.Items[0].Port = 70000;
        await vm.SaveAsync();
        Assert.Equal(0, store.SaveCount);
        Assert.Contains("端口", vm.ErrorText);

        // 4) 协议非 DICOM
        vm.Items[0].Port = 11113;
        vm.Items[0].Protocol = "HTTP";
        await vm.SaveAsync();
        Assert.Equal(0, store.SaveCount);
        Assert.Contains("协议", vm.ErrorText);
    }

    [Fact]
    public async Task Save_ConfigWriteFailure_Is_Visible()
    {
        var store = new FakeConfigStore(CfgWithTargets(T("A")));
        store.SaveException = new IOException("磁盘满，写入失败");
        var vm = new RemoteScpsViewModel(store);
        await vm.LoadAsync();

        await vm.SaveAsync();

        Assert.Equal("保存失败", vm.StatusText);
        Assert.NotNull(vm.ErrorText);
        Assert.Contains("磁盘满", vm.ErrorText);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Save_After_Load_Rejects_Duplicate_Case_Sensitively()
    {
        var store = new FakeConfigStore(CfgWithTargets(T("A"), T("B")));
        var vm = new RemoteScpsViewModel(store);
        await vm.LoadAsync();
        vm.Items[1].Name = "a"; // 与 "A" 大小写不同 → Ordinal 不视为重复，允许

        await vm.SaveAsync();

        Assert.Equal(1, store.SaveCount);
        Assert.Contains("a", store.LastSaved!.Scu!.RemoteScps!.Select(t => t.Name));
    }
}
