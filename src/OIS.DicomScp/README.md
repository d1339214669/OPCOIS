# OIS.DicomScp — DICOM Storage SCP 核心存储层 (task-001) + 出站 SCU (task-002)

用 fo-dicom 实现的 **DICOM Storage SCP**，接收 C-ECHO / C-STORE，按 **Orthanc 式** 匿名化落盘，并把元数据抽离到 PostgreSQL 索引库。

**task-002 新增**：作为 **SCU（Service Class User）** 向可配置的**远程 SCP**（PACS / 其他 OIS / TPS）发起 C-ECHO（连通性验证）与 C-STORE（转发本地已存实例），并把每次转发结果写入 `forward_log` 日志表（成功/失败各一行）。

## 目录结构

```
src/OIS.DicomScp/
├─ OIS.DicomScp.sln
├─ README.md
├─ OIS.DicomScp/
│  ├─ Program.cs                # 入口：读配置 → 幂等建表 → 启动 SCP
│  ├─ appsettings.json          # 配置（端口/AET/存储目录/PG 连接串）
│  ├─ Options.cs                # 配置 POCO
│  ├─ Scp/
│  │  ├─ DicomScpService.cs     # fo-dicom SCP（C-ECHO / C-STORE）
│  │  ├─ IStoreHandler.cs       # 存储编排接口
│  │  ├─ StoreHandler.cs        # 落盘+入库编排
│  │  ├─ StorageSopClassRegistry.cs  # Storage SOP Class 白名单
│  │  └─ ScpServiceRuntime.cs   # fo-dicom 静态运行时注入
│  ├─ Storage/
│  │  └─ FileStorage.cs         # 内部 ID 生成 + 分片落盘
│  └─ Index/
│     ├─ InstanceIndex.cs       # Npgsql 访问
│     └─ Schema.sql             # 幂等建表 DDL
└─ tests/OIS.DicomScp.Tests/
   └─ ScpIntegrationTests.cs    # 集成测试（C-ECHO/C-STORE/去重/元数据/异常）
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
dotnet test  OIS.DicomScp.sln   # 需要 localhost:5432 的 PostgreSQL dicom 库
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
