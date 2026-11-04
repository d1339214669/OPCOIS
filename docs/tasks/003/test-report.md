# task-003 测试验证结论 — WPF 管理控制台（UI 层）

- 测试角色：dev-test
- 测试分支：`feature/task-003-wpf-console`（相对 `develop`，提交 b99d93a / 40e38d8 / 55c5d53 / 025c7ee / 679d861 / 0a0713a / 4465ae0，共 7 个提交，与交接说明一致）
- 基准：`docs/tasks/003/spec.md`（AC-01~AC-10、§9 验收矩阵）；前置审查 `docs/tasks/003/review.md`（dev-review 结论=放行，已核验存在）
- 领域判据：`D:\MyOpc\DOMAIN.md`（UID/StudyDate 原样、缺失不臆造、失败必须可见）
- 环境：PostgreSQL localhost:5432 `dicom` 库（既有，未另起库）；.NET SDK 10.0.300；本机 `D:\OIS` 检出

## 结论：放行

全量验证（clean → build → test）可复现 75/75 全绿、build 0 错误 0 警告；AC-01~AC-10 逐条核对全部满足；领域关卡（DOMAIN.md）全部通过；越界/红线检查全部合格。无打回项。两条非阻塞发现与 review.md 记录一致（见文末），XAML 实际渲染与交互手感标注「需 PO 人工验收」。

---

## 1. 全量测试（干净状态复现）

| 步骤 | 命令 | 结果 |
|---|---|---|
| clean | `dotnet clean OIS.DicomScp.sln` | 成功 |
| build | `dotnet build OIS.DicomScp.sln` | **0 错误 0 警告**（Core/net10.0、host/net10.0、Wpf/net10.0-windows、两测试工程全部生成） |
| test | `dotnet test OIS.DicomScp.sln` | **OIS.DicomScp.Tests 27/27 + OIS.DicomScp.Wpf.Tests 48/48 = 75/75 全绿**，0 失败 0 跳过 |

与 dev-dev 自报（build 0 错 0 警；75/75 = 48 headless + 27 集成含 14 项回归）一致，可复现。

---

## 2. AC 逐条核对表（spec §9 验收矩阵）

| AC | 满足 | 验证手段 | 证据 |
|---|---|---|---|
| AC-01 结构+回归 | ✅ | 源码/csjproj 检查 + 既有 14 回归 + CLI 冒烟实测 | Core csproj = net10.0 纯类库（fo-dicom/Npgsql/MS.DI，无 WPF 依赖）；host 只引用 Core，Program.cs 相对 develop 仅把手工 JSON 读取换成 Core `IConfigStore`/`AppConfig`，三子命令 dispatch 与退出码逻辑零改动；Wpf csproj = net10.0-windows + UseWPF，只引用 Core。既有 14 回归（ScpIntegrationTests 6 + ScuIntegrationTests 8）测试文件与 develop blob 未变（不在 diff 内），本次全绿。CLI 冒烟（本机实跑 bin/Debug/net10.0/OIS.DicomScp.exe）：`echo-scu`（缺 --remote）→ 明确报错「缺少 --remote <Name> 参数（可用目标: TestPacs）」+ **exit 2**；`echo-scu --remote TestPacs`（11113 未监听）→ `status=Failed` + 可读原因（连接被拒）+ **exit 1**；`store-scu --remote TestPacs --uid <未知>` → `status=Failed reason=未找到该 SOPInstanceUID 的实例` + forward_log failed 行（psql 核实 id=279）+ **exit 1**。行为与 task-002 一致 |
| AC-02 实例浏览 | ✅ | 真实 PG 集成测试 + headless VM 测试（源码抽查） | `InstanceIndex.QueryInstancesAsync`：PatientID/StudyInstanceUID/Modality/received_at 范围过滤（null 不过滤，显式 NpgsqlParameter 类型防 42P08）、received_at 倒序、take（≤0 抛 ArgumentOutOfRangeException）。`InstanceQueryIntegrationTests`（真实 PG 预置 5 行、独立时间窗 2020-01-01）断言：全表/时间窗=恰好 5 行倒序、PatientID=2 行、StudyInstanceUID=2 行、Modality=3 行、时间范围/仅上界各 2 行、take=2 截断最新两行、无结果=空集合（合法）。`InstancesViewModelTests` 7 项：成功/空白→null 过滤/无结果非错误/PG 失败→ErrorText 可见（不静默空列表）/核对联动事件/清空过滤/重复调用不卡死。实例浏览 UI 显示为原样字符串（StudyDate 列无 StringFormat，XAML 核实） |
| AC-03 元数据核对 | ✅ | 真实 fo-dicom 文件 headless 测试（源码抽查） | `InstanceDetailViewModel`：`DicomFile.OpenAsync` 读 `<storageRoot>/<file_path>`，对 SOPInstanceUID/PatientID/StudyInstanceUID/Modality/StudyDate 逐项 `string.Equals(..., Ordinal)`（InstanceDetailViewModel.cs L154）；null==null 一致；文件缺失 → StatusText「核对失败：存储文件缺失」+ ErrorText；读失败 → catch 置「文件读取错误」+ ErrorText，不崩溃。`InstanceDetailViewModelTests` 8 项（临时目录写**真实 fo-dicom 文件**）：全一致→Consistent（5 项报告全 Consistent）；库 StudyDate=20250701 文件=20250702 → Inconsistent 且**点名 StudyDate** 不一致（file/db 值各自断言）、其余 4 项一致；文件缺失/文件损坏→可见错误不崩；单侧缺失→FileValue=null 不臆造且不一致；双侧缺失→一致；命令 CanExecute；null 行 no-op |
| AC-04 配置 CRUD | ✅ | round-trip + 校验拒绝 + 格式兼容（真实 ConfigStore + 假/真 PG 测试） | `RemoteScpsViewModel`：增/改/删/列表；SaveAsync 先 `Validate()`（Name 非空且 Ordinal 唯一、Host 非空、Port 1–65535、协议仅 DICOM）→ 拒绝=可读原因 + 「appsettings.json 未改动」；通过=**读-改-写**只替换 `Scu.RemoteScps`，其余节保留。`ConfigStore.SaveAsync` 同目录临时文件 + `File.Move(overwrite:true)` 原子替换。`ConfigStoreTests` 6 项：round-trip；写回 JSON 保持 `CallingAeTitle/RemoteScps/CalledAeTitle/Scp/Storage/Postgres` 字段名且 **task-002 `RemoteScpRepository` 重读成功**（格式兼容判据实测）；缺文件默认；读-改-写不丢其他节（Port/Storage/Postgres 重读断言）；原子写无 `.tmp-` 残留且覆盖成功；null 抛异常。`RemoteScpsViewModelTests` 10 项：加载/新增/删除/无选中 no-op/保存保留其他节/Name 空与重复被拒（2 case，文件不动）/Host、Port、协议非法被拒/写失败可见/大小写 Ordinal 语义（`P1` vs `p1` 判重） |
| AC-05 C-ECHO | ✅ | 可达/不可达两场景（回归 + CLI 实测 + VM 测试） | `ForwardingViewModel.EchoAsync` 调既有 `IDicomScuService.EchoAsync`（task-002 `DicomScuService` 100% 迁移，成功看 response.Status）；Success/Failed + Reason 可见；失败后状态复位可继续测下一目标；契约外异常双保险 catch 可见。可达 Success = 回归 `Echo_To_Reachable_Returns_Success`（本次全绿）；不可达 Failed + 可读原因 = 回归 `Echo_To_Unreachable_Returns_Failed_And_Service_Still_Works`（全绿）+ 本机 CLI 冒烟实测（见 AC-01 行）。`ForwardingViewModelTests`：Echo 成功/失败可重试下一目标/未预期异常不崩 |
| AC-06 C-STORE 转发 | ✅ | 捕获 SCP + forward_log + 未知实例（回归 + CLI 实测 + VM 测试） | `ForwardingViewModel.StoreAsync` 按选中实例 SOPInstanceUID 调既有 `StoreBySopInstanceUidAsync`（task-002 无损转发未动），结果 + 原因可见，转发后自动刷新历史。远端收到 + UID 一致 + forward_log success 行 = 回归 `Store_Forwards_Original_Instance_Lossless` / `Forward_Log_Writes_Success_Row`（全绿）；未知实例 Failed「未找到」且无副作用 = 回归 `Store_By_SopUid_Works_And_Unknown_Uid_No_Side_Effect`（全绿）+ 本机 CLI 冒烟实测（failed 行 psql 核实）；目标不可达 → failed 行 = 回归 `Forward_Log_Writes_Failed_Row_When_Unreachable`（全绿）。`ForwardingViewModelTests`：Store 成功刷新历史/失败原因可见可重试/未选中 no-op |
| AC-07 转发历史 | ✅ | forward_log 查询断言（真实 PG + VM 测试） | `ForwardLog.ForwardLogQueryAsync`：ORDER BY id DESC（时间倒序）、原样字段、take≤0 抛异常。`ForwardLogQueryIntegrationTests`（真实 PG 写 3 行 success/failed/success）：最近 2 条倒序（新 success 在前）、字段与 forward_log 实际值逐一致（含 failed 可读原因「连接被拒绝」断言）、接口 `IForwardLogQuery.RecentAsync` 等价、take≤0 报错。`ForwardingViewModelTests`：历史成功倒序填充/查询失败→可见错误 + 空历史 |
| AC-08 服务启停 | ✅ | 启→C-ECHO→停→连接失败 + 幂等（真实端口集成 + VM 测试） | Core `ScpHost` 封装 DicomServer 组装（幂等建表→存储→编排注入→服务容器）；`StartAsync` 已运行 → 抛 `ScpAlreadyRunningException`；`StopAsync` 未运行 → `InvalidOperationException`。`ScpHostIntegrationTests`（真实随机端口 + fo-dicom 对端，本次全绿）：启动→IsRunning→**C-ECHO Success**→重复启动**抛异常（不双监听）**→停止→**TCP 连接被拒（SocketException）**→可重启再 C-ECHO；未运行停止抛错。`ServiceStatusViewModelTests` 11 项：启动幂等「已在运行」提示（非错误）/停止未运行「服务未运行」提示/启停失败可见/实例数查询失败显示「—」（不臆造 0）/成功清错等 |
| AC-09 领域关卡 | ✅ | 源码全链路抽查 + psql 实测 + 专项测试 | **UID/StudyDate 原样**：Core 全目录 grep `DateTime.` = 0 命中（received_at 为真实时间戳，属合法使用）；`QueryInstancesAsync`/`ForwardLogQueryAsync` 全部 VARCHAR 经 GetString/GetOrNull 原样读；XAML StringFormat 仅用于 ReceivedAt/ForwardedAt（真实时间戳），StudyDate 列直接绑原值；核对用 Ordinal 严格相等。psql 实测 dicom_instance：study_date 全部 8 位 `YYYYMMDD` 原样（min 20250115 / max 20250910）。**缺失不臆造**：psql 实测存在 modality/study_date 双 NULL 行（PAT-MISSING-FIELDS）；测试 `Query_Returns_Raw_Strings_As_Is_and_Missing_Fields_Null`（Modality/StudyDate 断言 null）、`Load_File_Field_Missing_Is_Null_Not_Invented`、`Refresh_CountFailure_Is_Visible_With_Dash_Not_Fabricated_Zero`。**StudyDate 原样显示**：`Load_AllFields_Match` 断言 FileValue=DbValue=`"20250701"`（非 2025-07-01）。**失败必须可见**：5 个 ViewModel 全部 catch 逐一核实均置 StatusText/ErrorText（无空 catch）；ConfigStore 原子写清理失败 LogError；App.xaml.cs 启动失败 MessageBox + 退出码 1；CLI 各失败路径实测可见 |
| AC-10 MVVM 纪律 | ✅ | Wpf.Tests headless + 源码 grep 复核 | 本次独立 grep `System.Windows` 于 `OIS.DicomScp.Wpf/ViewModels/*.cs` = **0 命中**；Commands/ 中 `IAppCommand`/`RelayCommand` 纯 .NET，`System.Windows.Input` 唯一触点为 `AppCommandBridge`（View 层桥接，符合 spec §6.3 设计）；View 代码后置仅 ctor 接 VM + 命令绑定（无业务逻辑，5 个 View 逐一核实）；Core headless（net10.0 无 WPF 依赖）。`OIS.DicomScp.Wpf.Tests` csproj：net10.0-windows 但**未启用 UseWPF**、不打开窗口，48 项（5 个 fake 替身 + 临时目录真实 fo-dicom 文件）headless 跑通（325ms） |

---

## 3. 领域级验证（DOMAIN.md / spec AC-09）

- **UID/StudyDate 全链路原样**：数据层（Npgsql GetString/GetOrNull）→ ViewModel（原样属性绑定，无 StringFormat 改写）→ 核对（Ordinal 严格相等）全程无 DateTime 解析/格式化/大小写/trim 介入 UID 与 StudyDate。✅
- **缺失字段 NULL 不臆造**：DB null → C# null → UI 空显；实例数查询失败显示「—」而非 0；核对单侧缺失不补默认值。✅
- **失败必须可见**：查询/核对/配置保存/C-ECHO/转发/启停全路径实测或测试断言为可见错误文本/Failed 状态；无空 catch、无静默吞。✅
- **转发/核对走既有服务、不重写协议**：C-ECHO/C-STORE 经 task-002 原样 `DicomScuService`（R100 100% 迁移）；核对经 fo-dicom 只读打开原文件；`IRemoteForwarder`/`IMoveScu` 仍为纯占位。✅

## 4. 越界 / 红线检查

- **变更边界**（`git diff develop...HEAD`，74 个文件）：`src/OIS.DicomScp`（C#/WPF/测试/README/sln/csproj，71 个）+ `docs/tasks/003`（spec/review，2 个）+ `.gitignore`（1 个，`/.vs/` 放宽为 `.vs/` 以覆盖 src 子目录——属任务允许范围）。无其他层。✅
- **状态分布**：6 M / 50 A / 16 R100 / 1 R062 / 1 R057 / **0 D**。16 个文件 100% 原样搬家；InstanceIndex（62%）/ForwardLog（57%）仅新增查询/接口实现（已核对源码为纯增量，既有方法零改动）；M 文件逐一核对：.gitignore/sln/两 csproj/README 为结构项，Program.cs 仅配置读取改道 Core（行为不变）。✅
- **Native/(C++)、Interop/**：diff 名称匹配 0 命中。✅
- **out of scope**：无影像渲染/预览、无 C-FIND/C-MOVE 实现（`IMoveAndForwarderPlaceholders` 占位 100% 迁移）、无权限/审计、无 MSIX/ClickOnce 打包。✅
- **测试纪律**：全程未对 develop 做写操作、未 `git push --force`、未改写历史、未 push；仅操作 D:\OIS 与构建工具。✅

## 5. 需 PO 人工验收（无法自动验证，非打回理由）

- 窗口实际渲染、五页签布局/视觉观感/交互手感（XAML 渲染与按钮可用性未经真实窗口验证，本环境无法启动 GUI；UI 逻辑已由 48 项 headless 测试覆盖）；
- 真实 PACS/远程设备互操作（测试以 fo-dicom 捕获宿主与真实 PG 替代）；
- 长时间运行下的内存/句柄行为。

## 6. 非阻塞发现（与 review.md 一致，测试侧复核确认，不影响放行）

1. **`RemoteScpsViewModel.IsConfiguredButBlank` 死代码**（RemoteScpsViewModel.cs L215-216）：`!string.IsNullOrWhiteSpace(v) && v.Trim().Length == 0` 恒为 false，「AET 已配置但纯空白」拒绝分支不可达。已复核：行为上纯空白 AET 由 `ToTarget` 归一为 `""`/`null`，与 task-002 `RemoteScpRepository` 归一语义一致（spec 校验基准即「与 task-002 一致」），不违反 AC-04；建议后续任务删除。
2. **`ServiceStatusViewModel.InitializeAsync` 无 LoadAsync 异常兜底**（ServiceStatusViewModel.cs L80-87 + MainWindow.xaml.cs L42-46）：若 appsettings.json 为非法 JSON，`ConfigStore.LoadAsync` 抛 `InvalidDataException`，经 async-void `OnLoaded` 成为未处理 Dispatcher 异常（弹默认崩溃框）。已复核代码属实；触发条件苛刻（自带配置合法、需人工损坏配置），不在 spec AC 枚举的「查询/核对/配置保存/C-ECHO/转发/启停」操作范围内，不构成本次打回理由；建议后续补 try/catch 或挂 `DispatcherUnhandledException`。
3. **`InstancesViewModel.BlankToNull` 对过滤输入 Trim**：用户输入边界处理（README 已写明「空白=不过滤，trim」），非领域数据转换；存储/显示的 UID/StudyDate 不受影响，记录在案。
4. **WPF TFM**：Wpf 与 Wpf.Tests 为 net10.0-windows（.NET 10 SDK 原生支持，Wpf.Tests 不启用 UseWPF 满足 headless 判据），README 已记录理由，合规。
5. **C-ECHO/C-STORE 对「接受 TCP 但不回应」目标的潜在挂起**：继承自 task-002（`DicomScuService` 100% 未动），不在本任务范围，建议后续统一加超时兜底。

## 7. 测试数据卫生

- 冒烟产生的 forward_log 行（sop_instance_uid=`1.2.999.task003.smoke.unknown`，id=279）已 psql 核实后删除；
- 各集成测试自带 seed/cleanup（T003-* 前缀与 1.2.999.* 独占命名），运行前后库内无遗留本测试数据；
- 未另起 PostgreSQL 库，未改动任何仓库文件（本报告提交前 `git status` 除本报告外干净）。

---

## 结论

**放行。** 75/75 全绿可复现、build 0 错误 0 警告；AC-01~AC-10 逐条满足（可验证项全部有实现 + 测试 + 本次独立复验证据）；领域关卡（UID/StudyDate 全链路原样、缺失不臆造、失败可见、不重写协议）通过；未越界、未触红线。两条非阻塞建议（死代码、InitializeAsync 异常兜底）与 review 一致，留后续任务。XAML 实际渲染、真实 PACS 互操作、长时运行稳定性标注「需 PO 人工验收」。
