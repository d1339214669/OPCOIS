# Bugfix 记录 — WPF 启动死锁（task-003 交付后缺陷）

- 日期：2026-11-05
- 发现方式：PO 人工验收（WPF 窗口不显示）——正是 task-003 标注的「需 PO 人工验收」项之一，人工验收抓到了 headless 测试覆盖不到的 UI 线程行为缺陷。
- 影响范围：`OIS.DicomScp.Wpf` 应用启动（CLI 与 headless 测试均不受影响，故 75/75 全绿未能拦截）

## 症状

启动 WPF 管理控制台后**主窗口永不显示**：进程存活、任务栏无窗口、无报错弹窗、不退出。
进程内只有 WPF parking 窗口（`HwndWrapper[...]`）与 IME 窗口。

## 根因

经典 **async-over-sync 死锁（UI 线程 + SynchronizationContext 回投）**：

1. `App.OnStartup`（UI 线程，带 `DispatcherSynchronizationContext`）同步调用 `ConsoleServices.Create()`；
2. `ConsoleServices.Create()` 内 `index.EnsureSchemaAsync().GetAwaiter().GetResult()` 在 UI 线程同步阻塞；
3. `InstanceIndex.EnsureSchemaAsync` 中的 `await File.ReadAllTextAsync(...)` / `conn.OpenAsync()` / `cmd.ExecuteNonQueryAsync()` **均无 `ConfigureAwait(false)`**（`ConfigStore` 与 `ScpHost` 有，此方法漏了）；
4. 这些 await 的 continuation 被回投到 Dispatcher 队列，而 UI 线程正卡在 `GetResult()` 中、Dispatcher 永不运转 → **永久死锁**。

CLI（控制台主线程无 SynchronizationContext）与 headless 测试（线程池线程无 Dispatcher）均无此问题，故既有测试全绿未暴露。

## 修复

1. **根因修复**（`OIS.DicomScp.Core/Index/InstanceIndex.cs`）：`EnsureSchemaAsync` 的全部 3 个 await 补 `ConfigureAwait(false)`，该方法变为上下文无关，UI 线程同步等待不再触发回投死锁。
2. **防御加固**（`OIS.DicomScp.Wpf/App.xaml.cs`）：`ConsoleServices.Create()` 改为 `Task.Run(...)` 在线程池线程（无同步上下文）执行，UI 线程只等待结果——即使未来 Core 新增方法漏掉 `ConfigureAwait(false)`，也不会死锁 UI 线程。
3. **回归测试**（`tests/OIS.DicomScp.Wpf.Tests/StartupDeadlockRegressionTests.cs`）：用「Post/Send 只记录永不执行」的 `SynchronizationContext` 模拟被阻塞的 UI 线程，限时（60s）跑 `ConsoleServices.Create()`，并断言无任何 continuation 被回投。修复前该测试超时失败，修复后通过（49/49）。

## 验证

- `dotnet build OIS.DicomScp.sln`：0 错误 0 警告
- `dotnet test OIS.DicomScp.sln`：**76/76 全绿**（Wpf.Tests 49 + OIS.DicomScp.Tests 27）
- 实机启动 `OIS.DicomScp.Wpf.exe`：主窗口正常显示（标题「OIS DICOM 管理控制台（task-003）」），进程稳定存活

## 经验教训（团队备忘）

- **headless 测试无法覆盖 UI 线程 SynchronizationContext 行为**；涉及 UI 线程同步等待异步 API 的组装/启动路径，必须在真实 GUI 会话冒烟（PO 人工验收清单应固定包含「启动后窗口可见」）。
- Core 层 async 方法**统一 `ConfigureAwait(false)`**（与 `ConfigStore`/`ScpHost` 既有风格一致），杜绝上下文回投。
- 遗留改进项不变（SCU 等待无超时等，见 task-002/003 review 与 test-report）。
