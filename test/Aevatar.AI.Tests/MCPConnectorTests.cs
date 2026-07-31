using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.MCP;
using Aevatar.Foundation.Abstractions.Connectors;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class MCPConnectorTests
{
    private const long StableIssuedAtUnixMs = 1_800_000_000_000;

    [Fact]
    public async Task ExecuteAsync_WhenRequestIsAllowed_ShouldReturnToolResult()
    {
        var connector = CreateConnector(
            defaultTool: "tool-a",
            allowedTools: ["tool-a"],
            allowedInputKeys: ["q"],
            tools: [new FakeAgentTool("tool-a", "{}")]);

        var response = await connector.ExecuteAsync(CreateRequest(
            stepId: "step-success",
            idempotencyKey: "mcp-success",
            payload: "{\"q\":\"v\"}"));

        response.Success.Should().BeTrue();
        response.Metadata.Should().ContainKey("connector.mcp.tool").WhoseValue.Should().Be("tool-a");
    }

    [Fact]
    public async Task ExecuteAsync_WhenInputViolatesSchema_ShouldReturnError()
    {
        var connector = CreateConnector(
            defaultTool: "tool-a",
            allowedTools: ["tool-a"],
            allowedInputKeys: ["q"],
            tools: [new FakeAgentTool("tool-a", "{}")]);

        var response = await connector.ExecuteAsync(CreateRequest(
            stepId: "step-schema-rejected",
            idempotencyKey: "mcp-schema-rejected",
            payload: "{\"x\":1}"));

        response.Success.Should().BeFalse();
        response.Error.Should().Contain("schema violation");
    }

    [Fact]
    public async Task ExecuteAsync_WhenToolIsNotAllowlisted_ShouldReturnError()
    {
        var connector = CreateConnector(
            defaultTool: "tool-a",
            allowedTools: ["tool-a"],
            allowedInputKeys: ["q"],
            tools: [new FakeAgentTool("tool-a", "{}")]);

        var response = await connector.ExecuteAsync(CreateRequest(
            stepId: "step-allowlist-rejected",
            idempotencyKey: "mcp-allowlist-rejected",
            payload: "{\"q\":\"v\"}",
            operation: "tool-b"));

        response.Success.Should().BeFalse();
        response.Error.Should().Contain("not allowlisted");
    }

    [Fact]
    public async Task ExecuteAsync_WhenToolWasNotDiscovered_ShouldReturnError()
    {
        var connector = CreateConnector(
            allowedTools: [],
            allowedInputKeys: [],
            tools: []);

        var response = await connector.ExecuteAsync(CreateRequest(
            stepId: "step-not-discovered",
            idempotencyKey: "mcp-not-discovered",
            operation: "unknown-tool"));

        response.Success.Should().BeFalse();
        response.Error.Should().Contain("was not discovered");
    }

    [Fact]
    public async Task ExecuteAsync_WhenToolThrows_ShouldReturnSafeConnectorError()
    {
        var connector = CreateConnector(
            defaultTool: "tool-x",
            tools: [new ThrowingAgentTool("tool-x")]);

        var response = await connector.ExecuteAsync(CreateRequest(
            stepId: "step-tool-error",
            idempotencyKey: "mcp-tool-error"));

        response.Success.Should().BeFalse();
        response.Metadata.Should().ContainKey("connector.mcp.server").WhoseValue.Should().Be("srv");
    }

    [Fact]
    public async Task ExecuteAsync_ConcurrentFirstUse_ShouldConnectAndDiscoverOnce()
    {
        using var discovery = new BlockingDiscoveryPort(new FakeAgentTool("mcp_echo", """{"ok":true}"""));
        var connector = CreateConnector(
            defaultTool: "mcp_echo",
            discoveryPort: discovery);
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;

        var tasks = Enumerable.Range(0, 32)
            .Select(callIndex => Task.Run(async () =>
            {
                if (Interlocked.Increment(ref readyCount) == 32)
                    ready.TrySetResult(true);

                await start.Task;
                return await connector.ExecuteAsync(CreateRequest(
                    stepId: "step-concurrent",
                    idempotencyKey: $"mcp-concurrent-{callIndex}"));
            }))
            .ToArray();

        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        start.SetResult(true);
        await discovery.WaitForFirstDiscoveryAsync();
        discovery.Release();

        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));

        discovery.ConnectAndDiscoverCalls.Should().Be(1);
        results.Should().OnlyContain(result => result.Success && result.Output == """{"ok":true}""");
    }

    [Fact]
    public async Task ExecuteAsync_WhenLogicalCallIsRedelivered_ShouldReuseTypedWorkflowIdentity()
    {
        var executionPort = new RecordingExecutionPort();
        var connector = CreateConnector(
            defaultTool: "mcp_echo",
            tools: [new FakeAgentTool("mcp_echo", "{}")],
            executionPort: executionPort);
        var request = CreateRequest("step-alpha", "side-effect-alpha");

        await connector.ExecuteAsync(request);
        await connector.ExecuteAsync(request);
        await connector.ExecuteAsync(CreateRequest("step-beta", request.IdempotencyKey));

        executionPort.Requests.Should().HaveCount(3);
        executionPort.Requests[0].ExecutionContext.Request
            .Should().BeEquivalentTo(executionPort.Requests[1].ExecutionContext.Request);
        executionPort.Requests[0].ExecutionContext.Request.RequestId
            .Should().NotBe(executionPort.Requests[2].ExecutionContext.Request.RequestId);
        executionPort.Requests[0].ExecutionContext.Request.CallId.Should().Be("side-effect-alpha");
    }

    [Theory]
    [InlineData("run")]
    [InlineData("step")]
    [InlineData("idempotency")]
    [InlineData("issued")]
    public async Task ExecuteAsync_WhenStableWorkflowIdentityIsMissing_ShouldFailBeforeExecution(
        string missingIdentity)
    {
        var executionPort = new RecordingExecutionPort();
        var connector = CreateConnector(
            defaultTool: "mcp_echo",
            tools: [new FakeAgentTool("mcp_echo", "{}")],
            executionPort: executionPort);
        var request = CreateRequest(
            stepId: missingIdentity == "step" ? " " : "step-alpha",
            idempotencyKey: missingIdentity == "idempotency" ? " " : "side-effect-alpha",
            runId: missingIdentity == "run" ? " " : "run-core",
            issuedAtUnixMs: missingIdentity == "issued" ? 0 : StableIssuedAtUnixMs);

        var response = await connector.ExecuteAsync(request);

        response.Success.Should().BeFalse();
        response.Error.Should().Contain("stable");
        executionPort.Requests.Should().BeEmpty();
    }

    private static MCPConnector CreateConnector(
        string? defaultTool = null,
        IReadOnlyList<string>? allowedTools = null,
        IReadOnlyList<string>? allowedInputKeys = null,
        IReadOnlyList<IAgentTool>? tools = null,
        IMCPToolDiscoveryPort? discoveryPort = null,
        IAgentToolExecutionPort? executionPort = null) =>
        new(
            name: "mcp-connector",
            serverConfig: new MCPServerConfig { Name = "srv", Command = "cmd" },
            defaultTool: defaultTool,
            allowedTools: allowedTools,
            allowedInputKeys: allowedInputKeys,
            clientManager: discoveryPort ?? new StaticDiscoveryPort(tools?.ToArray() ?? []),
            toolExecutionPort: executionPort ?? TestAgentToolExecutionPort.Instance);

    private static ConnectorRequest CreateRequest(
        string stepId,
        string idempotencyKey,
        string payload = "{}",
        string operation = "",
        string runId = "run-core",
        long issuedAtUnixMs = StableIssuedAtUnixMs) =>
        new()
        {
            RunId = runId,
            StepId = stepId,
            IdempotencyKey = idempotencyKey,
            IssuedAtUnixMs = issuedAtUnixMs,
            Operation = operation,
            Payload = payload,
        };

    private sealed class StaticDiscoveryPort(params IAgentTool[] tools) : IMCPToolDiscoveryPort
    {
        public Task<IReadOnlyList<IAgentTool>> ConnectAndDiscoverAsync(
            MCPServerConfig config,
            CancellationToken ct = default)
        {
            _ = config;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<IAgentTool>>(tools);
        }
    }

    private sealed class BlockingDiscoveryPort(params IAgentTool[] tools) : IMCPToolDiscoveryPort, IDisposable
    {
        private readonly TaskCompletionSource<bool> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _connectAndDiscoverCalls;

        public int ConnectAndDiscoverCalls => Volatile.Read(ref _connectAndDiscoverCalls);

        public async Task<IReadOnlyList<IAgentTool>> ConnectAndDiscoverAsync(
            MCPServerConfig config,
            CancellationToken ct = default)
        {
            _ = config;
            Interlocked.Increment(ref _connectAndDiscoverCalls);
            _entered.TrySetResult(true);
            await _release.Task.WaitAsync(ct);
            return tools;
        }

        public Task WaitForFirstDiscoveryAsync() =>
            _entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void Release() => _release.SetResult(true);

        public void Dispose()
        {
        }
    }

    private sealed class RecordingExecutionPort : IAgentToolExecutionPort
    {
        public List<AgentToolExecutionRequest> Requests { get; } = [];

        public Task<AgentToolExecutionOutcome> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(new AgentToolExecutionOutcome(
                AgentToolExecutionOutcomeKind.Executed,
                "{}",
                new AgentToolReceipt
                {
                    CallId = request.ExecutionContext.Request.CallId ?? string.Empty,
                    ToolName = request.Tool.Name,
                    Status = AgentToolReceiptStatus.Success,
                    ResultJson = "{}",
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

    private sealed class FakeAgentTool(string name, string resultJson) : IAgentTool
    {
        public string Name { get; } = name;
        public string Description => "fake";
        public string ParametersSchema => "{}";

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            _ = argumentsJson;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(resultJson);
        }
    }

    private sealed class ThrowingAgentTool(string name) : IAgentTool
    {
        public string Name { get; } = name;
        public string Description => Name;
        public string ParametersSchema => "{}";

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            throw new InvalidOperationException("tool failed");
    }
}
