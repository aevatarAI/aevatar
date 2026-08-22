using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Application.Responses;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class LlmRunCoreTests
{
    [Fact]
    public async Task RunAsync_ShouldRecordStreamingCompletionThroughSink()
    {
        var provider = new ScriptedLlmProviderFactory([
            [
                new LLMStreamChunk { DeltaContent = "hello " },
                new LLMStreamChunk
                {
                    DeltaContent = "world",
                    Usage = new TokenUsage(3, 2, 5),
                    IsLast = true,
                },
            ],
        ]);
        var sink = new RecordingLlmRunSink();
        var core = new LlmRunCore(provider, [], TestToolSetRegistry.Empty, NullLogger<LlmRunCore>.Instance, new RecordingAgentToolExecutionPort());

        await core.RunAsync(
            new LlmRunCoreRequest(BuildRunRequest("resp_1"), "run_1", "ApiKey"),
            sink);

        sink.StreamChunks.Should().HaveCount(2);
        sink.Completed.Should().ContainSingle();
        var completed = sink.Completed[0];
        completed.ResponseId.Should().Be("resp_1");
        completed.RunId.Should().Be("run_1");
        completed.OutputText.Should().Be("hello world");
        completed.Usage!.TotalTokens.Should().Be(5);
        provider.Requests.Should().ContainSingle()
            .Which.CallerContext!.ResponseId.Should().Be("resp_1");
    }

    [Fact]
    public async Task RunAsync_ShouldPreserveCatalogServiceRouteTargetForProvider()
    {
        var provider = new ScriptedLlmProviderFactory([
            [new LLMStreamChunk { DeltaContent = "ok", IsLast = true }],
        ]);
        var command = BuildRunRequest("resp_catalog");
        command.RouteTarget = new LLMRouteTarget
        {
            CatalogServiceId = "catalog-alpha",
            ServiceSlugSnapshot = "route-alpha",
        };
        var core = new LlmRunCore(
            provider,
            [],
            TestToolSetRegistry.Empty,
            NullLogger<LlmRunCore>.Instance,
            new RecordingAgentToolExecutionPort());

        await core.RunAsync(
            new LlmRunCoreRequest(command, "run_catalog", "ApiKey"),
            new RecordingLlmRunSink());

        var recordedTarget = provider.Requests.Should().ContainSingle().Subject.RouteTarget;
        recordedTarget.Should().NotBeNull().And.NotBeSameAs(command.RouteTarget);
        recordedTarget!.SourceIdentityCase.Should().Be(
            LLMRouteTarget.SourceIdentityOneofCase.CatalogServiceId);
        recordedTarget.CatalogServiceId.Should().Be("catalog-alpha");
        recordedTarget.ServiceSlugSnapshot.Should().Be("route-alpha");
    }

    [Fact]
    public async Task RunAsync_ShouldPreserveUserServiceRouteTargetForProvider()
    {
        var provider = new ScriptedLlmProviderFactory([
            [new LLMStreamChunk { DeltaContent = "ok", IsLast = true }],
        ]);
        var command = BuildRunRequest("resp_user");
        command.RouteTarget = new LLMRouteTarget
        {
            UserServiceId = "us-alpha",
            ServiceSlugSnapshot = "route-alpha",
        };
        var core = new LlmRunCore(
            provider,
            [],
            TestToolSetRegistry.Empty,
            NullLogger<LlmRunCore>.Instance,
            new RecordingAgentToolExecutionPort());

        await core.RunAsync(
            new LlmRunCoreRequest(command, "run_user", "ApiKey"),
            new RecordingLlmRunSink());

        var recordedTarget = provider.Requests.Should().ContainSingle().Subject.RouteTarget;
        recordedTarget.Should().NotBeNull().And.NotBeSameAs(command.RouteTarget);
        recordedTarget!.SourceIdentityCase.Should().Be(
            LLMRouteTarget.SourceIdentityOneofCase.UserServiceId);
        recordedTarget.UserServiceId.Should().Be("us-alpha");
        recordedTarget.ServiceSlugSnapshot.Should().Be("route-alpha");
    }

    [Fact]
    public async Task RunAsync_ShouldPreserveWhitespaceOnlyStreamChunks()
    {
        // Whitespace-only chunks ("\n", "    ") carry the newline + indentation of
        // code/YAML output; dropping them corrupts any structured text the model emits.
        var provider = new ScriptedLlmProviderFactory([
            [
                new LLMStreamChunk { DeltaContent = "def add(a, b):" },
                new LLMStreamChunk { DeltaContent = "\n" },
                new LLMStreamChunk { DeltaContent = "    " },
                new LLMStreamChunk { DeltaContent = "return a + b", IsLast = true },
            ],
        ]);
        var sink = new RecordingLlmRunSink();
        var core = new LlmRunCore(provider, [], TestToolSetRegistry.Empty, NullLogger<LlmRunCore>.Instance, new RecordingAgentToolExecutionPort());

        await core.RunAsync(
            new LlmRunCoreRequest(BuildRunRequest("resp_ws"), "run_1", "ApiKey"),
            sink);

        sink.StreamChunks.Should().HaveCount(4);
        sink.StreamChunks[1].DeltaText.Should().Be("\n");
        sink.StreamChunks[2].DeltaText.Should().Be("    ");
        sink.Completed.Should().ContainSingle()
            .Which.OutputText.Should().Be("def add(a, b):\n    return a + b");
    }

    [Fact]
    public async Task RunAsync_ShouldEmitForwardedToolCallAndComplete()
    {
        var provider = new ScriptedLlmProviderFactory([
            [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call_1",
                        Name = "get_weather",
                        ArgumentsJson = """{"city":""",
                    },
                },
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call_1",
                        Name = "get_weather",
                        ArgumentsJson = "\"Singapore\"}",
                    },
                    IsLast = true,
                },
            ],
        ]);
        var sink = new RecordingLlmRunSink();
        var toolExecutionPort = new RecordingAgentToolExecutionPort();
        var core = new LlmRunCore(
            provider,
            [],
            TestToolSetRegistry.Empty,
            NullLogger<LlmRunCore>.Instance,
            toolExecutionPort);

        await core.RunAsync(
            new LlmRunCoreRequest(BuildRunRequest("resp_1", BuildForwardedSelection()), "run_1", "ApiKey"),
            sink);

        sink.ToolCalls.Should().BeEmpty();
        toolExecutionPort.Requests.Should().BeEmpty(
            "client-forwarded tools are returned to the caller and must never execute on the server");
        sink.Completed.Should().ContainSingle();
        var forwarded = sink.Completed[0].ForwardedToolCallRecords.Should().ContainSingle().Subject;
        forwarded.CallId.Should().Be("call_1");
        forwarded.ToolName.Should().Be("get_weather");
        forwarded.SchemaHash.Should().Be("schema-1");
        ResponsesJsonValues.ToBoundaryJson(forwarded.Arguments).Should().Be("""{"city":"Singapore"}""");
        sink.Completed[0].ForwardedToolCalls.Should().ContainSingle()
            .Which.Arguments.Fields["city"].StringValue.Should().Be("Singapore");
    }

    [Fact]
    public async Task RunAsync_ShouldExecuteSubstitutedToolAndContinueNextRound()
    {
        var tool = new RecordingAgentTool("get_weather", """{"temperature":28}""");
        var toolProvider = new StaticResponsesToolProvider(substituteTools: [tool]);
        var provider = new ScriptedLlmProviderFactory([
            [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call_1",
                        Name = "get_weather",
                        ArgumentsJson = """{"city":"Singapore"}""",
                    },
                    IsLast = true,
                },
            ],
            [
                new LLMStreamChunk
                {
                    DeltaContent = "local result accepted",
                    IsLast = true,
                },
            ],
        ]);
        var sink = new RecordingLlmRunSink();
        var toolExecutionPort = new RecordingAgentToolExecutionPort();
        var core = new LlmRunCore(
            provider,
            [toolProvider],
            TestToolSetRegistry.Empty,
            NullLogger<LlmRunCore>.Instance,
            toolExecutionPort);
        var selection = BuildForwardedSelection();
        selection.SubstitutedToolNames.Add("get_weather");

        await core.RunAsync(
            new LlmRunCoreRequest(BuildRunRequest("resp_1", selection), "run_1", "ApiKey"),
            sink);

        var executionRequest = toolExecutionPort.Requests.Should().ContainSingle().Subject;
        executionRequest.ExecutionContext.Request.CallId.Should().Be("call_1");
        executionRequest.ExecutionOwner.Kind.Should().Be(AgentToolExecutionOwnerKind.WorkflowRun);
        executionRequest.ExecutionOwner.OwnerId.Should().Be("run_1");
        tool.Executions.Should().ContainSingle().Which.Should().Be("""{"city":"Singapore"}""");
        sink.Completed.Should().ContainSingle()
            .Which.ForwardedToolCallRecords.Should().BeEmpty();
        sink.ToolCalls.Should().ContainSingle(observed => !observed.Forwarded)
            .Which.LocalResult.StructValue.Fields["temperature"].NumberValue.Should().Be(28);
        provider.Requests.Should().HaveCount(2);
        provider.Requests[1].Messages.Should().Contain(message =>
            string.Equals(message.Role, "tool", StringComparison.Ordinal) &&
            string.Equals(message.Content, """{"temperature":28}""", StringComparison.Ordinal));
        sink.Completed.Should().ContainSingle()
            .Which.OutputText.Should().Be("local result accepted");
    }

    [Fact]
    public async Task RunAsync_WhenSinkStopsAfterLocalToolObservation_ShouldStopDispatchingTerminalRecords()
    {
        var tool = new RecordingAgentTool("get_weather", """{"temperature":28}""");
        var toolProvider = new StaticResponsesToolProvider(substituteTools: [tool]);
        var provider = new ScriptedLlmProviderFactory([
            [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call_1",
                        Name = "get_weather",
                        ArgumentsJson = """{"city":"Singapore"}""",
                    },
                    IsLast = true,
                },
            ],
        ]);
        var selection = BuildForwardedSelection();
        selection.SubstitutedToolNames.Add("get_weather");
        var sink = new RecordingLlmRunSink
        {
            StopAfterToolObservation = true,
        };
        var core = new LlmRunCore(provider, [toolProvider], TestToolSetRegistry.Empty, NullLogger<LlmRunCore>.Instance, new RecordingAgentToolExecutionPort());

        await core.RunAsync(
            new LlmRunCoreRequest(BuildRunRequest("resp_1", selection), "run_1", "ApiKey"),
            sink);

        sink.ToolCalls.Should().ContainSingle(observed => !observed.Forwarded);
        sink.Completed.Should().BeEmpty();
        sink.Failed.Should().BeEmpty();
        sink.Cancelled.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_WhenOwnedAdditiveToolIsCalled_ShouldNotForwardCompletionToolCall()
    {
        var tool = new RecordingAgentTool("aevatar_invoke_team", """{"ok":true}""");
        var toolProvider = new StaticResponsesToolProvider(additiveTools: [tool]);
        var provider = new ScriptedLlmProviderFactory([
            [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call_owned",
                        Name = "aevatar_invoke_team",
                        ArgumentsJson = """{"team_id":"team-1"}""",
                    },
                    IsLast = true,
                },
            ],
            [
                new LLMStreamChunk
                {
                    DeltaContent = "local result accepted",
                    IsLast = true,
                },
            ],
        ]);
        var sink = new RecordingLlmRunSink();
        var toolExecutionPort = new RecordingAgentToolExecutionPort();
        var core = new LlmRunCore(
            provider,
            [toolProvider],
            TestToolSetRegistry.Empty,
            NullLogger<LlmRunCore>.Instance,
            toolExecutionPort);
        var selection = BuildForwardedSelection();
        selection.AdditiveToolNames.Add("aevatar_invoke_team");

        await core.RunAsync(
            new LlmRunCoreRequest(BuildRunRequest("resp_owned", selection), "run_1", "ApiKey"),
            sink);

        toolExecutionPort.Requests.Should().ContainSingle()
            .Which.ExecutionContext.Request.CallId.Should().Be("call_owned");
        tool.Executions.Should().ContainSingle().Which.Should().Be("""{"team_id":"team-1"}""");
        sink.ToolCalls.Should().ContainSingle(observed => !observed.Forwarded)
            .Which.ToolCall.ToolName.Should().Be("aevatar_invoke_team");
        sink.Completed.Should().ContainSingle()
            .Which.ForwardedToolCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_ShouldExecuteOwnedAdditiveCollisionLocally()
    {
        var tool = new RecordingAgentTool("use_skill", """{"loaded":true}""");
        var toolProvider = new StaticResponsesToolProvider(additiveTools: [tool]);
        var provider = new ScriptedLlmProviderFactory([
            [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call_1",
                        Name = "use_skill",
                        ArgumentsJson = """{"name":"writer"}""",
                    },
                    IsLast = true,
                },
            ],
            [
                new LLMStreamChunk
                {
                    DeltaContent = "skill loaded",
                    IsLast = true,
                },
            ],
        ]);
        var sink = new RecordingLlmRunSink();
        var core = new LlmRunCore(provider, [toolProvider], TestToolSetRegistry.Empty, NullLogger<LlmRunCore>.Instance, new RecordingAgentToolExecutionPort());
        var selection = new LlmSessionRuntimeToolSelection
        {
            ForwardedTools =
            {
                new LlmSessionRuntimeToolDeclaration
                {
                    ToolName = "use_skill",
                    Description = "Client collision",
                    ParametersJson = """{"type":"object"}""",
                    SchemaHash = "schema-1",
                },
            },
            AdditiveToolNames = { "use_skill" },
            OwnedToolNames = { "use_skill" },
        };

        await core.RunAsync(
            new LlmRunCoreRequest(BuildRunRequest("resp_1", selection), "run_1", "ApiKey"),
            sink);

        tool.Executions.Should().ContainSingle().Which.Should().Be("""{"name":"writer"}""");
        sink.Completed.Should().ContainSingle()
            .Which.ForwardedToolCallRecords.Should().BeEmpty();
        sink.ToolCalls.Should().ContainSingle(observed => !observed.Forwarded)
            .Which.ToolCall.ToolName.Should().Be("use_skill");
        provider.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task RunAsync_WhenOwnedToolIsMissingFromDiscovery_ShouldReturnLocalToolUnavailableResult()
    {
        var provider = new ScriptedLlmProviderFactory([
            [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call_missing",
                        Name = "use_skill",
                        ArgumentsJson = """{"name":"writer"}""",
                    },
                    IsLast = true,
                },
            ],
            [
                new LLMStreamChunk
                {
                    DeltaContent = "missing tool reported",
                    IsLast = true,
                },
            ],
        ]);
        var sink = new RecordingLlmRunSink();
        var core = new LlmRunCore(provider, [], TestToolSetRegistry.Empty, NullLogger<LlmRunCore>.Instance, new RecordingAgentToolExecutionPort());
        var selection = new LlmSessionRuntimeToolSelection
        {
            OwnedToolNames = { "use_skill" },
        };

        await core.RunAsync(
            new LlmRunCoreRequest(BuildRunRequest("resp_missing", selection), "run_1", "ApiKey"),
            sink);

        sink.ToolCalls.Should().NotContain(observed => observed.Forwarded);
        var observed = sink.ToolCalls.Should().ContainSingle(observed => !observed.Forwarded).Subject;
        observed.ToolCall.ToolName.Should().Be("use_skill");
        observed.LocalResultJson.Should().Be("""{"error":"tool_not_available","tool_name":"use_skill"}""");
        observed.LocalResult.StructValue.Fields["error"].StringValue.Should().Be("tool_not_available");
        provider.Requests.Should().HaveCount(2);
        provider.Requests[1].Messages.Should().Contain(message =>
            string.Equals(message.Role, "tool", StringComparison.Ordinal) &&
            message.Content != null &&
            message.Content.Contains("tool_not_available", StringComparison.Ordinal));
        sink.Completed.Should().ContainSingle()
            .Which.OutputText.Should().Be("missing tool reported");
    }

    [Fact]
    public async Task RunAsync_WhenModelCallsUnknownTool_ShouldReturnLocalToolNotDeclaredResult()
    {
        var provider = new ScriptedLlmProviderFactory([
            [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call_unknown",
                        Name = "unknown_tool",
                        ArgumentsJson = """{"value":1}""",
                    },
                    IsLast = true,
                },
            ],
            [
                new LLMStreamChunk
                {
                    DeltaContent = "unknown tool reported",
                    IsLast = true,
                },
            ],
        ]);
        var sink = new RecordingLlmRunSink();
        var core = new LlmRunCore(provider, [], TestToolSetRegistry.Empty, NullLogger<LlmRunCore>.Instance, new RecordingAgentToolExecutionPort());

        await core.RunAsync(
            new LlmRunCoreRequest(BuildRunRequest("resp_unknown"), "run_1", "ApiKey"),
            sink);

        sink.ToolCalls.Should().NotContain(observed => observed.Forwarded);
        var observed = sink.ToolCalls.Should().ContainSingle(observed => !observed.Forwarded).Subject;
        observed.ToolCall.ToolName.Should().Be("unknown_tool");
        observed.LocalResultJson.Should().Be("""{"error":"tool_not_declared","tool_name":"unknown_tool"}""");
        observed.LocalResult.StructValue.Fields["error"].StringValue.Should().Be("tool_not_declared");
        provider.Requests.Should().HaveCount(2);
        provider.Requests[1].Messages.Should().Contain(message =>
            string.Equals(message.Role, "tool", StringComparison.Ordinal) &&
            message.Content != null &&
            message.Content.Contains("tool_not_declared", StringComparison.Ordinal));
        sink.Completed.Should().ContainSingle()
            .Which.OutputText.Should().Be("unknown tool reported");
    }

    [Fact]
    public async Task RunAsync_WhenToolCallsExhaustMaximumRounds_ShouldRecordFailure()
    {
        var tool = new RecordingAgentTool("get_weather", """{"temperature":28}""");
        var toolProvider = new StaticResponsesToolProvider(substituteTools: [tool]);
        var provider = new ScriptedLlmProviderFactory([
            [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call_1",
                        Name = "get_weather",
                        ArgumentsJson = """{"city":"Singapore"}""",
                    },
                    IsLast = true,
                },
            ],
        ]);
        var sink = new RecordingLlmRunSink();
        var core = new LlmRunCore(provider, [toolProvider], TestToolSetRegistry.Empty, NullLogger<LlmRunCore>.Instance, new RecordingAgentToolExecutionPort());
        var selection = BuildForwardedSelection();
        selection.OwnedToolNames.Add("get_weather");
        selection.SubstitutedToolNames.Add("get_weather");

        await core.RunAsync(
            new LlmRunCoreRequest(BuildRunRequest("resp_rounds", selection), "run_1", "ApiKey"),
            sink);

        tool.Executions.Should().HaveCount(8);
        provider.Requests.Should().HaveCount(8);
        sink.Completed.Should().BeEmpty();
        sink.Failed.Should().ContainSingle()
            .Which.FailureCode.Should().Be("max_tool_rounds_exhausted");
    }

    [Fact]
    public async Task RunAsync_WhenProviderThrows_ShouldRecordFailureThroughSink()
    {
        var provider = new ThrowingLlmProviderFactory(
            new NyxIdAuthenticationRequiredException("nyxid"));
        var sink = new RecordingLlmRunSink();
        var core = new LlmRunCore(provider, [], TestToolSetRegistry.Empty, NullLogger<LlmRunCore>.Instance, new RecordingAgentToolExecutionPort());

        await core.RunAsync(
            new LlmRunCoreRequest(BuildRunRequest("resp_1"), "run_1", "ApiKey"),
            sink);

        sink.Failed.Should().ContainSingle();
        sink.Failed[0].FailureCode.Should().Be("authentication_required");
        sink.Failed[0].FailureMessage.Should().Contain("NyxID authentication required");
        sink.Completed.Should().BeEmpty();
        sink.Cancelled.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_WhenCancellationTokenIsCancelled_ShouldFlushCancelledFactThroughSink()
    {
        using var cts = new CancellationTokenSource();
        var provider = new CancellableLlmProviderFactory();
        var sink = new CancellationSensitiveLlmRunSink();
        var core = new LlmRunCore(provider, [], TestToolSetRegistry.Empty, NullLogger<LlmRunCore>.Instance, new RecordingAgentToolExecutionPort());
        cts.Cancel();

        await core.RunAsync(
            new LlmRunCoreRequest(BuildRunRequest("resp_1"), "run_1", "ApiKey"),
            sink,
            cts.Token);

        sink.Cancelled.Should().ContainSingle();
        sink.SinkCancellationTokens.Should().ContainSingle().Which.IsCancellationRequested.Should().BeFalse();
        sink.Completed.Should().BeEmpty();
        sink.Failed.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_WhenProviderNeverEmitsTerminalChunk_ShouldRecordCancelledFactWhenCancelled()
    {
        var streamEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new LlmRunAcceptanceHarness.NeverTerminalLlmProviderFactory(streamEntered);
        var sink = new LlmRunAcceptanceHarness.RecordingLlmRunSink();
        var core = new LlmRunCore(provider, [], TestToolSetRegistry.Empty, NullLogger<LlmRunCore>.Instance, new RecordingAgentToolExecutionPort());
        using var cts = new CancellationTokenSource();

        var runTask = core.RunAsync(
            new LlmRunCoreRequest(BuildRunRequest("resp_1"), "run_1", "ApiKey"),
            sink,
            cts.Token);
        await streamEntered.Task;
        await cts.CancelAsync();
        await runTask;

        sink.StreamChunks.Should().ContainSingle().Which.DeltaText.Should().Be("partial");
        sink.Cancelled.Should().ContainSingle().Which.RunId.Should().Be("run_1");
        sink.Completed.Should().BeEmpty();
        sink.Failed.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_WhenRouteToolSetNameSet_ShouldMaterializeRouteToolSetToolEvenWithoutDiProviders()
    {
        // Regression for the off-grain executor dropping the route tool set: the tool is NOT a DI
        // provider, so it can only reach the model if LlmRunCore re-resolves the persisted
        // tool_set_name from the registry.
        var tool = new RecordingAgentTool("nyxid_services", """{"services":[]}""");
        var registry = new TestToolSetRegistry(name =>
            string.Equals(name, "workspace.default", StringComparison.Ordinal)
                ? ToolSetResolveResult.Success(name, [new StaticAgentToolSource([tool])])
                : ToolSetResolveResult.Failure(new ToolSetResolveError(
                    ToolSetResolveError.UnknownNameCode, name, "unknown", [])));
        var provider = new ScriptedLlmProviderFactory([
            [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call_1",
                        Name = "nyxid_services",
                        ArgumentsJson = """{"action":"list"}""",
                    },
                    IsLast = true,
                },
            ],
            [
                new LLMStreamChunk { DeltaContent = "listed", IsLast = true },
            ],
        ]);
        var sink = new RecordingLlmRunSink();
        // DI tool providers intentionally empty — the tool can only come from the route tool set.
        var core = new LlmRunCore(provider, [], registry, NullLogger<LlmRunCore>.Instance, new RecordingAgentToolExecutionPort());
        var selection = new LlmSessionRuntimeToolSelection
        {
            ToolSetName = "workspace.default",
            AdditiveToolNames = { "nyxid_services" },
        };

        await core.RunAsync(
            new LlmRunCoreRequest(BuildRunRequest("resp_toolset", selection), "run_1", "ApiKey"),
            sink);

        tool.Executions.Should().ContainSingle().Which.Should().Be("""{"action":"list"}""");
        sink.ToolCalls.Should().ContainSingle(observed => !observed.Forwarded)
            .Which.ToolCall.ToolName.Should().Be("nyxid_services");
        sink.Completed.Should().ContainSingle()
            .Which.ForwardedToolCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_WhenRouteToolSetResolutionThrows_ShouldFailBeforeCallingModel()
    {
        // IToolSetRegistry.Resolve throws on unknown-include / cycle. A tool-set config problem
        // must degrade to DI-only providers, never fail the whole run.
        var registry = new TestToolSetRegistry(_ =>
            throw new InvalidOperationException("tool set cycle"));
        var provider = new ScriptedLlmProviderFactory([
            [
                new LLMStreamChunk { DeltaContent = "ok", IsLast = true },
            ],
        ]);
        var sink = new RecordingLlmRunSink();
        var core = new LlmRunCore(provider, [], registry, NullLogger<LlmRunCore>.Instance, new RecordingAgentToolExecutionPort());
        var selection = new LlmSessionRuntimeToolSelection
        {
            ToolSetName = "workspace.default",
        };

        await core.RunAsync(
            new LlmRunCoreRequest(BuildRunRequest("resp_throw", selection), "run_1", "ApiKey"),
            sink);

        provider.Requests.Should().BeEmpty();
        sink.Completed.Should().BeEmpty();
        sink.Failed.Should().ContainSingle()
            .Which.FailureCode.Should().Be(ToolSetResolveError.ResolutionFailedCode);
    }

    [Fact]
    public async Task RunAsync_WhenRouteToolSetResolutionReturnsFailure_ShouldFailBeforeCallingModel()
    {
        // Failure result (not an exception) — e.g. unknown tool set name. Must also degrade to
        // DI-only, never fail the run.
        var registry = new TestToolSetRegistry(name =>
            ToolSetResolveResult.Failure(new ToolSetResolveError(
                ToolSetResolveError.UnknownNameCode, name, "unknown", [])));
        var provider = new ScriptedLlmProviderFactory([
            [
                new LLMStreamChunk { DeltaContent = "ok", IsLast = true },
            ],
        ]);
        var sink = new RecordingLlmRunSink();
        var core = new LlmRunCore(provider, [], registry, NullLogger<LlmRunCore>.Instance, new RecordingAgentToolExecutionPort());
        var selection = new LlmSessionRuntimeToolSelection
        {
            ToolSetName = "workspace.default",
        };

        await core.RunAsync(
            new LlmRunCoreRequest(BuildRunRequest("resp_fail", selection), "run_1", "ApiKey"),
            sink);

        provider.Requests.Should().BeEmpty();
        sink.Completed.Should().BeEmpty();
        sink.Failed.Should().ContainSingle()
            .Which.FailureCode.Should().Be(ToolSetResolveError.UnknownNameCode);
    }

    [Fact]
    public async Task RunAsync_WithNewRestrictedEmptyProof_ShouldCallModelWithExplicitZeroToolCatalog()
    {
        var provider = new ScriptedLlmProviderFactory([
            [new LLMStreamChunk { DeltaContent = "ok", IsLast = true }],
        ]);
        var selection = ResponsesRuntimeToolSelectionFactory.Create(
            new ResponsesToolClassification([], [], [], [], [])
            {
                OwnedCatalog = AgentTurnToolCatalogFactory.RestrictedEmpty(),
            },
            ResponsesToolChoiceHintPlan.Empty,
            routeToolSetName: null);
        var core = new LlmRunCore(
            provider,
            [],
            TestToolSetRegistry.Empty,
            NullLogger<LlmRunCore>.Instance,
            new RecordingAgentToolExecutionPort());

        await core.RunAsync(
            new LlmRunCoreRequest(BuildRunRequest("resp_empty_proof", selection), "run_1", "ApiKey"),
            new RecordingLlmRunSink());

        var request = provider.Requests.Should().ContainSingle().Subject;
        request.Tools.Should().BeNullOrEmpty();
        request.ToolCatalogProof.Should().NotBeNull();
        request.ToolCatalogProof!.ToolCount.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_WhenPinnedProofMatchesRematerializedContract_ShouldUseExactRuntimeObject()
    {
        var ingressTool = new RecordingAgentTool("web_search", "{}", description: "search", schema: "{\"type\":\"object\"}");
        var runtimeTool = new RecordingAgentTool("WEB_SEARCH", "{}", description: "search", schema: "{\"type\":\"object\"}");
        var profile = PinnedProfile("workspace.default");
        var selection = PinnedSelection(ingressTool, profile);
        var registry = Registry("workspace.default", runtimeTool);
        var provider = new ScriptedLlmProviderFactory([
            [new LLMStreamChunk { DeltaContent = "ok", IsLast = true }],
        ]);
        var core = new LlmRunCore(
            provider,
            [],
            registry,
            NullLogger<LlmRunCore>.Instance,
            new RecordingAgentToolExecutionPort());

        await core.RunAsync(
            new LlmRunCoreRequest(BuildRunRequest("resp_matching_proof", selection), "run_1", "ApiKey"),
            new RecordingLlmRunSink());

        var request = provider.Requests.Should().ContainSingle().Subject;
        request.Tools.Should().ContainSingle().Which.Should().BeSameAs(runtimeTool);
        request.ToolCatalogProof!.CatalogDigest.Should().Be(selection.OwnedCatalogProof.CatalogDigest);
    }

    [Fact]
    public async Task RunAsync_WhenRematerializedContractDiffersFromPinnedProof_ShouldFailBeforeModel()
    {
        var ingressTool = new RecordingAgentTool("web_search", "{}", description: "search", schema: "{\"type\":\"object\"}");
        var driftedTool = new RecordingAgentTool("web_search", "{}", description: "search changed", schema: "{\"type\":\"object\"}");
        var profile = PinnedProfile("workspace.default");
        var selection = PinnedSelection(ingressTool, profile);
        var provider = new ScriptedLlmProviderFactory([
            [new LLMStreamChunk { DeltaContent = "must not run", IsLast = true }],
        ]);
        var sink = new RecordingLlmRunSink();
        var core = new LlmRunCore(
            provider,
            [],
            Registry("workspace.default", driftedTool),
            NullLogger<LlmRunCore>.Instance,
            new RecordingAgentToolExecutionPort());

        await core.RunAsync(
            new LlmRunCoreRequest(BuildRunRequest("resp_drifted_proof", selection), "run_1", "ApiKey"),
            sink);

        provider.Requests.Should().BeEmpty();
        sink.Completed.Should().BeEmpty();
        sink.Failed.Should().ContainSingle()
            .Which.FailureCode.Should().Be("tool_catalog_proof_mismatch");
    }

    private static LlmRunRequested BuildRunRequest(
        string responseId,
        LlmSessionRuntimeToolSelection? selection = null) =>
        new()
        {
            ResponseId = responseId,
            RunId = "run_1",
            ScopeId = "user-1",
            OwnerSubject = "user-1",
            BearerToken = "token-1",
            Model = "test-model",
            ToolSelection = selection,
            Messages =
            {
                new LlmSessionRuntimeChatMessage
                {
                    Role = "user",
                    Content = "What is the weather?",
                },
            },
        };

    private static LlmSessionRuntimeToolSelection BuildForwardedSelection() =>
        new()
        {
            ForwardedTools =
            {
                new LlmSessionRuntimeToolDeclaration
                {
                    ToolName = "get_weather",
                    Description = "Get weather",
                    ParametersJson = """{"type":"object"}""",
                    Parameters = new Struct
                    {
                        Fields =
                        {
                            ["type"] = Google.Protobuf.WellKnownTypes.Value.ForString("object"),
                        },
                    },
                    SchemaHash = "schema-1",
                },
            },
        };

    private static AgentProfileSnapshot PinnedProfile(string toolSetName) =>
        AgentProfileSnapshotCodec.Seal(new AgentProfileSnapshot
        {
            ProfileId = "profile-runtime",
            ProfileVersion = "1.0.0",
            AgentKind = "workspace.chat",
            PolicyRevision = "policy-1",
            RouteToolSetRef = toolSetName,
            PublishedRevision = 1,
            MaxOwnedToolCount = 8,
            MaxSchemaBytes = 48 * 1024,
        });

    private static LlmSessionRuntimeToolSelection PinnedSelection(
        IAgentTool ingressTool,
        AgentProfileSnapshot profile)
    {
        var catalog = new AgentTurnToolCatalog(
            [ingressTool.Name],
            profilePromptLayer: null,
            selectedSkillPromptLayer: null,
            selectedIntentId: "web",
            candidateIntentId: "web",
            diagnostics: null,
            exactTools: [ingressTool],
            // The planner always builds profiled catalogs on the sealed profile's budget, and the
            // run executor re-derives that budget rather than trusting the persisted proof.
            budget: AgentTurnToolCatalogFactory.ResolveProfileBudget(profile));
        return ResponsesRuntimeToolSelectionFactory.Create(
            new ResponsesToolClassification(
                [],
                [ingressTool],
                [],
                [ingressTool.Name],
                [ingressTool.Name])
            {
                OwnedCatalog = catalog,
            },
            ResponsesToolChoiceHintPlan.Empty,
            profile.RouteToolSetRef,
            profile);
    }

    private static TestToolSetRegistry Registry(string name, IAgentTool tool) =>
        new(candidate => string.Equals(candidate, name, StringComparison.Ordinal)
            ? ToolSetResolveResult.Success(candidate, [new StaticAgentToolSource([tool])])
            : ToolSetResolveResult.Failure(new ToolSetResolveError(
                ToolSetResolveError.UnknownNameCode,
                candidate,
                "unknown",
                [])));

    private sealed class RecordingLlmRunSink : ILlmRunSink
    {
        public List<LlmStreamChunkObserved> StreamChunks { get; } = [];
        public List<LlmToolCallObserved> ToolCalls { get; } = [];
        public List<LlmRunCompleted> Completed { get; } = [];
        public List<LlmRunFailed> Failed { get; } = [];
        public List<LlmRunCancelled> Cancelled { get; } = [];

        public bool StopAfterToolObservation { get; init; }

        public Task<LlmRunRecordDecision> RecordStreamChunkObservedAsync(
            LlmStreamChunkObserved observed,
            CancellationToken ct = default)
        {
            StreamChunks.Add(observed.Clone());
            return Task.FromResult(LlmRunRecordDecision.Continue);
        }

        public Task<LlmRunRecordDecision> RecordToolCallObservedAsync(
            LlmToolCallObserved observed,
            CancellationToken ct = default)
        {
            ToolCalls.Add(observed.Clone());
            return Task.FromResult(StopAfterToolObservation
                ? LlmRunRecordDecision.Stop
                : LlmRunRecordDecision.Continue);
        }

        public Task<LlmRunRecordDecision> RecordRunCompletedAsync(
            LlmRunCompleted completed,
            CancellationToken ct = default)
        {
            Completed.Add(completed.Clone());
            return Task.FromResult(LlmRunRecordDecision.Continue);
        }

        public Task<LlmRunRecordDecision> RecordRunFailedAsync(
            LlmRunFailed failed,
            CancellationToken ct = default)
        {
            Failed.Add(failed.Clone());
            return Task.FromResult(LlmRunRecordDecision.Continue);
        }

        public Task<LlmRunRecordDecision> RecordRunCancelledAsync(
            LlmRunCancelled cancelled,
            CancellationToken ct = default)
        {
            Cancelled.Add(cancelled.Clone());
            return Task.FromResult(LlmRunRecordDecision.Continue);
        }
    }

    private sealed class ScriptedLlmProviderFactory(
        IReadOnlyList<IReadOnlyList<LLMStreamChunk>> responses) : ILLMProviderFactory, ILLMProvider
    {
        private int _nextResponseIndex;

        public List<LLMRequest> Requests { get; } = [];

        public string Name => "test";

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            var response = responses[Math.Min(_nextResponseIndex, responses.Count - 1)];
            _nextResponseIndex++;
            foreach (var chunk in response)
            {
                ct.ThrowIfCancellationRequested();
                yield return chunk;
            }

            await Task.CompletedTask;
        }
    }

    private sealed class ThrowingLlmProviderFactory(Exception exception) : ILLMProviderFactory, ILLMProvider
    {
        public string Name => "test";

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            throw exception;
            #pragma warning disable CS0162
            yield return new LLMStreamChunk();
            #pragma warning restore CS0162
        }
    }

    private sealed class CancellableLlmProviderFactory : ILLMProviderFactory, ILLMProvider
    {
        public string Name => "test";

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class CancellationSensitiveLlmRunSink : ILlmRunSink
    {
        public List<LlmRunCompleted> Completed { get; } = [];
        public List<LlmRunFailed> Failed { get; } = [];
        public List<LlmRunCancelled> Cancelled { get; } = [];
        public List<CancellationToken> SinkCancellationTokens { get; } = [];

        public Task<LlmRunRecordDecision> RecordStreamChunkObservedAsync(
            LlmStreamChunkObserved observed,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(LlmRunRecordDecision.Continue);
        }

        public Task<LlmRunRecordDecision> RecordToolCallObservedAsync(
            LlmToolCallObserved observed,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(LlmRunRecordDecision.Continue);
        }

        public Task RecordForwardedToolCallEmittedAsync(
            LlmSessionForwardedToolCallEmittedEvent emitted,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<LlmRunRecordDecision> RecordRunCompletedAsync(
            LlmRunCompleted completed,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Completed.Add(completed.Clone());
            return Task.FromResult(LlmRunRecordDecision.Continue);
        }

        public Task<LlmRunRecordDecision> RecordRunFailedAsync(
            LlmRunFailed failed,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Failed.Add(failed.Clone());
            return Task.FromResult(LlmRunRecordDecision.Continue);
        }

        public Task<LlmRunRecordDecision> RecordRunCancelledAsync(
            LlmRunCancelled cancelled,
            CancellationToken ct = default)
        {
            SinkCancellationTokens.Add(ct);
            ct.ThrowIfCancellationRequested();
            Cancelled.Add(cancelled.Clone());
            return Task.FromResult(LlmRunRecordDecision.Continue);
        }
    }

    private sealed class StaticAgentToolSource(IReadOnlyList<IAgentTool> tools) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromResult(tools);
    }

    private sealed class StaticResponsesToolProvider(
        IReadOnlyList<IAgentTool>? substituteTools = null,
        IReadOnlyList<IAgentTool>? additiveTools = null) : IResponsesToolProvider
    {
        public ValueTask<IReadOnlyList<IAgentTool>> GetSubstituteToolsAsync(
            ResponsesToolProviderContext context,
            CancellationToken ct = default) =>
            ValueTask.FromResult(substituteTools ?? []);

        public ValueTask<IReadOnlyList<IAgentTool>> GetAdditiveToolsAsync(
            ResponsesToolProviderContext context,
            CancellationToken ct = default) =>
            ValueTask.FromResult(additiveTools ?? []);
    }

    private sealed class RecordingAgentTool(
        string name,
        string resultJson,
        string description = "test tool",
        string schema = "{\"type\":\"object\"}") : IAgentTool
    {
        public List<string> Executions { get; } = [];

        public string Name { get; } = name;

        public string Description => description;

        public string ParametersSchema => schema;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            Executions.Add(argumentsJson);
            return Task.FromResult(resultJson);
        }
    }
}
