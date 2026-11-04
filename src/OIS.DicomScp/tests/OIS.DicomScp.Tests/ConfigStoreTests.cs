using OIS.DicomScp;
using OIS.DicomScp.Scu;
using Xunit;

namespace OIS.DicomScp.Tests;

/// <summary>
/// task-003 AC-04 集成测试：IConfigStore / ConfigStore（appsettings.json 读 + 原子写回）。
/// 纯文件系统测试（无需 PG），验证 round-trip、字段名格式兼容（task-002 RemoteScpRepository 可重读）、
/// 读-改-写不丢其他配置节、原子写无临时文件残留。
/// </summary>
public sealed class ConfigStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;
    private readonly TestLogger _logger = new();

    public ConfigStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ois-config-store-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "appsettings.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static AppConfig FullConfig() => new()
    {
        Scp = new ScpSection
        {
            Port = 12345,
            AeTitle = "OIS",
            StorageServiceSopClasses = new List<string> { "*" },
        },
        Scu = new ScuSection
        {
            CallingAeTitle = "OIS",
            RemoteScps = new List<RemoteScpTarget>
            {
                new()
                {
                    Name = "P1",
                    Host = "10.0.0.1",
                    Port = 114,
                    CalledAeTitle = "PACS",
                    CallingAeTitle = "OIS",
                    Protocol = "DICOM",
                },
            },
        },
        Storage = new StorageSection { Directory = @"D:\OIS\storage" },
        Postgres = new PostgresSection
        {
            ConnectionString = "Host=localhost;Port=5432;Username=postgres;Database=dicom",
        },
    };

    [Fact]
    public async Task Save_Then_Load_RoundTrip()
    {
        var store = new ConfigStore(_path, _logger);
        var cfg = FullConfig();
        await store.SaveAsync(cfg);

        Assert.True(File.Exists(_path), "保存后 appsettings.json 应存在");
        var loaded = await store.LoadAsync();

        Assert.Equal(12345, loaded.Scp.Port);
        Assert.Equal("OIS", loaded.Scp.AeTitle);
        Assert.Equal("OIS", loaded.Scu.CallingAeTitle);
        Assert.NotNull(loaded.Scu.RemoteScps);
        var remote = Assert.Single(loaded.Scu.RemoteScps!);
        Assert.Equal("P1", remote.Name);
        Assert.Equal("10.0.0.1", remote.Host);
        Assert.Equal(114, remote.Port);
        Assert.Equal("PACS", remote.CalledAeTitle);
        Assert.Equal(@"D:\OIS\storage", loaded.Storage.Directory);
        Assert.Equal("Host=localhost;Port=5432;Username=postgres;Database=dicom", loaded.Postgres.ConnectionString);
    }

    [Fact]
    public async Task Saved_Json_Keeps_FieldNames_And_RemoteScpRepository_Can_Reload()
    {
        // AC-04 格式兼容：写回的 JSON 保持既有字段名/嵌套结构（Scu.CallingAeTitle 与 Scu.RemoteScps[]），
        // task-002 的 RemoteScpRepository 必须能重新加载成功。
        var store = new ConfigStore(_path, _logger);
        await store.SaveAsync(FullConfig());

        var json = await File.ReadAllTextAsync(_path);
        Assert.Contains("\"CallingAeTitle\"", json);
        Assert.Contains("\"RemoteScps\"", json);
        Assert.Contains("\"CalledAeTitle\"", json);
        Assert.Contains("\"Scp\"", json);
        Assert.Contains("\"Storage\"", json);
        Assert.Contains("\"Postgres\"", json);

        var loaded = await store.LoadAsync();
        var repo = new RemoteScpRepository(new ScuOptions
        {
            CallingAeTitle = loaded.Scu.CallingAeTitle,
            RemoteScps = loaded.Scu.RemoteScps ?? new List<RemoteScpTarget>(),
        }); // 不抛异常 = 格式兼容
        var found = repo.FindByName("P1");
        Assert.NotNull(found);
        Assert.Equal("10.0.0.1", found!.Host);
    }

    [Fact]
    public async Task Load_Missing_File_Returns_Defaults()
    {
        var store = new ConfigStore(Path.Combine(_dir, "missing.json"), _logger);
        var cfg = await store.LoadAsync();
        Assert.NotNull(cfg);
        Assert.Null(cfg.Scp.Port); // 默认配置：缺失即 null/空，不臆造（AC-09）
        Assert.NotNull(cfg.Scu.RemoteScps);
        Assert.Empty(cfg.Scu.RemoteScps!);
    }

    [Fact]
    public async Task Save_ReadModifyWrite_Preserves_Other_Sections()
    {
        // AC-04：保存远程 SCP 不得丢失其他配置节（Scp/Storage/Postgres）——「读-改-写」。
        var store = new ConfigStore(_path, _logger);
        await store.SaveAsync(FullConfig());

        var loaded = await store.LoadAsync();
        loaded.Scu.RemoteScps = new List<RemoteScpTarget>
        {
            new()
            {
                Name = "P2",
                Host = "10.0.0.2",
                Port = 115,
                CalledAeTitle = "PACS2",
                Protocol = "DICOM",
            },
        };
        await store.SaveAsync(loaded);

        var reloaded = await store.LoadAsync();
        Assert.Equal(12345, reloaded.Scp.Port); // 其他节未丢失
        Assert.Equal(@"D:\OIS\storage", reloaded.Storage.Directory);
        Assert.Equal("Host=localhost;Port=5432;Username=postgres;Database=dicom", reloaded.Postgres.ConnectionString);
        Assert.NotNull(reloaded.Scu.RemoteScps);
        var remote = Assert.Single(reloaded.Scu.RemoteScps!);
        Assert.Equal("P2", remote.Name);
    }

    [Fact]
    public async Task Save_Atomic_NoTempFile_Left_And_Overwrites()
    {
        var store = new ConfigStore(_path, _logger);
        var cfg1 = FullConfig();
        await store.SaveAsync(cfg1);

        var cfg2 = FullConfig();
        cfg2.Scp.Port = 23456;
        await store.SaveAsync(cfg2);

        // 原子写：无临时文件残留；目标文件为第二次保存内容（覆盖成功）。
        Assert.DoesNotContain(Directory.EnumerateFiles(_dir), f => f.Contains(".tmp-", StringComparison.Ordinal));
        var reloaded = await store.LoadAsync();
        Assert.Equal(23456, reloaded.Scp.Port);
    }

    [Fact]
    public async Task Save_Null_Throws()
    {
        var store = new ConfigStore(_path, _logger);
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.SaveAsync(null!));
    }
}
