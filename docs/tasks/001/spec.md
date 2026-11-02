# task-001 Spec — DICOM Storage SCP 核心存储层

- 任务编号：`task-001`
- 分支名：`feature/task-001-dicom-scp-storage`
- 角色：dev-req（需求 Agent）
- 状态：待 PO 确认（确认后 dev-dev 接手实现）
- 技术栈：C# / .NET 10、fo-dicom（NuGet `fo-dicom`，最新稳定版）、PostgreSQL

---

## 1. 背景

OIS（肿瘤信息系统）需要对放疗/肿瘤相关的 DICOM 数据进行接收、存储与检索。作为整个 OIS 数据交换的基础设施，首先需要一个**DICOM 存储服务（Storage SCP）**，把从影像设备、TPS（放疗计划系统）、其他 OIS 推过来的 DICOM 实例可靠地落盘并将元数据抽离到索引库。

`task-001` 是整个 OIS 项目的**第一个子任务**，只做**核心存储层**：接收 + 落盘 + 元数据索引 + 可配置。后续的查询（C-FIND）、移动（C-MOVE/SCU 转发）、影像服务、WPF 界面等都不在本任务范围内，需要时在后续 task 中扩展。

本 spec 是 dev-dev 的实现依据，也是 dev-review / dev-test 的验收依据。**AC 必须逐条可被命令/断言验证**，不能是「看起来不错」的定性描述。

## 2. 目标

1. 用 fo-dicom 实现一个 DICOM **Storage SCP**，监听 TCP `11112`，正确应答 C-ECHO 与 C-STORE。
2. 接收到的 DICOM 实例按 **Orthanc 式**的方式落盘存储：一个实例一个文件，文件名使用**内部生成的唯一 ID**（不暴露原始 PatientID / StudyInstanceUID 等），并按内部 ID 前 2 位做子目录分片。
3. 用 **PostgreSQL** 存索引/元数据，连接串与建表均可配置、幂等。
4. SCP 端口、AET、存储目录、PG 连接串**全部可配置**（从配置文件读取）。
5. 为后续 task（C-FIND/C-MOVE/SCU/UI）打基础：目录、命名、表结构、配置约定清晰且可扩展。

## 3. 领域术语（与 `D:\OIS\CONTEXT.md`、`D:\MyOpc\DOMAIN.md` 一致）

| 术语 | 含义（本 spec 采用） |
|---|---|
| DICOM 实例（Instance） | 一个 SOP Instance，即一个 DICOM 对象/文件（Patient→Study→Series→Instance 的最末层） |
| SOP Instance UID | 唯一标识某一实例的 DICOM UID（`1.2.840.10008...` 形式） |
| Storage SCP / SCP | 监听并接收 DICOM 网络请求的服务端角色 |
| SCU | 发起 DICOM 请求的客户端角色（本任务**不做**，仅测试时作为对端使用） |
| AET / AETitle | 应用实体标题，标识 DICOM 通信实体名称 |
| C-ECHO | 连接/服务存活校验（等价 ping） |
| C-STORE | 存储请求，把实例推给 SCP |
| 内部 ID | 系统内部为每个实例生成的唯一文件标识（不暴露业务 UID） |
| 元数据（Metadata） | 从 DICOM 标签抽离、入库的关系型字段（PatientID、各 UID、Modality 等） |

> 注：`PatientID` 与 `StudyInstanceUID` 等是 DICOM 标签内容；内部 ID 是系统自生成的，两者**绝不混用**（对应 CONTEXT.md「Flagged ambiguities」的纪律：标识符显式、不静默混用）。

## 4. 验收标准（AC，逐条可验证）

> 标记约定：`[手动]` 需人工在机器上执行并观察；`[自动]` 测试闸门（dev-test）可用脚本/断言自动核对。
> 默认测试对端使用 DCMTK 的 `echoscu` / `storescu`，或 fo-dicom 测试客户端，二者选一即可。

### AC-01 可执行程序与配置热载
- `[手动]` 产物为一个 .NET 可执行（`dotnet run` 或发布后 exe），启动时读取配置文件（`appsettings.json`）。
- `[手动]` 配置项包括且不限于：`ScpPort`（默认 `11112`）、`AeTitle`（默认 `OIS`）、`StorageDirectory`（默认 `D:\OIS\storage\` 或配置文件所在相对路径）、`Postgres:ConnectionString`（默认 `Host=localhost;Port=5432;Username=postgres;Database=dicom`，密码留空即 trust）。
- 判据：改配置文件后重启，端口/AET/目录/连接串生效。

### AC-02 服务启动与建表（幂等）
- `[手动]` 程序启动自动连接 PG 并执行建表 DDL；表已存在时**不报错、可重复启动**（用 `CREATE TABLE IF NOT EXISTS` 或等价机制）。
- `[自动]` 用 `psql` 或测试连接串查询 `information_schema.tables`，确认表 `dicom_instance`（或约定表名）存在。
- `[自动]` 连续启动/停止程序两次以上，均正常、无重复建表报错。

### AC-03 C-ECHO 应答
- `[手动]` 使用 `echoscu -v -aet TEST -aec OIS 127.0.0.1 11112`（或 fo-dicom 测试客户端）向 SCP 发送 C-ECHO，返回**成功**（Exit Success / RSP Success）。
- `[自动]` 断言：C-ECHO 响应状态为 Success，无超时/拒绝。

### AC-04 C-STORE 落盘（文件 + 匿名化文件名）
- `[手动]` 用 DICOM 样例（如 RTSTRUCT / CT / RTPLAN，可来自 RTPSystem 导出或构造）执行 `storescu -v -aet TEST -aec OIS 127.0.0.1 11112 sample.dcm`，返回某个 C-STORE 成功状态（`0000` / Success）。
- `[自动]` 断言：存储目录 `D:\OIS\storage\` 下出现**新文件**，其文件名**不等于**原文件的 SOPInstanceUID / StudyInstanceUID / PatientID，而是内部生成的唯一 ID 字符串。
- `[自动]` 断言：文件落在子目录分片下——内部 ID 前 2 位构成一层子目录（如内部 ID `ab1f2c...` → `storage\<ab>\ab1f2c...`）。
- `[自动]` 断言：`dcmdump`（或 fo-dicom 读回）读取落盘文件，其 DICOM 标签内容与存入前**一致**（无损闭环：存进去是什么，落盘就是什么）。

### AC-05 同名/同实例去重
- `[自动]` 对**同一 SOPInstanceUID** 的实例重复 C-STORE 两次：第二次不应产生第二个重复文件（同一内部 ID 覆盖或跳过，行为在实现中明确并记录），且不抛错；索引表该 SOPInstanceUID 只有一行（或显式冲突处理策略）。

### AC-06 元数据入库
- `[自动]` C-STORE 一个实例后，PG 的 `dicom_instance` 表新增且仅有**一行**，字段正确填充：`PatientID`、`StudyInstanceUID`、`SeriesInstanceUID`、`SOPInstanceUID`、`Modality`、`StudyDate`、`InstanceNumber`、`internal_id`、`file_path`（相对存储目录的路径，含分片子目录）、`sop_class_uid`、`received_at`、`file_size_bytes`。
- `[自动]` 断言：`file_path` 与 AC-04 实际落盘路径**一致**；`internal_id` 与文件名一致；各 UID 与原 DICOM 标签值一致（用 `dcmdump` 对照）。
- `[自动]` 断言：`Modality` 或 `StudyDate` 在原实例缺失/为空时，表内对应字段为 `NULL`（不臆造默认值，静默补值属领域错误）。

### AC-07 异常与边界
- `[自动]` 发送**非 DICOM / 损坏数据**到 11112：连接被正常关闭或返回错误，**服务不崩溃、继续响应**后续 C-ECHO。
- `[自动]` 发送 C-STORE 但 PatientID 为空 / 必填 UID 缺失：行为明确（成功入库且字段为 NULL，或返回失败并记录），**不得静默吞错**（对应 DOMAIN.md「失败必须可见」）。
- `[自动]` 存储目录不存在时：自动创建（或明确报错并退出，实现中二选一并记录）；权限不足时输出明确错误。

### AC-08 身份与配置正确性（领域关卡）
- `[自动]` SCP 宣告的 AET 为配置值（默认 `OIS`），对端用 `-aec OIS` 才能成功联机；用错误 AET 联机时行为明确（拒绝或记录）。
- `[自动]` 数据一致性对照 `D:\MyOpc\DOMAIN.md`：Uid 字段严格字符串一致（无隐式大小写/前后空格篡改）；`StudyDate` 保持 DICOM `YYYYMMDD` 原样入库，不做时区/格式转换。

## 5. 不做的事（Out of Scope，留给后续 task）

- **不做** C-FIND（查询 SCU/SCP）——后续。
- **不做** C-MOVE（移动 SCU/SCP，转发到其他 AET）。
- **不做** 任何 SCU 主动转发/推送（本任务 SCP 只收不发）。
- **不做** WPF / 任何 UI（`UI/(WPF)` 层属红线，不动）。
- **不做** 影像渲染、预览、DICOM 显示。
- **不做** 原生/Native(C++)、Interop 层改动（红线）。
- **不做** 多模态/优先级队列/断点续传等高级传输特性。
- **不做** 患者/病例/计划等 OIS 业务对象建模（那是后续 OIS 功能，本任务只是通用 DICOM 存储）。
- **不做** 影像归档/删除策略（保留策略后续）。

## 6. 接口定义

### 6.1 外部 DICOM 服务接口（SCP 对外）
| 项 | 值 |
|---|---|
| 协议 | DICOM 3.0，TCP/IP |
| 监听地址 | `0.0.0.0`（所有网卡），端口配置（默认 `11112`） |
| 对外 AET | 配置（默认 `OIS`） |
| 支持的 SOP Class（接收用） | `StorageServiceClass` 的所有 Storage SOP Class（`Verification` 与全部 Storage 类），可配置白名单 |
| C-ECHO | 返回 Success |
| C-STORE | 接收实例 → 落盘 → 入库 → 返回 Success |

### 6.2 内部文件存储约定
- 根目录：`<StorageDirectory>`（配置，默认 `D:\OIS\storage\`）。
- 分片：内部 ID 前 2 位字符作一层子目录（`<id[0..1]>/`）。
- 文件名：内部 ID（全小写、仅 `[0-9a-f]`，推荐 GUID/ULID 派生，保证唯一）。
- 文件路径（相对）：`<id[0..1]>/<id>`，无扩展名。该相对路径写入 `file_path` 字段。
- 内部 ID 生成为**随机唯一值**，与任何业务 UID **无关联可推断**（匿名化要求）。

### 6.3 配置约定（`appsettings.json`）
```jsonc
{
  "Scp": {
    "Port": 11112,
    "AeTitle": "OIS",
    "StorageServiceSopClasses": ["*"]  // "*" 表示全部 Storage SOP Class
  },
  "Storage": {
    "Directory": "D:\\OIS\\storage"
  },
  "Postgres": {
    "ConnectionString": "Host=localhost;Port=5432;Username=postgres;Database=dicom"
  }
}
```
> 密码留空即 trust 认证（当前本机 postgres 无密码）。连接串从配置读取，不硬编码。

## 7. 目录结构

```
D:\OIS\
├─ .gitignore
├─ CONTEXT.md
├─ README.md
├─ docs\
│  └─ tasks\
│     └─ 001\
│        └─ spec.md            ← 本文件（交接物）
├─ src\
│  └─ OIS.DicomScp\            ← 方案/项目根（dev-dev 新增）
│     ├─ OIS.DicomScp.sln
│     ├─ OIS.DicomScp\
│     │  ├─ Program.cs         ← 入口，读取配置、启动 SCP
│     │  ├─ appsettings.json   ← 配置
│     │  ├─ Scp\               ← SCP 服务（fo-dicom DICOM Server）
│     │  │  └─ DicomScpService.cs
│     │  ├─ Storage\           ← 落盘（内部 ID 生成 + 分片）
│     │  │  └─ FileStorage.cs
│     │  ├─ Index\             ← PG 索引访问
│     │  │  ├─ InstanceIndex.cs
│     │  │  └─ Schema.sql      ← 建表 DDL（幂等）
│     │  └─ ...
│     └─ tests\
│        └─ OIS.DicomScp.Tests\  ← 测试（C-ECHO/C-STORE 集成测试）
├─ data\                       ← 实际存储目录（gitignore 排除，勿提交样例影像）
└─ ...
```

> 目录结构为**建议布局**，dev-dev 可在保持分层清晰（Scp / Storage / Index 分界）的前提下微调；`docs/` 与 `.gitignore` 规则已就绪（`/docs/*` + `!/docs/tasks/`），不要改 `.gitignore` 的交接物规则。

## 8. 数据库表结构（`dicom` 库，表 `dicom_instance`）

```sql
-- 幂等建表：重复执行不报错
CREATE TABLE IF NOT EXISTS dicom_instance (
    internal_id          VARCHAR(64)  PRIMARY KEY,      -- 内部唯一 ID，即文件名
    sop_instance_uid     VARCHAR(64)  NOT NULL UNIQUE,  -- 业务 UID，DICOM 全局唯一
    sop_class_uid        VARCHAR(64),                    -- 如 1.2.840.10008.5.1.4.1.1.2
    patient_id           VARCHAR(64),
    patient_name         VARCHAR(255),
    study_instance_uid   VARCHAR(64),                    -- 注意：非唯一（一 study 多 series）
    series_instance_uid  VARCHAR(64),
    modality             VARCHAR(16),
    study_date           VARCHAR(8),                     -- DICOM YYYYMMDD 原样
    study_time           VARCHAR(14),                    -- 可选，HHMMSS.FFFFFF 原样
    study_description    VARCHAR(1024),
    series_number        VARCHAR(16),
    instance_number      VARCHAR(16),
    file_path            TEXT         NOT NULL,          -- 相对存储目录，如 <ab>/<id>
    file_size_bytes      BIGINT,
    received_at          TIMESTAMPTZ  NOT NULL DEFAULT now(),
    CONSTRAINT uq_file_path UNIQUE (file_path)
);

-- 辅助索引（可选，供后续 C-FIND 用；本任务可先建，便于核对）
CREATE INDEX IF NOT EXISTS idx_dicom_patient_id     ON dicom_instance (patient_id);
CREATE INDEX IF NOT EXISTS idx_dicom_study_uid      ON dicom_instance (study_instance_uid);
CREATE INDEX IF NOT EXISTS idx_dicom_series_uid     ON dicom_instance (series_instance_uid);
CREATE INDEX IF NOT EXISTS idx_dicom_modality_date  ON dicom_instance (modality, study_date);
```

> 设计说明：
> - `sop_instance_uid` 加 `UNIQUE`：保证同实例去重（AC-05），重复 C-STORE 时用 `ON CONFLICT` 或先查后写，行为明确。
> - `internal_id` 是主键且有分片，文件名即主键，先写文件后入库或先入库后写文件需保证一致性（dev-dev 在实现里明确事务/回滚顺序）。
> - 表结构在 `Schema.sql` 中维护，支持幂等；后续 task 加字段仅 `ALTER TABLE ADD COLUMN IF NOT EXISTS`。

## 9. 对 dev-dev 的注意事项

1. **fo-dicom**：用 .NET 10，`fo-dicom` NuGet 最新稳定版；SCP 用 `DicomServer` / 自建 `DicomService`，正确处理 C-ECHO 与 C-STORE；网络异常/断连不崩溃。
2. **匿名化**：文件名只能用内部 ID，**绝不**用 SOPInstanceUID/StudyInstanceUID/PatientID 当文件名（AC-04 硬性要求）。
3. **一致性**：「先写文件后入库」与「先入库后写文件」二选一并写清；失败时按 DOMAIN.md「失败必须可见」原则记录日志，不回滚成静默成功。
4. **幂等**：建表重复执行不报错（`IF NOT EXISTS`）；同 SOPInstanceUID 重复接收按 AC-05 去重。
5. **配置**：端口/AET/存储目录/连接串全部来自配置，不硬编码。
6. **领域关卡**：UID 字符串严格一致、StudyDate 保持 `YYYYMMDD` 原样、缺失字段置 `NULL` 不臆造——对照 `D:\MyOpc\DOMAIN.md` 第 8 节 self-check。
7. 试点阶段只写 C#/Infrastructure 层，**不碰** Native/(C++)、UI/(WPF)、Interop 生成文件（红线，TEAM.md §5）。

## 10. 验收矩阵（供 dev-test 逐条勾选）

| AC | 可自动断言 | 验证手段 |
|---|---|---|
| AC-01 可执行与配置 | 部分 | `dotnet run` + 读配置项 |
| AC-02 幂等建表 | ✅ | `information_schema.tables` 查询 |
| AC-03 C-ECHO | ✅ | `echoscu` / fo-dicom 客户端 |
| AC-04 C-STORE 落盘 | ✅ | `storescu` + 文件检查 + `dcmdump` 闭环 |
| AC-05 去重 | ✅ | 重复 C-STORE + 行数断言 |
| AC-06 元数据入库 | ✅ | PG 查询对照 `dcmdump` |
| AC-07 异常/边界 | ✅ | 坏数据/空字段/目录处理 |
| AC-08 领域关卡 | ✅ | AET 联机 + UID/日期一致性 |