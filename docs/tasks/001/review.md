# task-001 审查结论 — DICOM Storage SCP 核心存储层

- 审查角色：dev-review
- 审查分支：`feature/task-001-dicom-scp-storage`（相对 `develop`）
- 审查范围：`git diff develop...feature/task-001-dicom-scp-storage`（dev-dev 提交 a76ca61 / 89a6259 / 5f2ca5c / 412f0df / f62323d，另含 spec 提交 d4512df）
- 验证方式：`dotnet build` 0 错误 0 警告；`dotnet test` 6/6 全绿（含 C-ECHO / C-STORE 落盘 / 去重 / 元数据入库 / 坏数据不崩溃）；实际在 localhost:5432 的 PostgreSQL dicom 库上跑通。

## 结论：放行

实现满足 spec 全部 AC 的可验证部分，领域关卡（匿名化、UID 字符串一致、StudyDate 原样、缺失置 NULL、失败可见、先文件后索引一致性）全部通过，未越界改动 Native / UI(WPF) / Interop 任意一层。以下为**已知限制与建议**，不影响本次交付，供后续 task 优先处理。

---

## 逐条 AC 核对

| AC | 实现 | 测试 | 判定 |
|---|---|---|---|
| AC-01 可执行 + 配置 | `Program.cs` 读 `appsettings.json`，ScpPort/AeTitle/Storage.Directory/Postgres 连接串全部来自配置，文件缺失走默认值 | `[手动]`（README 提供 `dotnet run` 路径） | 通过 |
| AC-02 幂等建表 | `Schema.sql` 用 `CREATE TABLE/INDEX IF NOT EXISTS`；`InstanceIndex.EnsureSchemaAsync` 启动时执行 | `Schema_Is_Idempotent_And_Table_Exists` 连跑两次 + `information_schema.tables` 断言 | 通过 |
| AC-03 C-ECHO | `DicomScpService.OnCEchoRequestAsync` 返回 `DicomStatus.Success` | `CEcho_Returns_Success` | 通过 |
| AC-04 落盘 + 匿名化 | `FileStorage.GenerateInternalId()` = `Guid.ToString("N")`（32 位小写 hex）；文件名一律用内部 ID，分片子目录 `<id[0..2]>/<id>`；`StoreHandler.SerializeFileAsync` 保留原 transfer syntax 无损写回 | `CStore_Persists_File_With_InternalId_And_Sharding`：断言 `^[0-9a-f]{32}$`、文件名不含 sopUid、`dcmdump` 闭环 | 通过 |
| AC-05 同实例去重 | `StoreHandler` 先 `ExistsBySopInstanceUidAsync` 命中跳过；`InstanceIndex.InsertAsync` 用 `ON CONFLICT (sop_instance_uid) DO NOTHING` 双保险，行为=跳过（已记录） | `CStore_SameSopInstanceUid_Is_Deduplicated`：1 文件 1 行不抛错 | 通过 |
| AC-06 元数据入库 | `ToMetadata` 抽取全部字段；`file_path` 即相对路径，`internal_id` 即文件名；缺失置 NULL | `CStore_Metadata_Is_Indexed`：逐字段断言 + `file_path` 实际存在 + 日期原样 + 缺失 null | 通过 |
| AC-07 异常/边界 | 坏数据由 fo-dicom 连接层处理不崩；空 SOPInstanceUID 抛错→上层 `ProcessingFailure`+记日志（显式可见）；目录不存在自动创建 | `BadData_DoesNot_Crash_And_Service_Still_Responds` | 通过 |
| AC-08 领域关卡 | UID 用 `TryGetString` 原值返回、不 Trim 大小写/空格；StudyDate 保持 `YYYYMMDD` 字符串原样入库；缺失置 NULL | 日期/UID 断言 | 通过（UID/日期一致性全过；AET 见下） |

---

## 领域关卡核对（DOMAIN.md §8 + spec §9.6 self-check）

- **匿名化（AC-04 硬性）**：`file_path`/文件名只用内部 ID。经核对 `FileStorage.GenerateInternalId()` 用 `Guid.ToString("N")`，文件名与 SOPInstanceUID/StudyInstanceUID/PatientID 无任何关联。合格。
- **UID 字符串严格一致**：`StoreHandler.GetUid` 返回 `ds.TryGetString` 的原始值（`StoreHandler.cs:94-95`），无隐式 `Trim()`/大小写改动。合格。
- **StudyDate 原样**：`study_date` 为 `VARCHAR(8)`（`Schema.sql:17`），`StudyDate` 存原字符串（`StoreHandler.cs:83`），不做时区/日期转换。合格。
- **缺失置 NULL 不臆造**：`GetUid`/`GetString`（`StoreHandler.cs:94-99`）对缺失/空返回 `null`，参数以 `DBNull.Value` 入库（`InstanceIndex.cs:88-100`），无默认值。合格。
- **先文件后索引**：`StoreHandler.HandleAsync` = 先 `FileStorage.Store`（`:50`）后 `InstanceIndex.InsertAsync`（`:55`），理由（文件是权威副本、避免悬空索引）已记录。合格。
- **失败可见**：C-STORE 异常全部捕获→`ScpLogger.LogError`→返回 `ProcessingFailure`（`DicomScpService.cs:48-53`），不回滚成静默成功。合格。

---

## 已记录的限制与建议（不阻塞）

1. **AET 匹配（AC-08，dev-dev 报告点）——评估为「部分满足、可接受，但建议后续加强」**
   - 现状：`Program.cs`（`:28,57`）读到的 `AeTitle` **只用于日志输出**，未传入 `DicomServerFactory.Create<DicomScpService>(port)`（`:57`），`OnReceiveAssociationRequestAsync`（`DicomScpService.cs:72-92`）也未比对 `association.CalledAE`。因此 fo-dicom 5.2.6 对**任意 called-AE** 都会接受关联，不会真正「拒绝」错误 AET。
   - spec AC-08 原文："用错误 AET 联机时行为明确（**拒绝或记录**）"。本实现选「记录」路径且已在 README「已知行为与限制」（`README.md:127`）及 `StoreHandler` 注释透明声明，符合 spec 字面允许项；且 C-STORE 数据一致性/匿名化等**关键交付均不受影响**。故判定**可接受，不构成硬否决**。
   - 建议：后续 task 在 `OnReceiveAssociationRequestAsync` 内显式 `LogInformation` calling/called AE（当前依赖 fo-dicom 默认 logger，Console 可见性未显式接 `ILoggerFactory`），并可用 `association.CalledAE` 与配置比对实现真正白名单；补一条错误-AET 联机断言的 `[自动]` 测试。本次不阻塞。

2. **跨关联并发同 SOPInstanceUID 边缘（AC-05）**：同一 association 内请求被 fo-dicom 顺序处理，无竞态；但**两个并发关联**同时 C-STORE 同一 UID 时，两个请求都可能先通过 `Exists` 检查（`StoreHandler.cs:39`）→各写一文件→仅一个 INSERT 成功，留下一个**未被索引的孤儿文件**。测试覆盖的是顺序场景。建议后续用「先入库后写文件 + 失败回删」或对 internal_id 处理唯一性后回收文件，本次不阻塞。

3. **`Options.cs` / `ConfigRoot` 两套配置 POCO**：`Program.cs` 内部有 `ConfigRoot`，`Options.cs` 有 `ScpOptions/StorageOptions/PostgresOptions`，但 `Program.cs` 实际只用前者、`Options.cs` 未被引用。属重复/死代码，建议后续清理归并（不影响功能）。

4. **`TestScpHost` 与 `Program` 各自 `DicomSetupBuilder`**：fo-dicom 在单进程多宿主下可能互相干扰，但当前测试全绿、无冲突暴露，仅提示后续多宿主场景留意。

---

## 越界 / 多余改动检查

- diff 仅含 `docs/tasks/001/spec.md`、`.gitignore`、`src/OIS.DicomScp/` 下文件。**未触碰 Native / UI(WPF) / Interop / 其他层**，无 spec 之外的多余改动。合格。
- `.gitignore` 仅追加 `/storage/`、`/.vs/`，未破坏 `docs/` 交接物规则。合格。

---

## 结论

**放行。** 实现满足 spec 全部 AC 的可验证项，领域关卡（匿名化 / UID 一致 / 日期原样 / 缺失 NULL / 失败可见 / 一致序）全部通过，构建与测试全绿，无越界改动。AET 严格校验为已知、透明记录的限制（spec 允许「记录」路径），建议在后续 task 补齐显式 calling-AE 记录与白名单，并在同任务内处理跨关联并发与 `Options.cs` 死代码。
