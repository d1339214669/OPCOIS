# OIS.DicomScp — DICOM Storage SCP 核心存储层 (task-001) + 出站 SCU (task-002) + WPF 管理控制台 (task-003)

用 fo-dicom 实现的 **DICOM Storage SCP**，接收 C-ECHO / C-STORE，按 **Orthanc 式** 匿名化落盘，并把元数据抽离到 PostgreSQL 索引库。

**task-002 新增**：作为 **SCU（Service Class User）** 向可配置的**远程 SCP**（PACS / 其他 OIS / TPS）发起 C-ECHO（连通性验证）与 C-STORE（转发本地已存实例），并把每次转发结果写入 `forward_log` 日志表（成功/失败各一行）。

## 目录结构（task-003 重组后：Core 类库 + 控制台 host + WPF 管理控制台）

```
src/OIS.DicomScp/
├─ OIS.DicomScp.sln
├─ README.md
├─ OIS.DicomScp.Core/            # 核心类库（net10.0，无 WPF 依赖；CLI 与 WPF 共用）
│  ├─ ScpHost.cs                 # IScpHost/ScpHost：本地 SCP 启停封装（幂等，AC-08）
│  ├─ AppConfig.cs               # 配置模型（Scp/Scu/Storage/Postgres 节）
│  ├─ ConfigStore.cs             # IConfigStore/ConfigStore：读-改-写 + 原子落盘（temp+Move）
│  ├─ Options.cs                 # 配置 POCO
│  ├─ Scp/                       # fo-dicom SCP（C-ECHO/C-STORE）+ 落盘编排（task-001）
│  ├─ Storage/FileStorage.cs     # 内部 ID 生成 + 分片落盘
│  ├─ Index/                     # Npgsql 访问 + IInstanceQuery/InstanceRow + Schema.sql
│  └─ Scu/                       # 出站 SCU（task-002）+ IForwardLogQuery/ForwardLogRow
├─ OIS.DicomScp/                 # 控制台 host（net10.0）
│  ├─ Program.cs                 # 入口：读配置 → 幂等建表 → SCP 服务 / SCU CLI
│  └─ appsettings.json
├─ OIS.DicomScp.Wpf/             # WPF 管理控制台（net10.0-windows，UseWPF；task-003）
│  ├─ App.xaml(.cs)              # 启动组装 ConsoleServices；失败可见弹窗
│  ├─ MainWindow.xaml(.cs)       # 五页签主窗口（实例浏览/详情核对/远程SCP配置/转发+历史/服务状态）
│  ├─ Composition/ConsoleServices.cs   # 组装：配置/索引/SCU/SCP 宿主 → 5 个 ViewModel
│  ├─ Commands/                  # IAppCommand/RelayCommand/AppCommandBridge（不依赖 WPF 类型）
│  ├─ ViewModels/                # 5 个 headless ViewModel（不引用 WPF 命名空间，AC-10）
│  ├─ Views/                     # 5 个 XAML 视图（代码后置仅做 AppCommandBridge 接线）
│  └─ appsettings.json
└─ tests/
   ├─ OIS.DicomScp.Tests/        # Core + host 集成测试（需 localhost PG；27 项）
   └─ OIS.DicomScp.Wpf.Tests/    # WPF ViewModel headless 测试（fakes；48 项；无需 PG/网络/WPF 窗口）
```

## 一致性抉择（写清，AC-04/AC-05）

**先写文件，后写数据库索引。**

- 理由：文件是 DICOM 实例的**权威副本（无损）**。若索引写入失败，文件仍保留、后续可重建索引；
  反之若「先入库后写文件」，入库成功而写文件失败会留下指向不存在文件的**悬空索引**，更难恢复。
- 失败按 DOMAIN.md「失败必须可见」记录错误并向上抛出，返回 C-STORE 失败状态，不静默吞掉。

**同 SOPInstanceUID 去重策略（AC-05）：跳过（不覆盖、不新增）。**

- C-STORE 收到后**先查索引**：若该 SOPInstanceUID 已存在，直接返回 Success、不写第二个文件、不插第二行。
- 归档表 `sop_instance_uid` 带 `UNIQUE` 约束，且 `INSERT ... ON CONFLICT (sop_instance_uid) DO NOTHING` 双保险。

## 配置（appsettings.json）

```jsonc
{
  "Scp": {
    "Port": 11112,
    "AeTitle": "OIS",
    "StorageServiceSopClasses": ["*"]   // "*"=全部 Storage SOP Class
  },
  "Storage": { "Directory": "D:\\OIS\\storage" },
  "Postgres": { "ConnectionString": "Host=localhost;Port=5432;Username=postgres;Database=dicom" }
}
```

连接串可留空密码（trust 认证）。配置文件缺失时使用以上默认值。

## 构建 & 测试

```bash
dotnet build OIS.DicomScp.sln
dotnet test  OIS.DicomScp.sln   # OIS.DicomScp.Tests 需要 localhost:5432 的 PostgreSQL dicom 库；
                                # OIS.DicomScp.Wpf.Tests 为 headless（fakes + 临时目录真实 DICOM 文件），无需 PG
```

## 运行

```bash
dotnet run --project OIS.DicomScp/OIS.DicomScp.csproj
```

## 手工验证（AC 自查）

SCP 监听 `0.0.0.0:11112`，AET=`OIS`。

### C-ECHO（AC-03）

用 fo-dicom 测试客户端或 DCMTK：

```bash
# DCMTK
$DCMTK_BIN/echoscu -v -aet TEST -aec OIS 127.0.0.1 11112
```

返回 `Echo SCU Status: Success` 即通过。

### C-STORE 落盘 + 明文校验（AC-04）

```bash
# DCMTK storescu
$DCMTK_BIN/storescu -v -aet TEST -aec OIS 127.0.0.1 11112 sample.dcm
```

检查 `D:\OIS\storage\` 下出现新文件：

```
D:\OIS\storage\<id[0..2]>\<id>
```

- 文件名 `<id>` 为 **32 位小写十六进制**（内部 GUID 派生），**不等于**原 SOPInstanceUID / StudyInstanceUID / PatientID。
- 用 `dcmdump`（或 fo-dicom 读回）对照标签：`dcmdump D:\OIS\storage\ab\ab12...` 应与存入前标签一致（无损闭环）。

### 元数据入库（AC-06）

```bash
psql -h localhost -p 5432 -U postgres -d dicom -c 'SELECT internal_id, sop_instance_uid, patient_id, modality, study_date, file_path, file_size_bytes FROM dicom_instance;'
```

- `file_path` 与落盘路径一致；`internal_id` 与文件名一致。
- 缺失字段（如无 Modality / StudyDate）为 `NULL`，不臆造默认值。

### 同实例去重（AC-05）

对**同一** SOP UID 重复 C-STORE 两次：索引里该 SOPInstanceUID 只有一行，存储目录只有一份文件，且不报错。

### 幂等建表（AC-02）

重复启动/停止程序多次，均正常，无「table already exists」报错。

### 异常/边界（AC-07）

- 向 11112 发送非 DICOM 字节：连接被关闭，服务不崩溃，后续 C-ECHO 仍响应。
- 存储目录不存在时自动创建；权限不足时报明确错误并退出。

## 已知行为与限制

- **AET 匹配（AC-08）**：fo-dicom 5.2.6 默认不强制校验 called AE 是否等于配置 AET；SCP 会在日志中记录对端 calling AE。若需要严格 `-aec` 白名单校验，需在后续 task 扩展 `OnReceiveAssociationRequestAsync`。
- fo-dicom 5.2.6 目标框架为 `.NET Standard 2.0`，在 .NET 10 下正常引用运行（已实测，无兼容问题）。
- 本任务**不做** C-FIND / C-MOVE / SCU 转发 / WPF UI（见 spec §5）。

---

# task-002 出站 SCU（转发 / 推送）

OIS 作为 **SCU** 主动向配置的远端 SCP 发起连接，实现 C-ECHO（连通性验证）与 C-STORE（转发本地已存实例）。

## 配置（appsettings.json 新增 `Scu` 节）

```jsonc
{
  "Scu": {
    "CallingAeTitle": "OIS",      // SCU 侧默认 calling AE（fallback）
    "RemoteScps": [               // 远程 SCP 目标列表，可 0..n 个
      { "Name": "TestPacs", "Host": "127.0.0.1", "Port": 11113,
        "CalledAeTitle": "PACS", "CallingAeTitle": "OIS", "Protocol": "DICOM" }
    ]
  }
}
```

- `Name` 是定位目标的关键，**必须唯一**（重复启动报错，AC-01）。
- 配 0 / 1 / 多目标均可正常启动；不配 `RemoteScps`（或空数组）时出站能力可用但无可选目标。
- **calling AE fallback 链**（AC-08 写明）：目标 `CallingAeTitle` → `Scu:CallingAeTitle` → `Scp.AeTitle` → 空串（由 fo-dicom 生成）。
- `Protocol` 预留，本期仅支持 `"DICOM"`。

## 命令行触发

```bash
# 启动 SCP（默认，兼容 task-001）
dotnet run --project OIS.DicomScp/OIS.DicomScp.csproj -- scp

# C-ECHO 验证到某远程 SCP（成功 exit=0，失败 exit=1）
dotnet run ... -- echo-scu --remote <Name>

# C-STORE 转发：按内部 ID 或按 SOPInstanceUID 推送
# 以下 ... 表示 OIS.DicomScp/OIS.DicomScp.csproj 项目路径
dotnet run ... -- store-scu --remote <Name> --id <internalId>
dotnet run ... -- store-scu --remote <Name> --uid <sopInstanceUid>
```

## SCU 服务核心接口（`Scu/` 目录）

- `IDicomScuService`：`EchoAsync`（C-ECHO）、`StoreByInternalIdAsync` / `StoreBySopInstanceUidAsync`（C-STORE 转发）。
- `DicomScuService`：fo-dicom `DicomClient` 实现。**成功与否看响应 `response.Status`**（非“不抛异常即成功”）；`SendAsync` 完成且响应 `Status==Success` 才算成功。
- `RemoteScpRepository`：从配置读取并校验 `RemoteScps`（Name 唯一性、host/port/Protocol 合法性）。
- `IForwardLog` / `ForwardLog`：写 `forward_log` 表（成功/失败各一行）。
- `IRemoteForwarder` / `IMoveScu`：Study/Series 批转发、C-MOVE 拉取的**纯签名占位**，本期不实现。

## 转发内容 = 原文（无损闭环，AC-04/AC-08）

- 从本地存储文件目录按 `file_path` + 存储根目录解析绝对路径，`DicomFile.Open` 读取原文件。
- `DicomCStoreRequest(file)` 原样发送：不重编码、不改 UID、`StudyDate` 保持 `YYYYMMDD` 原样。

## forward_log 转发记录（AC-05）

每次 C-STORE 转发写一行（成功 `status='success'`，失败 `status='failed'`），失败必须可见。

```bash
psql -h localhost -p 5432 -U postgres -d dicom -c \
  "SELECT id, remote_scp_name, internal_id, sop_instance_uid, status, detail, forwarded_at FROM forward_log ORDER BY id DESC LIMIT 10;"
```

- 同实例重复转发**追加多行**（不覆盖首次记录；去重策略由调用方决定）。

---

# task-003 WPF 管理控制台

五页签 MVVM 桌面控制台：**实例浏览 / 详情核对 / 远程 SCP 配置 / 转发 + 历史 / 服务状态**（spec：`docs/tasks/003/spec.md`，AC-01~AC-10）。

## 目标框架（task-003 记录）

| 项目 | TFM | 说明 |
| --- | --- | --- |
| `OIS.DicomScp.Core` | `net10.0` | 纯类库；CLI host 与 WPF 共用（task-003 自 host 抽出，搬家不重写） |
| `OIS.DicomScp`（控制台 host） | `net10.0` | CLI 行为不变（AC-01 冒烟复核） |
| `OIS.DicomScp.Wpf` | **`net10.0-windows`** | `UseWPF`；.NET 10 SDK（10.0.300）构建通过，无需回退 net8.0 |
| `OIS.DicomScp.Wpf.Tests` | **`net10.0-windows`** | 须匹配 WPF 项目 TFM（普通 `net10.0` 无法引用 `net10.0-windows`）；**headless**：不启用 `UseWPF`、不打开任何窗口，在 Windows 桌面运行时上运行 |

## 运行

```bash
dotnet run --project OIS.DicomScp.Wpf/OIS.DicomScp.Wpf.csproj
```

启动时从 exe 目录 `appsettings.json` 组装（端口/AET/存储目录/PG/远程 SCP 目标列表）；
PG 建表失败仅记日志不阻断启动（控制台可继续用，涉及库的页签给出可见错误）。

- **实例浏览**：按 PatientID / StudyInstanceUID / Modality / 入库时间范围（UTC，`yyyy-MM-dd HH:mm`）过滤（空白=不过滤，trim；默认取最近 200 行，received_at 倒序）；「核对」联动到详情核对页签；查询失败显示明确错误 + 空结果，不静默。
- **详情核对**：fo-dicom 打开 `<存储目录>/<file_path>` 原文件，比对 SOPInstanceUID / PatientID / StudyInstanceUID / Modality / StudyDate 与库记录——**严格原样字符串相等**（StudyDate 保持 `YYYYMMDD`，缺失=null，不做时区/格式/大小写转换，AC-09）；文件缺失/损坏 → 明确错误状态，不崩溃。
- **远程 SCP 配置**：编辑目标列表（Name/Host/Port/CalledAe/CallingAe/Protocol）；保存前校验（Name 非空且 Ordinal 唯一、Host 非空、Port 1–65535、CalledAe 已配置不得空白、协议仅 DICOM）；校验拒绝 → 配置保持原样 + 明确原因；通过 → 读-改-写原子落盘，**其他配置节（Scp/Storage/Postgres 与全局 calling AE）不丢失**。
- **转发 + 历史**：选目标 C-ECHO 连通验证 / 选实例 C-STORE 转发（按 SOPInstanceUID，原文无损）；结果与原因可见；转发后自动刷新 `forward_log` 最近 50 行。
- **服务状态**：显示配置端口/AET/运行状态/实例总数（PG）；启动幂等（已运行 → 「服务已在运行」提示而非错误）、停止未运行 → 「服务未运行」提示；实例数查询失败显示「—」，**不臆造 0**。
- 失败一律可见：各页签操作失败给出原因文本，无空 catch、无静默吞错（AC-02~AC-09 纪律）。

## Headless 测试（AC-10）

`OIS.DicomScp.Wpf.Tests`（48 项）用 fake 替身（`FakeInstanceQuery` / `FakeForwardLogQuery` / `FakeScu` / `FakeConfigStore` / `FakeScpHost`）驱动 5 个 ViewModel 的 AC-02~AC-09 逻辑分支；
详情核对测试在**临时存储根写入真实 fo-dicom 文件**（全一致 / 单字段不一致 / 文件缺失字段 / 双缺失 / 文件缺失 / 文件损坏）。

ViewModel 层不引用 WPF 命名空间：命令抽象 `IAppCommand`/`RelayCommand`（`Commands/`，纯 .NET）+ `AppCommandBridge`（桥接 `System.Windows.Input.ICommand`）；
对 `ViewModels/` 源码做 WPF 命名空间 grep = 0 命中（AC-10 判据，CI 可复验）。

## CLI（重构后不变）

控制台 host 的 `scp` / `echo-scu` / `store-scu` 命令行行为不变（AC-01 冒烟复核通过）；
`IScpHost` 封装同一套 fo-dicom 组装逻辑（幂等建表 → 文件存储 → 存储编排注入 → 服务容器初始化），供 WPF「服务状态」页签与测试复用。
