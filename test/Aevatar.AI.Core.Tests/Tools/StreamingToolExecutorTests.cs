using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Tools;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.Core.Tests.Tools;

public sealed class StreamingToolExecutorTests
{
    [Fact]
    public async Task GetRemainingResultsAsync_ShouldPassStableExecutionIdentitiesToExecutionPort()
    {
        var tools = new ToolManager();
        tools.Register(new FakeAgentTool("echo"));
        var executionPort = new RecordingExecutionPort();
        var executor = new StreamingToolExecutor(
            tools,
            toolContext: AgentToolExecutionContext.Empty with
            {
                Request = new AgentToolRequestIdentity("session-1", null),
                ExecutionOwner = AgentToolExecutionOwners.Actor("role-agent-1"),
            },
            toolExecutionPort: executionPort);
        using var state = executor.CreateExecutionState();

        var prepared = await executor.PrepareBatchAsync(
            "session-1",
            round: 0,
            [new ToolCall
            {
                Id = "call-1",
                Name = "echo",
                ArgumentsJson = "{}",
            }]);
        executor.AddTool(state, prepared.Single());

        var results = new List<ToolExecutionResult>();
        await foreach (var toolResult in executor.GetRemainingResultsAsync(state, CancellationToken.None))
            results.Add(toolResult);

        results.Should().ContainSingle().Which.IsError.Should().BeFalse();
        var request = executionPort.Requests.Should().ContainSingle().Subject;
        request.Tool.Name.Should().Be("echo");
        request.ExecutionContext.Request.RequestId.Should().Be("session-1");
        request.ExecutionContext.Request.CallId.Should().Be("call-1");
        request.ExecutionOwner.Kind.Should().Be(AgentToolExecutionOwnerKind.Actor);
        request.ExecutionOwner.OwnerId.Should().Be("role-agent-1");
    }

    [Fact]
    public async Task GetRemainingResultsAsync_WhenExecutionPortThrows_LogsOriginalFailureAndReturnsSafeError()
    {
        var tools = new ToolManager();
        tools.Register(new FakeAgentTool("echo"));
        var logger = new CapturingLogger();
        var executor = new StreamingToolExecutor(
            tools,
            toolExecutionPort: new ThrowingExecutionPort(),
            logger: logger);
        using var state = executor.CreateExecutionState();

        var prepared = await executor.PrepareBatchAsync(
            "session-failed-finalization",
            round: 0,
            [new ToolCall
            {
                Id = "call-failed-finalization",
                Name = "echo",
                ArgumentsJson = "{}",
            }]);
        executor.AddTool(state, prepared.Single());

        var results = new List<ToolExecutionResult>();
        await foreach (var toolResult in executor.GetRemainingResultsAsync(state, CancellationToken.None))
            results.Add(toolResult);

        results.Should().ContainSingle();
        var failure = results.Single();
        failure.IsError.Should().BeTrue();
        failure.Result.Should().Be("{\"error\":\"The tool request failed.\"}");
        failure.Receipt.Should().NotBeNull();
        failure.Receipt!.ErrorMessage.Should().Be("The tool request failed.");

        logger.Entries.Should().ContainSingle(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Exception is InvalidOperationException &&
            entry.Exception.Message == "execution port failed" &&
            entry.Message.Contains("Tool execution failed before receipt finalization for tool echo and call call-failed-finalization"));
    }

    private sealed class RecordingExecutionPort : IAgentToolExecutionPort
    {
        public List<AgentToolExecutionRequest> Requests { get; } = [];

        public Task<AgentToolExecutionOutcome> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            var resultJson = "{\"ok\":true}";
            return Task.FromResult(new AgentToolExecutionOutcome(
                AgentToolExecutionOutcomeKind.Executed,
                resultJson,
                new AgentToolReceipt
                {
                    CallId = request.ExecutionContext.Request.CallId ?? string.Empty,
                    ToolName = request.Tool.Name,
                    Status = AgentToolReceiptStatus.Success,
                    ResultJson = resultJson,
                },
                IsMutation: false,
                FailureCode: string.Empty,
                SafeMessage: string.Empty,
                AgentToolExecutionFailureStage.None,
                TerminalInvoked: true,
                Retryable: false,
                AuditCompleted: true));
        }
    }

    private sealed class ThrowingExecutionPort : IAgentToolExecutionPort
    {
        public Task<AgentToolExecutionOutcome> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("execution port failed");
    }

    private sealed class FakeAgentTool(string name) : IAgentTool
    {
        public string Name { get; } = name;
        public string Description => "fake";
        public string ParametersSchema => "{}";
        public ToolApprovalMode ApprovalMode => ToolApprovalMode.NeverRequire;
        public bool IsReadOnly => true;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult("{\"ok\":true}");
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull =>
            NullScope.Instance;

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

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
