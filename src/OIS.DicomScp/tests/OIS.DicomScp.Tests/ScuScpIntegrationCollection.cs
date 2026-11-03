using Xunit;

namespace OIS.DicomScp.Tests;

/// <summary>
/// 串行化集成测试：SCP 与 SCU 测试共享进程内的 fo-dicom 静态运行时
/// （<c>ScpServiceRuntime.StoreHandler</c>、<c>StorageSopClassRegistry.Configured</c> 及 PG 建表 DDL）。
/// 并发运行会互相覆盖静态状态并触发 PG 目录竞态，故纳入同一 collection 串行执行。
/// </summary>
[CollectionDefinition("ScuScpIntegration", DisableParallelization = true)]
public sealed class ScuScpIntegrationCollection
{
}
