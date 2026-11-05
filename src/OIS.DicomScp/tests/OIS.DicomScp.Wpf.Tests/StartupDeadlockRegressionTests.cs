using OIS.DicomScp.Wpf.Composition;
using Xunit;

namespace OIS.DicomScp.Wpf.Tests;

/// <summary>
/// 启动死锁回归测试（bugfix 2026-11-05，真实故障：WPF 主窗口永不显示）。
///
/// 故障机理：App.OnStartup（UI 线程，带 DispatcherSynchronizationContext）同步调用
/// ConsoleServices.Create() → 其中 index.EnsureSchemaAsync().GetAwaiter().GetResult()。
/// 若 EnsureSchemaAsync 内任一 await 的 continuation 被回投到当前被 GetResult() 阻塞的
/// Dispatcher，则 continuation 永远不执行 → 永久死锁（进程活着、无主窗口、无报错）。
///
/// 本测试用「Post/Send 只记录、永不执行」的 SynchronizationContext 模拟被阻塞的 UI 线程，
/// 在带该上下文的线程上跑 ConsoleServices.Create() 并限时等待：
/// 修复前（await 无 ConfigureAwait(false)）→ continuation 被回投到永不执行的上下文 → 超时失败；
/// 修复后（EnsureSchemaAsync 上下文无关 + 组装在线程池线程）→ 限时内正常返回。
/// 注意：该测试需要本机 PostgreSQL（localhost:5432 库 dicom）；PG 不可用时
/// EnsureSchemaAsync 的连接超时（&lt;60s）被 Create() 捕获降级，仍应在限时内完成。
/// </summary>
public sealed class StartupDeadlockRegressionTests
{
    /// <summary>模拟「被 GetResult() 阻塞的 UI 线程」：Post/Send 的工作永不执行（只计数）。</summary>
    private sealed class NeverRunningSynchronizationContext : SynchronizationContext
    {
        private int _posted;

        public int PostedCount => Volatile.Read(ref _posted);

        public override void Post(SendOrPostCallback d, object? state) => Interlocked.Increment(ref _posted);

        public override void Send(SendOrPostCallback d, object? state) => Interlocked.Increment(ref _posted);
    }

    [Fact]
    public void ConsoleServices_Create_Completes_On_Thread_Whose_Context_NeverRunsPostedWork()
    {
        // 在任务线程上安装「永不执行」的同步上下文，再调 Create()——
        // 等价于在带 DispatcherSynchronizationContext 且正被 GetResult() 阻塞的 UI 线程上组装。
        var createTask = Task.Run(() =>
        {
            var ctx = new NeverRunningSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(ctx);
            try
            {
                return (ConsoleServices.Create(), ctx);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(null);
            }
        });

        var started = DateTime.UtcNow;
        bool finished = createTask.Wait(TimeSpan.FromSeconds(60));

        Assert.True(
            finished,
            "ConsoleServices.Create() 在 60 秒内未完成：疑似 UI 线程式同步死锁"
            + "（某 await 的 continuation 被回投到被阻塞的同步上下文，永远无法执行）。"
            + "请检查 OIS.DicomScp.Core 中被 .GetAwaiter().GetResult() 调用的 async 方法是否全部 ConfigureAwait(false)。");

        var (services, ctx) = createTask.Result;
        try
        {
            // 组装成功本身即证明死锁已解除；被回投的工作应为 0（上下文无关执行）。
            Assert.True(
                ctx.PostedCount == 0,
                "有 await continuation 被回投到（模拟的）同步上下文：该路径仍会在真实 UI 线程上死锁，"
                + "对应 async 方法需补 ConfigureAwait(false)");
            Assert.True(
                DateTime.UtcNow - started < TimeSpan.FromSeconds(60),
                "组装耗时异常");
        }
        finally
        {
            services.Dispose();
        }
    }
}
