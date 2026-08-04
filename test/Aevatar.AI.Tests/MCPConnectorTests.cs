using System.IO.Pipelines;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.ToolProviders.MCP;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Foundation.Abstractions.Connectors;
using FluentAssertions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

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
    public async Task ExecuteAsync_WhenAdmittedTerminalFails_ShouldPreserveFailureSemantics()
    {
        var executionPort = new FixedOutcomeExecutionPort(new AgentToolExecutionOutcome(
            AgentToolExecutionOutcomeKind.Failed,
            "{}",
            new AgentToolReceipt
            {
                CallId = "mcp-terminal-failure",
                ToolName = "mcp_echo",
                Status = AgentToolReceiptStatus.Error,
                ResultJson = "{}",
            },
            IsMutation: true,
            FailureCode: "tool_execution_failed",
            SafeMessage: "The admitted MCP terminal failed.",
            AgentToolExecutionFailureStage.TerminalExecution,
            TerminalInvoked: true,
            Retryable: false,
            AuditCompleted: true));
        var connector = CreateConnector(
            defaultTool: "mcp_echo",
            tools: [new FakeAgentTool("mcp_echo", "{}")],
            executionPort: executionPort);

        var response = await connector.ExecuteAsync(CreateRequest(
            stepId: "step-terminal-failure",
            idempotencyKey: "mcp-terminal-failure"));
        var responseJson = JsonSerializer.SerializeToElement(response);

        response.Success.Should().BeFalse();
        response.Error.Should().Be("The admitted MCP terminal failed.");
        responseJson.GetProperty("TerminalInvoked").GetBoolean().Should().BeTrue();
        responseJson.GetProperty("Retryable").GetBoolean().Should().BeFalse();
        executionPort.Requests.Should().ContainSingle();
    }

    [Theory]
    [InlineData(AgentToolReceiptStatus.Unspecified)]
    [InlineData(AgentToolReceiptStatus.Error)]
    [InlineData(AgentToolReceiptStatus.Denied)]
    [InlineData(AgentToolReceiptStatus.AuthorizationRequired)]
    public async Task ExecuteAsync_WhenAdmittedOutcomeReceiptIsNotSuccessful_ShouldFailClosed(
        AgentToolReceiptStatus receiptStatus)
    {
        var executionPort = new FixedOutcomeExecutionPort(new AgentToolExecutionOutcome(
            AgentToolExecutionOutcomeKind.Executed,
            "{}",
            new AgentToolReceipt
            {
                CallId = "mcp-unverified-outcome",
                ToolName = "mcp_echo",
                Status = receiptStatus,
                ResultJson = "{}",
                ErrorCode = "tool_execution_error",
                ErrorMessage = "The MCP tool did not report a verified success.",
            },
            IsMutation: true,
            FailureCode: string.Empty,
            SafeMessage: string.Empty,
            AgentToolExecutionFailureStage.None,
            TerminalInvoked: true,
            Retryable: false,
            AuditCompleted: true));
        var connector = CreateConnector(
            defaultTool: "mcp_echo",
            tools: [new FakeAgentTool("mcp_echo", "{}")],
            executionPort: executionPort);

        var response = await connector.ExecuteAsync(CreateRequest(
            stepId: "step-unverified-outcome",
            idempotencyKey: "mcp-unverified-outcome"));

        response.Success.Should().BeFalse();
        response.Error.Should().Be("The MCP tool did not report a verified success.");
        response.TerminalInvoked.Should().BeTrue();
        response.Retryable.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_WhenMCPProtocolReportsToolError_ShouldFailWithTypedTerminalAudit()
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        using var serverCancellation = new CancellationTokenSource();
        var serverOptions = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "test-mcp", Version = "1.0.0" },
            Handlers = new McpServerHandlers
            {
                CallToolHandler = (_, _) => ValueTask.FromResult(new CallToolResult
                {
                    Content = [new TextContentBlock { Text = "remote tool failed", Type = "text" }],
                    IsError = true,
                }),
            },
        };
        await using var server = McpServer.Create(
            new StreamServerTransport(
                clientToServer.Reader.AsStream(),
                serverToClient.Writer.AsStream(),
                "test-mcp"),
            serverOptions);
        var serverTask = server.RunAsync(serverCancellation.Token);

        try
        {
            await using var client = await McpClient.CreateAsync(
                new StreamClientTransport(
                    clientToServer.Writer.AsStream(),
                    serverToClient.Reader.AsStream()),
                cancellationToken: serverCancellation.Token);
            var adapter = new MCPToolAdapter("mcp_echo", "echo", "{}", client, "test-mcp");
            var auditTrail = new RecordingAuditTrailAppender();
            var executor = new AdmittedAgentToolExecutor(
                AlwaysStartingAgentToolAdmissionLedger.Instance,
                auditTrail,
                new StableIdentityHasher());
            var connector = CreateConnector(
                defaultTool: adapter.Name,
                tools: [adapter],
                executionPort: executor);

            var response = await connector.ExecuteAsync(CreateRequest(
                stepId: "step-mcp-protocol-error",
                idempotencyKey: "mcp-protocol-error"));

            response.Success.Should().BeFalse();
            response.TerminalInvoked.Should().BeTrue();
            response.Retryable.Should().BeFalse();
            auditTrail.Records.Should().Contain(record =>
                record.LifecyclePhase == AuditLifecyclePhase.Terminal &&
                record.Annotations["tool_receipt_status"] == AgentToolReceiptStatus.Error.ToString());
        }
        finally
        {
            serverCancellation.Cancel();
            try
            {
                await serverTask;
            }
            catch (OperationCanceledException) when (serverCancellation.IsCancellationRequested)
            {
            }
        }
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
        executionPort.Requests.Select(static item => item.ExecutionOwner.Kind)
            .Should().OnlyContain(static kind => kind == AgentToolExecutionOwnerKind.Connector);
        executionPort.Requests.Select(static item => item.ExecutionOwner.OwnerId)
            .Should().OnlyContain(static ownerId => ownerId == "mcp-connector");
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

    private sealed class FixedOutcomeExecutionPort(AgentToolExecutionOutcome outcome) : IAgentToolExecutionPort
    {
        public List<AgentToolExecutionRequest> Requests { get; } = [];

        public Task<AgentToolExecutionOutcome> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(outcome);
        }
    }

    private sealed class RecordingAuditTrailAppender : IAuditTrailAppender
    {
        public List<AuditRecord> Records { get; } = [];

        public Task<AuditTrailAppendResult> AppendAsync(
            AuditRecord record,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Records.Add(record);
            return Task.FromResult(AuditTrailAppendResult.Appended(record.AuditId));
        }
    }

    private sealed class StableIdentityHasher : IAuditActorIdentityHasher
    {
        public AuditActorIdentity Hash(string canonicalActorKey) => new("actor-hash", "test-key");

        public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId) => true;
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
