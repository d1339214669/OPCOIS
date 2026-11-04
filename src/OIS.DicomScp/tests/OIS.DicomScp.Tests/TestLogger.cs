using Microsoft.Extensions.Logging;

namespace OIS.DicomScp.Tests;

/// <summary>
/// 共享无操作日志器（task-003 新测试用；与 task-001/002 测试类内的 TestLogger 行为一致）。
/// </summary>
internal sealed class TestLogger : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => false;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
    }
}
