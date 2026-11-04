# task-003 Spec — WPF 管理控制台（UI 层）

- 任务编号：`task-003`
- 分支名：`feature/task-003-wpf-console`
- 角色：dev-req（需求 Agent）
- 状态：待 PO 确认（确认后 dev-dev 接手实现）
- 技术栈：WPF（net10.0-windows）+ 复用 OIS.DicomScp 服务层（C# / .NET 10、fo-dicom 5.2.6、Npgsql/PostgreSQL）
- 授权说明：TEAM.md §5 试点红线"只碰 C#/Infrastructure 层，不动 UI/(WPF)"的例外条件（"除非 PO 明确要求"）已满足——PO 明确授权本任务实现 WPF UI。

---

## 1. 背景

OIS 已有两个子任务交付：

- **task-001**：DICOM Storage SCP 核心存储层（fo-dicom 接收 C-ECHO/C-STORE、Orthanc 式匿名化落盘、PostgreSQL `dicom_instance` 索引、配置化）。
- **task-002**：SCU 出站转发（可配置远程 SCP 列表 `Scu:RemoteScps`、C-ECHO 连通性验证、C-STORE 无损转发、`forward_log` 日志表）。

当前操作方式全部是命令行（`OIS.DicomScp.exe scp / echo-scu / store-scu`）+ 手工编辑 `appsettings.json`。PO 需要一个 **WPF 桌面管理控制台**，让运维/PO 不碰命令行和 JSON 就能完成：实例浏览与核对、远程 SCP 配置管理、连通性测试、转发操作、服务启停与状态。

## 2. 目标

1. 新建 WPF 管理控制台 `OIS.DicomScp.Wpf`，复用既有服务层（**不重写** DICOM 逻辑）。
2. 为复用而做一次结构微调：把 Scp/Storage/Index/Scu/配置 抽为类库 `OIS.DicomScp.Core`，控制台 host（`OIS.DicomScp`）与 WPF 都引用它。
3. MVVM 分层：View 无业务逻辑，逻辑全部在 ViewModel/服务层，**ViewModel 不依赖 System.Windows（可 headless 测试）**。
4. 六大功能：实例浏览、元数据详情与一致性核对、远程 SCP 配置 CRUD、C-ECHO 连通性测试、C-STORE 转发 + 转发历史、本地 SCP 服务启停与状态。

## 3. 领域术语（与 CONTEXT.md / DOMAIN.md 一致）

| 术语 | 含义 |
|---|---|
| 实例（Instance） | 一个 SOP Instance；UI 列表即 `dicom_instance` 一行 |
| 内部 ID | 系统生成的文件标识（主键），转发定位可用它或 SOPInstanceUID |
| 远程 SCP | `Scu:RemoteScps` 配置项（Name/host/port/called AE/calling AE） |
| C-ECHO / C-STORE | 复用 task-002 的 SCU 能力；UI 只触发，不实现协议 |
| 转发记录 | `forward_log` 一行（status/target/实例/时间/原因） |

## 4. 验收标准（AC，逐条可验证）

> 标记约定：`[自动]` 可用测试断言核对；`[手动]` 需 PO 在 WPF 窗口上操作验证；纯视觉/交互手感项标注「需 PO 人工验收」。
> 环境：PostgreSQL localhost:5432（库 dicom），测试对端 SCP 用 fo-dicom 捕获宿主（task-001/002 已有 CapturingScpHost 模式）。

### AC-01 项目结构与控制台回归 `[自动]`
- 新建类库 `OIS.DicomScp.Core`（net10.0），包含既有 Scp/Storage/Index/Scu/Options/配置读写；`OIS.DicomScp`（控制台 host）改为引用 Core，CLI 子命令（scp/echo-scu/store-scu）行为不变。
- 新建 `OIS.DicomScp.Wpf`（net10.0-windows，UseWPF=true），引用 Core。
- 判据：`dotnet build OIS.DicomScp.sln` 0 错误；既有 14 个集成测试全绿（回归）；`OIS.DicomScp.exe echo-scu` 等 CLI 冒烟行为与 task-002 一致。

### AC-02 实例浏览（查询 + 过滤）`[自动]`
- Core 增加只读查询 API（如 `IInstanceQuery.QueryAsync(filter, take)`）：支持按 PatientID / StudyInstanceUID / Modality / received_at 时间范围过滤，按 received_at 倒序，返回 `dicom_instance` 行（全部 task-001 已定义字段）。
- `InstancesViewModel` 暴露查询结果集合与过滤条件；查询失败（PG 不可用）时显示明确错误，不静默空列表。
- 判据：预置 N 行 dicom_instance 后，各过滤条件返回正确行集（断言行数与字段值）；过滤无结果返回空集合（合法，非错误）。

### AC-03 元数据详情与一致性核对 `[自动]`
- 选中实例时，ViewModel 用 fo-dicom 读 `<StorageDirectory>/<file_path>` 原文件，与库中记录比对关键标签：SOPInstanceUID、PatientID、StudyInstanceUID、Modality、StudyDate。
- 输出逐字段 一致/不一致 报告；文件缺失/读失败 → 明确错误状态（不得静默）。
- 判据：对已存实例执行核对，全部字段一致 → `Consistent`；人为把库中某行 StudyDate 改错 → 报出 StudyDate 不一致。

### AC-04 远程 SCP 配置管理（CRUD + 校验 + 持久化）`[自动]`
- `RemoteScpsViewModel` 支持增/改/删/列表 `Scu:RemoteScps`；保存写回 appsettings.json（保持既有 JSON 结构：`Scu.CallingAeTitle` 与 `Scu.RemoteScps[]`）。
- 校验规则（与 task-002 RemoteScpRepository 一致）：Name 非空且唯一；Host 非空；Port 1–65535；CalledAe/CallingAe 非空（若配置）；校验失败拒绝保存并给出可读原因。
- 判据：新增/修改/删除后重读配置文件，内容与操作一致（round-trip）；重复 Name 保存被拒且文件未被写坏；保存后 task-002 的 `RemoteScpRepository` 重新加载成功（格式兼容）。

### AC-05 连通性测试（C-ECHO）`[自动]`
- 选中远程 SCP 目标，ViewModel 调 `IDicomScuService.EchoAsync(target)`，显示 `Success`/`Failed` + 原因。
- 判据：对可达测试 SCP（fo-dicom 捕获宿主）返回 Success；对不可达目标（关闭端口/坏 host）返回 Failed + 可读原因，UI 不崩溃，可继续对下一目标测试。

### AC-06 转发操作（C-STORE）`[自动]`
- 选中实例（内部 ID 或 SOPInstanceUID）+ 目标 SCP → ViewModel 调 `IDicomScuService.StoreBySopInstanceUidAsync`（或按内部 ID），显示结果（Success/Failed + 原因）。
- 判据：对测试捕获 SCP 转发成功（远端收到，SOPInstanceUID 一致）；`forward_log` 新增 1 行 success；对未知实例转发 → Failed"未找到"且无转发副作用；转发期间目标不可达 → Failed + 原因，`forward_log` 有 failed 行。

### AC-07 转发历史 `[自动]`
- Core 增加 `forward_log` 只读查询 API（最近 N 条，倒序）；`ForwardingViewModel` 展示：时间、目标 Name、实例定位（internal_id/SOPInstanceUID）、status、失败原因。
- 判据：AC-06 产生记录后，历史列表包含对应行，字段与 forward_log 实际值一致。

### AC-08 服务状态与启停 `[自动]`
- `ServiceStatusViewModel` 可启动/停止本地 DICOM SCP（复用 Core 的 `DicomServer` 组装逻辑），显示：监听端口、AET、是否运行中、已存实例数（count dicom_instance）。
- 启动需幂等：已运行时再启动 → 明确提示"已运行"，不重复监听不崩；停止后监听释放。
- 判据：启动后对 127.0.0.1:配置端口 C-ECHO = Success；停止后连接失败/拒绝；重复启动不报错不双监听。

### AC-09 领域关卡（DOMAIN.md）`[自动]`
- UI 显示与数据流保持：StudyDate 一律 `YYYYMMDD` 原样（不做时区/日期转换显示）；UID 字符串严格原样（无 trim/大小写/空格篡改）；缺失字段显示为空，**不臆造默认值**；所有操作失败（查询/核对/配置保存/C-ECHO/转发/启停）必须可见（错误消息或 Failed 状态），禁止静默吞。
- 判据：构造缺失 Modality/StudyDate 的实例入库，UI 显示为空；构造 StudyDate=20250701 实例，UI 显示 `20250701` 非 `2025-07-01`。

### AC-10 可测性边界（MVVM 纪律）`[自动]`
- `OIS.DicomScp.Wpf` 的 ViewModel 层不引用 `System.Windows`/`System.Windows.Controls`（无 WPF 类型进 ViewModel）；命令/绑定用轻量 ICommand 抽象；Core 保持 headless。
- 判据：新增 `OIS.DicomScp.Wpf.Tests`（net10.0，xunit）在 headless 环境驱动 ViewModel 逻辑（用 fake 仓储/服务）断言 AC-02~AC-09 的逻辑分支；`grep System.Windows` 于 ViewModel 源码应无命中。

### 需 PO 人工验收（无法自动验证）
- 窗口布局、视觉观感、交互手感（列表流畅度、按钮可用性）；
- 真实 PACS/远程设备互操作（测试以 fo-dicom 捕获宿主与 task-001 SCP 替代）；
- 长时间运行下的内存/句柄行为。

## 5. 不做的事（Out of Scope，留后续 task）

- DICOM 影像渲染/预览（像素级显示）。
- 患者/病例/计划等 OIS 业务对象建模（本任务只是 DICOM 管理控制台）。
- C-FIND / C-MOVE 拉取（task-002 已留 `IMoveScu` 占位，后续启用）。
- 多用户/权限/审计。
- 非 Windows 平台支持（WPF 仅 Windows）。
- 生产部署打包（MSIX/ClickOnce/服务化）。
- 修改 Native/(C++)、Interop/ 生成文件（红线）。
- 修改 task-001/002 已验收的 DICOM 协议行为（只允许为复用做结构抽取，行为不变——AC-01 回归保证）。

## 6. 接口定义

### 6.1 结构抽取（AC-01）

```
OIS.DicomScp.Core（net10.0，classlib）
├─ Scp/      DicomScpService、ScpServiceRuntime、StorageSopClassRegistry（原样迁入）
├─ Storage/  FileStorage
├─ Index/    InstanceIndex（+ 新增 QueryInstancesAsync）、InstanceMetadata、Schema.sql
├─ Scu/      IDicomScuService/DicomScuService、RemoteScpRepository、ForwardLog
│            （+ 新增 ForwardLogQueryAsync）、占位接口
├─ Options.cs、ConfigStore（appsettings 读取+写回，原子写：临时文件+rename）
└─ IScpHost（启动/停止本地 DicomServer 的封装，供 WPF 与 CLI 共用）
```

- `OIS.DicomScp`（控制台 host）：只保留 Program.cs CLI 分发，引用 Core；CLI 行为不变。
- `OIS.DicomScp.Wpf`：引用 Core；不引用控制台 host。

### 6.2 新增 Core API（示意签名）

```csharp
IInstanceQuery {
  Task<IReadOnlyList<InstanceRow>> QueryAsync(
      string? patientId, string? studyInstanceUid, string? modality,
      DateTime? fromUtc, DateTime? toUtc, int take, CancellationToken ct);
  Task<int> CountAsync(CancellationToken ct);
}
IForwardLogQuery {
  Task<IReadOnlyList<ForwardLogRow>> RecentAsync(int take, CancellationToken ct);
}
IScpHost {
  Task StartAsync(CancellationToken ct);      // 幂等：已运行 → 抛 ScpAlreadyRunningException 或返回
  Task StopAsync();
  bool IsRunning { get; }
  int Port { get; }
}
IConfigStore {
  Task<AppConfig> LoadAsync();
  Task SaveAsync(AppConfig cfg);              // 原子写 appsettings.json
}
```

### 6.3 WPF 结构（MVVM）

```
OIS.DicomScp.Wpf（net10.0-windows，UseWPF）
├─ App.xaml / MainWindow.xaml（页签：实例 / 详情 / 远程SCP / 转发 / 服务）
├─ Views/       各页签 View（纯绑定，无逻辑）
├─ ViewModels/  InstancesViewModel、InstanceDetailViewModel、
│               RemoteScpsViewModel、ForwardingViewModel、ServiceStatusViewModel
├─ Commands/    RelayCommand（ICommand 实现，headless）
└─ （ViewModel 禁止 System.Windows 引用；依赖注入用简单容器或 ctor 注入）
```

- 线程：DICOM/DB 调用在后台 Task，结果经 `IProgress`/事件回到 UI 线程；UI 线程不阻塞。
- 配置路径：WPF 默认使用与 Core 相同的 appsettings.json 位置约定（可配相对/绝对路径），保存时走 `IConfigStore.SaveAsync`（原子写）。

## 7. 测试策略

| 层 | 测试 |
|---|---|
| Core 回归 | task-001/002 既有 14 个测试全绿（AC-01） |
| Core 新 API | 查询/forward_log 查询/配置写回/ScpHost 启停 集成测试（对真实 PG + fo-dicom 对端） |
| WPF ViewModel | 新增 `OIS.DicomScp.Wpf.Tests`（headless xunit），fake 仓储/服务驱动 AC-02~AC-09 逻辑 |
| 需 PO 人工验收 | 窗口观感、真实 PACS 互操作、长时运行稳定性 |

## 8. 给 dev-dev 的特别注意事项

1. **结构抽取行为不变**：Core 抽取是搬家不是重写——task-001/002 的 14 个测试必须原样全绿；CLI 冒烟（echo-scu 对测试 SCP）必须通过后才算 AC-01 完成。
2. **配置写回保持格式兼容**：`RemoteScps` 的 JSON 字段名/嵌套结构不得变（task-002 的 RemoteScpRepository 要能继续读）；写回用"读-改-原子写"，不得丢其他配置节（Scp/Storage/Postgres）。
3. **fo-dicom 5.2.6 的 WPF 线程注意**：`DicomServer`/`DicomClient` 的回调不保证 UI 线程，ViewModel 侧统一 marshal（`Dispatcher`/同步上下文）；ViewModel 本体仍保持 headless（不直接引 System.Windows）。
4. **StudyDate/UID 显示纪律**：数据层→显示层全链路原样字符串；禁止任何 DateTime 解析/格式化介入 UID/日期。
5. **失败必须可见**：每个 ViewModel 操作有明确的 Result 状态（Success/Failed/原因 + 日志），UI 绑定显示；禁止空 catch。
6. **WPF 项目目标框架**：`net10.0-windows`（机器 .NET 10 SDK）；若 WPF 在 net10 有已知问题，降为 `net8.0-windows` 并记录（Core 仍 net10.0，WPF 可引用）。
7. 试点阶段红线不变：不动 Native/(C++)、Interop/；只碰 C# 层。

## 9. 验收矩阵（供 dev-test 勾选）

| AC | 可自动断言 | 验证手段 |
|---|---|---|
| AC-01 结构+回归 | ✅ | build + 既有 14 测试 + CLI 冒烟 |
| AC-02 实例浏览 | ✅ | 预置数据 + 查询断言 |
| AC-03 一致性核对 | ✅ | 正常实例 + 改错库值两种场景 |
| AC-04 配置 CRUD | ✅ | round-trip + 校验拒绝 + 格式兼容 |
| AC-05 C-ECHO | ✅ | 可达/不可达两场景 |
| AC-06 C-STORE 转发 | ✅ | 捕获 SCP + forward_log + 未知实例 |
| AC-07 转发历史 | ✅ | forward_log 查询断言 |
| AC-08 服务启停 | ✅ | 启→C-ECHO→停→连接失败 + 幂等 |
| AC-09 领域关卡 | ✅ | 缺失字段空显 + StudyDate 原样 |
| AC-10 MVVM 纪律 | ✅ | Wpf.Tests headless + 源码检查 |
