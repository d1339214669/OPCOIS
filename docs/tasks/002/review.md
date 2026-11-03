# task-002 审查结论 — 可配置远程 SCP 出站连接（SCU 转发/推送）

- 审查角色：dev-review
- 审查分支：`feature/task-002-scu-forward`（相对 `develop`，提交 37f4cf7 / f9fbed8 / 7e0dc84 / 8768ced）
- 审查范围：`git diff develop...feature/task-002-scu-forward`
- 验证方式：`dotnet build` **0 错误 0 警告**；`dotnet test` **14/14 全绿**（含 task-001 的 6 个回归 + task-002 的 8 个 SCU 集成测试，重复执行两次结果一致）；实测在 localhost:5432 的 PostgreSQL `dicom` 库上跑通（已自建 `dicom` 库并启动服务）。

## 结论：放行

实现满足 spec 全部 AC 的可验证部分，领域关卡（转发无损、成功看 response.Status、forward_log 成功/失败均写行、calling AE fallback 链明确、Name 唯一性、不可达异常隔离）全部通过，未越界改动 task-001 已验收落盘/入库/SCP 行为，未触碰 Native / UI(WPF) / Interop 任意一层。以下为**已知限制与建议**，不影响本次交付，供后续 task 优先处理。

---

## 逐条 AC 核对

| AC | 实现 | 测试 | 判定 |
|---|---|---|---|
| AC-01 可配置远程 SCP 列表 | `appsettings.json` 新增 `Scu:RemoteScps` 数组；`RemoteScpRepository` 支持 0/1/多个目标，空 Name/Host/非法端口/非 DICOM 协议均抛明确错误；`Name` 用 `HashSet` 校验唯一，重复即抛 `InvalidOperationException`（`RemoteScpRepository.cs:30-31`） | `[手动]`（README 提供 CLI）；`RemoteScpRepository` 校验逻辑代码审查 | 通过 |
| AC-02 C-ECHO 可达成功 | `EchoAsync` 用 `DicomClientFactory` 发 C-ECHO，**看 `response.Status == DicomStatus.Success`**（`DicomScuService.cs:57`），非 Success 返回 Failed | `Echo_To_Reachable_Returns_Success` | 通过 |
| AC-03 C-ECHO 不可达失败不崩 | `EchoAsync` 外层 `catch(Exception)` 捕获连接拒绝/超时 → 返回 `EchoStatus.Failed` + 可读 reason（`DicomScuService.cs:68-73`），不抛未捕获异常；异常隔离 | `Echo_To_Unreachable_Returns_Failed_And_Service_Still_Works`：先不可达后可达仍成功 | 通过 |
| AC-04 C-STORE 转发原文 | `StoreByInternalIdAsync` → `AbsolutePathOf` 解析 → `DicomFile.Open` 原文件 → `new DicomCStoreRequest(file)` 原样发送（`DicomScuService.cs:141,212`），不重编码、不改 UID/StudyDate | `Store_Forwards_Original_Instance_Lossless`：断言远端收到 UID/StudyDate/Modality 与源**严格一致** | 通过 |
| AC-05 转发记录表 | `forward_log` 表 + `ForwardLog.WriteAsync` 追加写行（无 `ON CONFLICT`，append 日志）；成功/失败各写一行；失败经 `WriteFailedLogAsync` 写 `status='failed'`（`DicomScuService.cs:220-243`） | `Forward_Log_Writes_Success_Row` / `Forward_Log_Writes_Failed_Row_When_Unreachable` / `Repeated_Forward_Appends_Multiple_Rows_No_Overwrite` | 通过 |
| AC-06 按 SOPUID 定位 | `InstanceIndex.FindBySopInstanceUidAsync`（新增只读查询，未找到返回 null）；`StoreBySopInstanceUidAsync` 未找到 → Failed + 写 failed 行，无 C-STORE 发送副作用 | `Store_By_SopUid_Works_And_Unknown_Uid_No_Side_Effect` | 通过 |
| AC-07 异常/边界 | 文件缺失 → Failed「文件缺失」；连接拒绝/异常 → catch 转 Failed；读文件失败 → Failed；均不崩、不伪造 Success；`CapturingScpHost` 加锁收数据，xUnit collection 串行化避免静态竞态 | `Store_When_File_Missing_Returns_Failed_Lossless` 等 | 通过 |
| AC-08 领域/配置正确性 | calling AE fallback 链 `目标.CallingAeTitle → Scu.CallingAeTitle → Scp.AeTitle → 空串(fo-dicom)`（`ResolveCallingAe`, `DicomScuService.cs:249-254`）；转发不篡改 UID/StudyDate/时区；多目标各自独立 calling/remote AE | `Store_Forwards_Original_Instance_Lossless` 断言无损 | 通过 |

---

## 领域关卡核对（DOMAIN.md §8 + task-002 spec §10 self-check）

- **转发内容无损（硬性）**：`ForwardFileAsync` 先 `DicomFile.Open(absolutePath)` 读**原文件**，再用 `new DicomCStoreRequest(file)` 构造请求体，**不重编码、不改 UID/StudyDate**（`DicomScuService.cs:141,212`）。实测断言远端收到的 SOPInstanceUID/StudyInstanceUID/StudyDate 与源严格一致。合格。
- **成功与否看 response.Status（硬性）**：C-ECHO `EchoAsync` 校验 `responseState == DicomStatus.Success`（`:57`）；C-STORE `ForwardFileAsync` 校验 `status == DicomStatus.Success`（`:163`）。**未把「SendAsync 不抛异常」当成功**。合格。
- **forward_log 成功/失败均写行、失败可见（硬性）**：成功走 `_forwardLog.WriteAsync(...Status="success")`（`:166-176`）；失败（文件缺失/读文件异常/响应非 Success/连接异常）均经 `WriteFailedLogAsync` 写 `status='failed'`（`:220-243`）。测试断言 failed 行存在。合格。
- **calling AE fallback 链明确（硬性）**：`目标.CallingAeTitle → Scu.CallingAeTitle(构造传入) → Scp.AeTitle(构造传入) → 空串(fo-dicom)`，在 `ResolveCallingAe` 明码实现并注释（`:245-254`）。合格。
- **Name 唯一性校验（硬性）**：`RemoteScpRepository` 用 `HashSet` 去重，重复即抛 `InvalidOperationException`（`:30-31`），不静默产生歧义。合格。
- **不可达目标异常隔离不崩（硬性）**：`EchoAsync`/`ForwardFileAsync` 外层 catch 全部异常 → 返回 Failed，异常不上抛/不崩溃；测试验证不可达后可达仍成功。合格。
- **配置缺失/非法**：空 `RemoteScps`=0 目标可用（AC-01 允许）；空 Name/Host/端口越界/非 DICOM 协议均抛明确错误，无随机端口/默认 AET 的静默错误转发。合格。

---

## 只增不改检查（task-001 已验收行为）

- `Index/Schema.sql`：**末尾追加** `forward_log` 表 + 索引（`IF NOT EXISTS`），`dicom_instance` 表结构未动。合格。
- `Index/InstanceIndex.cs`：仅新增 `FindBySopInstanceUidAsync` 只读查询 + `GetOrNull`；`EnsureSchemaAsync` 增加对 PostgreSQL `42P07`（duplicate table/index）竞态的**幂等兜底捕获**（并发启动时 DDL 竞态，忽略即可，AC-02）。未改变既有插入/去重/查询语义。合格。
- `Options.cs` / `appsettings.json` / `Program.cs`：新增 `Scu` 节与 SCU 命令；`scp` 子命令完整保留 task-001 的 SCP 启动逻辑（`RunScpAsync`）。`Options.cs` 仍是 task-001 遗留的未引用死代码（非本任务引入）。合格。
- 实测 task-001 的 6 个回归测试（C-ECHO/C-STORE 落盘/去重/元数据入库/建表幂等/坏数据不崩）**全部通过**，证明未破坏 task-001 行为。

---

## 越界 / 多余改动检查

- `git diff --name-only develop...HEAD` 全部落在 `docs/tasks/002/` 与 `src/OIS.DicomScp/`（C#/Infrastructure 层）。**未触碰 Native / UI(WPF) / Interop / 其他层**，无 spec 之外的多余改动。合格。
- 分支下有未跟踪的 `src/OIS.DicomScp/.vs/`，但已被 `.gitignore` 忽略（task-001 已加 `/.vs/` 规则），不入库，无影响。

---

## 已知限制与建议（不阻塞）

1. **C-ECHO/C-STORE 无响应目标的潜在挂起（AC-07）**：`SendEchoAsync`/`SendStoreAsync` 用 `TaskCompletionSource` 等待响应（`DicomScuService.cs:200-205, 213-217`），若目标**接受 TCP 连接但不回关联/响应**，`tcs.Task` 可能长期挂起（无显式超时）。连接被拒/端口不可达已由 fo-dicom 抛异常、测试覆盖并隔离。建议后续为 `tcs.Task` 加 `CancellationToken`/关联超时，用 `Task.WhenAny` 兜底。本次不阻塞。
2. **`Options.cs` 死代码（task-001 遗留）**：`ScpOptions/StorageOptions/PostgresOptions` 未被 `Program.cs` 使用（其内部用 `ConfigRoot`），非本任务引入，建议后续清理。
3. **失败记录本身写库失败**：`WriteFailedLogAsync` 捕获写库异常并打日志（`:238-242`），不掩盖原始失败原因；若 PG 不可用则无法落库，符合「失败可见」但不落 `forward_log`，属可接受边界。

---

## 结论

**放行。** 实现满足 spec 全部 AC 的可验证项，领域关卡（转发无损 / 成功看 response.Status / forward_log 成功失败均写行 / calling AE fallback 链 / Name 唯一性 / 不可达异常隔离）全部通过；`dotnet build` 0 错误 0 警告、`dotnet test` 14/14 全绿且可复现；未越界改动 task-001 已验收的落盘/入库/SCP 行为，未触碰 Native / UI(WPF) / Interop。无硬否决项。