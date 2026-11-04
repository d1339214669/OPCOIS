# task-003 审查结论 — WPF 管理控制台（UI 层）

- 审查角色：dev-review
- 审查分支：`feature/task-003-wpf-console`（相对 `develop`，提交 b99d93a / 40e38d8 / 55c5d53 / 025c7ee / 679d861 / 0a0713a）
- 审查范围：`git diff develop...feature/task-003-wpf-console`（73 个文件，逐一阅读；对「搬家」文件另用 `git diff -M5%` + blob 哈希复核）
- 验证方式（非空口，全部本机实测）：
  - `dotnet build OIS.DicomScp.sln --no-incremental` **0 错误 0 警告**；
  - `dotnet test OIS.DicomScp.sln` **75/75 全绿**（OIS.DicomScp.Tests 27 项 = 14 项既有回归 + 13 项新集成；OIS.DicomScp.Wpf.Tests 48 项 headless），**重复执行两次结果一致**；
  - CLI 冒烟（`OIS.DicomScp.exe` 直接运行）：`echo-scu --remote TestPacs`（不可达）→ `status=Failed` + 可读原因 + exit 1；`store-scu --uid <未知>` → `status=Failed`「未找到该 SOPInstanceUID 的实例」+ forward_log failed 行 + exit 1（冒烟后已自行清理该行）；`echo-scu`（缺 `--remote`）→ 明确报错 + exit 2。行为与 task-002 一致；
  - `grep "System.Windows"` 于 `OIS.DicomScp.Wpf/ViewModels/*.cs` **0 命中**（AC-10 判据复核）；
  - PostgreSQL 使用既有 localhost:5432 `dicom` 库，未另起库。

## 结论：放行

Core 抽取确系「搬家不重写」（16 个文件 100% 原样迁移，仅 InstanceIndex/ForwardLog 纯增量；既有 14 项回归 + 既有测试文件 blob 哈希逐一致 + CLI 冒烟全部通过）；AC-01~AC-10 的可验证项全部有实现与对应测试；领域关卡（UID/StudyDate 全链路原样字符串、缺失不臆造、失败可见、转发/核对走既有服务不重写协议）全部通过；未触碰 out of scope（无影像预览、无 C-FIND/C-MOVE 实现、无权限、无部署打包），未触碰 Native/(C++)、Interop/ 生成文件（红线）。两条非阻塞建议见文末，不影响本次交付。

---

## 逐条 AC 核对

| AC | 实现 | 测试 | 判定 |
|---|---|---|---|
| AC-01 结构抽取 + 回归 | 新建 `OIS.DicomScp.Core`（net10.0，含 Scp/Storage/Index/Scu/Options/AppConfig/ConfigStore/ScpHost + Schema.sql 复制到输出目录）；`OIS.DicomScp` host 只保留 Program.cs 分发，引用 Core；`OIS.DicomScp.Wpf`（net10.0-windows，UseWPF）只引用 Core。搬家复核：`git diff -M5%` 显示 Options/Scp×5/Storage/Index×2/Scu×8 共 16 文件 **similarity 100%**；InstanceIndex（62%）/ForwardLog（57%）仅新增接口实现与查询方法，既有方法零改动；host 与测试 csproj 仅增删 ProjectReference/Schema 复制项；`appsettings.json`、既有 5 个测试文件 blob 哈希与 develop 逐一相同 | 既有 14 项回归（ScpIntegrationTests 6 + ScuIntegrationTests 8）原样全绿；CLI 三子命令冒烟行为不变（本审查实测） | 通过 |
| AC-02 实例浏览 | `IInstanceQuery.QueryAsync/CountAsync`（`InstanceIndex` 显式参数类型防 42P08；null 参数不过滤；received_at 倒序 + take；无结果=空集合合法）；`InstancesViewModel` 查询失败 → `ErrorText` 明确原因 + 空结果，不静默 | 新 `InstanceQueryIntegrationTests`（真实 PG 预置 5 行：各过滤/时间窗/take/倒序/空结果/原样字符串/缺失 null，4 项）；`InstancesViewModelTests`（7 项：成功/空白→null/无结果/PG 失败可见/核对联动/清空/重复调用不锁死） | 通过 |
| AC-03 详情核对 | `InstanceDetailViewModel` 用 fo-dicom `DicomFile.OpenAsync` 读 `<storageRoot>/<file_path>` 原文件，对 SOPInstanceUID/PatientID/StudyInstanceUID/Modality/StudyDate 逐项 `string.Equals(..., Ordinal)`；缺失为 null（null==null 一致）；文件缺失/读失败 → 明确错误状态不崩溃 | 新 `InstanceDetailViewModelTests`（8 项，**真实 fo-dicom 文件**写临时目录：全一致/StudyDate 改错报出/文件缺失/文件损坏/单侧缺失不臆造/双侧缺失一致/命令可用性/null 行） | 通过 |
| AC-04 配置 CRUD | `RemoteScpsViewModel` 增/改/删/列表 + 保存前校验（Name 非空且 Ordinal 唯一、Host 非空、Port 1–65535、协议仅 DICOM）→ 拒绝给可读原因且文件不动；通过 → **读-改-写**只替换 `Scu.RemoteScps`，其余节（Scp/Storage/Postgres/Scu.CallingAeTitle）原样保留；`ConfigStore.SaveAsync` **同目录临时文件 + `File.Move(overwrite:true)` 原子替换**，清理失败记 LogError | 新 `ConfigStoreTests`（6 项：round-trip；**写回 JSON 保持字段名且 `RemoteScpRepository` 重读成功**（格式兼容）；缺省默认；读-改-写不丢其他节；原子写无 `.tmp-` 残留；null 拒绝）；`RemoteScpsViewModelTests`（11 项：加载/新增/删除/保存保留其他节/Name 空与重复被拒/Host/Port/协议拒绝/写失败可见/大小写 Ordinal 语义） | 通过 |
| AC-05 连通性测试 | `ForwardingViewModel.EchoAsync` 调既有 `IDicomScuService.EchoAsync`（task-002 实现 100% 未动，成功仍看 response.Status），Success/Failed + 原因可见；失败后可继续测下一目标；契约外的未预期异常也有双保险可见 | `ForwardingViewModelTests`（成功/失败原因可见且可测下一目标/未预期异常不崩）；可达 Success 场景由既有回归 `Echo_To_Reachable_Returns_Success` 覆盖（全绿） | 通过 |
| AC-06 转发操作 | `ForwardingViewModel.StoreAsync` 按选中实例的 SOPInstanceUID 调既有 `StoreBySopInstanceUidAsync`（task-002 无损转发逻辑未动），结果 + 原因可见；转发后自动刷新历史 | `ForwardingViewModelTests`（成功刷新历史/失败原因可见可重试/未选中无副作用）；远端收到 + UID 一致 + forward_log 成功/失败行由既有回归（`Store_Forwards_Original_Instance_Lossless` 等 8 项）覆盖 | 通过 |
| AC-07 转发历史 | `IForwardLogQuery.RecentAsync`（`ForwardLog.ForwardLogQueryAsync`，id 倒序、原样字段）；`ForwardingViewModel.History` 展示时间/目标/实例定位/status/原因；查询失败 → 可见错误 + 空历史 | 新 `ForwardLogQueryIntegrationTests`（真实 PG 写 3 行 success/failed/success：最近 N 倒序、字段与实际一致、失败可读原因、take≤0 报错）；`ForwardingViewModelTests`（历史成功倒序/失败可见） | 通过 |
| AC-08 服务启停 | `ScpHost`（Core）封装 DicomServer 组装（幂等建表→存储→编排注入→服务容器），`StartAsync` 信号量串行化、已运行 → 抛 `ScpAlreadyRunningException`；`StopAsync` 释放监听、未运行 → `InvalidOperationException`；`ServiceStatusViewModel` 将两类异常转为「已运行/未运行」明确提示（非错误），其他失败可见 | 新 `ScpHostIntegrationTests`（真实端口 + fo-dicom 对端：启动→C-ECHO Success→**重复启动抛异常（不双监听）**→停止→连接拒绝→可重启再 C-ECHO；未运行停止报错）；`ServiceStatusViewModelTests`（幂等提示/未运行提示/启停失败可见/实例数回退） | 通过 |
| AC-09 领域关卡 | 见下方「领域关卡核对」 | 见下方「领域关卡核对」 | 通过 |
| AC-10 MVVM 纪律 | ViewModels 全部只依赖 Core 接口 + 纯 .NET `IAppCommand`/`RelayCommand`（`Commands/`）；WPF 桥接集中在 View 层 `AppCommandBridge`（唯一 `System.Windows.Input` 触点）；Core 无 WPF 依赖 | 本审查 grep 复核 ViewModels 0 命中；`OIS.DicomScp.Wpf.Tests`（net10.0-windows，**不启用 UseWPF**，无窗口）48 项全 headless（fakes + 临时目录真实 DICOM 文件）跑通 | 通过 |

---

## 领域关卡核对（DOMAIN.md / spec AC-09）

- **UID/StudyDate 全链路原样字符串**：`InstanceRow`/`ForwardLogRow` 全部 VARCHAR 字段经 `GetString`/`GetOrNull` 原样读出（`InstanceIndex.cs` QueryInstancesAsync/FindBySopInstanceUidAsync、`ForwardLog.cs` ForwardLogQueryAsync）；UI 绑定无 StringFormat 改写（`InstanceListView` StudyDate 列直接绑原值；`ForwardingView`/`InstanceListView` 仅对 received_at/forwarded_at 这类**真实时间戳**做显示格式化，不涉 DICOM 日期）；核对用 `StringComparison.Ordinal` 严格相等（`InstanceDetailViewModel.cs:154`）。**全链路无 DateTime 解析介入 UID/StudyDate 显示**。合格。
- **缺失字段不臆造**：DB null → `null`（ViewModel 显示为空；`ServiceStatusViewModel` PG 查询失败时实例数显示「—」而非 0，`InstanceDetailViewModel` 文件缺失标签为 null 不补默认值）；测试 `Query_Returns_Raw_Strings_As_Is_and_Missing_Fields_Null`、`Load_File_Field_Missing_Is_Null_Not_Invented_And_Mismatched`、`Refresh_CountFailure_Is_Visible_With_Dash_Not_Fabricated_Zero` 覆盖。合格。
- **失败必须可见（无空 catch/无静默吞）**：逐文件通读，所有 catch 均产生可见状态——5 个 ViewModel 的每个 catch 都设置 `ErrorText`/`StatusText`（部分双保险）；`ConfigStore.SaveAsync` 临时文件清理失败 `LogError`；`ConsoleServices` 建表失败 `LogError` 且后续查询在 UI 报可见错误；`App.xaml.cs` 启动失败弹窗 + 退出码 1。`RemoteScpRepository`/`DicomScuService`（task-002）的既有失败可见语义 100% 未动。合格。
- **转发/核对走既有服务、不重写协议**：C-ECHO/C-STORE 全部经 task-002 原样的 `DicomScuService`（100% 迁移）；核对经 fo-dicom 只读打开原文件；未新增任何协议实现；`IRemoteForwarder`/`IMoveScu` 仍为纯占位（100% 迁移）。合格。

---

## 只增不改 / 越界 / 红线检查

- **只增不改（task-001/002 已验收行为）**：16 个协议/存储/索引/SCU 文件 100% 原样；`Schema.sql` 未加任何 DDL（只查询）；`appsettings.json`（host）blob 相同；既有测试文件 blob 相同；14 项回归全绿。合格。
- **越界（out of scope）**：73 个变更文件全部落在 `docs/tasks/003/`、`src/OIS.DicomScp/`（C#/WPF/测试）、`.gitignore`。无影像渲染/预览、无 C-FIND/C-MOVE 实现、无权限/审计、无 MSIX/ClickOnce 打包。WPF UI 属 PO 明确授权的 spec 范围（TEAM.md §5 例外已满足）。合格。
- **红线**：diff 中无 Native/(C++)、无 Interop/ 生成文件、无任何其他层；本审查全程未对 develop 做写操作、未用 `--force`、未改写历史、未 push。合格。

---

## 已知限制与建议（不阻塞）

1. **`RemoteScpsViewModel.IsConfiguredButBlank` 是死代码**（`OIS.DicomScp.Wpf/ViewModels/RemoteScpsViewModel.cs:215-216`）：`!string.IsNullOrWhiteSpace(v) && v.Trim().Length == 0` 逻辑上恒为 false（含非空白字符则 Trim 后长度 ≥1，纯空白则前者为 false），故「CalledAe/CallingAe 已配置但为纯空白」的拒绝分支**永不触发**、对应错误文案不可达。行为上不影响 AC-04：纯空白 AET 被 `ToTarget` 归一为 `""`/`null`，与 task-002 `RemoteScpRepository` 的既有归一语义**一致**（spec 的校验基准即「与 task-002 一致」）。建议后续任务删除该死检查、把「空白=未配置」的语义写进注释，避免误导。
2. **`ServiceStatusViewModel.InitializeAsync` 缺少 LoadAsync 的异常兜底**（`ServiceStatusViewModel.cs:80-87` + `MainWindow.xaml.cs:42-46`）：若 appsettings.json 存在但为非法 JSON，`ConfigStore.LoadAsync` 抛 `InvalidDataException`，经 `MainWindow.OnLoaded`（async void）成为未处理的 Dispatcher 异常，以崩溃对话框结束而非 UI 内可见错误态。触发条件苛刻（自带配置合法，需人工损坏配置文件），不构成本次打回理由；建议后续补 try/catch 或在 `App.xaml.cs` 挂 `DispatcherUnhandledException` 兜底。
3. **`InstancesViewModel.BlankToNull` 对过滤输入做 Trim**（`InstancesViewModel.cs:158-159`）：属用户输入边界处理（README 已写明「空白=不过滤，trim」），非领域数据转换——存储与显示的 UID/StudyDate 不受影响。记录在案，不视为 AC-09 违反。
4. **WPF 目标框架**：`OIS.DicomScp.Wpf` 与 `Wpf.Tests` 用 `net10.0-windows`（Wpf.Tests 不启用 UseWPF，headless 语义满足 AC-10 判据）。spec §7 的「net10.0」字面与 §8 第 6 条「若 WPF 在 net10 有已知问题，降为/调整并记录」的口径下：.NET 10 SDK 直接支持 net10.0-windows，无需回退；TFM 选择已在 README「目标框架（task-003 记录）」写明理由。合规，记录在案。
5. **C-ECHO/C-STORE 对「接受 TCP 但不回应」目标的潜在挂起**：继承自 task-002 审查已记录的非阻塞项（`DicomScuService` 未变），不在本任务范围内，建议后续统一加超时兜底。

---

## 需 PO 人工验收（无法自动验证，按 spec §4 惯例标注，非打回理由）

- 窗口实际渲染、五页签布局/视觉观感/交互手感（XAML 渲染与按钮可用性未经实际窗口验证）；
- 真实 PACS/远程设备互操作（测试以 fo-dicom 捕获宿主与真实 PG 替代）；
- 长时间运行下的内存/句柄行为。

---

## 结论

**放行。** 实现满足 spec 全部 AC（AC-01~AC-10）的可验证项：Core 抽取搬家不重写（14 项回归 + CLI 冒烟 + 16 文件 100% 迁移复核）、新 API 与 WPF 逻辑均有测试覆盖（75/75 全绿且可复现、build 0 警告 0 错误）、领域关卡（原样字符串/缺失不臆造/失败可见/不重写协议）全部通过、MVVM 纪律 grep 复核 0 命中；未越界、未触红线。两条非阻塞建议（死代码 `IsConfiguredButBlank`、`InitializeAsync` 异常兜底）留后续任务处理。XAML 实际渲染与交互手感标注「需 PO 人工验收」。
