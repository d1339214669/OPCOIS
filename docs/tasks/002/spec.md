# task-002 Spec — 可配置远程 SCP 出站连接（SCU 转发/推送）

- 任务编号：`task-002`
- 分支名：`feature/task-002-scu-forward`
- 角色：dev-req（需求 Agent）
- 状态：待 PO 确认（确认后 dev-dev 接手实现）
- 技术栈：C# / .NET 10、fo-dicom 5.2.6（NuGet `fo-dicom`）、Npgsql/PostgreSQL、配置文件驱动
- 前置：基于 task-001 已实现的 DICOM Storage SCP（`src/OIS.DicomScp/`）扩展

---

## 1. 背景

task-001（`feature/task-001-dicom-scp-storage`，已合并 develop）实现了 **DICOM Storage SCP**：监听 TCP `11112`，接收 C-ECHO / C-STORE，按 **Orthanc 式** 匿名化落盘，并把元数据抽离到 PostgreSQL 索引库（`dicom_instance` 表）。当前 SCP **只收不发**——它是一个被动接收方。

`task-002` 为它增加**出站连接能力**：让 OIS 能作为 **SCU（Service Class User）** 主动向**可配置的远程 SCP**（如 PACS、其他 OIS、TPS 工作站）发起 DICOM 网络连接，实现两类基本操作：

1. **C-ECHO SCU** —— 验证到某个远程 SCP 的连通性（连接/服务可用性检测，等价 ping）。
2. **C-STORE SCU** —— 把本地已存储（task-001 落盘 + 索引进库）的某个 DICOM 实例，从存储目录读到内存，作为 SCU 推送到指定远程 SCP。

这是一切出站/互操作（转发到 PACS、下发到 TPS 工作站、灾备复制）的**基础设施**。后续的「按 Study/Series 批量转发」「C-MOVE 从远程拉取」等更高级能力，本期只做 **abstraction 预留**（接口定义），不要求完整实现。

本 spec 是 dev-dev 的实现依据，也是 dev-review / dev-test 的验收依据。**AC 必须逐条可被命令/断言验证**，不能是定性描述。

## 2. 目标

1. 在配置文件（`appsettings.json`）中维护**一个或多个远程 SCP 目标**，每个目标含：名称/别名、主机 host、端口、called/remote AE Title、calling/local AE Title、可选协议（默认 DICOM TCP）。支持在配置文件中增删改。
2. 实现 **C-ECHO SCU**：对指定远程 SCP 发起验证，返回成功/失败 + 失败原因，不崩。
3. 实现 **C-STORE SCU**：按内部 ID 或 SOP Instance UID 定位本地已存储实例，从存储目录读取 DICOM 文件 → 与远程 SCP 建关联（用配置的 calling AE + remote AE）→ C-STORE 发送 → 返回状态 → 记录转发结果。
4. 为「转发 Study 下所有实例 / Series 下所有实例」「C-MOVE 拉取」预留抽象接口，本期可只定义不实现。
5. 配置 / 代码分层 / 命名 / 表结构 / 一致性约定与 task-001 完全一致，可扩展。

## 3. 领域术语（与 `D:\OIS\CONTEXT.md`、`D:\MyOpc\DOMAIN.md`、task-001 spec 一致）

| 术语 | 含义（本 spec 采用） |
|---|---|
| SCU | Service Class User，**发起** DICOM 网络请求的客户端角色（本任务的主角） |
| SCP | 接收 DICOM 请求的服务端角色；本任务 OIS 作为 SCU 连接**远程 SCP** |
| 远程 SCP（远端目标） | 配置文件中声明的出站对端（PACS / 其他 OIS / TPS 工作站等），含 host/port/AET |
| calling/local AE Title | 本端（OIS）发起连接时宣告的应用实体标题 |
| called/remote AE Title | 远端 SCP 期望收到的应用实体标题，用于路由/鉴权 |
| C-ECHO | 连接/服务存活校验（等价 ping），本任务 OIS 作为**发起方** |
| C-STORE | 存储请求，把实例从本端**推送给**远端 SCP |
| 内部 ID | task-001 生成的实例唯一文件标识（文件名），用于从存储目录定位 |
| 转发结果（ForwardLog / 转发记录） | 每次 C-STORE 转发产生一条日志行：目标、实例、时间、状态 |

> 标识符纪律（CONTEXT.md「Flagged ambiguities」）：**内部 ID** 是系统自生成的匿名标识（文件名）；**SOPInstanceUID 等是 DICOM 业务 UID**。两者定位方式均可（`LookupSopInstanceUid` 内部也要经内部 ID 解析到文件），但**内部 ID 绝不替换/混用业务 UID**，转发出去的是**原样 DICOM 内容**（含原业务 UID 标签）。

---

## 4. 验收标准（AC，逐条可验证）

> 标记约定：`[手动]` 需人工在机器上执行并观察；`[自动]` 测试闸门（dev-test）可用脚本/断言自动核对。
> 测试对端（模拟远程 SCP）使用 DCMTK 的 `echoscu` / `storescu --store` 反向场景，或 fo-dicom 起的存储 SCP 测试宿主，或用 task-001 本 SCP 实例作为接收对端，其一即可。
> 本任务需要**两个 DICOM 实体**：OIS（SCU）与一个模拟/真实的远端 SCP（接收方）。测试时可用 task-001 的 SCP 程序、DCMTK 的 `storescp`、或 fo-dicom 测试宿主充当远端 SCP。

### AC-01 可配置的远程 SCP 列表（配置文件）

- `[手动]` 配置文件（`appsettings.json`）新增 `RemoteScps` 节（或 `Scu` 下 `RemoteScps`），为**数组**，每项含：`Name`（别名，唯一，用于命令行/接口定位目标）、`Host`、`Port`、`CalledAeTitle`（remote AE）、`CallingAeTitle`（local/calling AE，不填则用 OIS 侧 AET 或留空由 fo-dicom 自行生成）、`Protocol`（默认 `"DICOM"`，预留；本期只支持 DICOM）。
- `[自动]` 程序启动时能正确地解析出该列表；配 0 个、1 个、多个目标时均能正常启动（0 个 = 不配该项或配空数组，出站能力可用但无可选目标，逻辑照常）。
- `[自动]` 列表项的 `Name` 重复时：行为明确（启动报错并给出清晰错误，或后者覆盖前者——实现中二选一并记录），**不静默产生歧义**（DOMAIN.md「失败必须可见」）。

### AC-02 C-ECHO SCU —— 对可达目标返回成功

- `[手动]` 用**一个真实/模拟的远端 SCP**（如 `storescp` 或 fo-dicom 测试 SCP、或 task-001 的 SCP 实例）监听随机端口，配置 OIS 指向该目标后，执行 `C-ECHO` 命令对 `Name` 对应目标发送验证，返回**成功（Success）**，无超时/拒绝。
- `[自动]` 断言：返回结果状态为 Success；`Success` 时无异常、无错误日志。

### AC-03 C-ECHO SCU —— 对不可达/错误目标返回失败且不崩

- `[手动]` 配置一个**不可达**目标（如 `127.0.0.1` 上一个不存在的/未监听的端口，或错误 host/port），对其发 C-ECHO，返回**失败（Fail）** 且带**原因**（连接被拒 / 超时 / AET 不匹配均可，给出可读消息）。
- `[自动]` 断言：该方法**不抛未捕获异常**、进程/服务**不崩溃**、CMD 返回非零/状态为 Fail；随后对可达目标再发一次 C-ECHO 仍成功（异常隔离，任务继续可用）。

### AC-04 C-STORE SCU —— 从本地存储读实例并推送

- `[手动]` 前提：先用 task-001 的 C-STORE SCP 将一个 DICOM 样例（如 RTSTRUCT / CT / RTPLAN）接收落盘，记下其 `internal_id`（或 SOPInstanceUID）。
- `[手动]` 启动一个**远端 SCP 接收端**（`storescp -aec <remoteAe> -od <outdir>` 或 fo-dicom 测试 SCP），把 OIS 配置成指向它，执行 `C-STORE` 命令、用内部 ID（或 SOPInstanceUID）定位实例并推送到该目标。
- `[自动]` 断言：远端 SCP 实际**收到**该实例，且收到的 SOPInstanceUID 与本地存储索引中该实例的 **SOPInstanceUID 完全一致**（字符串严格相等，无篡改/改写）。
- `[自动]` 断言：远端收到的**文件内容**与本地存储文件**字节一致**（用文件哈希或 fo-dicom 读回对照标签，无损闭环——转发不是重编码）。
- `[自动]` 断言：返回状态为 Success/C-STORE 成功。

### AC-05 转发结果记录（数据库表 `forward_log`）

- `[自动]` 每次 C-STORE 转发后，PG 新增**一行**转发记录，字段正确填写：`id`、`remote_scp_name`（目标 Name）、`internal_id`、`sop_instance_uid`、`sop_class_uid`（可选）、`study_instance_uid`（可选）、`series_instance_uid`（可选）、`status`（`success` / `failed`）、`detail`（成功为 Success 状态码或空，失败为可读原因）、`forwarded_at`（`TIMESTAMPTZ DEFAULT now()`）。
- `[自动]` 对**同一个实例**连续转发两次到同一目标：第一次成功写入一行；第二次由实现明确（去重跳过同 `(remote_scp_name, sop_instance_uid)` 的重复成功记录，或允许重复——二选一并记录），**不静默覆盖/丢失第一次记录**。
- `[自动]` 转发**失败**（目标不可达等）时：也**写入一条 `status='failed'` 的记录**（DOMAIN.md「失败必须可见」，失败不能被静默吞掉）。

### AC-06 按 SOPInstanceUID 定位（Lookup）

- `[自动]` 提供一个查找接口：给定 `sopInstanceUid`，返回该实例在本地索引中的 `internal_id` 与存储文件绝对路径（结合 task-001 的 `file_path` 与存储根目录解析）。
- `[自动]` 对**不存在**的 SOPInstanceUID：返回明确「未找到」结果，**不抛未处理异常**，且不产生任何转发副作用（无 C-STORE 发送、无转发记录）。

### AC-07 异常与边界（SCU 侧）

- `[自动]` 目标 host 非法 / 端口非法 / 连接超时 / 连接被远端重置：C-ECHO 与 C-STORE 均**返回失败**（不是崩溃），错误原因可读并记录。
- `[自动]` 本地存储文件**不存在**（如 internal_id 引用的文件被删）：C-STORE 返回失败并给出「文件缺失」原因，不崩溃；不产生伪造的 Success。
- `[自动]` 并发安全：多个实例/多次转发串行或按实现并发处理，不产生数据竞争导致的崩溃或转发结果串线（可串行化验证即可）。

### AC-08 领域与配置正确性（领域关卡）

- `[自动]` 转发时**发出去的 DICOM 内容 = 本地存储的原文**：UID 字符串严格一致、StudyDate 保持 `YYYYMMDD` 原样、不做隐式大小写/前后空格窜改、不做时区/格式转换（对照 `D:\MyOpc\DOMAIN.md` §8 self-check）。
- `[自动]` calling AE / remote AE 使用**配置值**拼接关联请求；多个 remote 目标使用各自独立的 calling/remote AE 配置，互不串扰。
- `[自动]` 配置缺失（不配 `RemoteScps` / 单项目字段缺失）时：使用明确默认值或报错，行为在实现中写明；**不允许**因配置为空产生随机端口/默认 AET 的静默错误转发。

---

## 5. 不做的事（Out of Scope，留给后续 task）

- **不做** 完整 **C-MOVE 拉取**（从远程 SCP 把实例拉回 OIS）——本期只保留**接口定义/占位**（如 `IMoveScu` 方法签名带 TODO），不实现传输逻辑。
- **不做** 「转发整个 Study / 整个 Series 下所有实例」的**批处理编排/并发队列**——本期只提供单实例转发 + 一个 `IForwardPlanner`/`IRemoteForwarder` 抽象以便后续扩展，不实现 Study/Series 遍历与多目标并发队列。
- **不做** WPF / 任何 GUI（`UI/(WPF)` 层属红线，不动）。转发/验证靠命令行参数或最小触发接口触发。
- **不做** 断点续传 / 多连接池 / 优先级 / TLS 加密 / DICOM TLS（`DicomTlsOptions`）等高级传输特性。
- **不做** 转发失败重试策略 / 死信队列 / 离线积压 **（后补）**——失败仅记录 `forward_log`，重试留给后续。
- **不做** 对 task-001 已落盘文件/表/配置的破坏性改动；新功能**只增不改**（加配置节、加表、加接口）。
- **不做** 原生 / Native(C++) / Interop / `UI/(WPF)` 层的任何改动（红线，TEAM.md §5）。

---

## 6. 接口定义

### 6.1 公开 CLI / 触发方式（给测试与运维用，最小实现）

建议在现有 `Program.Main(args)` 增加**子命令模式**，或单独可执行入口，二选一（dev-dev 定，保持分层清晰）。示例：

```
dotnet run --project OIS.DicomScp/OIS.DicomScp.csproj -- scp                 # 默认：启动 SCP（兼容 task-001 行为）
dotnet run -- ... -- echo-scu --remote <Name>                                # 对 <Name> 远程 SCP 发 C-ECHO
dotnet run -- ... -- store-scu --remote <Name> --id <internalId>            # 按内部 ID 推送实例
dotnet run -- ... -- store-scu --remote <Name> --uid <sopInstanceUid>       # 按 SOPInstanceUID 推送实例
```

> 触发接口只要能驱动对应服务方法即可，具体形式（命令参数 / 测试内直接调用服务类）由 dev-dev 实现，AC 均已落到服务方法/副作用可断言。

### 6.2 核心 C# 接口（建议，供 dev-review 核对）

```csharp
// 一个已配置的远程 SCP 目标（绑定 appsettings 的 RemoteScps 数组项）
public sealed record RemoteScpTarget
{
    public string Name { get; init; } = "";          // 别名，唯一（AC-01）
    public string Host { get; init; } = "";          // 主机名或 IP
    public int Port { get; init; } = 11112;          // 目标端口
    public string CalledAeTitle { get; init; } = ""; // remote AE
    public string? CallingAeTitle { get; init; }     // local/calling AE，空则用 OIS AET
    public string Protocol { get; init; } = "DICOM"; // 预留，本期仅 "DICOM"
}

// C-ECHO 结果
public enum EchoStatus { Success, Failed }
public sealed record EchoResult(EchoStatus Status, string? Reason); // Reason 失败时给可读原因

// C-STORE 转发结果
public enum StoreStatus { Success, Failed }
public sealed record ForwardResult(StoreStatus Status, string? Reason); // Reason 失败原因/状态码

// 出站 SCU 服务：C-ECHO + C-STORE
public interface IDicomScuService
{
    Task<EchoResult> EchoAsync(RemoteScpTarget target, CancellationToken ct = default);
    // 按内部 ID 定位并从存储目录读取文件后 C-STORE 推送
    Task<ForwardResult> StoreByInternalIdAsync(RemoteScpTarget target, string internalId, CancellationToken ct = default);
    // 按 SOPInstanceUID 定位（经内部 ID 解析到文件）后 C-STORE 推送
    Task<ForwardResult> StoreBySopInstanceUidAsync(RemoteScpTarget target, string sopInstanceUid, CancellationToken ct = default);
}

// 转发记录写入（表 forward_log）
public interface IForwardLog
{
    Task WriteAsync(ForwardLogEntry entry, CancellationToken ct = default);
    Task<long> CountByInstanceAndTargetAsync(string remoteScpName, string sopInstanceUid, CancellationToken ct = default);
}
```

**Abstraction 预留（本期可不实现或用 `NotImplementedException`/置空，dev-review 不因此打回）：**

```csharp
// 后续：按 Study / Series 定位所有实例并逐个转发（本期仅签名，不实现）
public interface IRemoteForwarder
{
    Task<ForwardResult> ForwardStudyAsync(RemoteScpTarget target, string studyInstanceUid, CancellationToken ct = default);
    Task<ForwardResult> ForwardSeriesAsync(RemoteScpTarget target, string seriesInstanceUid, CancellationToken ct = default);
}
// 后续：C-MOVE 拉取（本期仅接口定义占位）
public interface IMoveScu
{
    Task<MoveResult> MoveAsync(RemoteScpTarget source, string sopInstanceUid, CancellationToken ct = default);
}
```

> 说明：`IRemoteForwarder` / `IMoveScu` 是**纯预留**，本期不要求有任何实现，仅需在代码/文档中声明、被 review 认可即可（满足 PO 需求第 4 点「abstraction 预留」）。

### 6.3 与 task-001 存储/索引层的衔接

- **定位实例**：`sopInstanceUid → internal_id + file_path` 的查找，建议在 `InstanceIndex` 增加一个查询方法（如 `FindBySopInstanceUidAsync` 返回 `InstanceMetadata`/`internal_id`），或新增只读查询组件；`file_path` + 存储根目录 → 绝对文件路径，用 `FileStorage.AbsolutePathOf` 风格解析。
- **读文件**：从存储目录读取原始 DICOM 文件字节（`DicomFile.Open`/`File.ReadAllBytes`），再构造 C-STORE 请求字节发送。
- **去重/一致性**：转发是**读操作**，不改动 task-001 的 `dicom_instance` 落盘/入库逻辑；只**新增** `forward_log` 表与查询方法（AC-05/AC-08）。

## 7. 配置格式（`appsettings.json` 扩展）

```jsonc
{
  "Scp": {
    "Port": 11112,
    "AeTitle": "OIS",
    "StorageServiceSopClasses": ["*"]
  },
  "Scu": {
    "CallingAeTitle": "OIS",            // 可选：SCU 侧默认 calling AE（不配则用 Scp.AeTitle 或 fo-dicom 默认）
    "RemoteScps": [                      // 远程 SCP 目标列表，可 0..n 个
      {
        "Name": "TestPacs",
        "Host": "127.0.0.1",
        "Port": 11113,
        "CalledAeTitle": "PACS",
        "CallingAeTitle": "OIS",
        "Protocol": "DICOM"
      },
      {
        "Name": "OtherOis",
        "Host": "192.168.1.50",
        "Port": 11112,
        "CalledAeTitle": "OIS2",
        "CallingAeTitle": "OIS",
        "Protocol": "DICOM"
      }
    ]
  },
  "Storage": { "Directory": "D:\\OIS\\storage" },
  "Postgres": { "ConnectionString": "Host=localhost;Port=5432;Username=postgres;Database=dicom" }
}
```

> `Name` 是命令行/测试定位目标的关键，**要求唯一**（AC-01）。calling AE 缺省策略：优先用该目标 `CallingAeTitle`，否则用 `Scu:CallingAeTitle`，否则用 `Scp.AeTitle`，最终 fallback 由 fo-dicom 生成——逐级 fallback 在实现中写明。

## 8. 目录 / 类归属建议

建议扩展 task-001 项目目录，**保持 Scp / Storage / Index 分层**，新增 `Scu/` 与 `Scu/FwdLog/` 子层：

```
src/OIS.DicomScp/OIS.DicomScp/
├─ Program.cs                    # 扩展：子命令 dispatch（scp / echo-scu / store-scu）
├─ appsettings.json              # 新增 Scu:RemoteScps 节
├─ Options.cs                    # 新增 RemoteScpTarget / ScuOptions
├─ Scp/                          # task-001 已有，不动
│  └─ ...
├─ Storage/                      # task-001 已有
│  └─ FileStorage.cs             # 可加: 由 file_path 解析绝对路径（读文件用）
├─ Index/
│  ├─ InstanceIndex.cs           # 可加: FindBySopInstanceUidAsync 等只读查询
│  └─ Schema.sql                 # 追加: forward_log 建表 DDL（幂等）
├─ Scu/                          # ★ 本任务新增：出站 SCU
│  ├─ IDicomScuService.cs        # 接口
│  ├─ DicomScuService.cs         # fo-dicom DicomClient 实现（C-ECHO / C-STORE）
│  ├─ RemoteScpRepository.cs     # 从配置读取/校验 RemoteScps 列表（Name 唯一性校验）
│  ├─ IForwardLog.cs             # 转发记录接口
│  ├─ ForwardLog.cs              # Npgsql 写 forward_log
│  └─ (IRemoteForwarder.cs)      # 预留：Study/Series 批转发签名（可空实现）
└─ (IMoveScu.cs)                 # 预留：C-MOVE 签名占位
src/OIS.DicomScp/tests/OIS.DicomScp.Tests/
└─ ScuIntegrationTests.cs        # 新: C-ECHO/C-STORE/转发记录/异常/定位 集成测试
```

> 目录结构为**建议布局**，dev-dev 可在保持分层清晰（Scp / Storage / Index / **Scu** 分界）的前提下微调（TEAM.md §3 规定 dev-review/dev-test 之后才进入既有层改动，本任务只加新层/新节/新表，不破坏 task-001 已验收行为）。

## 9. 数据库表结构（`dicom` 库，新增表 `forward_log`）

在 `Index/Schema.sql` **末尾追加**（幂等，`CREATE TABLE IF NOT EXISTS`）：

```sql
-- task-002：C-STORE 转发记录（每次成功/失败各一行，AC-05）
CREATE TABLE IF NOT EXISTS forward_log (
    id                 BIGSERIAL     PRIMARY KEY,
    remote_scp_name    VARCHAR(128)  NOT NULL,          -- 目标别名（RemoteScps[].Name）
    internal_id        VARCHAR(64)   NOT NULL,          -- 转发实例的内部 ID（文件名）
    sop_instance_uid   VARCHAR(64),                     -- 转发的实例业务 UID
    sop_class_uid      VARCHAR(64),
    study_instance_uid VARCHAR(64),
    series_instance_uid VARCHAR(64),
    status             VARCHAR(16)   NOT NULL,          -- 'success' | 'failed'
    detail             TEXT,                            -- 成功: 状态码/空; 失败: 可读原因
    forwarded_at       TIMESTAMPTZ   NOT NULL DEFAULT now()
);

-- 加速: 按目标+实例查转发历史、按时间倒序
CREATE INDEX IF NOT EXISTS idx_fwd_target_instance ON forward_log (remote_scp_name, sop_instance_uid);
CREATE INDEX IF NOT EXISTS idx_fwd_time            ON forward_log (forwarded_at DESC);
```

> 设计说明：
> - `forward_log` 是**追加日志表**，不做 `ON CONFLICT` 去重；同实例重复转发的去重策略由调用方决定（AC-05：写多行或按 `(remote_scp_name, sop_instance_uid, status='success')` 去重二选一，`detail` 保留首次原因，实现中记录）。
> - 与 task-001 `dicom_instance` 是**外键弱关联**（不强制 FK），转发失败（实例被删等）时 `internal_id` 仍可记录该引用，`detail` 写清原因（`failed`），符合「失败必须可见」。
> - 该 DDL 追加在 `Schema.sql` 尾部，`InstanceIndex.EnsureSchemaAsync` 已执行整个文件，重复执行不报错（`IF NOT EXISTS`），满足 AC-02/AC-07 的幂等原则。

## 10. 对 dev-dev 的注意事项

1. **fo-dicom 5.2.6**：SCU 用 `DicomClient` / `DicomClientFactory`（`FellowOakDicom.Network.Client`）。发送前 `AddRequestAsync`，`SendAsync` 后通过 response 的 `Status` 判断 C-ECHO/C-STORE 成功与否——fo-dicom 的 `SendAsync` 只保证「关联发送完成且未被拒绝」，**成功与否须看 response status**（对照 task-001 测试注释，勿把「SendAsync 不抛异常」误当成功）。
2. **不发则已，发则原样**：转发出去的内容 = 本地存储文件的**原字节/原标签**，绝不重编码、绝不栽改 UID（AC-04/AC-08 硬性要求，无损闭环）。读文件→作为 C-STORE 请求体的过程中不改变任何标签值。
3. **calling/remote AE 用配置值**：关联请求携带配置的 calling AE 与 called/remote AE；多目标各自独立（AC-08）。默认 calling AE 的 fallback 链在实现写明。
4. **异常隔离**：C-ECHO / C-STORE 任何异常（连接失败、超时、读文件失败、AET 拒绝）都**catch 并转为 `Failed` 结果 + `forward_log` 记录 `status='failed'`**，不让异常升级到调用方/进程层（DOMAIN.md「失败必须可见」）。
5. **幂等与不破坏**：只新增表/配置节/接口/目录；不改动 task-001 已验收的落盘/入库/SCP 行为。`forward_log` 用 `IF NOT EXISTS`，重复启动不报错。
6. **lookup 语义**：`StoreBySopInstanceUidAsync` 内部先查 `dicom_instance` 得到 `internal_id` 与 `file_path`，再解析绝对路径读文件；实例不存在 → `Failed`（原因「未找到」），不产生转发副作用（AC-06）。
7. **领域关卡**：转发不篡改 UID/日期；不静默吞失败；配置 Name 唯一性校验（AC-01/AC-08）——对照 `D:\MyOpc\DOMAIN.md` §8 self-check。
8. 试点阶段只写 C# 层/配置/表，**不碰** Native/(C++)、`UI/(WPF)`、Interop 生成文件（红线，TEAM.md §5）。`IRemoteForwarder` / `IMoveScu` 仅签名占位即可，不强制实现。

## 11. 验收矩阵（供 dev-test 逐条勾选）

| AC | 可自动断言 | 验证手段 |
|---|---|---|
| AC-01 远程 SCP 列表配置 | ✅ | 配置解析 + 多目标/无目标启动 |
| AC-02 可达 C-ECHO 成功 | ✅ | `storescp`/fo-dicom SCP + OIS echo-scu |
| AC-03 不可达 C-ECHO 失败不崩 | ✅ | 错误端口 + 状态断言 + 后续 C-ECHO 仍成功 |
| AC-04 C-STORE 推送（内容/UID 一致） | ✅ | 远端 SCP 收到 + SOPUID 对照 + 文件哈希/标签对照 |
| AC-05 转发记录表 | ✅ | PG `forward_log` 行断言 + 失败也写 failed 行 |
| AC-06 按 SOPUID 定位 | ✅ | 存在/不存在实例 lookup + 无副作用 |
| AC-07 异常/边界 | ✅ | 坏 host/文件缺失/等 + 不崩溃 |
| AC-08 领域关卡 | ✅ | 内容原样 + calling/remote AE 用配置 + 一致性 |