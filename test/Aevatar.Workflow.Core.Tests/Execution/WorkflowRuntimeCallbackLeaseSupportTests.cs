using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Core.Execution;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Tests.Execution;

public sealed class WorkflowRuntimeCallbackLeaseSupportTests
{
    [Fact]
    public async Task TryCancelAsync_LogsWarning_WhenCancellationCleanupFails()
    {
        var logger = new RecordingLogger();
        var lease = new RuntimeCallbackLease(
            ActorId: "workflow-run-1",
            CallbackId: "callback-1",
            Generation: 2,
            Backend: RuntimeCallbackBackend.InMemory);

        await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
            (_, _) => throw new InvalidOperationException("store unavailable"),
            logger,
            lease,
            "test cleanup",
            CancellationToken.None);

        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("test cleanup", StringComparison.Ordinal) &&
            entry.Exception is InvalidOperationException);
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class RecordingLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }
}
