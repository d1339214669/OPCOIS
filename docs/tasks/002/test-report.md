# task-002 测试验证结论 — 可配置远程 SCP 出站连接（SCU 转发/推送）

- 测试角色：dev-test
- 测试分支：`feature/task-002-scu-forward`（相对 `develop`，提交 37f4cf7 / f9fbed8 / 7e0dc84 / 8768ced / fa0703d）
- 前置确认：`docs/tasks/002/review.md` 存在，结论 = **放行**（dev-review），进入测试闸门。
- 测试环境：Windows + PostgreSQL 18.3 便携版（localhost:5432，postgres trust，库 `dicom`，`dicom_instance` / `forward_log` 表均在）；.NET 10 SDK；fo-dicom 5.2.6。
- 测试方式：干净构建 + 全量测试（两次复跑）+ CLI 端到端实测（`OIS.DicomScp.exe` 子命令 echo-scu / store-scu / scp，多套 appsettings 变体）+ psql 逐行核对 + 远端接收侧字节级比对（fo-dicom 捕获宿主 + 十六进制比对 + 标签对照）。

## 结论：放行

全量测试 14/14 绿且可复现；spec 全部 8 条 AC 逐条实测满足；DOMAIN.md 领域关卡（转发内容无损、成功以 response.Status 为准、forward_log 成功/失败均写行、calling AE fallback 链、Name 唯一性、不可达异常隔离）全部通过；未越界改动 Native / UI(WPF) / Interop，未破坏 task-001 已验收行为（只增不改，6 个 task-001 回归测试全绿）。无硬否决项。

---

## 1. 构建与全量测试（可复现性）

| 步骤 | 结果 |
|---|---|
| `dotnet clean OIS.DicomScp.sln` | 成功 |
| `dotnet build OIS.DicomScp.sln` | **0 错误 0 警告** |
| `dotnet test`（第 1 次） | **14/14 通过**（失败 0，跳过 0，5 s） |
| `dotnet test`（第 2 次复跑） | **14/14 通过**（失败 0，跳过 0，6 s） |

14 = task-001 的 6 个回归（C-ECHO/C-STORE 落盘/去重/元数据入库/建表幂等/坏数据不崩）+ task-002 的 8 个 SCU 集成测试。两次结果一致，可复现。

## 2. AC 逐条核对表

| AC | 判定 | 验证手段 | 证据 |
|---|---|---|---|
| AC-01 可配置远程 SCP 列表 | **满足** | 4 套 appsettings 变体实测（0/1/多目标）+ 重复 Name/缺 Host 配置 | ① 无 `Scu` 节（0 目标）：程序正常解析启动，`scp` 子命令启动成功（日志「DICOM Storage SCP 已启动，监听 0.0.0.0:11112」），该实例 C-ECHO 探测 = Success；`echo-scu --remote Any` → exit 2「未找到名为 'Any' 的远程 SCP 目标（可用: [空]）」，无静默歧义。② 1 目标（发货 appsettings 的 TestPacs）：解析正常。③ 4 目标（AppScp/Cap/Dead/BadHost）：解析正常，各目标可独立寻址。④ 重复 Name：exit 1「启动失败: RemoteScps 目标 Name 重复: 'Dup'（Name 必须唯一，AC-01）」（`RemoteScpRepository.cs:30-31` 构造时抛 `InvalidOperationException`，实现选择 = 启动报错，已记录）。⑤ 缺 Host：exit 1「RemoteScps 目标 'NoHost' 缺少 Host」（非法端口/非 DICOM 协议同路径，`RemoteScpRepository.cs:33-40` 代码核对）。 |
| AC-02 C-ECHO 可达成功 | **满足** | CLI echo-scu + 远端接收断言 + 集成测试 | `echo-scu --remote AppScp` → `C-ECHO[AppScp] status=Success reason=-`，**exit 0**；`echo-scu --remote Cap` → Success exit 0；成功时无异常、无错误日志。集成测试 `Echo_To_Reachable_Returns_Success` 断言远端实际收到 C-ECHO（EchoCount≥1）。 |
| AC-03 C-ECHO 不可达失败不崩 | **满足** | 错误端口/坏 host/发货配置 + 隔离复测 + 集成测试 | `echo-scu --remote Dead`（127.0.0.1:59999 无监听）→ `status=Failed reason=…由于目标计算机积极拒绝，无法连接。`，**exit 1**，进程正常退出无崩溃；`BadHost`（DNS 不存在）→ Failed「不知道这样的主机」exit 1；随后对可达目标再发 C-ECHO 仍 Success（**异常隔离**验证）；发货配置 TestPacs（11113 未监听）同样 Failed exit 1。集成测试 `Echo_To_Unreachable_Returns_Failed_And_Service_Still_Works` 覆盖。 |
| AC-04 C-STORE 转发原文 | **满足** | seed 新实例 → store-scu（--id 与 --uid 两路）→ 远端接收比对（捕获宿主 + psql + 十六进制比对） | ① `store-scu --remote AppScp --id 3ebbe6ba…` → `status=Success` exit 0，远端（task-001 SCP）返回 C-STORE Success。② `store-scu --remote Cap --uid 2.25.2143…821` → Success exit 0，捕获宿主记录 `CAPTURE_STORE sop=2.25.2143…821 study=2.25.2949…394 date=20250701 mod=CT`。③ **SOPInstanceUID 字符串严格相等**（源/远端/PSQL 三处一致）。④ **内容无损**：十六进制逐字节比对源文件与远端接收文件——全部 11 个 dataset 元素（含 SOP/Study/Series UID、PatientID、PatientName、Modality、StudyDate `20250701` YYYYMMDD 原样、StudyTime、SeriesNumber、InstanceNumber 及填充字节）完全一致；原始文件 SHA256 相差 14 字节，差异定位并归因于**接收侧**文件 meta 的 `(0002,0016) CalledAETitle="OISCAP"`（fo-dicom 接收端合成 + 捕获工具 Save 写入），SCU 侧不存在任何改标签/重编码代码路径（`DicomFile.Open` → `new DicomCStoreRequest(file)` 原样发送）。spec 明确允许「文件哈希**或** fo-dicom 读回对照标签」为验证手段。⑤ 集成测试 `Store_Forwards_Original_Instance_Lossless` 断言 UID/StudyDate/Modality 与源严格一致。 |
| AC-05 forward_log 记录 | **满足** | psql 逐行核对 6 条本次写入行 + 集成测试 | id 64/65/66 = success（AppScp、Cap、AppScp 重复），字段全对：`remote_scp_name`、`internal_id`、`sop_instance_uid`、`sop_class_uid`(1.2.840.10008.5.1.4.1.1.2)、`study_instance_uid`、`series_instance_uid`、`status='success'`、`detail='Success'`、`forwarded_at`（TIMESTAMPTZ，now() 默认）。id 67 = failed（目标不可达，detail 可读）；id 68 = failed（文件缺失，`internal_id` 引用保留、`sop_instance_uid=NULL` 不臆造）；id 69 = failed（未找到 UID）。**同实例重复转发追加 2 行（64+66），首行不被覆盖**——实现选择 = 追加（无 ON CONFLICT），已在 Schema.sql/README 记录。失败必写 failed 行（67/68/69）→ 失败可见。集成测试 `Forward_Log_Writes_Success_Row` / `_Failed_Row_When_Unreachable` / `Repeated_Forward_Appends_Multiple_Rows_No_Overwrite` 覆盖。 |
| AC-06 按 SOPUID 定位 | **满足**（带 1 条记录性说明，见下） | 存在/不存在 UID 两路 CLI + psql 副作用核对 | ① `FindBySopInstanceUidAsync` 只读查询（`InstanceIndex.cs:137`，未找到返回 null）；`store-scu --uid <已存在>` 解析到 internal_id 并转发成功（AC-04②）。② 不存在 UID `9.9.9.9.9.9.9`：`status=Failed reason=未找到该 SOPInstanceUID 的实例`，exit 1，无未处理异常；**无 C-STORE 发送**（远端未收）、**无 dicom_instance 新行**（该 UID 行数 = 0；已 seed 实例仍仅 1 行）。③ 记录性说明：实现对该 lookup-miss 写了一条 `status='failed'` 的可见性行（id 69，detail「未找到该 SOPInstanceUID 的实例」，internal_id 为空）。AC-06 字面「无转发记录」与 AC-05/DOMAIN.md「失败必须可见」存在 spec 自相矛盾；实现按 DOMAIN.md 方向取舍（失败可见、无任何转发发生的记录、不伪造 Success），dev-review 已认可，测试判定为满足并在此明示，供 PO 合并时知悉。 |
| AC-07 异常与边界 | **满足** | 坏 host/连接拒绝/文件缺失/重复转发 CLI + 集成测试 | ① 目标不可达（连接拒绝/DNS 失败）：C-ECHO 与 C-STORE 均返回 Failed + 可读原因，不崩、exit 1，不产生伪造 Success（C-STORE 不可达时写 failed 行 id 67）。② 本地文件缺失（`0000…ff` 不存在）：`reason=本地存储文件缺失: D:\OIS\storage\00\0000…`，Failed exit 1，写 failed 行（id 68），不伪造 Success。③ 并发：按 spec「可串行化验证即可」——CLI 串行执行 + 集成测试 xUnit collection 串行化（`ScuScpIntegrationCollection`），各 forward_log 行 target/uid 配对正确，无串线、无崩溃。集成测试 `Store_When_File_Missing_Returns_Failed_Lossless` 等覆盖。 |
| AC-08 领域/配置正确性 | **满足** | 字节级比对 + 多目标 wire 级 AET 证据 + fallback 链三级实测 | ① 发出内容 = 本地原文：dataset 逐字节一致（AC-04④），UID 严格一致、StudyDate 原样、无大小写/空格/时区窜改。② calling/remote AE 用配置值且多目标互不串扰：AppScp 目标走 `OIS/PACS`（日志 `C-ECHO -> AppScp (OIS@127.0.0.1:11112 called=PACS)`），Cap 目标走 `OISCAP/CAPA`，捕获宿主 wire 级记录 `CAPTURE_ASSOC calling=OISCAP called=CAPA`——两目标关联各自独立。③ calling AE fallback 链逐级实测：目标级 `CallingAeTitle`（OIS/OISCAP 生效）→ `Scu:CallingAeTitle` 级（exfb1 目标无目标级 AE → 日志 `C-ECHO -> Fb1 (FBSCU@…)`）→ `Scp.AeTitle` 级（exfb2 无 Scu 级 → 日志 `C-ECHO -> Fb2 (OIS@…)`）；末级「空串由 fo-dicom 生成」代码核对（`ResolveCallingAe`, `DicomScuService.cs:249-254`）。④ 配置缺失：无 `RemoteScps` → 0 目标可用但命令明确报「目标未找到」（无随机端口/默认 AET 的静默转发）；单项目缺 Host/端口越界/Name 重复/非 DICOM 协议均报明确错误（AC-01 证据④⑤）。 |

## 3. 领域关卡核对（DOMAIN.md §8 + spec §10 self-check）

- **转发内容无损（硬性）**：源/收 dataset 逐字节一致（十六进制比对），不重编码、不改 UID/StudyDate（AC-04/AC-08 实测）。合格。
- **成功以 response.Status 为准（硬性）**：`EchoAsync` 校验 `response.Status == DicomStatus.Success`（`DicomScuService.cs:57`）；C-STORE 校验 `status == DicomStatus.Success`（`:163`）。未把「不抛异常」当成功；失败路径（连接拒绝）走 catch 转 Failed。实测成功/失败判定与响应状态一致。合格。
- **forward_log 成功/失败均写行、失败可见（硬性）**：实测 6 行（3 success + 3 failed），psql 逐行核对；失败 detail 均为可读原因。合格。
- **calling AE fallback 链明确（硬性）**：目标 → Scu:CallingAeTitle → Scp.AeTitle → 空串(fo-dicom)，逐级 E2E 日志证据 + 代码明码（`:245-254`）。合格。
- **Name 唯一性（硬性）**：重复即抛 `InvalidOperationException` 并给出清晰错误（实测 exdup），不静默产生歧义。合格。
- **不可达目标异常隔离不崩（硬性）**：连接拒绝/坏 host 均 catch 转 Failed，后续可达目标仍成功（实测 + 集成测试）。合格。

## 4. 只增不改 / 越界检查

- `git diff --name-only develop...HEAD`：全部落在 `docs/tasks/002/` 与 `src/OIS.DicomScp/`（C# 层、`appsettings.json`、`Schema.sql` 尾部追加、测试、README）。**未触碰 Native / UI(WPF) / Interop 任何文件**。合格。
- task-001 已验收文件的改动均为**追加**：`InstanceIndex.cs`（新增 42P07 幂等兜底 + 只读查询 `FindBySopInstanceUidAsync`，既有方法未动）；`Options.cs`（新增 POCO）；`Schema.sql`（尾部追加 forward_log DDL，`dicom_instance` 未动）；`Program.cs`（新增子命令 dispatch，`RunScpAsync` 完整保留）；`ScpIntegrationTests.cs`（仅加 1 行 collection 特性）。
- task-001 的 6 个回归测试（落盘/去重/元数据入库/建表幂等/异常不崩）**全部通过**，且本次 E2E 中 task-001 SCP 作为接收端正常工作（C-ECHO Success、C-STORE 去重命中返回 Success），已验收行为未破坏。合格。
- 观察项（不阻塞）：分支下未跟踪的 `src/OIS.DicomScp/.vs/` 实际上**未**被 `.gitignore` 覆盖（review.md 称已忽略，`git check-ignore` 证伪；现规则 `/.vs/` 只匹配仓库根 `.vs/`）。该目录为 VS 构建元数据，不在任何提交中，无交付影响；建议后续 task 将 `.gitignore` 改为 `**/.vs/`。

## 5. 已知限制与建议（不阻塞，供后续 task）

1. **无响应目标潜在挂起**：`SendEchoAsync`/`SendStoreAsync` 的 `TaskCompletionSource` 等待无显式超时；目标若接受 TCP 连接但不回响应，可能长期挂起（连接被拒/不可达已由 fo-dicom 抛异常并被隔离，测试覆盖）。建议后续为等待加 `CancellationToken`/`Task.WhenAny` 超时兜底。
2. **AC-06 lookup-miss 写 failed 可见性行**：spec AC-05 与 AC-06 自相矛盾，实现按 DOMAIN.md「失败必须可见」取舍（见 AC-06 记录性说明）。PO 合并时知悉即可；如需严格「零记录」，改一行即可，但会降低失败可见性，不建议。
3. **`.gitignore` 未覆盖子目录 `.vs/`**（见 §4 观察项）。
4. **`Options.cs` 死代码**（task-001 遗留，非本任务引入）：建议后续清理。

## 6. 需 PO 人工验收项（无法在本环境自动验证，均不阻塞放行）

- **真实 PACS 互操作**：本 spec 明确允许用 task-001 SCP / fo-dicom 测试宿主充当远端对端（已用两者）；对真实 PACS 的跨系统互操作留待后续 task 或 PO 环境人工验证。
- **calling AE fallback 链末级**（配置全空时由 fo-dicom 生成默认 AE）：仅代码核对，未 E2E 实测（E2E 各级 fallback 均被 Scp.AeTitle 兜住，属正常配置语义）。

## 7. 测试产物与数据说明

- 本次 E2E 在 `dicom` 库写入 `forward_log` 行 id 64–69、`dicom_instance` 行 1 条（seed 实例 `3ebbe6ba5c1146688e947d0c136c6f23`，`D:\OIS\storage\3e\3ebbe6ba…`），与历次集成测试留下的开发库数据性质相同，保留不清理（PO 人工复验可直接用 psql 复核上述 id）。
- 临时测试工具（fo-dicom 捕获宿主/seed 客户端及 appsettings 变体）已用完删除，仓库工作树除 `docs/tasks/002/test-report.md` 外无新增文件。

## 结论

**放行。** 14/14 全绿可复现；AC-01 ~ AC-08 逐条实测满足；领域关卡全部通过；只增不改、无越界。PO 可合并 `feature/task-002-scu-forward` 至 `develop`。
