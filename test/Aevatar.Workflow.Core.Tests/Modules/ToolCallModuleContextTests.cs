using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Abstractions.Credentials;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Core.Tests.Modules;

public sealed class ToolCallModuleContextTests
{
    [Fact]
    public void ToolExecutionIssuedTime_ShouldCrossWorkflowBoundariesAsTypedData()
    {
        typeof(WorkflowToolExecutionRequest).GetProperty("IssuedAtUnixMs")
            .Should().NotBeNull();
        PendingToolCallApprovalState.Descriptor.FindFieldByName("issued_at_unix_ms")
            .Should().NotBeNull();
    }

    [Fact]
    public void ExternalOperationAdmissionContract_ShouldCrossEveryRuntimeBoundaryAsTypedData()
    {
        BindWorkflowRunDefinitionEvent.Descriptor.FindFieldByName("capability_admission_plan")
            .Should().NotBeNull();
        WorkflowRunState.Descriptor.FindFieldByName("capability_admission_plan")
            .Should().NotBeNull();
        StepRequestEvent.Descriptor.FindFieldByName("external_invocation")
            .Should().NotBeNull();
        typeof(WorkflowToolExecutionRequest).GetProperty("InvocationAdmission")
            .Should().NotBeNull();
    }

    [Fact]
    public async Task ToolCallModule_ShouldHandTheExactCallSiteProofToTheTool()
    {
        var tool = new RecordingWorkflowTool("nyxid_proxy");
        var module = CreateModule(tool);
        var plan = AdmissionPlan("wf-alpha/other_step", operationId: "list_items");
        plan.InvocationAdmissions.Add(AdmissionPlan("wf-alpha/call_proxy").InvocationAdmissions[0]);
        var ctx = new RecordingWorkflowContext { CapabilityAdmissionPlan = plan };

        await ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            externalInvocation: NyxIdInvocation("wf-alpha/call_proxy"));

        var admission = tool.Requests.Should().ContainSingle().Subject.InvocationAdmission;
        admission.Should().NotBeNull();
        admission!.Capability.NyxIdUserService.EndpointId.Should().Be("get_item");
        admission.Capability.NyxIdUserService.PathTemplate.Should().Be("/items/{item_id}");
        admission.Capability.NyxIdUserService.ContractDigest.Should().Be("server-derived-digest");
    }

    [Theory]
    [InlineData("wf-alpha/wrong_step", "us-shop-alpha", "get_item", "EXTERNAL_CAPABILITY_CALL_SITE_NOT_ADMITTED")]
    [InlineData("wf-alpha/call_proxy", "us-shop-beta", "get_item", "EXTERNAL_CAPABILITY_PROOF_SELECTOR_MISMATCH")]
    [InlineData("wf-alpha/call_proxy", "us-shop-alpha", "delete_item", "EXTERNAL_CAPABILITY_PROOF_SELECTOR_MISMATCH")]
    public async Task ToolCallModule_ShouldFailClosed_WhenTheCallSiteProofDoesNotMatch(
        string callSiteId,
        string userServiceId,
        string operationId,
        string expectedCode)
    {
        var tool = new RecordingWorkflowTool("nyxid_proxy");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext
        {
            CapabilityAdmissionPlan = AdmissionPlan("wf-alpha/call_proxy"),
        };

        await ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            externalInvocation: NyxIdInvocation(callSiteId, userServiceId, operationId));

        tool.Requests.Should().BeEmpty();
        LastCompleted(ctx).Success.Should().BeFalse();
        LastCompleted(ctx).Error.Should().Contain(expectedCode);
    }

    [Fact]
    public async Task ToolCallModule_ShouldFailClosed_WhenAnAdmittedToolHasNoCompiledCallSite()
    {
        var tool = new RecordingWorkflowTool("nyxid_proxy");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext
        {
            CapabilityAdmissionPlan = AdmissionPlan("wf-alpha/call_proxy"),
        };

        await ExecuteToolCallAsync(module, ctx, tool.Name);

        tool.Requests.Should().BeEmpty();
        LastCompleted(ctx).Success.Should().BeFalse();
        LastCompleted(ctx).Error.Should().Contain("EXTERNAL_CAPABILITY_CALL_SITE_NOT_ADMITTED");
    }

    [Fact]
    public async Task ToolCallModule_ShouldFailClosed_WhenTheRunHasNoCommittedAdmissionPlan()
    {
        var tool = new RecordingWorkflowTool("nyxid_proxy");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            externalInvocation: NyxIdInvocation("wf-alpha/call_proxy"));

        tool.Requests.Should().BeEmpty();
        LastCompleted(ctx).Success.Should().BeFalse();
        LastCompleted(ctx).Error.Should().Contain("EXTERNAL_CAPABILITY_ADMISSION_PLAN_MISSING");
    }

    [Fact]
    public async Task ToolCallModule_ShouldNotRequireAdmission_ForToolsOutsideExternalCapabilityPolicy()
    {
        var tool = new RecordingWorkflowTool("summarize");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name);

        tool.Requests.Should().ContainSingle().Which.InvocationAdmission.Should().BeNull();
        LastCompleted(ctx).Success.Should().BeTrue();
    }

    [Fact]
    public async Task ToolCallModule_ShouldPublishToolEventsWithWorkflowExecutionCallId()
    {
        var tool = new FakeAgentTool("call_id_reader", _ => "{}");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name, stepId: "call_proxy", executionId: "exec-1");

        ctx.Published.Select(x => x.Event).OfType<WorkflowToolCallStartedEvent>().Single().CallId
            .Should().Be("workflow:run-1:call_proxy:exec-1");
        ctx.Published.Select(x => x.Event).OfType<WorkflowToolCallCompletedEvent>().Single().CallId
            .Should().Be("workflow:run-1:call_proxy:exec-1");
    }

    [Fact]
    public async Task ToolCallModule_ShouldSetExecutionIdOnStepCompletion()
    {
        var tool = new FakeAgentTool("execution_reader", _ => "{}");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name, stepId: "call_proxy", executionId: "exec-1");

        LastCompleted(ctx).ExecutionId.Should().Be("exec-1");
    }

    [Fact]
    public async Task ToolCallModule_ShouldSetExecutionIdOnFailureStepCompletion()
    {
        var tool = new FakeAgentTool("failing_tool", _ => throw new InvalidOperationException("boom"));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name, stepId: "call_proxy", executionId: "exec-1");

        var completed = LastCompleted(ctx);
        completed.Success.Should().BeFalse();
        completed.ExecutionId.Should().Be("exec-1");
    }

    [Fact]
    public async Task ToolCallModule_WhenToolReturnsTypedFailure_ShouldPublishFailedToolAndStepOutcomes()
    {
        const string resultJson = """{"error":true,"status":503}""";
        var tool = new ScriptedResultWorkflowTool(
            "nyxid_proxy",
            WorkflowToolExecutionResult.Failed(
                resultJson,
                "NYXID_PROXY_HTTP_503",
                "The service request failed."));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext
        {
            CapabilityAdmissionPlan = AdmissionPlan("wf-alpha/call_proxy"),
        };

        await ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            executionId: "exec-1",
            externalInvocation: NyxIdInvocation("wf-alpha/call_proxy"));

        var toolCompleted = ctx.Published.Select(x => x.Event)
            .OfType<WorkflowToolCallCompletedEvent>()
            .Single();
        toolCompleted.Success.Should().BeFalse();
        toolCompleted.ResultJson.Should().Be(resultJson);
        toolCompleted.Error.Should().Contain("tool 'nyxid_proxy' execution failed");
        toolCompleted.Error.Should().Contain("NYXID_PROXY_HTTP_503");
        toolCompleted.Error.Should().Contain("The service request failed.");

        var stepCompleted = LastCompleted(ctx);
        stepCompleted.Success.Should().BeFalse();
        stepCompleted.Output.Should().Be(resultJson);
        stepCompleted.Error.Should().Be(toolCompleted.Error);
        stepCompleted.ExecutionId.Should().Be("exec-1");
    }

    [Fact]
    public async Task ToolCallModule_ShouldSetExecutionIdWhenToolParameterIsMissing()
    {
        var tool = new FakeAgentTool("unused", _ => "{}");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "call_proxy",
                StepType = "tool_call",
                RunId = ctx.RunId,
                ExecutionId = "exec-1",
                Input = "{}",
            }),
            ctx,
            CancellationToken.None);

        var completed = LastCompleted(ctx);
        completed.Success.Should().BeFalse();
        completed.ExecutionId.Should().Be("exec-1");
    }

    [Fact]
    public async Task ToolCallModule_ShouldExecuteTypedWorkflowToolRequest()
    {
        var tool = new FakeAgentTool("echo", argumentsJson => argumentsJson);
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name, input: """{"msg":"ok"}""");

        var completed = LastCompleted(ctx);
        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("""{"msg":"ok"}""");
    }

    [Fact]
    public async Task ToolCallModule_ShouldPreferExplicitArgumentsParameter()
    {
        var tool = new CapturingWorkflowTool("echo");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "call_proxy",
                StepType = "tool_call",
                RunId = ctx.RunId,
                Input = """{"from":"input"}""",
                Parameters =
                {
                    ["tool"] = tool.Name,
                    ["arguments"] = """{"from":"parameters"}""",
                },
            }),
            ctx,
            CancellationToken.None);

        tool.LastRequest.Should().NotBeNull();
        tool.LastRequest!.ArgumentsJson.Should().Be("""{"from":"parameters"}""");
        LastCompleted(ctx).Success.Should().BeTrue();
    }

    [Fact]
    public async Task ToolCallModule_ShouldPassTypedWorkflowToolExecutionRequestToDirectTool()
    {
        var issuedAt = new DateTimeOffset(2026, 7, 31, 10, 11, 12, TimeSpan.Zero);
        var tool = new CapturingWorkflowTool("nyxid_tool");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext
        {
            ScheduleId = "schedule-tool",
        };
        ctx.ExecutionContextState.CallerCredential = new WorkflowCallerCredentialState
        {
            BearerToken = " typed-token ",
        };
        ctx.ExecutionContextState.WorkflowRuntime = new WorkflowToolRuntimeContextState
        {
            ParentActorId = "parent-actor",
            ParentRunId = "parent-run",
            ParentStepId = "parent-step",
            RootRunId = "root-run",
            Depth = 2,
        };
        ctx.ExecutionContextState.Llm = new WorkflowLlmExecutionContextState
        {
            ModelOverride = " model-alpha ",
            RoutePreference = " route-alpha ",
            UserMemoryPrompt = " remember-alpha ",
            MaxToolRoundsOverride = 4,
        };
        ctx.RuntimeContext.ApplySenderNyxIdAccessToken(" sender-alpha ");
        ctx.RuntimeContext.ApplyRequestMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["connector.http.authorization"] = "Bearer metadata-token",
        });

        await ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            input: """{"operation":"read"}""",
            executionId: "exec-1",
            idempotencyKey: "idem-tool-1",
            issuedAt: issuedAt);

        tool.LastRequest.Should().NotBeNull();
        tool.LastRequest!.ArgumentsJson.Should().Be("""{"operation":"read"}""");
        tool.LastRequest.RunId.Should().Be("run-1");
        tool.LastRequest.StepId.Should().Be("call_proxy");
        tool.LastRequest.ExecutionId.Should().Be("exec-1");
        tool.LastRequest.CallId.Should().Be("workflow:run-1:call_proxy:exec-1");
        tool.LastRequest.IdempotencyKey.Should().Be("idem-tool-1");
        tool.LastRequest.ScopeId.Should().Be("scope-1");
        tool.LastRequest.CallerCredential.BearerToken.Should().Be("typed-token");
        tool.LastRequest.ScheduleId.Should().Be("schedule-tool");
        tool.LastRequest.IssuedAtUnixMs.Should().Be(issuedAt.ToUnixTimeMilliseconds());
        tool.LastRequest.RuntimeContext.ParentActorId.Should().Be("agent-1");
        tool.LastRequest.RuntimeContext.ParentRunId.Should().Be("run-1");
        tool.LastRequest.RuntimeContext.ParentStepId.Should().Be("call_proxy");
        tool.LastRequest.RuntimeContext.RootRunId.Should().Be("root-run");
        tool.LastRequest.RuntimeContext.Depth.Should().Be(2);
        tool.LastRequest.LlmControl.Should().NotBeNull();
        tool.LastRequest.LlmControl!.ModelOverride.Should().Be("model-alpha");
        tool.LastRequest.LlmControl.RoutePreference.Should().Be("route-alpha");
        tool.LastRequest.LlmControl.UserMemoryPrompt.Should().Be("remember-alpha");
        tool.LastRequest.LlmControl.MaxToolRoundsOverride.Should().Be(4);
        tool.LastRequest.LlmControl.SenderNyxIdAccessToken.Should().Be("sender-alpha");
        LastCompleted(ctx).Success.Should().BeTrue();
    }

    [Fact]
    public async Task ToolCallModule_ShouldIssueFreshCallerTokenForEveryExecution()
    {
        var tool = new CapturingWorkflowTool("nyxid_tool");
        var tokenProvider = new RotatingCallerAccessTokenProvider();
        var module = CreateModule(tool, tokenProvider);
        var ctx = new RecordingWorkflowContext();
        ctx.ExecutionContextState.CallerCredential = new WorkflowCallerCredentialState
        {
            NyxIdAuthority = CreateCallerAuthority(),
        };

        await ExecuteToolCallAsync(module, ctx, tool.Name, stepId: "call-1");
        await ExecuteToolCallAsync(module, ctx, tool.Name, stepId: "call-2");

        tool.Requests.Select(request => request.CallerCredential.BearerToken)
            .Should().Equal("token-1", "token-2");
        tokenProvider.Authorities.Should().HaveCount(2);
    }

    [Fact]
    public async Task ToolCallModule_ShouldPassCurrentStepInputFileRefsToDirectTool()
    {
        var tool = new CapturingWorkflowTool("document_extract");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();
        var fileRef = BuildWorkflowFileRef("file-step");

        await ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            inputFileRefs: [fileRef]);

        tool.LastRequest.Should().NotBeNull();
        var requestFileRef = tool.LastRequest!.InputFileRefs.Should().ContainSingle().Subject;
        requestFileRef.FileId.Should().Be("file-step");
        requestFileRef.Should().NotBeSameAs(fileRef);
        LastCompleted(ctx).Success.Should().BeTrue();
    }

    [Fact]
    public async Task ToolCallModule_WhenToolReturnsManagedHandoff_ShouldLeaveParentStepPending()
    {
        var handoff = new WorkflowManagedHandoffOutcome
        {
            ParentActorId = "parent-actor",
            ParentRunId = "run-1",
            ParentStepId = "call_proxy",
            InvocationId = "run-1:workflow_tool:call_proxy:call-1",
            ChildRunId = "run-1:workflow_tool:call_proxy:call-1",
        };
        var tool = new ManagedHandoffWorkflowTool("aevatar_start_workflow", handoff);
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name, executionId: "exec-1");

        var completed = ctx.Published.Select(x => x.Event).OfType<WorkflowToolCallCompletedEvent>().Single();
        completed.Success.Should().BeTrue();
        completed.ManagedHandoff.Should().NotBeNull();
        completed.ManagedHandoff.InvocationId.Should().Be(handoff.InvocationId);
        ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task ToolCallModule_ShouldFallbackToEmptyScopeIdWhenContextScopeIdIsNull()
    {
        var tool = new CapturingWorkflowTool("nyxid_tool");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext { ScopeIdOverride = null };

        await ExecuteToolCallAsync(module, ctx, tool.Name);

        tool.LastRequest.Should().NotBeNull();
        tool.LastRequest!.ScopeId.Should().BeEmpty();
        LastCompleted(ctx).Success.Should().BeTrue();
    }

    [Fact]
    public async Task ToolCallModule_ShouldNotUseRequestMetadataAsCallerCredential()
    {
        var tool = new CapturingWorkflowTool("nyxid_tool");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();
        ctx.RuntimeContext.ApplyRequestMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["connector.http.authorization"] = "Bearer metadata-token",
        });

        await ExecuteToolCallAsync(module, ctx, tool.Name);

        tool.LastRequest.Should().NotBeNull();
        tool.LastRequest!.CallerCredential.BearerToken.Should().BeEmpty();
        LastCompleted(ctx).Success.Should().BeTrue();
    }

    [Fact]
    public void IWorkflowTool_ShouldExposeOnlyTypedWorkflowExecutionMethod()
    {
        var executeMethods = typeof(IWorkflowTool)
            .GetMethods()
            .Where(method => method.Name == nameof(IWorkflowTool.ExecuteAsync))
            .ToList();

        executeMethods.Should().ContainSingle();
        executeMethods[0].GetParameters().First().ParameterType.Should().Be(typeof(WorkflowToolExecutionRequest));
        executeMethods[0].GetParameters().Should().NotContain(parameter => parameter.ParameterType == typeof(string));
    }

    private static ToolCallModule CreateModule(
        IWorkflowTool tool,
        IWorkflowCallerAccessTokenProvider? tokenProvider = null) =>
        new(
            [new SingleToolSource(tool)],
            NullLogger<ToolCallModule>.Instance,
            tokenProvider);

    private static WorkflowCallerNyxIdAuthority CreateCallerAuthority() =>
        new()
        {
            Platform = "nyxid",
            Tenant = "tenant-1",
            ExternalUserId = "m-alpha",
            Scope = "invoke",
        };

    private static ExternalToolInvocationSpec NyxIdInvocation(
        string callSiteId,
        string userServiceId = "us-shop-alpha",
        string operationId = "get_item") =>
        new()
        {
            CallSiteId = callSiteId,
            ToolName = "nyxid_proxy",
            Selector = new ExternalWorkflowCapabilitySelector
            {
                NyxIdOperation = new NyxIdOperationSelector
                {
                    UserServiceId = userServiceId,
                    EndpointId = operationId,
                },
            },
        };

    private static WorkflowCapabilityAdmissionPlan AdmissionPlan(
        string callSiteId,
        string userServiceId = "us-shop-alpha",
        string operationId = "get_item")
    {
        var plan = new WorkflowCapabilityAdmissionPlan
        {
            SchemaVersion = WorkflowCapabilityAdmissionPlanIntegrity.SchemaVersion,
        };
        plan.InvocationAdmissions.Add(new WorkflowCapabilityInvocationAdmission
        {
            CallSiteId = callSiteId,
            Capability = new ExternalWorkflowCapabilityRef
            {
                NyxIdUserService = new NyxIdUserServiceCapabilityRef
                {
                    UserServiceId = userServiceId,
                    ServiceSlugSnapshot = "api-shop",
                    EndpointId = operationId,
                    HttpMethod = "GET",
                    PathTemplate = "/items/{item_id}",
                    ContractDigest = "server-derived-digest",
                },
            },
        });
        return plan;
    }

    private static async Task ExecuteToolCallAsync(
        ToolCallModule module,
        RecordingWorkflowContext ctx,
        string toolName,
        string stepId = "call_proxy",
        string input = "{}",
        string executionId = "",
        IReadOnlyList<WorkflowFileRef>? inputFileRefs = null,
        string idempotencyKey = "",
        ExternalToolInvocationSpec? externalInvocation = null,
        DateTimeOffset? issuedAt = null)
    {
        var request = new StepRequestEvent
        {
            StepId = stepId,
            StepType = "tool_call",
            RunId = ctx.RunId,
            ExecutionId = executionId,
            IdempotencyKey = idempotencyKey,
            Input = input,
            Parameters = { ["tool"] = toolName },
            ExternalInvocation = externalInvocation,
        };
        request.InputFileRefs.Add(inputFileRefs?.Select(static fileRef => fileRef.Clone()) ?? []);

        await module.HandleAsync(
            Envelope(request, issuedAt),
            ctx,
            CancellationToken.None);
    }

    private static WorkflowFileRef BuildWorkflowFileRef(string fileId) =>
        new()
        {
            FileId = fileId,
            ArtifactId = $"artifact-{fileId}",
            SourceKind = WorkflowFileSourceKind.ChatInput,
            FileName = $"{fileId}.txt",
            MediaType = "text/plain",
        };

    private static StepCompletedEvent LastCompleted(RecordingWorkflowContext ctx) =>
        ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Last();

    private static EventEnvelope Envelope(IMessage evt, DateTimeOffset? issuedAt = null)
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(issuedAt ?? DateTimeOffset.UtcNow),
            Payload = Any.Pack(evt),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
        };
    }

    private sealed class FakeAgentTool(string name, Func<string, string> execute) : IWorkflowTool
    {
        public string Name { get; } = name;

        public Task<WorkflowToolExecutionResult> ExecuteAsync(WorkflowToolExecutionRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(WorkflowToolExecutionResult.Success(execute(request.ArgumentsJson)));
        }
    }

    private sealed class CountingAgentTool(string name, Func<string, string> execute) : IWorkflowTool
    {
        public string Name { get; } = name;

        public int ExecuteCalls { get; private set; }

        public Task<WorkflowToolExecutionResult> ExecuteAsync(WorkflowToolExecutionRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ExecuteCalls++;
            return Task.FromResult(WorkflowToolExecutionResult.Success(execute(request.ArgumentsJson)));
        }
    }

    private sealed class ScriptedResultWorkflowTool(
        string name,
        WorkflowToolExecutionResult result) : IWorkflowTool
    {
        public string Name { get; } = name;

        public Task<WorkflowToolExecutionResult> ExecuteAsync(
            WorkflowToolExecutionRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingWorkflowTool(string name) : IWorkflowTool
    {
        public string Name { get; } = name;

        public List<WorkflowToolExecutionRequest> Requests { get; } = [];

        public Task<WorkflowToolExecutionResult> ExecuteAsync(
            WorkflowToolExecutionRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(WorkflowToolExecutionResult.Success("{}"));
        }
    }

    private sealed class CapturingWorkflowTool(string name) : IWorkflowTool
    {
        public string Name { get; } = name;

        public WorkflowToolExecutionRequest? LastRequest { get; private set; }

        public List<WorkflowToolExecutionRequest> Requests { get; } = [];

        public Task<WorkflowToolExecutionResult> ExecuteAsync(WorkflowToolExecutionRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LastRequest = request;
            Requests.Add(request);
            return Task.FromResult(WorkflowToolExecutionResult.Success("""{"typed":true}"""));
        }
    }

    private sealed class RotatingCallerAccessTokenProvider : IWorkflowCallerAccessTokenProvider
    {
        public List<WorkflowCallerNyxIdAuthority> Authorities { get; } = [];

        public Task<string> IssueAsync(WorkflowCallerNyxIdAuthority authority, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Authorities.Add(authority.Clone());
            return Task.FromResult($"token-{Authorities.Count}");
        }
    }

    private sealed class ManagedHandoffWorkflowTool(string name, WorkflowManagedHandoffOutcome handoff) : IWorkflowTool
    {
        public string Name { get; } = name;

        public Task<WorkflowToolExecutionResult> ExecuteAsync(WorkflowToolExecutionRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new WorkflowToolExecutionResult("""{"status":"accepted"}""", handoff));
        }
    }

    private sealed class SingleToolSource(IWorkflowTool tool) : IWorkflowToolSource
    {
        public Task<IReadOnlyList<IWorkflowTool>> GetToolsAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<IWorkflowTool>>([tool]);
        }
    }

    private sealed class RecordingWorkflowContext
        : IWorkflowExecutionContext, IWorkflowExecutionRuntimeContextAccessor, IWorkflowExecutionStateHost
    {
        private readonly Dictionary<string, Any> _states = new(StringComparer.Ordinal);

        public EventEnvelope InboundEnvelope { get; } = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        public string AgentId => "agent-1";

        public string RunId => "run-1";

        public string? ScopeIdOverride { get; init; } = "scope-1";

        public string ScopeId => ScopeIdOverride!;

        public string ScheduleId { get; init; } = string.Empty;

        public IServiceProvider Services { get; } = new EmptyServiceProvider();

        public ILogger Logger { get; } = NullLogger.Instance;

        public WorkflowExecutionRuntimeContext RuntimeContext { get; } = new();

        public WorkflowRunExecutionContextState ExecutionContextState { get; } = new();

        public WorkflowRunExecutionContextState ExecutionContextSnapshot => ExecutionContextState.Clone();

        public WorkflowCapabilityAdmissionPlan CapabilityAdmissionPlan { get; init; } = new();

        public WorkflowCapabilityAdmissionPlan CapabilityAdmissionPlanSnapshot => CapabilityAdmissionPlan.Clone();

        public List<(IMessage Event, TopologyAudience Direction)> Published { get; } = [];

        public TState LoadState<TState>(string scopeKey)
            where TState : class, IMessage<TState>, new()
        {
            if (!_states.TryGetValue(scopeKey, out var packed) || !packed.Is(new TState().Descriptor))
                return new TState();

            return packed.Unpack<TState>() ?? new TState();
        }

        public IReadOnlyList<KeyValuePair<string, TState>> LoadStates<TState>(string scopeKeyPrefix = "")
            where TState : class, IMessage<TState>, new() =>
            _states
                .Where(x => string.IsNullOrEmpty(scopeKeyPrefix) || x.Key.StartsWith(scopeKeyPrefix, StringComparison.Ordinal))
                .Where(x => x.Value.Is(new TState().Descriptor))
                .Select(x => new KeyValuePair<string, TState>(x.Key, x.Value.Unpack<TState>() ?? new TState()))
                .ToList();

        public Task SaveStateAsync<TState>(string scopeKey, TState state, CancellationToken ct = default)
            where TState : class, IMessage<TState>
        {
            ct.ThrowIfCancellationRequested();
            _states[scopeKey] = Any.Pack(state);
            return Task.CompletedTask;
        }

        public Task ClearStateAsync(string scopeKey, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _states.Remove(scopeKey);
            return Task.CompletedTask;
        }

        public Any? GetExecutionState(string scopeKey) =>
            _states.GetValueOrDefault(scopeKey);

        public IReadOnlyList<KeyValuePair<string, Any>> GetExecutionStates() =>
            _states.ToList();

        public Task UpsertExecutionStateAsync(string scopeKey, Any state, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _states[scopeKey] = state;
            return Task.CompletedTask;
        }

        public Task ClearExecutionStateAsync(string scopeKey, CancellationToken ct = default) =>
            ClearStateAsync(scopeKey, ct);

        Task<WorkflowCompensationTransitionResult> IWorkflowExecutionStateHost.TryStartCompensationAsync(
            WorkflowCompletedEvent terminalFailure,
            StepCompletedEvent? terminalStep,
            CancellationToken ct)
        {
            _ = terminalFailure;
            _ = terminalStep;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(NoCompensableLedger());
        }

        Task IWorkflowExecutionStateHost.RecordCompensableStepDispatchAsync(
            CompensableStepDispatchedEvent evt,
            CancellationToken ct)
        {
            _ = evt;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<WorkflowCompensationTransitionResult> RecordCompensationStepCompletionAsync(
            CompensationStepCompletedEvent completion,
            CancellationToken ct = default)
        {
            _ = completion;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(NoCompensableLedger());
        }

        public Task<WorkflowCompensationTransitionResult> RecordCompensationPhaseDeadlineExceededAsync(
            string runId,
            string error,
            CancellationToken ct = default)
        {
            _ = runId;
            _ = error;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(NoCompensableLedger());
        }

        private static WorkflowCompensationTransitionResult NoCompensableLedger() =>
            new(
                WorkflowCompensationTransitionStatus.NoCompensableLedger,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty);

        public Task UpdateExecutionContextAsync(
            WorkflowRunExecutionContextDelta delta,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (delta.ClearLlm)
                ExecutionContextState.Llm = null;
            if (delta.ClearCallerCredential)
                ExecutionContextState.CallerCredential = null;
            if (delta.Llm != null)
            {
                ExecutionContextState.Llm = new WorkflowLlmExecutionContextState
                {
                    ModelOverride = delta.Llm.ModelOverride,
                    UserMemoryPrompt = delta.Llm.UserMemoryPrompt,
                    RoutePreference = delta.Llm.RoutePreference,
                };
                if (delta.Llm.HasMaxToolRoundsOverride)
                    ExecutionContextState.Llm.MaxToolRoundsOverride = delta.Llm.MaxToolRoundsOverride;
            }

            if (delta.CallerCredential != null)
            {
                ExecutionContextState.CallerCredential = new WorkflowCallerCredentialState
                {
                    BearerToken = delta.CallerCredential.BearerToken,
                };
            }

            return Task.CompletedTask;
        }

        public Task ClearExecutionContextAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ExecutionContextState.Llm = null;
            ExecutionContextState.CallerCredential = null;
            return Task.CompletedTask;
        }

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience direction = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            _ = options;
            Published.Add((evt, direction));
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            _ = targetActorId;
            _ = evt;
            _ = options;
            return Task.CompletedTask;
        }

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _ = dueTime;
            _ = evt;
            _ = options;
            return Task.FromResult(new RuntimeCallbackLease(AgentId, callbackId, 1, RuntimeCallbackBackend.InMemory));
        }

        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _ = lease;
            return Task.CompletedTask;
        }
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(System.Type serviceType) => null;
    }
}
