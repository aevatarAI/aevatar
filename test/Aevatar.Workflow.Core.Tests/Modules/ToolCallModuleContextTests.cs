using Aevatar.AI.Abstractions.CodeExecution;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Abstractions.Credentials;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Aevatar.Workflow.Core.Tests.Modules;

public sealed class ToolCallModuleContextTests
{
    [Fact]
    public void ProtectedMaterial_ShouldCaptureTheCompleteRequestOutsideDurablePendingState()
    {
        var ctx = new RecordingWorkflowContext();
        var request = ToolRequest(
            ctx,
            "nyxid_proxy",
            "call_proxy",
            "exec-material",
            NyxIdInvocation("wf-alpha/call_proxy"));
        request.Input = "request-input";
        request.Parameters["arguments"] = """{"account":"vendor-alpha"}""";
        request.IdempotencyKey = "idem-material";
        request.DisplayName = "Vendor lookup";
        request.InputFileRefs.Add(BuildWorkflowFileRef("file-material"));

        var material = ToolCallModule.BuildProtectedMaterial(
            request,
            ctx.RunId,
            "nyxid_proxy",
            "call-material",
            "approval-material");

        material.SchemaVersion.Should().Be(ToolCallModule.ProtectedMaterialSchema);
        material.RunId.Should().Be(ctx.RunId);
        material.StepId.Should().Be("call_proxy");
        material.ExecutionId.Should().Be("exec-material");
        material.ToolName.Should().Be("nyxid_proxy");
        material.CallId.Should().Be("call-material");
        material.ApprovalRequestId.Should().Be("approval-material");
        material.ArgumentsJson.Should().Be("""{"account":"vendor-alpha"}""");
        material.Input.Should().Be("request-input");
        material.InputFileRefs.Should().ContainSingle().Which.FileId.Should().Be("file-material");
        material.IdempotencyKey.Should().Be("idem-material");
        material.ExternalInvocation.Should().NotBeNull();
        material.ExternalInvocation.CallSiteId.Should().Be("wf-alpha/call_proxy");
        material.DisplayName.Should().Be("Vendor lookup");
        ToolCallModule.ComputeProtectedMaterialDigest(material)
            .Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public async Task ProtectedMaterial_ShouldRoundTripOnlyWithTheExpectedDigest()
    {
        var ctx = new RecordingWorkflowContext();
        var request = ToolRequest(ctx, "tool-alpha", "step-alpha", "exec-alpha");
        request.Parameters["arguments"] = """{"secret":"material-alpha"}""";
        var material = ToolCallModule.BuildProtectedMaterial(
            request,
            ctx.RunId,
            "tool-alpha",
            "call-alpha",
            string.Empty);
        var digest = ToolCallModule.ComputeProtectedMaterialDigest(material);

        var reference = await ToolCallModule.StoreProtectedMaterialAsync(
            material,
            ctx,
            CancellationToken.None);
        var resolved = await ToolCallModule.ResolveAndVerifyProtectedMaterialAsync(
            reference,
            digest,
            ctx.RunId,
            "step-alpha",
            "exec-alpha",
            "call-alpha",
            ctx,
            CancellationToken.None);

        resolved.Resolved.Should().BeTrue();
        resolved.IsTransientFailure.Should().BeFalse();
        resolved.ErrorCode.Should().BeEmpty();
        resolved.Material.Should().NotBeNull();
        resolved.Material!.ToByteArray().Should().Equal(material.ToByteArray());

        var tampered = await ToolCallModule.ResolveAndVerifyProtectedMaterialAsync(
            reference,
            new string('0', 64),
            ctx.RunId,
            "step-alpha",
            "exec-alpha",
            "call-alpha",
            ctx,
            CancellationToken.None);

        tampered.Resolved.Should().BeFalse();
        tampered.IsTransientFailure.Should().BeFalse();
        tampered.Material.Should().BeNull();
        tampered.ErrorCode.Should().Be(
            ToolCallModule.ToolCallProtectedMaterialErrorCodes.DigestMismatch);
    }

    [Fact]
    public async Task ProtectedMaterial_ShouldBecomeUnavailableAfterRevocation()
    {
        var ctx = new RecordingWorkflowContext();
        var request = ToolRequest(ctx, "tool-alpha", "step-alpha", "exec-alpha");
        var material = ToolCallModule.BuildProtectedMaterial(
            request,
            ctx.RunId,
            "tool-alpha",
            "call-alpha",
            string.Empty);
        var digest = ToolCallModule.ComputeProtectedMaterialDigest(material);
        var reference = await ToolCallModule.StoreProtectedMaterialAsync(
            material,
            ctx,
            CancellationToken.None);

        var revoked = await ToolCallModule.RevokeProtectedMaterialAsync(
            reference,
            ctx,
            CancellationToken.None);
        var resolved = await ToolCallModule.ResolveAndVerifyProtectedMaterialAsync(
            reference,
            digest,
            ctx.RunId,
            "step-alpha",
            "exec-alpha",
            "call-alpha",
            ctx,
            CancellationToken.None);

        revoked.Should().BeTrue();
        resolved.Resolved.Should().BeFalse();
        resolved.IsTransientFailure.Should().BeFalse();
        resolved.ErrorCode.Should().Be(
            ToolCallModule.ToolCallProtectedMaterialErrorCodes.Unavailable);
    }

    [Fact]
    public async Task ProtectedMaterial_WhenRuntimeSecretStoreIsUnavailable_ShouldReturnTransientResolutionFailure()
    {
        var storeContext = new RecordingWorkflowContext();
        var request = ToolRequest(storeContext, "tool-alpha", "step-alpha", "exec-alpha");
        var material = ToolCallModule.BuildProtectedMaterial(
            request,
            storeContext.RunId,
            "tool-alpha",
            "call-alpha",
            string.Empty);
        var reference = await ToolCallModule.StoreProtectedMaterialAsync(
            material,
            storeContext,
            CancellationToken.None);
        var unavailableContext = new RecordingWorkflowContext { RuntimeSecretStore = null };

        var resolved = await ToolCallModule.ResolveAndVerifyProtectedMaterialAsync(
            reference,
            ToolCallModule.ComputeProtectedMaterialDigest(material),
            unavailableContext.RunId,
            "step-alpha",
            "exec-alpha",
            "call-alpha",
            unavailableContext,
            CancellationToken.None);

        resolved.Resolved.Should().BeFalse();
        resolved.IsTransientFailure.Should().BeTrue();
        resolved.ErrorCode.Should().Be(
            ToolCallModule.ToolCallProtectedMaterialErrorCodes.StoreUnavailable);
    }

    [Fact]
    public async Task ProtectedMaterial_ShouldFailClosedWithoutARuntimeSecretStore()
    {
        var ctx = new RecordingWorkflowContext { RuntimeSecretStore = null };
        var request = ToolRequest(ctx, "tool-alpha", "step-alpha", "exec-alpha");
        var material = ToolCallModule.BuildProtectedMaterial(
            request,
            ctx.RunId,
            "tool-alpha",
            "call-alpha",
            string.Empty);

        var action = () => ToolCallModule.StoreProtectedMaterialAsync(
            material,
            ctx,
            CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(ToolCallModule.ToolCallProtectedMaterialErrorCodes.StoreUnavailable);
    }

    [Fact]
    public async Task ToolCallModule_WhenInitialPendingSaveFailsBeforeCommit_ShouldRevokeUnownedProtectedMaterial()
    {
        var logger = new RecordingToolCallLogger();
        var tool = new CountingAgentTool("tool-alpha", static _ => "{}");
        var store = new TrackingRuntimeSecretStore();
        var module = CreateModule(tool, logger: logger);
        var ctx = new RecordingWorkflowContext
        {
            RuntimeSecretStore = store,
            FailStateSavesRemaining = 1,
            Logger = logger,
        };
        var request = ToolRequest(ctx, tool.Name, "step-alpha", "exec-save-before-commit");

        await FluentActions.Awaiting(() =>
                module.HandleAsync(Envelope(request), ctx, CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("simulated state save failure");

        store.PutCalls.Should().Be(1);
        store.RevokeCalls.Should().Be(1);
        store.LastStoredReference.Should().NotBeNull();
        var resolved = await store.ResolveAsync(
            ResolveRequest(store.LastStoredReference!),
            CancellationToken.None);
        resolved.Resolved.Should().BeFalse();
        ctx.LoadState<ToolCallModuleState>("tool_call").PendingExecutions.Should().BeEmpty();
        ctx.Scheduled.Should().BeEmpty();
        tool.ExecuteCalls.Should().Be(0);
        logger.Entries.Should().NotContain(entry =>
            Equals(entry.Properties.GetValueOrDefault("Waterline"), "pending_state_persisted"));
    }

    [Fact]
    public async Task ToolCallModule_WhenInitialPendingCommitSucceedsButStatePublicationFails_ShouldRetainOwnedProtectedMaterial()
    {
        var logger = new RecordingToolCallLogger();
        var tool = new CountingAgentTool("tool-alpha", static _ => "{}");
        var store = new TrackingRuntimeSecretStore();
        var module = CreateModule(tool, logger: logger);
        var ctx = new RecordingWorkflowContext
        {
            RuntimeSecretStore = store,
            FailStatePublicationsAfterCommitRemaining = 1,
            Logger = logger,
        };
        var request = ToolRequest(ctx, tool.Name, "step-alpha", "exec-save-after-commit");

        await FluentActions.Awaiting(() =>
                module.HandleAsync(Envelope(request), ctx, CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("simulated state publication failure");

        store.PutCalls.Should().Be(1);
        store.RevokeCalls.Should().Be(0);
        store.LastStoredReference.Should().NotBeNull();
        var reference = store.LastStoredReference!;
        var pending = ctx.LoadState<ToolCallModuleState>("tool_call")
            .PendingExecutions.Values.Should().ContainSingle().Subject;
        pending.AttemptPreparationStartedAtUtc.Should().NotBeNull();
        pending.ProtectedMaterialReference.ToByteArray().Should().Equal(reference.ToByteArray());
        var resolved = await store.ResolveAsync(ResolveRequest(reference), CancellationToken.None);
        resolved.Resolved.Should().BeTrue();
        ctx.Scheduled.Should().BeEmpty();
        tool.ExecuteCalls.Should().Be(0);
        var persisted = logger.Entries.Should().ContainSingle(entry =>
            Equals(entry.Properties.GetValueOrDefault("Waterline"), "pending_state_persisted")).Subject;
        persisted.Properties["CommittedEventId"].Should().Be("recording-workflow-state-1");
        persisted.Properties["CommittedStateVersion"].Should().Be(1L);
    }

    [Fact]
    public async Task ToolCallModule_WhenOrphanCleanupFails_ShouldPreserveTheOriginalStateSaveException()
    {
        var tool = new CountingAgentTool("tool-alpha", static _ => "{}");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext
        {
            RuntimeSecretStore = new FailingCleanupRuntimeSecretStore(),
            FailStateSavesRemaining = 1,
            Logger = new ThrowingLogger(),
        };
        var request = ToolRequest(ctx, tool.Name, "step-alpha", "exec-cleanup-failure");

        var failure = await FluentActions.Awaiting(() =>
                module.HandleAsync(Envelope(request), ctx, CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("simulated state save failure");

        failure.Which.Data.Contains(
            "WorkflowToolCallProtectedMaterialCleanupFailure").Should().BeTrue();
        failure.Which.Data.Contains(
            "WorkflowToolCallProtectedMaterialCleanupLoggingFailure").Should().BeTrue();
        tool.ExecuteCalls.Should().Be(0);
    }

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
    public async Task ToolCallModule_ShouldProjectNestedJsonBeforeDurableCompletionPersistence()
    {
        const string form =
            """[{"id":"field-list","value":[[{"id":"vendor-widget","value":"Acme Pte Ltd"},{"id":"secret-widget","value":"secret-bank-account"}]]}]""";
        var response = JsonSerializer.Serialize(new
        {
            code = 0,
            data = new
            {
                instance_code = "instance-1",
                status = "PENDING",
                form,
                sensitive_member_id = "ou-sensitive-member",
            },
        });
        var projection = ApprovalDetailProjection();
        var tool = new FakeAgentTool("nyxid_proxy", _ => response);
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext
        {
            CapabilityAdmissionPlan = AdmissionPlan("wf-alpha/call_proxy", projection: projection),
            FailNextPublishType = typeof(WorkflowToolCallCompletedEvent),
        };

        var act = () => ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            executionId: "exec-projection",
            externalInvocation: NyxIdInvocation("wf-alpha/call_proxy", projection: projection));

        await act.Should().ThrowAsync<WorkflowDurablePublicationPendingException>();
        var durable = ctx.LoadState<ToolCallModuleState>("tool_call")
            .Completions.Should().ContainSingle().Subject;
        durable.ToolCompletion.ResultJson.Should().Be(
            "{\"instance_code\":\"instance-1\",\"status\":\"PENDING\",\"vendor\":\"Acme Pte Ltd\"}");
        durable.StepCompletion.Output.Should().Be(durable.ToolCompletion.ResultJson);
        durable.ToString().Should().NotContain("secret-bank-account");
        durable.ToString().Should().NotContain("ou-sensitive-member");
    }

    [Fact]
    public async Task ToolCallModule_ShouldFailWithoutPersistingRawResponse_WhenProjectionDoesNotMatch()
    {
        const string rawResponse =
            """{"code":0,"data":{"instance_code":"instance-1","status":"PENDING","form":"[]","sensitive":"must-not-persist"}}""";
        var projection = ApprovalDetailProjection();
        var tool = new FakeAgentTool("nyxid_proxy", _ => rawResponse);
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext
        {
            CapabilityAdmissionPlan = AdmissionPlan("wf-alpha/call_proxy", projection: projection),
        };

        await ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            externalInvocation: NyxIdInvocation("wf-alpha/call_proxy", projection: projection));

        var toolCompletion = ctx.Published.Select(static item => item.Event)
            .OfType<WorkflowToolCallCompletedEvent>()
            .Should().ContainSingle().Subject;
        toolCompletion.Success.Should().BeFalse();
        toolCompletion.ResultJson.Should().BeEmpty();
        toolCompletion.Error.Should().Contain("WORKFLOW_TOOL_RESPONSE_PROJECTION_FAILED");
        LastCompleted(ctx).Output.Should().BeEmpty();
        ctx.Published.Select(static item => item.Event).Select(static item => item.ToString())
            .Should().NotContain(static value => value.Contains("must-not-persist", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ToolCallModule_ShouldFailWithoutPersistingRawResponse_WhenProjectionExceedsDurableLimit()
    {
        const string sensitiveMarker = "oversized-sensitive-marker";
        var response = JsonSerializer.Serialize(new
        {
            payload = sensitiveMarker + new string('x', 64 * 1024),
        });
        var projection = new WorkflowToolResponseProjection
        {
            Fields =
            {
                Field("payload", Pointer("/payload")),
            },
        };
        var tool = new FakeAgentTool("nyxid_proxy", _ => response);
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext
        {
            CapabilityAdmissionPlan = AdmissionPlan("wf-alpha/call_proxy", projection: projection),
        };

        await ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            externalInvocation: NyxIdInvocation("wf-alpha/call_proxy", projection: projection));

        var toolCompletion = ctx.Published.Select(static item => item.Event)
            .OfType<WorkflowToolCallCompletedEvent>()
            .Should().ContainSingle().Subject;
        toolCompletion.Success.Should().BeFalse();
        toolCompletion.ResultJson.Should().BeEmpty();
        toolCompletion.Error.Should().Contain("WORKFLOW_TOOL_RESPONSE_PROJECTION_FAILED");
        ctx.LoadState<ToolCallModuleState>("tool_call").ToString()
            .Should().NotContain(sensitiveMarker);
        ctx.Published.Select(static item => item.Event).Select(static item => item.ToString())
            .Should().NotContain(value => value.Contains(sensitiveMarker, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ToolCallModule_ShouldSanitizeProjectedProviderFailureBeforeDurablePersistenceAndReplay()
    {
        const string sensitiveMarker = "sensitive-marker";
        var projection = ApprovalDetailProjection();
        var tool = new ScriptedResultWorkflowTool(
            "nyxid_proxy",
            WorkflowToolExecutionResult.Failed(
                $$"""{"raw":"{{sensitiveMarker}}"}""",
                "NYXID_PROXY_HTTP_503",
                $"Provider response contained {sensitiveMarker}."));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext
        {
            CapabilityAdmissionPlan = AdmissionPlan("wf-alpha/call_proxy", projection: projection),
            FailNextPublishType = typeof(WorkflowToolCallCompletedEvent),
        };

        var firstAttempt = () => ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            executionId: "exec-projected-failure",
            externalInvocation: NyxIdInvocation("wf-alpha/call_proxy", projection: projection));

        await firstAttempt.Should().ThrowAsync<WorkflowDurablePublicationPendingException>();
        tool.ExecuteCalls.Should().Be(1);
        var durableState = ctx.LoadState<ToolCallModuleState>("tool_call");
        var durable = durableState.Completions.Should().ContainSingle().Subject;
        durable.ToolCompletion.ResultJson.Should().BeEmpty();
        durable.ToolCompletion.Error.Should().Contain("NYXID_PROXY_HTTP_503");
        durable.ToolCompletion.Error.Should().Contain(
            "The projected tool call failed before a durable response was produced.");
        durableState.ToString().Should().NotContain(sensitiveMarker);

        await ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            executionId: "exec-projected-failure",
            externalInvocation: NyxIdInvocation("wf-alpha/call_proxy", projection: projection));

        tool.ExecuteCalls.Should().Be(1);
        var publishedCompletions = ctx.Published.Select(static item => item.Event)
            .Where(static item => item is WorkflowToolCallCompletedEvent or StepCompletedEvent)
            .ToList();
        publishedCompletions.Should().HaveCount(2);
        publishedCompletions.Select(static item => item.ToString())
            .Should().NotContain(value => value.Contains(sensitiveMarker, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ToolCallModule_ShouldReplaceUntrustedProjectedProviderFailureCode()
    {
        const string sensitiveMarker = "sensitive-marker";
        var projection = ApprovalDetailProjection();
        var tool = new ScriptedResultWorkflowTool(
            "nyxid_proxy",
            WorkflowToolExecutionResult.Failed(
                "{}",
                $"NYXID_PROXY_HTTP_503-{sensitiveMarker}",
                "Provider failure."));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext
        {
            CapabilityAdmissionPlan = AdmissionPlan("wf-alpha/call_proxy", projection: projection),
        };

        await ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            externalInvocation: NyxIdInvocation("wf-alpha/call_proxy", projection: projection));

        var toolCompletion = ctx.Published.Select(static item => item.Event)
            .OfType<WorkflowToolCallCompletedEvent>()
            .Should().ContainSingle().Subject;
        toolCompletion.Error.Should().Contain("WORKFLOW_PROJECTED_TOOL_CALL_FAILED");
        toolCompletion.Error.Should().NotContain(sensitiveMarker);
    }

    [Fact]
    public async Task ToolCallModule_ShouldSanitizeProjectedToolExceptionsBeforeLoggingOrPersistence()
    {
        const string sensitiveMarker = "sensitive-exception-marker";
        var projection = ApprovalDetailProjection();
        var tool = new FakeAgentTool(
            "nyxid_proxy",
            _ => throw new InvalidOperationException($"Provider response contained {sensitiveMarker}."));
        var logger = new RecordingLogger();
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext
        {
            Logger = logger,
            CapabilityAdmissionPlan = AdmissionPlan("wf-alpha/call_proxy", projection: projection),
        };

        await ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            externalInvocation: NyxIdInvocation("wf-alpha/call_proxy", projection: projection));

        var toolCompletion = ctx.Published.Select(static item => item.Event)
            .OfType<WorkflowToolCallCompletedEvent>()
            .Should().ContainSingle().Subject;
        toolCompletion.Error.Should().Contain("WORKFLOW_PROJECTED_TOOL_CALL_FAILED");
        ctx.LoadState<ToolCallModuleState>("tool_call").ToString()
            .Should().NotContain(sensitiveMarker);
        logger.Entries.Should().NotContain(
            entry => entry.Contains(sensitiveMarker, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ToolCallModule_ShouldPersistAuthoritativeArguments_WhenProjectedToolRequestsApproval()
    {
        const string sensitiveMarker = "provider-replaced-arguments";
        const string requestArguments = "{\"visible\":true}";
        var projection = ApprovalDetailProjection();
        var tool = new ScriptedResultWorkflowTool(
            "nyxid_proxy",
            new WorkflowToolExecutionResult(
                string.Empty,
                PendingApproval: new WorkflowToolApprovalPendingOutcome(
                    "approval-alpha",
                    "nyxid_proxy",
                    "tool-call-alpha",
                    $"{{\"raw\":\"{sensitiveMarker}\"}}",
                    "interactive",
                    IsReadOnly: true,
                    IsDestructive: false)));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext
        {
            CapabilityAdmissionPlan = AdmissionPlan("wf-alpha/call_proxy", projection: projection),
        };

        await ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            input: requestArguments,
            executionId: "exec-approval",
            externalInvocation: NyxIdInvocation("wf-alpha/call_proxy", projection: projection));

        var durable = ctx.LoadState<ToolCallModuleState>("tool_call");
        durable.PendingApprovals.Should().ContainSingle();
        var pending = durable.PendingApprovals.Values.Single();
        pending.ProtectedMaterialReference.Should().NotBeNull();
        pending.ProtectedMaterialDigestSha256.Should().MatchRegex("^[0-9a-f]{64}$");
        pending.ExecutionPhase.Should().Be(WorkflowToolCallExecutionPhase.ApprovalPending);
        pending.ArgumentsJson.Should().BeEmpty();
        pending.Input.Should().BeEmpty();
        pending.InputFileRefs.Should().BeEmpty();
        pending.IdempotencyKey.Should().BeEmpty();
        pending.ExternalInvocation.Should().BeNull();
        pending.DisplayName.Should().BeEmpty();
        durable.ToString().Should().NotContain(sensitiveMarker);
        durable.ToString().Should().NotContain(requestArguments);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task ToolCallModule_ShouldEnforceProjectedResponseByteLimit(
        int bytesOverLimit,
        bool expectedSuccess)
    {
        const string sensitiveMarker = "sensitive-boundary-marker";
        const string jsonEnvelope = "{\"payload\":\"\"}";
        var payloadLength = WorkflowToolResponseProjectionContract.MaxProjectedResponseBytes -
                            Encoding.UTF8.GetByteCount(jsonEnvelope) +
                            bytesOverLimit;
        var payload = sensitiveMarker + new string('x', payloadLength - sensitiveMarker.Length);
        var response = JsonSerializer.Serialize(new { payload });
        var projection = PayloadProjection();
        var tool = new FakeAgentTool("nyxid_proxy", _ => response);
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext
        {
            CapabilityAdmissionPlan = AdmissionPlan("wf-alpha/call_proxy", projection: projection),
        };

        await ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            externalInvocation: NyxIdInvocation("wf-alpha/call_proxy", projection: projection));

        var completion = LastCompleted(ctx);
        completion.Success.Should().Be(expectedSuccess);
        if (expectedSuccess)
        {
            Encoding.UTF8.GetByteCount(completion.Output).Should()
                .Be(WorkflowToolResponseProjectionContract.MaxProjectedResponseBytes);
        }
        else
        {
            completion.Output.Should().BeEmpty();
            completion.Error.Should().Contain("WORKFLOW_TOOL_RESPONSE_PROJECTION_FAILED");
            ctx.Published.Select(static item => item.Event).Select(static item => item.ToString())
                .Should().NotContain(value => value.Contains(sensitiveMarker, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task ToolCallModule_ShouldRejectProjectionThatDiffersFromAdmissionProof()
    {
        var admittedProjection = ApprovalDetailProjection();
        var runtimeProjection = ApprovalDetailProjection();
        runtimeProjection.Fields.Single(static field => field.OutputName == "status")
            .Operations[0].JsonPointer = "/data/other_status";
        var tool = new RecordingWorkflowTool("nyxid_proxy");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext
        {
            CapabilityAdmissionPlan = AdmissionPlan(
                "wf-alpha/call_proxy",
                projection: admittedProjection),
        };

        await ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            externalInvocation: NyxIdInvocation(
                "wf-alpha/call_proxy",
                projection: runtimeProjection));

        tool.Requests.Should().BeEmpty();
        LastCompleted(ctx).Error.Should().Contain("EXTERNAL_CAPABILITY_RESPONSE_PROJECTION_MISMATCH");
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
    public async Task ToolCallModule_PreviousSchemaCodeExecute_ShouldRevalidateAtRuntimeWithoutRebind()
    {
        var tool = new RecordingWorkflowTool("code_execute");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext
        {
            CapabilityAdmissionPlan = new WorkflowCapabilityAdmissionPlan
            {
                SchemaVersion = WorkflowCapabilityAdmissionPlanIntegrity.PreviousSchemaVersion,
            },
        };

        await ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            externalInvocation: CodeExecutionInvocation("wf-alpha/call_code"));

        tool.Requests.Should().ContainSingle().Which.InvocationAdmission.Should().BeNull();
        LastCompleted(ctx).Success.Should().BeTrue();
    }

    [Fact]
    public async Task ToolCallModule_PreviousSchemaNyxIdProof_ShouldRemainExecutable()
    {
        var tool = new RecordingWorkflowTool("nyxid_proxy");
        var module = CreateModule(tool);
        var plan = AdmissionPlan("wf-alpha/call_proxy");
        plan.SchemaVersion = WorkflowCapabilityAdmissionPlanIntegrity.PreviousSchemaVersion;
        var ctx = new RecordingWorkflowContext { CapabilityAdmissionPlan = plan };

        await ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            externalInvocation: NyxIdInvocation("wf-alpha/call_proxy"));

        tool.Requests.Should().ContainSingle();
        LastCompleted(ctx).Success.Should().BeTrue();
    }

    [Fact]
    public async Task ToolCallModule_PreviousSchemaProjectedProof_ShouldRequireRebind()
    {
        var projection = ApprovalDetailProjection();
        var tool = new RecordingWorkflowTool("nyxid_proxy");
        var module = CreateModule(tool);
        var plan = AdmissionPlan("wf-alpha/call_proxy", projection: projection);
        plan.SchemaVersion = WorkflowCapabilityAdmissionPlanIntegrity.PreviousSchemaVersion;
        var ctx = new RecordingWorkflowContext { CapabilityAdmissionPlan = plan };

        await ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            externalInvocation: NyxIdInvocation("wf-alpha/call_proxy", projection: projection));

        tool.Requests.Should().BeEmpty();
        LastCompleted(ctx).Success.Should().BeFalse();
        LastCompleted(ctx).Error.Should().Contain(
            WorkflowCapabilityAdmissionPlanIntegrity.RebindRequiredCode);
    }

    [Fact]
    public async Task ToolCallModule_V5ProjectedProof_ShouldRequireRebindBeforeDirectExecution()
    {
        var projection = ApprovalDetailProjection();
        var tool = new RecordingWorkflowTool("nyxid_proxy");
        var module = CreateModule(tool);
        var plan = AdmissionPlan("wf-alpha/call_proxy", projection: projection);
        plan.SchemaVersion = WorkflowCapabilityAdmissionPlanIntegrity.CodeRouteSchemaVersion;
        var ctx = new RecordingWorkflowContext { CapabilityAdmissionPlan = plan };

        await ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            externalInvocation: NyxIdInvocation("wf-alpha/call_proxy", projection: projection));

        tool.Requests.Should().BeEmpty();
        LastCompleted(ctx).Success.Should().BeFalse();
        LastCompleted(ctx).Error.Should().Contain(
            WorkflowCapabilityAdmissionPlanIntegrity.RebindRequiredCode);
    }

    [Fact]
    public async Task ToolCallModule_V5ProjectedProof_ShouldRequireRebindBeforeResumeExecution()
    {
        const string stepId = "call_proxy";
        const string executionId = "exec-approval";
        const string toolCallId = "workflow:run-1:call_proxy:exec-approval";
        const string approvalRequestId = "approval-alpha";
        var projection = ApprovalDetailProjection();
        var invocation = NyxIdInvocation("wf-alpha/call_proxy", projection: projection);
        var plan = AdmissionPlan("wf-alpha/call_proxy", projection: projection);
        plan.SchemaVersion = WorkflowCapabilityAdmissionPlanIntegrity.CodeRouteSchemaVersion;
        var tool = new RecordingWorkflowTool("nyxid_proxy");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext { CapabilityAdmissionPlan = plan };
        var protectedRequest = ToolRequest(ctx, tool.Name, stepId, executionId, invocation);
        var protectedMaterial = ToolCallModule.BuildProtectedMaterial(
            protectedRequest,
            ctx.RunId,
            tool.Name,
            toolCallId,
            approvalRequestId);
        var protectedMaterialReference = await ToolCallModule.StoreProtectedMaterialAsync(
            protectedMaterial,
            ctx,
            CancellationToken.None);
        var pending = new PendingToolCallApprovalState
        {
            RunId = ctx.RunId,
            StepId = stepId,
            ExecutionId = executionId,
            ToolName = tool.Name,
            ToolCallId = toolCallId,
            ApprovalRequestId = approvalRequestId,
            ProtectedMaterialReference = protectedMaterialReference,
            ProtectedMaterialDigestSha256 = ToolCallModule.ComputeProtectedMaterialDigest(protectedMaterial),
            ExecutionPhase = WorkflowToolCallExecutionPhase.ApprovalPending,
            TimeoutMs = 60_000,
            TimeoutDeadlineUnixMs = ((IWorkflowExecutionContext)ctx).UtcNow
                .AddMinutes(1)
                .ToUnixTimeMilliseconds(),
            ContinuationId = "approval-continuation-alpha",
            Attempt = 1,
        };
        await ctx.SaveStateAsync("tool_call", new ToolCallModuleState
        {
            PendingApprovals =
            {
                [$"{ctx.RunId}:{stepId}:{executionId}:{toolCallId}:{approvalRequestId}"] = pending,
            },
        });

        await module.HandleAsync(
            Envelope(new WorkflowResumedEvent
            {
                RunId = ctx.RunId,
                StepId = stepId,
                Approved = true,
                ToolApproval = new WorkflowToolApprovalResume
                {
                    ExecutionId = executionId,
                    ToolCallId = toolCallId,
                    ApprovalRequestId = approvalRequestId,
                },
            }),
            ctx,
            CancellationToken.None);

        tool.Requests.Should().BeEmpty();
        LastCompleted(ctx).Success.Should().BeFalse();
        LastCompleted(ctx).Error.Should().Contain(
            WorkflowCapabilityAdmissionPlanIntegrity.RebindRequiredCode);
        ctx.LoadState<ToolCallModuleState>("tool_call").PendingApprovals.Should().BeEmpty();
    }

    [Fact]
    public async Task ToolCallModule_CurrentSchemaCodeExecuteWithoutProof_ShouldFailClosed()
    {
        var tool = new RecordingWorkflowTool("code_execute");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext
        {
            CapabilityAdmissionPlan = new WorkflowCapabilityAdmissionPlan
            {
                SchemaVersion = WorkflowCapabilityAdmissionPlanIntegrity.SchemaVersion,
            },
        };

        await ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            externalInvocation: CodeExecutionInvocation("wf-alpha/call_code"));

        tool.Requests.Should().BeEmpty();
        LastCompleted(ctx).Success.Should().BeFalse();
        LastCompleted(ctx).Error.Should().Contain("EXTERNAL_CAPABILITY_ADMISSION_PLAN_MISSING");
    }

    [Fact]
    public async Task ToolCallModule_ShouldPublishToolEventsWithWorkflowExecutionCallId()
    {
        const string sensitiveArguments = """{"token":"started-event-secret"}""";
        var tool = new FakeAgentTool("call_id_reader", _ => "{}");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            stepId: "call_proxy",
            input: sensitiveArguments,
            executionId: "exec-1");

        var started = ctx.Published.Select(x => x.Event).OfType<WorkflowToolCallStartedEvent>().Single();
        started.CallId.Should().Be("workflow:run-1:call_proxy:exec-1");
        started.ArgumentsJson.Should().BeEmpty();
        started.ToString().Should().NotContain("started-event-secret");
        ctx.Published.Select(x => x.Event).OfType<WorkflowToolCallCompletedEvent>().Single().CallId
            .Should().Be("workflow:run-1:call_proxy:exec-1");
    }

    [Fact]
    public async Task ToolCallModule_WhenStepRequestIsRedelivered_ShouldReusePublishedSuccess()
    {
        var tool = new CountingAgentTool("counting_tool", _ => """{"ok":true}""");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name, executionId: "exec-1");
        await ExecuteToolCallAsync(module, ctx, tool.Name, executionId: "exec-1");

        tool.ExecuteCalls.Should().Be(1);
        ctx.Published.Select(x => x.Event).OfType<WorkflowToolCallStartedEvent>().Should().ContainSingle();
        ctx.Published.Select(x => x.Event).OfType<WorkflowToolCallCompletedEvent>()
            .Should().ContainSingle().Which.Success.Should().BeTrue();
        ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>()
            .Should().ContainSingle().Which.Success.Should().BeTrue();
        var reloaded = ctx.LoadState<ToolCallModuleState>("tool_call");
        reloaded.Completions.Should().BeEmpty();
        var tombstone = reloaded.CompletionTombstones.Should().ContainSingle().Subject.Value;
        tombstone.RunId.Should().Be("run-1");
        tombstone.StepId.Should().Be("call_proxy");
        tombstone.CallId.Should().Be("workflow:run-1:call_proxy:exec-1");
        tombstone.ExecutionId.Should().Be("exec-1");
        tombstone.TerminalDecision.Should().Be(WorkflowToolCallTerminalDecision.NoApproval);
    }

    [Fact]
    public async Task ToolCallModule_AfterMoreThan128TerminalCalls_ShouldStillDedupeTheFirstCall()
    {
        var tool = new CountingAgentTool("counting_tool", _ => "{}");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        for (var index = 0; index < 129; index++)
            await ExecuteToolCallAsync(module, ctx, tool.Name, executionId: $"exec-{index}");

        var reloaded = ToolCallModuleState.Parser.ParseFrom(
            ctx.LoadState<ToolCallModuleState>("tool_call").ToByteArray());
        await ctx.SaveStateAsync("tool_call", reloaded);
        await ExecuteToolCallAsync(module, ctx, tool.Name, executionId: "exec-0");

        tool.ExecuteCalls.Should().Be(129);
        var finalState = ctx.LoadState<ToolCallModuleState>("tool_call");
        finalState.Completions.Should().BeEmpty();
        finalState.CompletionTombstones.Should().HaveCount(129);
        ctx.Published.Select(x => x.Event).OfType<WorkflowToolCallStartedEvent>().Should().HaveCount(129);
        ctx.Published.Select(x => x.Event).OfType<WorkflowToolCallCompletedEvent>().Should().HaveCount(129);
        ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Should().HaveCount(129);
    }

    [Fact]
    public async Task ToolCallModule_WhenTypedFailureIsRedelivered_ShouldNotCreateAnotherFailure()
    {
        var tool = new ScriptedResultWorkflowTool(
            "failing_tool",
            WorkflowToolExecutionResult.Failed(
                """{"status":503}""",
                "NYXID_PROXY_HTTP_503",
                "The service request failed."));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name, executionId: "exec-1");
        await ExecuteToolCallAsync(module, ctx, tool.Name, executionId: "exec-1");

        tool.ExecuteCalls.Should().Be(1);
        var toolFailure = ctx.Published.Select(x => x.Event)
            .OfType<WorkflowToolCallCompletedEvent>()
            .Should().ContainSingle().Subject;
        toolFailure.Success.Should().BeFalse();
        toolFailure.Error.Should().Contain("NYXID_PROXY_HTTP_503");
        toolFailure.Error.Should().NotContain("outcome_uncertain");
        ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>()
            .Should().ContainSingle().Which.Success.Should().BeFalse();
    }

    [Fact]
    public async Task ToolCallModule_ShouldRoundTripTypedUncertainFailureThroughAttemptSignal()
    {
        var tool = new ScriptedResultWorkflowTool(
            "uncertain_tool",
            WorkflowToolExecutionResult.Failed(
                string.Empty,
                "code_execution_outcome_uncertain",
                "The provider outcome is uncertain.",
                terminalInvoked: true,
                retryable: false,
                WorkflowStepFailureOutcome.OutcomeUncertain));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        var request = ToolRequest(ctx, tool.Name, "step-uncertain", "exec-uncertain");
        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);
        var attempt = await ctx.WaitForPublishedAsync<WorkflowToolCallAttemptCompletedEvent>();
        attempt.Failure.FailureOutcome.Should().Be(WorkflowStepFailureOutcome.OutcomeUncertain);
        var completionEnvelope = ctx.PublishedEnvelope(attempt);
        completionEnvelope.Payload = Any.Pack(
            WorkflowToolCallAttemptCompletedEvent.Parser.ParseFrom(attempt.ToByteArray()));

        await module.HandleAsync(completionEnvelope, ctx, CancellationToken.None);

        ctx.Published.Select(static item => item.Event)
            .OfType<StepCompletedEvent>()
            .Should().ContainSingle()
            .Which.FailureOutcome.Should().Be(WorkflowStepFailureOutcome.OutcomeUncertain);
    }

    [Fact]
    public async Task ToolCallModule_WhenCompletionPublishFails_ShouldReplayDurableOutcomeWithoutReexecution()
    {
        var tool = new CountingAgentTool("counting_tool", _ => """{"ok":true}""");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext
        {
            FailNextPublishType = typeof(WorkflowToolCallCompletedEvent),
        };

        var firstAttempt = () => ExecuteToolCallAsync(module, ctx, tool.Name, executionId: "exec-1");
        await firstAttempt.Should().ThrowAsync<WorkflowDurablePublicationPendingException>()
            .WithMessage("Durable workflow tool completion remains pending.");

        tool.ExecuteCalls.Should().Be(1);
        var unpublished = ctx.LoadState<ToolCallModuleState>("tool_call")
            .Completions.Should().ContainSingle().Subject;
        unpublished.ToolCompletionPublished.Should().BeFalse();
        unpublished.StepCompletionPublished.Should().BeFalse();

        await ExecuteToolCallAsync(module, ctx, tool.Name, executionId: "exec-1");

        tool.ExecuteCalls.Should().Be(1);
        ctx.Published.Select(x => x.Event).OfType<WorkflowToolCallStartedEvent>().Should().ContainSingle();
        ctx.Published.Select(x => x.Event).OfType<WorkflowToolCallCompletedEvent>()
            .Should().ContainSingle().Which.Success.Should().BeTrue();
        ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>()
            .Should().ContainSingle().Which.Success.Should().BeTrue();
        var published = ctx.LoadState<ToolCallModuleState>("tool_call");
        published.Completions.Should().BeEmpty();
        published.CompletionTombstones.Should().ContainSingle();
    }

    [Fact]
    public async Task ToolCallModule_WhenToolCompletionPublishesBeforeTransportFailure_ShouldReplayWithStableOperationId()
    {
        var tool = new CountingAgentTool("counting_tool", _ => """{"ok":true}""");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext
        {
            FailAfterNextPublishType = typeof(WorkflowToolCallCompletedEvent),
        };

        var firstAttempt = () => ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            executionId: "exec-tool-published");
        await firstAttempt.Should().ThrowAsync<WorkflowDurablePublicationPendingException>()
            .WithMessage("Durable workflow tool completion remains pending.");

        var retained = ctx.LoadState<ToolCallModuleState>("tool_call");
        var retainedCompletion = retained.Completions.Should().ContainSingle().Subject;
        retainedCompletion.ToolCompletionPublished.Should().BeFalse();
        retainedCompletion.StepCompletionPublished.Should().BeFalse();
        retained.CompletionTombstones.Should().BeEmpty();

        await ExecuteToolCallAsync(module, ctx, tool.Name, executionId: "exec-tool-published");

        tool.ExecuteCalls.Should().Be(1);
        var toolPublications = ctx.Published.Select(static item => item.Event)
            .OfType<WorkflowToolCallCompletedEvent>()
            .ToList();
        toolPublications.Should().HaveCount(2);
        toolPublications.Select(ctx.PublishedOperationId).Distinct()
            .Should().ContainSingle().Which.Should().NotBeNullOrWhiteSpace();
        ctx.Published.Select(static item => item.Event)
            .OfType<StepCompletedEvent>().Should().ContainSingle();
        var settled = ctx.LoadState<ToolCallModuleState>("tool_call");
        settled.Completions.Should().BeEmpty();
        settled.CompletionTombstones.Should().ContainSingle();
    }

    [Fact]
    public async Task ToolCallModule_WhenStepCompletionPublishesBeforeTransportFailure_ShouldReplayWithStableOperationIds()
    {
        var tool = new CountingAgentTool("counting_tool", _ => """{"ok":true}""");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext
        {
            FailAfterNextPublishType = typeof(StepCompletedEvent),
        };

        var firstAttempt = () => ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            executionId: "exec-step-published");
        await firstAttempt.Should().ThrowAsync<WorkflowDurablePublicationPendingException>()
            .WithMessage("Durable workflow step completion remains pending.");

        var retained = ctx.LoadState<ToolCallModuleState>("tool_call");
        var retainedCompletion = retained.Completions.Should().ContainSingle().Subject;
        retainedCompletion.ToolCompletionPublished.Should().BeFalse();
        retainedCompletion.StepCompletionPublished.Should().BeFalse();
        retainedCompletion.ProtectedMaterialReference.Should().NotBeNull();
        retained.CompletionTombstones.Should().BeEmpty();

        await ExecuteToolCallAsync(module, ctx, tool.Name, executionId: "exec-step-published");

        tool.ExecuteCalls.Should().Be(1);
        var toolPublications = ctx.Published.Select(static item => item.Event)
            .OfType<WorkflowToolCallCompletedEvent>()
            .ToList();
        var stepPublications = ctx.Published.Select(static item => item.Event)
            .OfType<StepCompletedEvent>()
            .ToList();
        toolPublications.Should().HaveCount(2);
        stepPublications.Should().HaveCount(2);
        toolPublications.Select(ctx.PublishedOperationId).Distinct()
            .Should().ContainSingle().Which.Should().NotBeNullOrWhiteSpace();
        stepPublications.Select(ctx.PublishedOperationId).Distinct()
            .Should().ContainSingle().Which.Should().NotBeNullOrWhiteSpace();
        var settled = ctx.LoadState<ToolCallModuleState>("tool_call");
        settled.Completions.Should().BeEmpty();
        settled.CompletionTombstones.Should().ContainSingle();
    }

    [Fact]
    public async Task ToolCallModule_WhenCompletionSettles_ShouldUseOnlyOutboxAndTombstoneStateSaves()
    {
        var tool = new CountingAgentTool("counting_tool", _ => "{}");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(
            module,
            ctx,
            "missing_tool",
            executionId: "exec-two-checkpoints");

        tool.ExecuteCalls.Should().Be(0);
        ctx.StateSaveCalls.Should().Be(2);
        ctx.Published.Select(static item => item.Event)
            .OfType<WorkflowToolCallCompletedEvent>().Should().ContainSingle();
        ctx.Published.Select(static item => item.Event)
            .OfType<StepCompletedEvent>().Should().ContainSingle();
        var settled = ctx.LoadState<ToolCallModuleState>("tool_call");
        settled.Completions.Should().BeEmpty();
        settled.CompletionTombstones.Should().ContainSingle();
    }

    [Fact]
    public async Task ToolCallModule_WhenExecutionIdentityIsMissing_ShouldRetainLegacyPublicationCheckpoints()
    {
        var module = CreateModule(new CountingAgentTool("counting_tool", _ => "{}"));
        var ctx = new RecordingWorkflowContext
        {
            FailNextPublishType = typeof(StepCompletedEvent),
        };

        var firstAttempt = () => ExecuteToolCallAsync(
            module,
            ctx,
            "missing_tool",
            executionId: string.Empty);
        await firstAttempt.Should().ThrowAsync<WorkflowDurablePublicationPendingException>()
            .WithMessage("Durable workflow step completion remains pending.");

        var retained = ctx.LoadState<ToolCallModuleState>("tool_call")
            .Completions.Should().ContainSingle().Subject;
        retained.ToolCompletionPublished.Should().BeTrue();
        retained.StepCompletionPublished.Should().BeFalse();
        ctx.StateSaveCalls.Should().Be(2);
        ctx.Published.Select(static item => item.Event)
            .OfType<WorkflowToolCallCompletedEvent>().Should().ContainSingle();
        ctx.Published.Select(static item => item.Event)
            .OfType<StepCompletedEvent>().Should().BeEmpty();

        await ExecuteToolCallAsync(
            module,
            ctx,
            "missing_tool",
            executionId: string.Empty);

        ctx.StateSaveCalls.Should().Be(4);
        ctx.Published.Select(static item => item.Event)
            .OfType<WorkflowToolCallCompletedEvent>().Should().ContainSingle();
        ctx.Published.Select(static item => item.Event)
            .OfType<StepCompletedEvent>().Should().ContainSingle();
        var settled = ctx.LoadState<ToolCallModuleState>("tool_call");
        settled.Completions.Should().BeEmpty();
        settled.CompletionTombstones.Should().ContainSingle();
    }

    [Fact]
    public async Task ToolCallModule_WhenOnlyStepCompletionIsUnpublished_ShouldReplayOnlyThatEvent()
    {
        var tool = new CountingAgentTool("counting_tool", _ => "{}");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();
        await ctx.SaveStateAsync("tool_call", new ToolCallModuleState
        {
            Completions =
            {
                new WorkflowToolCallCompletionOutboxEntry
                {
                    CallId = "workflow:run-1:call_proxy:exec-1",
                    ExecutionId = "exec-1",
                    RunId = "run-1",
                    StepId = "call_proxy",
                    TerminalDecision = WorkflowToolCallTerminalDecision.NoApproval,
                    ToolCompletion = new WorkflowToolCallCompletedEvent
                    {
                        RunId = "run-1",
                        StepId = "call_proxy",
                        CallId = "workflow:run-1:call_proxy:exec-1",
                        Success = true,
                        ResultJson = "{}",
                    },
                    StepCompletion = new StepCompletedEvent
                    {
                        RunId = "run-1",
                        StepId = "call_proxy",
                        ExecutionId = "exec-1",
                        Success = true,
                        Output = "{}",
                    },
                    ToolCompletionPublished = true,
                },
            },
        });

        await ExecuteToolCallAsync(module, ctx, tool.Name, executionId: "exec-1");

        tool.ExecuteCalls.Should().Be(0);
        ctx.Published.Select(x => x.Event).OfType<WorkflowToolCallStartedEvent>().Should().BeEmpty();
        ctx.Published.Select(x => x.Event).OfType<WorkflowToolCallCompletedEvent>().Should().BeEmpty();
        ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Should().ContainSingle();
        var state = ctx.LoadState<ToolCallModuleState>("tool_call");
        state.Completions.Should().BeEmpty();
        state.CompletionTombstones.Should().ContainSingle();
    }

    [Fact]
    public void ToolCallCompletionOutbox_ShouldRoundTripTypedTerminalOutcomes()
    {
        var state = new ToolCallModuleState
        {
            Completions =
            {
                new WorkflowToolCallCompletionOutboxEntry
                {
                    CallId = "workflow:run-1:handoff:exec-1",
                    ExecutionId = "exec-1",
                    ToolCompletion = new WorkflowToolCallCompletedEvent
                    {
                        RunId = "run-1",
                        StepId = "handoff",
                        CallId = "workflow:run-1:handoff:exec-1",
                        Success = true,
                        ResultJson = """{"status":"accepted"}""",
                        ManagedHandoff = new WorkflowManagedHandoffOutcome
                        {
                            ParentActorId = "parent-actor",
                            InvocationId = "invocation-1",
                            ChildRunId = "child-run-1",
                        },
                    },
                    ToolCompletionPublished = true,
                },
                new WorkflowToolCallCompletionOutboxEntry
                {
                    CallId = "workflow:run-1:failure:exec-2",
                    ExecutionId = "exec-2",
                    ToolCompletion = new WorkflowToolCallCompletedEvent
                    {
                        RunId = "run-1",
                        StepId = "failure",
                        CallId = "workflow:run-1:failure:exec-2",
                        Error = "NYXID_PROXY_HTTP_503",
                        ResultJson = """{"status":"failed"}""",
                    },
                    StepCompletion = new StepCompletedEvent
                    {
                        RunId = "run-1",
                        StepId = "failure",
                        ExecutionId = "exec-2",
                        Error = "NYXID_PROXY_HTTP_503",
                        Output = """{"status":"failed"}""",
                    },
                    StepCompletionPublished = true,
                },
            },
        };

        var parsed = ToolCallModuleState.Parser.ParseFrom(state.ToByteArray());

        parsed.Completions.Should().HaveCount(2);
        parsed.Completions[0].ToolCompletion.ManagedHandoff.InvocationId.Should().Be("invocation-1");
        parsed.Completions[0].StepCompletion.Should().BeNull();
        parsed.Completions[0].ToolCompletionPublished.Should().BeTrue();
        parsed.Completions[1].ToolCompletion.Error.Should().Be("NYXID_PROXY_HTTP_503");
        parsed.Completions[1].StepCompletion.ExecutionId.Should().Be("exec-2");
        parsed.Completions[1].StepCompletionPublished.Should().BeTrue();
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
        await DrainToolCallContinuationsAsync(module, ctx);

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
            BearerToken = "short-lived-token",
            NyxIdAuthority = CreateCallerAuthority(),
            Kind = NyxIdCallerCredentialKind.ProxyDelegation,
        };

        await ExecuteToolCallAsync(module, ctx, tool.Name, stepId: "call-1");
        await ExecuteToolCallAsync(module, ctx, tool.Name, stepId: "call-2");

        tool.Requests.Select(request => request.CallerCredential.BearerToken)
            .Should().Equal("token-1", "token-2");
        tool.Requests.Select(request => request.CallerCredential.Kind)
            .Should().OnlyContain(kind => kind == NyxIdCallerCredentialKind.ProxyDelegation);
        tokenProvider.Authorities.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(
        DurableCallerCredentialSourceKind.ChannelRegistration,
        CredentialSecretPurposes.ChannelNyxIdAgentKey)]
    [InlineData(
        DurableCallerCredentialSourceKind.WebhookBinding,
        CredentialSecretPurposes.WorkflowWebhookBindingAgentKey)]
    [InlineData(
        DurableCallerCredentialSourceKind.ScheduledDispatch,
        CredentialSecretPurposes.ScheduledInvocationAgentKey)]
    public async Task ToolCallModule_WithDurableAgentKey_ShouldNeverRefreshOrForwardUserToken(
        DurableCallerCredentialSourceKind sourceKind,
        string purpose)
    {
        const string agentKey = "nyxid_ag_workflow_tool_key";
        var vault = new InMemorySecretVault();
        var stored = await vault.PutAsync(new StoreSecretRequest(
            purpose,
            "scope-workflow",
            "agent-key-workflow",
            agentKey,
            "workflow-tool-test"));
        var tool = new CapturingWorkflowTool("nyxid_tool");
        var tokenProvider = new RotatingCallerAccessTokenProvider();
        var module = CreateModule(tool, tokenProvider);
        var ctx = new RecordingWorkflowContext
        {
            SecretVault = vault,
        };
        ctx.ExecutionContextState.CallerCredential = new WorkflowCallerCredentialState
        {
            DurableCallerCredential = new DurableCallerCredentialRef
            {
                Ref = stored.Reference.Ref,
                Purpose = stored.Reference.Purpose,
                OwnerScopeKey = stored.Reference.OwnerScopeKey,
                SubjectId = "agent-key-workflow",
                SourceKind = sourceKind,
                SecretReference = stored.Reference.Clone(),
                ProviderCredentialId = sourceKind == DurableCallerCredentialSourceKind.WebhookBinding
                    ? "provider-key-workflow"
                    : string.Empty,
            },
            NyxIdAuthority = CreateCallerAuthority(),
            Kind = NyxIdCallerCredentialKind.AgentKey,
        };
        ctx.ExecutionContextState.Llm = new WorkflowLlmExecutionContextState
        {
            ModelOverride = "channel-agent-model",
        };
        ctx.RuntimeContext.ApplySenderNyxIdAccessToken("short-lived-user-token");

        await ExecuteToolCallAsync(module, ctx, tool.Name, stepId: "channel-agent-key-tool");

        tool.LastRequest.Should().NotBeNull();
        tool.LastRequest!.CallerCredential.BearerToken.Should().Be(agentKey);
        tool.LastRequest.CallerCredential.Kind.Should().Be(NyxIdCallerCredentialKind.AgentKey);
        tool.LastRequest.CallerCredential.DurableCallerCredential.Should().NotBeNull();
        tool.LastRequest.CallerCredential.DurableCallerCredential.SourceKind.Should()
            .Be(sourceKind);
        tool.LastRequest.LlmControl.Should().NotBeNull();
        tool.LastRequest.LlmControl!.SenderNyxIdAccessToken.Should().BeEmpty();
        tokenProvider.Authorities.Should().BeEmpty();
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
        await ExecuteToolCallAsync(module, ctx, tool.Name, executionId: "exec-1");

        tool.ExecuteCalls.Should().Be(1);
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
    public async Task ToolCallModule_ShouldDispatchIndependentRequestsWhileEarlierToolIsBlocked()
    {
        var tool = new BlockingWorkflowTool("blocking_tool");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();
        var firstRequest = ToolRequest(ctx, tool.Name, "step-1", "exec-1");
        var secondRequest = ToolRequest(ctx, tool.Name, "step-2", "exec-2");

        await module.HandleAsync(Envelope(firstRequest), ctx, CancellationToken.None);
        var first = await tool.ReadInvocationAsync();
        first.Request.ExecutionId.Should().Be("exec-1");
        first.Completion.Task.IsCompleted.Should().BeFalse();

        await module.HandleAsync(Envelope(secondRequest), ctx, CancellationToken.None);
        var second = await tool.ReadInvocationAsync();
        second.Request.ExecutionId.Should().Be("exec-2");
        first.Completion.Task.IsCompleted.Should().BeFalse();
        second.Completion.Task.IsCompleted.Should().BeFalse();
        tool.ExecuteCalls.Should().Be(2);
        var pendingExecutions = ctx.LoadState<ToolCallModuleState>("tool_call").PendingExecutions;
        pendingExecutions.Should().HaveCount(2);
        foreach (var pending in pendingExecutions.Values)
            AssertProtectedPendingExecution(pending);
        ctx.Scheduled.Count(static callback => callback.Event is WorkflowToolCallTimeoutFiredEvent)
            .Should().Be(2);

        first.Completion.SetResult(WorkflowToolExecutionResult.Success("""{"order":1}"""));
        var firstCompletion = await ctx.WaitForPublishedAsync<WorkflowToolCallAttemptCompletedEvent>(
            static completion => completion.ExecutionId == "exec-1");
        await module.HandleAsync(ctx.PublishedEnvelope(firstCompletion), ctx, CancellationToken.None);

        second.Completion.SetResult(WorkflowToolExecutionResult.Success("""{"order":2}"""));
        var secondCompletion = await ctx.WaitForPublishedAsync<WorkflowToolCallAttemptCompletedEvent>(
            static completion => completion.ExecutionId == "exec-2");
        await module.HandleAsync(ctx.PublishedEnvelope(secondCompletion), ctx, CancellationToken.None);

        var settled = ctx.LoadState<ToolCallModuleState>("tool_call");
        settled.PendingExecutions.Should().BeEmpty();
        settled.CompletionTombstones.Should().HaveCount(2);
        ctx.Published.Select(static item => item.Event)
            .OfType<StepCompletedEvent>()
            .Should().HaveCount(2)
            .And.OnlyContain(static completion => completion.Success);
    }

    [Fact]
    public async Task ToolCallModule_ShouldCorrelateProviderDeliveryAndReconciliationWaterlines()
    {
        var clock = new FakeTimeProvider(
            new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero));
        var logger = new RecordingToolCallLogger();
        var tool = new BlockingWorkflowTool("timed_tool");
        var module = CreateModule(tool, logger: logger);
        var ctx = new RecordingWorkflowContext { Clock = clock, Logger = logger };
        var request = ToolRequest(ctx, tool.Name, "step-timed", "exec-timed");

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);
        var invocation = await tool.ReadInvocationAsync();
        clock.Advance(TimeSpan.FromMilliseconds(37));
        invocation.Completion.SetResult(WorkflowToolExecutionResult.Success("{}"));
        var completed = await ctx.WaitForPublishedAsync<WorkflowToolCallAttemptCompletedEvent>();

        completed.ProviderTiming.Should().NotBeNull();
        completed.ProviderTiming.DispatchId.Should().NotBeNullOrWhiteSpace();
        completed.ProviderTiming.ExternalExecutionElapsedMs.Should().Be(37);
        completed.ProviderTiming.Disposition.Should().Be(WorkflowToolCallProviderDisposition.Succeeded);
        completed.ProviderTiming.DispatchStartedAtUtc.ToDateTimeOffset().Should().Be(
            new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero));
        completed.ProviderTiming.ProviderReturnedAtUtc.ToDateTimeOffset().Should().Be(
            new DateTimeOffset(2026, 8, 18, 12, 0, 0, 37, TimeSpan.Zero));

        await module.HandleAsync(ctx.PublishedEnvelope(completed), ctx, CancellationToken.None);
        // The producer records completion_delivery_producer_confirmed only after its self-publish
        // returns, so it may land before or after the actor's reconciliation line. Sync on it
        // (TCS, no polling) AFTER handing the completion to the actor, then assert only the
        // causally deterministic edges plus the exact waterline set.
        await logger.WaitForEntryAsync(entry =>
            Equals(entry.Properties.GetValueOrDefault("Waterline"), "completion_delivery_producer_confirmed") &&
            Equals(entry.Properties.GetValueOrDefault("DispatchId"), completed.ProviderTiming.DispatchId));

        var observations = logger.Entries
            .Where(entry => entry.Properties.ContainsKey("Waterline"))
            .ToList();
        var waterlines = observations.Select(entry => (string)entry.Properties["Waterline"]!).ToList();
        waterlines.Should().BeEquivalentTo(
            "pending_state_persisted",
            "external_dispatch_started",
            "provider_returned",
            "completion_delivery_producer_confirmed",
            "actor_reconciliation_completed");
        // Actor persists before the worker dispatches; the worker returns from the provider
        // before the actor reconciles that dispatch; the worker confirms delivery after it returned.
        waterlines.IndexOf("pending_state_persisted").Should().BeLessThan(waterlines.IndexOf("external_dispatch_started"));
        waterlines.IndexOf("external_dispatch_started").Should().BeLessThan(waterlines.IndexOf("provider_returned"));
        waterlines.IndexOf("provider_returned").Should().BeLessThan(waterlines.IndexOf("actor_reconciliation_completed"));
        waterlines.IndexOf("provider_returned").Should().BeLessThan(waterlines.IndexOf("completion_delivery_producer_confirmed"));
        observations.Should().OnlyContain(entry =>
            Equals(entry.Properties["RunId"], request.RunId) &&
            Equals(entry.Properties["StepId"], request.StepId) &&
            Equals(entry.Properties["CallId"], completed.CallId) &&
            Equals(entry.Properties["ExecutionId"], request.ExecutionId));
        observations
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Properties["DispatchId"]?.ToString()))
            .Select(entry => entry.Properties["DispatchId"])
            .Should().OnlyContain(dispatchId => Equals(dispatchId, completed.ProviderTiming.DispatchId));
    }

    [Fact]
    public async Task ToolCallModule_ShouldNotRedispatchAnInflightStepRequest()
    {
        var tool = new BlockingWorkflowTool("blocking_tool");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();
        var request = ToolRequest(ctx, tool.Name, "step-1", "exec-1");

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);
        var invocation = await tool.ReadInvocationAsync();
        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

        tool.ExecuteCalls.Should().Be(1);
        invocation.Completion.Task.IsCompleted.Should().BeFalse();
        var pending = ctx.LoadState<ToolCallModuleState>("tool_call")
            .PendingExecutions.Values.Should().ContainSingle().Subject;
        AssertProtectedPendingExecution(pending);
        ctx.Scheduled.Count(static callback => callback.Event is WorkflowToolCallTimeoutFiredEvent)
            .Should().Be(1);

        invocation.Completion.SetResult(WorkflowToolExecutionResult.Success("{}"));
        var completion = await ctx.WaitForPublishedAsync<WorkflowToolCallAttemptCompletedEvent>();
        await module.HandleAsync(ctx.PublishedEnvelope(completion), ctx, CancellationToken.None);

        tool.ExecuteCalls.Should().Be(1);
        ctx.LoadState<ToolCallModuleState>("tool_call").CompletionTombstones.Should().ContainSingle();
        ctx.Published.Select(static item => item.Event)
            .OfType<StepCompletedEvent>()
            .Should().ContainSingle();
    }

    [Fact]
    public void ToolCallModule_ActivationRecovery_ShouldExcludeUnspecifiedAndNonExecutionPhases()
    {
        var execution = new PendingToolCallExecutionState
        {
            RunId = "run-1",
            StepId = "step-1",
            ExecutionId = "exec-1",
            ToolName = "read_tool",
            CallId = "call-1",
            Attempt = 1,
            ContinuationId = "continuation-1",
            ExecutionPhase = WorkflowToolCallExecutionPhase.ExecutionPending,
        };
        var unspecified = execution.Clone();
        unspecified.ExecutionId = "exec-legacy";
        unspecified.CallId = "call-legacy";
        unspecified.ExecutionPhase = WorkflowToolCallExecutionPhase.Unspecified;
        var retry = execution.Clone();
        retry.ExecutionId = "exec-retry";
        retry.CallId = "call-retry";
        retry.ExecutionPhase = WorkflowToolCallExecutionPhase.RetryPending;
        var state = new ToolCallModuleState
        {
            PendingExecutions =
            {
                ["execution"] = execution,
                ["legacy"] = unspecified,
                ["retry"] = retry,
            },
        };

        var recoveries = ToolCallModule.BuildPendingExecutionRecoveries(state);

        recoveries.Should().ContainSingle().Which.PendingKey.Should().Be("execution");
    }

    [Fact]
    public void ToolCallModule_ActivationRecovery_ShouldRearmApprovalDeadlineWithoutDurableLease()
    {
        var pending = new PendingToolCallApprovalState
        {
            RunId = "run-1",
            StepId = "step-1",
            ExecutionId = "exec-approval",
            ToolName = "read_tool",
            ToolCallId = "call-approval",
            ApprovalRequestId = "approval-1",
            Attempt = 1,
            ContinuationId = "continuation-approval",
            TimeoutMs = 60_000,
            TimeoutDeadlineUnixMs = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(),
            ExecutionPhase = WorkflowToolCallExecutionPhase.ApprovalPending,
        };
        var state = new ToolCallModuleState
        {
            PendingApprovals = { ["approval"] = pending },
        };

        var recoveries = ToolCallModule.BuildPendingApprovalWatchdogRecoveries(
            state,
            DateTimeOffset.UtcNow);

        var recovery = recoveries.Should().ContainSingle().Subject;
        recovery.PendingKey.Should().Be("approval");
        recovery.CallbackId.Should().NotBeNullOrWhiteSpace();
        recovery.Timeout.ContinuationId.Should().Be(pending.ContinuationId);
    }

    [Fact]
    public async Task ToolCallModule_RedeliveredApprovedResumeWithoutLocalWorker_ShouldScheduleRecovery()
    {
        const string callId = "workflow:run-1:step-1:exec-approved";
        const string executionId = "exec-approved";
        var tool = new BlockingWorkflowTool(
            "read_tool",
            WorkflowToolRecoverySafety.ReplayableReadOnly);
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();
        var pending = new PendingToolCallExecutionState
        {
            RunId = ctx.RunId,
            StepId = "step-1",
            ExecutionId = executionId,
            ToolName = tool.Name,
            CallId = callId,
            ApprovalRequestId = "approval-1",
            TerminalDecision = WorkflowToolCallTerminalDecision.Approved,
            Attempt = 2,
            ContinuationId = "continuation-approved",
            TimeoutDeadlineUnixMs = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(),
            TimeoutCallbackId = "timeout-approved",
            TimeoutLease = new WorkflowRuntimeCallbackLeaseState
            {
                ActorId = ctx.AgentId,
                CallbackId = "timeout-approved",
                Generation = 1,
                Backend = WorkflowRuntimeCallbackBackendState.Dedicated,
            },
            ExecutionPhase = WorkflowToolCallExecutionPhase.ExecutionPending,
        };
        await ctx.SaveStateAsync("tool_call", new ToolCallModuleState
        {
            PendingExecutions =
            {
                [RuntimeCallbackKeyComposer.BuildKey('|', callId, executionId)] = pending,
            },
        });
        var resumed = new WorkflowResumedEvent
        {
            RunId = ctx.RunId,
            StepId = pending.StepId,
            Approved = true,
            ToolApproval = new WorkflowToolApprovalResume
            {
                ExecutionId = executionId,
                ToolCallId = callId,
                ApprovalRequestId = pending.ApprovalRequestId,
            },
        };

        await module.HandleAsync(Envelope(resumed), ctx, CancellationToken.None);

        tool.ExecuteCalls.Should().Be(0);
        ctx.Scheduled.Select(static item => item.Event)
            .OfType<WorkflowToolCallExecutionRecoveryFiredEvent>()
            .Should().ContainSingle()
            .Which.ContinuationId.Should().Be(pending.ContinuationId);
    }

    [Fact]
    public async Task ToolCallModule_RedeliveredPendingWithoutLocalTask_ShouldScheduleSameTokenRecovery()
    {
        var tool = new BlockingWorkflowTool(
            "read_tool",
            WorkflowToolRecoverySafety.ReplayableReadOnly);
        var firstModule = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();
        var request = ToolRequest(ctx, tool.Name, "step-1", "exec-recovery");

        await firstModule.HandleAsync(Envelope(request), ctx, CancellationToken.None);
        var firstInvocation = await tool.ReadInvocationAsync();
        var pending = ctx.LoadState<ToolCallModuleState>("tool_call")
            .PendingExecutions.Values.Should().ContainSingle().Subject.Clone();
        ((IWorkflowExecutionBackgroundWorkOwner)firstModule).CancelBackgroundWork();

        var recoveredModule = CreateModule(tool);
        await recoveredModule.HandleAsync(Envelope(request), ctx, CancellationToken.None);

        var recoveryCallback = ctx.Scheduled
            .Last(callback => callback.Event is WorkflowToolCallExecutionRecoveryFiredEvent);
        var recovery = recoveryCallback.Event.Should()
            .BeOfType<WorkflowToolCallExecutionRecoveryFiredEvent>().Subject;
        recovery.ContinuationId.Should().Be(pending.ContinuationId);
        recovery.Attempt.Should().Be(pending.Attempt);
        var recoveryEnvelope = CallbackEnvelope(recoveryCallback);
        recoveryEnvelope.Route = EnvelopeRouteSemantics.CreateTopologyPublication(
            ctx.AgentId,
            TopologyAudience.Self);

        await recoveredModule.HandleAsync(recoveryEnvelope, ctx, CancellationToken.None);
        var recoveredInvocation = await tool.ReadInvocationAsync().AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        tool.ExecuteCalls.Should().Be(2);
        firstInvocation.CancellationToken.IsCancellationRequested.Should().BeTrue();
        recoveredInvocation.Request.CallId.Should().Be(firstInvocation.Request.CallId);
        recoveredInvocation.Completion.SetResult(WorkflowToolExecutionResult.Success("{}"));
        var completed = await ctx.WaitForPublishedAsync<WorkflowToolCallAttemptCompletedEvent>(candidate =>
            candidate.ExecutionId == pending.ExecutionId &&
            candidate.Attempt == pending.Attempt &&
            candidate.ContinuationId == pending.ContinuationId);
        await recoveredModule.HandleAsync(ctx.PublishedEnvelope(completed), ctx, CancellationToken.None);
        firstInvocation.Completion.SetResult(WorkflowToolExecutionResult.Success("{}"));

        ctx.LoadState<ToolCallModuleState>("tool_call").CompletionTombstones.Should().ContainSingle();
    }

    [Fact]
    public async Task ToolCallModule_UncertainRecovery_ShouldNotRedispatchEffectfulTool()
    {
        var tool = new BlockingWorkflowTool(
            "effectful_tool",
            WorkflowToolRecoverySafety.EffectfulNonReplayable);
        var firstModule = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();
        var request = ToolRequest(ctx, tool.Name, "step-1", "exec-effectful");

        await firstModule.HandleAsync(Envelope(request), ctx, CancellationToken.None);
        var firstInvocation = await tool.ReadInvocationAsync();
        ((IWorkflowExecutionBackgroundWorkOwner)firstModule).CancelBackgroundWork();
        var recoveredModule = CreateModule(tool);
        await recoveredModule.HandleAsync(Envelope(request), ctx, CancellationToken.None);
        var recoveryCallback = ctx.Scheduled
            .Last(callback => callback.Event is WorkflowToolCallExecutionRecoveryFiredEvent);
        var recoveryEnvelope = CallbackEnvelope(recoveryCallback);
        recoveryEnvelope.Route = EnvelopeRouteSemantics.CreateTopologyPublication(
            ctx.AgentId,
            TopologyAudience.Self);

        await recoveredModule.HandleAsync(recoveryEnvelope, ctx, CancellationToken.None);
        firstInvocation.Completion.SetResult(WorkflowToolExecutionResult.Success("{}"));

        tool.ExecuteCalls.Should().Be(1);
        ctx.Published.Select(static item => item.Event)
            .OfType<WorkflowToolCallCompletedEvent>()
            .Should().ContainSingle()
            .Which.Error.Should().Contain("tool_outcome_unknown");
        ctx.Published.Select(static item => item.Event)
            .OfType<StepCompletedEvent>()
            .Should().ContainSingle()
            .Which.FailureOutcome.Should().Be(WorkflowStepFailureOutcome.OutcomeUncertain);
    }

    [Fact]
    public async Task ToolCallModule_WhenEveryPublicationWakeupFails_ShouldRuntimeRedeliverAndRecover()
    {
        var tool = new CountingAgentTool("counting_tool", _ => "{}");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext
        {
            FailPublicationRetrySchedulesRemaining = 1,
            FailPublicationRetryPublishesRemaining = 1,
            FailToolCompletionPublishesRemaining = 1,
        };
        var request = ToolRequest(ctx, tool.Name, "call_proxy", "exec-wakeup");
        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);
        var completion = await ctx.WaitForPublishedAsync<WorkflowToolCallAttemptCompletedEvent>();
        var completionEnvelope = ctx.PublishedEnvelope(completion);

        var failure = await FluentActions.Awaiting(() =>
                module.HandleAsync(completionEnvelope, ctx, CancellationToken.None))
            .Should().ThrowAsync<WorkflowDurablePublicationPendingException>();
        failure.Which.Should().BeAssignableTo<IRuntimeEnvelopeRetryableException>();
        var durable = ctx.LoadState<ToolCallModuleState>("tool_call")
            .Completions.Should().ContainSingle().Subject;
        durable.ProtectedMaterialReference.Should().NotBeNull();
        durable.ToolCompletionPublished.Should().BeFalse();
        durable.StepCompletionPublished.Should().BeFalse();

        await module.HandleAsync(completionEnvelope, ctx, CancellationToken.None);

        tool.ExecuteCalls.Should().Be(1);
        var recovered = ctx.LoadState<ToolCallModuleState>("tool_call");
        recovered.Completions.Should().BeEmpty();
        recovered.CompletionTombstones.Should().ContainSingle();
        ctx.Published.Select(static item => item.Event)
            .OfType<WorkflowToolCallCompletedEvent>().Should().ContainSingle();
        ctx.Published.Select(static item => item.Event)
            .OfType<StepCompletedEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task ToolCallModule_PublicationRetrySchedulerFailure_ShouldLeaveTransportUnackedWithoutChainingContinuation()
    {
        var tool = new CountingAgentTool("counting_tool", _ => "{}");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext
        {
            FailToolCompletionPublishesRemaining = 2,
        };

        var first = () => ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            executionId: "exec-retry-continuation");
        await first.Should().ThrowAsync<WorkflowDurablePublicationPendingException>();
        var firstRetry = ctx.Scheduled
            .Should().ContainSingle(static callback =>
                callback.Event is WorkflowToolCallPublicationRetryFiredEvent)
            .Subject;
        ctx.FailPublicationRetrySchedulesRemaining = 1;

        var retryEnvelope = CallbackEnvelope(firstRetry);
        var failure = await FluentActions.Awaiting(() =>
                module.HandleAsync(retryEnvelope, ctx, CancellationToken.None))
            .Should().ThrowAsync<WorkflowRuntimeEnvelopeRetryablePublicationPendingException>();
        failure.Which.Should().BeAssignableTo<IRuntimeEnvelopeRetryableException>();
        ctx.Published.Select(static item => item.Event)
            .OfType<WorkflowToolCallPublicationRetryFiredEvent>()
            .Should().BeEmpty();
        ctx.LoadState<ToolCallModuleState>("tool_call").Completions.Should().ContainSingle();

        await module.HandleAsync(retryEnvelope, ctx, CancellationToken.None);

        tool.ExecuteCalls.Should().Be(1);
        var settled = ctx.LoadState<ToolCallModuleState>("tool_call");
        settled.Completions.Should().BeEmpty();
        settled.CompletionTombstones.Should().ContainSingle();
    }

    [Fact]
    public async Task ToolCallModule_ShouldRetainCleanupReferenceUntilRevocationIsConfirmed()
    {
        var store = new FailFirstRevokeRuntimeSecretStore();
        var tool = new CountingAgentTool("counting_tool", _ => "{}");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext { RuntimeSecretStore = store };

        var first = () => ExecuteToolCallAsync(module, ctx, tool.Name, executionId: "exec-cleanup");

        await first.Should().ThrowAsync<WorkflowDurablePublicationPendingException>()
            .WithMessage("Durable workflow protected tool-call material cleanup remains pending.");
        var pendingCleanup = ctx.LoadState<ToolCallModuleState>("tool_call")
            .Completions.Should().ContainSingle().Subject;
        pendingCleanup.ProtectedMaterialReference.Should().NotBeNull();
        pendingCleanup.ToolCompletionPublished.Should().BeFalse();
        pendingCleanup.StepCompletionPublished.Should().BeFalse();

        await ExecuteToolCallAsync(module, ctx, tool.Name, executionId: "exec-cleanup");

        store.RevokeCalls.Should().Be(2);
        tool.ExecuteCalls.Should().Be(1);
        ctx.LoadState<ToolCallModuleState>("tool_call").CompletionTombstones.Should().ContainSingle();
        var toolPublications = ctx.Published.Select(static item => item.Event)
            .OfType<WorkflowToolCallCompletedEvent>()
            .ToList();
        var stepPublications = ctx.Published.Select(static item => item.Event)
            .OfType<StepCompletedEvent>()
            .ToList();
        toolPublications.Should().HaveCount(2);
        stepPublications.Should().HaveCount(2);
        toolPublications.Select(ctx.PublishedOperationId).Distinct()
            .Should().ContainSingle().Which.Should().NotBeNullOrWhiteSpace();
        stepPublications.Select(ctx.PublishedOperationId).Distinct()
            .Should().ContainSingle().Which.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ToolCallModule_ShouldIgnoreForgedCompletionEnvelopes()
    {
        var tool = new BlockingWorkflowTool("blocking_tool");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();
        var request = ToolRequest(ctx, tool.Name, "step-1", "exec-forged");

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);
        var invocation = await tool.ReadInvocationAsync();
        invocation.Completion.SetResult(WorkflowToolExecutionResult.Success("""{"accepted":true}"""));
        var completion = await ctx.WaitForPublishedAsync<WorkflowToolCallAttemptCompletedEvent>();

        var forgedPublisher = ctx.PublishedEnvelope(completion);
        forgedPublisher.Route.PublisherActorId = "actor-forged";
        await module.HandleAsync(forgedPublisher, ctx, CancellationToken.None);

        var forgedOperation = ctx.PublishedEnvelope(completion);
        forgedOperation.Runtime.DeliveryIdentity.OperationId = "operation-forged";
        await module.HandleAsync(forgedOperation, ctx, CancellationToken.None);

        ctx.LoadState<ToolCallModuleState>("tool_call")
            .PendingExecutions.Should().ContainSingle();
        ctx.Published.Select(static item => item.Event)
            .OfType<StepCompletedEvent>()
            .Should().BeEmpty();

        await module.HandleAsync(ctx.PublishedEnvelope(completion), ctx, CancellationToken.None);

        ctx.LoadState<ToolCallModuleState>("tool_call").PendingExecutions.Should().BeEmpty();
        ctx.Published.Select(static item => item.Event)
            .OfType<StepCompletedEvent>()
            .Should().ContainSingle()
            .Which.Success.Should().BeTrue();
    }

    [Fact]
    public async Task ToolCallModule_CancelBackgroundWork_ShouldKeepInvocationTokenUsableUntilTaskExits()
    {
        var tool = new BlockingWorkflowTool("blocking_tool");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();
        var request = ToolRequest(ctx, tool.Name, "step-1", "exec-cancel-ownership");

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);
        var invocation = await tool.ReadInvocationAsync();

        ((IWorkflowExecutionBackgroundWorkOwner)module).CancelBackgroundWork();

        invocation.CancellationToken.IsCancellationRequested.Should().BeTrue();
        invocation.CancellationToken.WaitHandle.WaitOne(0).Should().BeTrue();
        invocation.Completion.SetResult(WorkflowToolExecutionResult.Success("{}"));
    }

    [Fact]
    public async Task ToolCallModule_WhenStartedObservationPublishFails_ShouldStillDispatchAndComplete()
    {
        var tool = new BlockingWorkflowTool("blocking_tool");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext
        {
            FailNextPublishType = typeof(WorkflowToolCallStartedEvent),
        };
        var request = ToolRequest(ctx, tool.Name, "step-1", "exec-start-observation");

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);
        var invocation = await tool.ReadInvocationAsync();
        invocation.Completion.SetResult(WorkflowToolExecutionResult.Success("{}"));
        var completion = await ctx.WaitForPublishedAsync<WorkflowToolCallAttemptCompletedEvent>();
        await module.HandleAsync(ctx.PublishedEnvelope(completion), ctx, CancellationToken.None);

        tool.ExecuteCalls.Should().Be(1);
        ctx.LoadState<ToolCallModuleState>("tool_call").PendingExecutions.Should().BeEmpty();
        ctx.Published.Select(static item => item.Event)
            .OfType<StepCompletedEvent>().Should().ContainSingle()
            .Which.Success.Should().BeTrue();
    }

    [Fact]
    public async Task ToolCallModule_RedeliveryWhileCompletionIsQueued_ShouldNotScheduleRedispatch()
    {
        var tool = new BlockingWorkflowTool(
            "blocking_tool",
            WorkflowToolRecoverySafety.ReplayableReadOnly);
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();
        var request = ToolRequest(ctx, tool.Name, "step-1", "exec-completion-in-flight");

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);
        var invocation = await tool.ReadInvocationAsync();
        invocation.Completion.SetResult(WorkflowToolExecutionResult.Success("{}"));
        var completion = await ctx.WaitForPublishedAsync<WorkflowToolCallAttemptCompletedEvent>();

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

        tool.ExecuteCalls.Should().Be(1);
        ctx.Scheduled.Select(static callback => callback.Event)
            .OfType<WorkflowToolCallExecutionRecoveryFiredEvent>().Should().BeEmpty();

        await module.HandleAsync(ctx.PublishedEnvelope(completion), ctx, CancellationToken.None);
        ctx.Published.Select(static item => item.Event)
            .OfType<StepCompletedEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task ToolCallModule_WhenCompletionSuccessorSaveFails_ShouldRetainRedispatchGuard()
    {
        var tool = new BlockingWorkflowTool(
            "blocking_tool",
            WorkflowToolRecoverySafety.ReplayableReadOnly);
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();
        var request = ToolRequest(ctx, tool.Name, "step-1", "exec-successor-save");

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);
        var invocation = await tool.ReadInvocationAsync();
        invocation.Completion.SetResult(WorkflowToolExecutionResult.Success("{}"));
        var completion = await ctx.WaitForPublishedAsync<WorkflowToolCallAttemptCompletedEvent>();
        ctx.FailStateSavesRemaining = 1;

        await FluentActions.Awaiting(() =>
                module.HandleAsync(ctx.PublishedEnvelope(completion), ctx, CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("simulated state save failure");

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

        tool.ExecuteCalls.Should().Be(1);
        ctx.LoadState<ToolCallModuleState>("tool_call").PendingExecutions.Should().ContainSingle();
        ctx.Scheduled.Select(static callback => callback.Event)
            .OfType<WorkflowToolCallExecutionRecoveryFiredEvent>().Should().BeEmpty();

        await module.HandleAsync(ctx.PublishedEnvelope(completion), ctx, CancellationToken.None);
        ctx.LoadState<ToolCallModuleState>("tool_call").PendingExecutions.Should().BeEmpty();
    }

    [Fact]
    public async Task ToolCallModule_WhenCompletionTransportsRejectResult_ShouldRetryKnownResultBeforeDeadline()
    {
        var tool = new CountingAgentTool("counting_tool", _ => "{}");
        var logger = new RecordingToolCallLogger();
        var module = CreateModule(tool, logger: logger);
        var ctx = new RecordingWorkflowContext
        {
            FailAttemptCompletionPublishesRemaining = 1,
            FailAttemptCompletionSchedulesRemaining = 1,
        };
        var request = ToolRequest(ctx, tool.Name, "step-1", "exec-transport-fallback");

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);
        await ctx.AttemptCompletionScheduleFailureObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var completed = await ctx.WaitForPublishedAsync<WorkflowToolCallAttemptCompletedEvent>();
        await module.HandleAsync(ctx.PublishedEnvelope(completed), ctx, CancellationToken.None);

        tool.ExecuteCalls.Should().Be(1);
        var toolCompletion = ctx.Published.Select(static item => item.Event)
            .OfType<WorkflowToolCallCompletedEvent>()
            .Should().ContainSingle().Subject;
        toolCompletion.Success.Should().BeTrue();
        toolCompletion.Error.Should().BeEmpty();
        ctx.Published.Select(static item => item.Event)
            .OfType<StepCompletedEvent>()
            .Should().ContainSingle()
            .Which.Success.Should().BeTrue();
        ctx.LoadState<ToolCallModuleState>("tool_call").PendingExecutions.Should().BeEmpty();
        var deliveryObservations = logger.Entries
            .Where(entry => Equals(
                entry.Properties.GetValueOrDefault("Waterline"),
                "completion_delivery_producer_confirmed"))
            .ToList();
        deliveryObservations.Select(entry => new
            {
                Method = entry.Properties["DeliveryMethod"],
                Acceptance = entry.Properties["DeliveryAcceptance"],
            })
            .Should().ContainInOrder(
                new { Method = (object?)"self_publish", Acceptance = (object?)"unknown" },
                new { Method = (object?)"durable_callback", Acceptance = (object?)"unknown" },
                new { Method = (object?)"self_publish", Acceptance = (object?)"confirmed" });
    }

    [Fact]
    public async Task ToolCallModule_ApprovalDeadlineWithoutResume_ShouldFailAndCleanup()
    {
        var approval = new WorkflowToolApprovalPendingOutcome(
            "approval-deadline",
            "approval_tool",
            "provider-call-1",
            "{}",
            "AlwaysRequire",
            true,
            false);
        var tool = new ScriptedResultWorkflowTool(
            "approval_tool",
            new WorkflowToolExecutionResult(string.Empty, PendingApproval: approval));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();
        await ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            stepId: "approval-step",
            executionId: "exec-approval-deadline");

        var pending = ctx.LoadState<ToolCallModuleState>("tool_call")
            .PendingApprovals.Values.Should().ContainSingle().Subject;
        pending.TimeoutLease.Should().NotBeNull();
        pending.TimeoutCallbackId.Should().NotBeNullOrWhiteSpace();
        var timeout = ctx.Scheduled.Should().ContainSingle(callback =>
                callback.Event is WorkflowToolCallTimeoutFiredEvent &&
                callback.Lease.CallbackId == pending.TimeoutCallbackId)
            .Subject;

        await module.HandleAsync(CallbackEnvelope(timeout), ctx, CancellationToken.None);

        tool.ExecuteCalls.Should().Be(1);
        var settled = ctx.LoadState<ToolCallModuleState>("tool_call");
        settled.PendingApprovals.Should().BeEmpty();
        settled.Completions.Should().BeEmpty();
        settled.CompletionTombstones.Should().ContainSingle();
        ctx.Published.Select(static item => item.Event)
            .OfType<StepCompletedEvent>()
            .Should().ContainSingle()
            .Which.Error.Should().Contain("tool_approval_deadline_exceeded");
    }

    [Fact]
    public async Task ToolCallModule_ShouldSettleTimeoutOnceAndIgnoreLateCompletion()
    {
        var tool = new BlockingWorkflowTool("blocking_tool");
        var logger = new RecordingToolCallLogger();
        var module = CreateModule(tool, logger: logger);
        var ctx = new RecordingWorkflowContext();
        var request = ToolRequest(ctx, tool.Name, "step-1", "exec-timeout");

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);
        var invocation = await tool.ReadInvocationAsync();
        var pendingBeforeTimeout = ctx.LoadState<ToolCallModuleState>("tool_call")
            .PendingExecutions.Values.Should().ContainSingle().Subject.Clone();
        var timeout = ctx.Scheduled
            .Should().ContainSingle(static callback => callback.Event is WorkflowToolCallTimeoutFiredEvent)
            .Subject;

        await module.HandleAsync(CallbackEnvelope(timeout), ctx, CancellationToken.None);

        invocation.CancellationToken.IsCancellationRequested.Should().BeTrue();
        var toolCompletion = ctx.Published.Select(static item => item.Event)
            .OfType<WorkflowToolCallCompletedEvent>()
            .Should().ContainSingle().Subject;
        toolCompletion.Success.Should().BeFalse();
        toolCompletion.Error.Should().Contain("tool_outcome_unknown");
        var stepCompletion = ctx.Published.Select(static item => item.Event)
            .OfType<StepCompletedEvent>()
            .Should().ContainSingle().Subject;
        stepCompletion.FailureOutcome.Should().Be(WorkflowStepFailureOutcome.OutcomeUncertain);
        var timedOutState = ctx.LoadState<ToolCallModuleState>("tool_call");
        timedOutState.PendingExecutions.Should().BeEmpty();
        timedOutState.CompletionTombstones.Should().ContainSingle();

        invocation.Completion.SetResult(WorkflowToolExecutionResult.Success("""{"late":true}"""));
        var lateCompletion = new WorkflowToolCallAttemptCompletedEvent
        {
            RunId = pendingBeforeTimeout.RunId,
            StepId = pendingBeforeTimeout.StepId,
            ExecutionId = pendingBeforeTimeout.ExecutionId,
            CallId = pendingBeforeTimeout.CallId,
            Attempt = pendingBeforeTimeout.Attempt,
            ContinuationId = pendingBeforeTimeout.ContinuationId,
            Success = new WorkflowToolCallAttemptSuccessOutcome { ResultJson = """{"late":true}""" },
        };
        var lateEnvelope = Envelope(lateCompletion);
        lateEnvelope.Route = EnvelopeRouteSemantics.CreateTopologyPublication(
            ctx.AgentId,
            TopologyAudience.Self);
        lateEnvelope.Runtime = new EnvelopeRuntime
        {
            DeliveryIdentity = new DeliveryIdentity
            {
                OperationId = RuntimeCallbackKeyComposer.BuildCallbackId(
                    "workflow-tool-attempt-completed",
                    lateCompletion.RunId,
                    lateCompletion.StepId,
                    lateCompletion.CallId,
                    lateCompletion.ExecutionId,
                    lateCompletion.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    lateCompletion.ContinuationId),
            },
        };
        await module.HandleAsync(lateEnvelope, ctx, CancellationToken.None);

        tool.ExecuteCalls.Should().Be(1);
        ctx.Published.Select(static item => item.Event)
            .OfType<WorkflowToolCallCompletedEvent>()
            .Should().ContainSingle();
        ctx.Published.Select(static item => item.Event)
            .OfType<StepCompletedEvent>()
            .Should().ContainSingle();
        ctx.LoadState<ToolCallModuleState>("tool_call")
            .CompletionTombstones.Should().ContainSingle();
        logger.Entries
            .Where(entry => Equals(
                entry.Properties.GetValueOrDefault("Waterline"),
                "actor_reconciliation_completed"))
            .Select(entry => entry.Properties["ReconciliationDisposition"])
            .Should().ContainInOrder("timeout_outcome_unknown", "duplicate");
    }

    [Fact]
    public async Task ToolCallModule_WhenTrustedCompletionHasNoSuccessor_ShouldReportNoPendingExecution()
    {
        var logger = new RecordingToolCallLogger();
        var module = CreateModule(
            new CountingAgentTool("unused_tool", _ => "{}"),
            logger: logger);
        var ctx = new RecordingWorkflowContext();
        var completed = new WorkflowToolCallAttemptCompletedEvent
        {
            RunId = ctx.RunId,
            StepId = "step-missing",
            ExecutionId = "exec-missing",
            CallId = "call-missing",
            Attempt = 1,
            ContinuationId = "continuation-missing",
            Success = new WorkflowToolCallAttemptSuccessOutcome { ResultJson = "{}" },
        };
        var envelope = Envelope(completed);
        envelope.Route = EnvelopeRouteSemantics.CreateTopologyPublication(
            ctx.AgentId,
            TopologyAudience.Self);
        envelope.Runtime = new EnvelopeRuntime
        {
            DeliveryIdentity = new DeliveryIdentity
            {
                OperationId = RuntimeCallbackKeyComposer.BuildCallbackId(
                    "workflow-tool-attempt-completed",
                    completed.RunId,
                    completed.StepId,
                    completed.CallId,
                    completed.ExecutionId,
                    completed.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    completed.ContinuationId),
            },
        };

        await module.HandleAsync(envelope, ctx, CancellationToken.None);

        ctx.Published.Should().BeEmpty();
        logger.Entries
            .Where(entry => Equals(
                entry.Properties.GetValueOrDefault("Waterline"),
                "actor_reconciliation_completed"))
            .Select(entry => entry.Properties["ReconciliationDisposition"])
            .Should().ContainSingle()
            .Which.Should().Be("no_pending_execution");
    }

    [Fact]
    public async Task ToolCallModule_ShouldProjectBeforePublishingCompletionSignalOrPersistingState()
    {
        const string sensitiveMarker = "raw-provider-secret";
        var projection = PayloadProjection();
        var tool = new BlockingWorkflowTool("nyxid_proxy");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext
        {
            CapabilityAdmissionPlan = AdmissionPlan("wf-alpha/call_proxy", projection: projection),
        };
        var request = ToolRequest(
            ctx,
            tool.Name,
            "call_proxy",
            "exec-projection",
            NyxIdInvocation("wf-alpha/call_proxy", projection: projection));

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);
        var invocation = await tool.ReadInvocationAsync();
        invocation.Completion.SetResult(WorkflowToolExecutionResult.Success(
            $$"""{"payload":"visible","secret":"{{sensitiveMarker}}"}"""));
        var completion = await ctx.WaitForPublishedAsync<WorkflowToolCallAttemptCompletedEvent>();

        completion.Success.ResultJson.Should().Be("""{"payload":"visible"}""");
        completion.ToString().Should().NotContain(sensitiveMarker);
        ctx.LoadState<ToolCallModuleState>("tool_call").ToString().Should().NotContain(sensitiveMarker);

        await module.HandleAsync(ctx.PublishedEnvelope(completion), ctx, CancellationToken.None);

        ctx.LoadState<ToolCallModuleState>("tool_call").ToString().Should().NotContain(sensitiveMarker);
        ctx.Published.Select(static item => item.Event.ToString())
            .Should().NotContain(value => value.Contains(sensitiveMarker, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ToolCallModule_WhenToolReturnsPendingOperation_ShouldPersistBeforeSchedulingAndNotResubmit()
    {
        const string script = "return 1";
        const string idempotencyKey = "workflow-idempotency-key";
        const string bearerToken = "must-not-be-persisted";
        var operation = DurableOperation(
            status: WorkflowToolPendingOperationStatus.Queued,
            etag: "etag-1");
        var tool = new ScriptedDurableOperationTool(
            "durable_tool",
            new WorkflowToolExecutionResult(string.Empty, PendingOperation: operation));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();
        ctx.ExecutionContextState.CallerCredential = new WorkflowCallerCredentialState
        {
            BearerToken = bearerToken,
        };
        var request = new StepRequestEvent
        {
            StepId = "call_proxy",
            StepType = "tool_call",
            RunId = ctx.RunId,
            ExecutionId = "exec-durable",
            IdempotencyKey = idempotencyKey,
            Input = $$"""{"script":"{{script}}"}""",
            Parameters = { ["tool"] = tool.Name },
        };

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);
        var completed = await ctx.WaitForPublishedAsync<WorkflowToolCallAttemptCompletedEvent>(candidate =>
            candidate.ExecutionId == request.ExecutionId);

        completed.OutcomeCase.Should().Be(
            WorkflowToolCallAttemptCompletedEvent.OutcomeOneofCase.PendingOperation);
        completed.OutcomeCase.Should().NotBe(
            WorkflowToolCallAttemptCompletedEvent.OutcomeOneofCase.Success);
        completed.Success.Should().BeNull();
        var executing = ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey);
        executing.PendingExecutions.Should().ContainSingle();
        executing.PendingOperations.Should().BeEmpty();

        await module.HandleAsync(ctx.PublishedEnvelope(completed), ctx, CancellationToken.None);

        var state = ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey);
        state.PendingExecutions.Should().BeEmpty();
        var pending = state.PendingOperations.Should().ContainSingle().Subject.Value;
        pending.ProtectedMaterialReference.Should().NotBeNull();
        pending.ProtectedMaterialDigestSha256.Should().MatchRegex("^[0-9a-f]{64}$");
        state.ToString().Should().NotContain(script);
        state.ToString().Should().NotContain(idempotencyKey);
        state.ToString().Should().NotContain(bearerToken);

        pending.OperationId.Should().Be(operation.OperationId);
        pending.ProviderOperationId.Should().Be(operation.ProviderOperationId);
        pending.Etag.Should().Be("etag-1");
        pending.PollAttempt.Should().Be(1);
        pending.PollCallbackId.Should().StartWith("workflow-tool-operation-poll:");
        state.ToString().Should().NotContain("must-not-be-persisted");
        ctx.Scheduled.Select(static callback => callback.Event)
            .OfType<WorkflowToolCallOperationPollFiredEvent>()
            .Should().ContainSingle();

        await ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            input: "{\"script\":\"return 1\"}",
            executionId: "exec-durable",
            idempotencyKey: "workflow-idempotency-key");

        tool.ExecuteCalls.Should().Be(1);
        tool.ReconcileCalls.Should().Be(0);
        ctx.Published.Select(static publication => publication.Event)
            .OfType<WorkflowToolCallStartedEvent>()
            .Should().ContainSingle();
        ctx.Published.Select(static publication => publication.Event)
            .OfType<StepCompletedEvent>()
            .Should().BeEmpty();
    }

    [Fact]
    public async Task ToolCallModule_WhenToolReturnsMalformedPendingReceipt_ShouldPreserveUncertainOutcome()
    {
        var malformed = DurableOperation(
            status: WorkflowToolPendingOperationStatus.Running,
            etag: "etag-1") with
        {
            CancelPath = string.Empty,
        };
        var tool = new ScriptedDurableOperationTool(
            "durable_tool",
            new WorkflowToolExecutionResult(string.Empty, PendingOperation: malformed));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();
        var request = ToolRequest(ctx, tool.Name, "call_proxy", "exec-malformed-receipt");

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);
        var attempt = await ctx.WaitForPublishedAsync<WorkflowToolCallAttemptCompletedEvent>();

        attempt.OutcomeCase.Should().Be(WorkflowToolCallAttemptCompletedEvent.OutcomeOneofCase.Failure);
        attempt.Failure.ErrorCode.Should().Be("workflow_tool_pending_operation_invalid");
        attempt.Failure.FailureOutcome.Should().Be(WorkflowStepFailureOutcome.OutcomeUncertain);

        await module.HandleAsync(ctx.PublishedEnvelope(attempt), ctx, CancellationToken.None);

        ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey)
            .PendingOperations.Should().BeEmpty();
        ctx.Published.Select(static publication => publication.Event)
            .OfType<StepCompletedEvent>()
            .Should().ContainSingle()
            .Which.FailureOutcome.Should().Be(WorkflowStepFailureOutcome.OutcomeUncertain);
    }

    [Fact]
    public async Task ToolCallModule_WhenMalformedPendingReceiptMeetsSecretStoreOutage_ShouldPreserveUncertainOutcome()
    {
        var store = new TrackingRuntimeSecretStore();
        var malformed = DurableOperation(
            status: WorkflowToolPendingOperationStatus.Running,
            etag: "etag-1") with
        {
            CancelPath = string.Empty,
        };
        var tool = new ScriptedDurableOperationTool(
            "durable_tool",
            new WorkflowToolExecutionResult(string.Empty, PendingOperation: malformed));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext { RuntimeSecretStore = store };
        var request = ToolRequest(ctx, tool.Name, "call_proxy", "exec-malformed-secret-outage");

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);
        var attempt = await ctx.WaitForPublishedAsync<WorkflowToolCallAttemptCompletedEvent>();
        var attemptEnvelope = ctx.PublishedEnvelope(attempt);
        attempt.Failure.FailureOutcome.Should().Be(WorkflowStepFailureOutcome.OutcomeUncertain);
        store.ThrowOnResolve = true;

        var outage = await FluentActions.Awaiting(() =>
                module.HandleAsync(attemptEnvelope, ctx, CancellationToken.None))
            .Should().ThrowAsync<Exception>();

        outage.Which.Should().BeAssignableTo<IRuntimeEnvelopeRetryableException>();
        var pending = ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey);
        pending.PendingExecutions.Should().ContainSingle();
        pending.Completions.Should().BeEmpty();
        ctx.Published.Select(static publication => publication.Event)
            .OfType<StepCompletedEvent>().Should().BeEmpty();

        store.ThrowOnResolve = false;
        await module.HandleAsync(attemptEnvelope, ctx, CancellationToken.None);

        var completed = ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey);
        completed.PendingExecutions.Should().BeEmpty();
        ctx.Published.Select(static publication => publication.Event)
            .OfType<StepCompletedEvent>()
            .Should().ContainSingle()
            .Which.FailureOutcome.Should().Be(WorkflowStepFailureOutcome.OutcomeUncertain);
    }

    [Fact]
    public async Task ToolCallModule_WhenAttemptMaterialIsConfirmedUnavailable_ShouldRemainOutcomeUncertain()
    {
        var store = new TrackingRuntimeSecretStore();
        var tool = new CountingAgentTool("counting_tool", _ => "{\"ok\":true}");
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext { RuntimeSecretStore = store };
        var request = ToolRequest(ctx, tool.Name, "call_proxy", "exec-material-unavailable-at-completion");

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);
        var attempt = await ctx.WaitForPublishedAsync<WorkflowToolCallAttemptCompletedEvent>();
        store.ReturnUnavailableOnResolve = true;

        await module.HandleAsync(ctx.PublishedEnvelope(attempt), ctx, CancellationToken.None);

        var completed = ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey);
        completed.PendingExecutions.Should().BeEmpty();
        var step = ctx.Published.Select(static publication => publication.Event)
            .OfType<StepCompletedEvent>()
            .Should().ContainSingle().Subject;
        step.Success.Should().BeFalse();
        step.Error.Should().Contain(ToolCallModule.ToolCallProtectedMaterialErrorCodes.Unavailable);
        step.FailureOutcome.Should().Be(WorkflowStepFailureOutcome.OutcomeUncertain);
    }

    [Fact]
    public async Task ToolCallModule_WhenPersistedPendingReceiptDecodesMalformed_ShouldPreserveUncertainOutcome()
    {
        var operation = DurableOperation(
            status: WorkflowToolPendingOperationStatus.Running,
            etag: "etag-1");
        var tool = new ScriptedDurableOperationTool(
            "durable_tool",
            new WorkflowToolExecutionResult(string.Empty, PendingOperation: operation));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();
        var request = ToolRequest(ctx, tool.Name, "call_proxy", "exec-malformed-signal");

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);
        var attempt = await ctx.WaitForPublishedAsync<WorkflowToolCallAttemptCompletedEvent>();
        attempt.OutcomeCase.Should().Be(
            WorkflowToolCallAttemptCompletedEvent.OutcomeOneofCase.PendingOperation);
        attempt.PendingOperation.CancelPath = string.Empty;

        await module.HandleAsync(ctx.PublishedEnvelope(attempt), ctx, CancellationToken.None);

        ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey)
            .PendingOperations.Should().BeEmpty();
        ctx.Published.Select(static publication => publication.Event)
            .OfType<StepCompletedEvent>()
            .Should().ContainSingle()
            .Which.FailureOutcome.Should().Be(WorkflowStepFailureOutcome.OutcomeUncertain);
    }

    [Fact]
    public async Task ToolCallModule_WhenProviderExpiryExceedsSubmitWatchdog_ShouldPreserveProviderDeadline()
    {
        var providerDeadlineUnixMs = DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeMilliseconds();
        var operation = DurableOperation(
            status: WorkflowToolPendingOperationStatus.Running,
            etag: "etag-1") with
        {
            ExpiresAtUnixMs = providerDeadlineUnixMs,
        };
        var tool = new ScriptedDurableOperationTool(
            "durable_tool",
            new WorkflowToolExecutionResult(string.Empty, PendingOperation: operation));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();
        var request = ToolRequest(ctx, tool.Name, "call_proxy", "exec-600-seconds");
        request.Input = """{"language":"python","code":"pass","timeout_secs":600}""";
        request.TimeoutMs = 360_000;

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);
        var executing = ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey)
            .PendingExecutions.Should().ContainSingle().Subject.Value;
        executing.TimeoutDeadlineUnixMs.Should().BeLessThan(providerDeadlineUnixMs);
        var completed = await ctx.WaitForPublishedAsync<WorkflowToolCallAttemptCompletedEvent>(candidate =>
            candidate.ExecutionId == request.ExecutionId);

        await module.HandleAsync(ctx.PublishedEnvelope(completed), ctx, CancellationToken.None);

        ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey)
            .PendingOperations.Should().ContainSingle().Subject.Value.ExpiresAtUnixMs
            .Should().Be(providerDeadlineUnixMs);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ResolvePendingOperationDeadlineUnixMs_WhenProviderExpiryIsMissing_ShouldUseFiniteFallback(
        long providerExpiresAtUnixMs)
    {
        var acceptedAt = new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);

        var deadlineUnixMs = ToolCallModule.ResolvePendingOperationDeadlineUnixMs(
            providerExpiresAtUnixMs,
            acceptedAt);

        deadlineUnixMs.Should().Be(acceptedAt.AddMinutes(12).ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task ToolCallModule_WhenRecoveredProviderReceiptOmitsExpiry_ShouldResetFiniteReconciliationWatchdog()
    {
        var now = new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);
        var originalDeadline = now.AddMinutes(12).ToUnixTimeMilliseconds();
        var providerReceipt = DurableOperation(
            status: WorkflowToolPendingOperationStatus.Running,
            etag: "etag-2") with
        {
            ExpiresAtUnixMs = 0,
        };
        var submitted = providerReceipt with
        {
            ProviderOperationId = string.Empty,
            StatusPath = string.Empty,
            ResultPath = string.Empty,
            CancelPath = string.Empty,
            Status = WorkflowToolPendingOperationStatus.SubmissionUncertain,
            ETag = null,
            ExpiresAtUnixMs = originalDeadline,
        };
        var tool = new ScriptedDurableOperationTool(
            "durable_tool",
            new WorkflowToolExecutionResult(string.Empty, PendingOperation: submitted),
            new WorkflowToolExecutionResult(string.Empty, PendingOperation: providerReceipt));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext { UtcNowOverride = now };

        await ExecuteToolCallAsync(module, ctx, tool.Name, executionId: "exec-missing-provider-expiry");
        var poll = ctx.Scheduled.Select(static callback => callback.Event)
            .OfType<WorkflowToolCallOperationPollFiredEvent>()
            .Should().ContainSingle().Which;
        ctx.UtcNowOverride = now.AddMinutes(2);

        await module.HandleAsync(OperationPollEnvelope(poll, ctx), ctx, CancellationToken.None);

        ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey)
            .PendingOperations.Should().ContainSingle().Subject.Value.ExpiresAtUnixMs
            .Should().Be(now.AddMinutes(14).ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task ToolCallModule_WhenV4SubmissionUncertainRecoversPersonalCatalogRoute_ShouldRefinePendingReceipt()
    {
        var now = new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);
        var providerReceipt = DurableOperation(
            status: WorkflowToolPendingOperationStatus.Running,
            etag: "etag-catalog") with
        {
            ServiceSlug = "chrono-sandbox-aevatar",
            UserServiceId = "catalog-service",
            RouteIdentitySource = WorkflowToolPendingOperationRouteIdentitySource.NyxIdUserServiceCatalog,
            ExpiresAtUnixMs = now.AddMinutes(10).ToUnixTimeMilliseconds(),
        };
        var submitted = providerReceipt with
        {
            ProviderOperationId = string.Empty,
            StatusPath = string.Empty,
            ResultPath = string.Empty,
            CancelPath = string.Empty,
            Status = WorkflowToolPendingOperationStatus.SubmissionUncertain,
            ETag = null,
            ServiceSlug = CodeExecutionContract.ServiceSlug,
            UserServiceId = null,
            RouteIdentitySource = WorkflowToolPendingOperationRouteIdentitySource.CodeExecutionContract,
            ExpiresAtUnixMs = now.AddMinutes(12).ToUnixTimeMilliseconds(),
        };
        var tool = new ScriptedDurableOperationTool(
            WorkflowAuthorizationDependencyEvaluator.CodeExecuteToolName,
            new WorkflowToolExecutionResult(string.Empty, PendingOperation: submitted),
            new WorkflowToolExecutionResult(string.Empty, PendingOperation: providerReceipt));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext
        {
            UtcNowOverride = now,
            CapabilityAdmissionPlan = new WorkflowCapabilityAdmissionPlan
            {
                SchemaVersion = WorkflowCapabilityAdmissionPlanIntegrity.PreviousSchemaVersion,
            },
        };

        await ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            stepId: "call_code",
            executionId: "exec-v4-route-refinement",
            externalInvocation: CodeExecutionInvocation("wf-alpha/call_code"));
        var poll = ctx.Scheduled.Select(static callback => callback.Event)
            .OfType<WorkflowToolCallOperationPollFiredEvent>()
            .Should().ContainSingle().Which;

        await module.HandleAsync(OperationPollEnvelope(poll, ctx), ctx, CancellationToken.None);

        var pending = ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey)
            .PendingOperations.Should().ContainSingle().Subject.Value;
        pending.ProviderOperationId.Should().Be(providerReceipt.ProviderOperationId);
        pending.ServiceSlug.Should().Be("chrono-sandbox-aevatar");
        pending.UserServiceId.Should().Be("catalog-service");
        pending.RouteIdentitySource.Should()
            .Be(WorkflowToolPendingOperationRouteIdentitySource.NyxIdUserServiceCatalog);
        tool.ReconcileCalls.Should().Be(1);
    }

    [Fact]
    public async Task ToolCallModule_WhenNonCodeToolAttemptsV4RouteRefinement_ShouldRejectReceipt()
    {
        var providerReceipt = DurableOperation(
            status: WorkflowToolPendingOperationStatus.Running,
            etag: "etag-catalog") with
        {
            ServiceSlug = CodeExecutionContract.ServiceSlug,
            UserServiceId = "catalog-service",
            RouteIdentitySource = WorkflowToolPendingOperationRouteIdentitySource.NyxIdUserServiceCatalog,
        };
        var submitted = providerReceipt with
        {
            ProviderOperationId = string.Empty,
            StatusPath = string.Empty,
            ResultPath = string.Empty,
            CancelPath = string.Empty,
            Status = WorkflowToolPendingOperationStatus.SubmissionUncertain,
            ETag = null,
            UserServiceId = null,
            RouteIdentitySource = WorkflowToolPendingOperationRouteIdentitySource.CodeExecutionContract,
        };
        var tool = new ScriptedDurableOperationTool(
            "durable_tool",
            new WorkflowToolExecutionResult(string.Empty, PendingOperation: submitted),
            new WorkflowToolExecutionResult(string.Empty, PendingOperation: providerReceipt));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name, executionId: "exec-non-code-refinement");
        var poll = ctx.Scheduled.Select(static callback => callback.Event)
            .OfType<WorkflowToolCallOperationPollFiredEvent>()
            .Should().ContainSingle().Which;

        await module.HandleAsync(OperationPollEnvelope(poll, ctx), ctx, CancellationToken.None);

        ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey)
            .PendingOperations.Should().BeEmpty();
        ctx.Published.Select(static publication => publication.Event)
            .OfType<StepCompletedEvent>()
            .Should().ContainSingle()
            .Which.FailureOutcome.Should().Be(WorkflowStepFailureOutcome.OutcomeUncertain);
    }

    [Fact]
    public async Task ToolCallModule_WhenPendingOperationRecoveryIntentIsMalformed_ShouldRejectAttemptSignal()
    {
        var operation = DurableOperation(
            status: WorkflowToolPendingOperationStatus.Running,
            etag: "etag-1");
        var invalidIntent = new WorkflowToolCancellationTerminalAuditIntent(
            WorkflowToolExecutionResult.Failed(
                string.Empty,
                "code_execution_cancel_outcome_uncertain",
                "Cancellation outcome is uncertain.",
                terminalInvoked: true,
                retryable: false,
                WorkflowStepFailureOutcome.OutcomeUncertain),
            ArgumentsSha256: "not-a-sha256-digest");
        var tool = new ScriptedDurableOperationTool(
            "durable_tool",
            new WorkflowToolExecutionResult(
                string.Empty,
                PendingOperation: operation,
                CancellationRecoveryIntent: invalidIntent));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();
        var request = ToolRequest(ctx, tool.Name, "call_proxy", "exec-invalid-recovery-intent");

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);
        var attempt = await ctx.WaitForPublishedAsync<WorkflowToolCallAttemptCompletedEvent>();

        attempt.OutcomeCase.Should().Be(
            WorkflowToolCallAttemptCompletedEvent.OutcomeOneofCase.Failure);
        attempt.Failure.ErrorCode.Should().Be("workflow_tool_pending_operation_invalid");
        await module.HandleAsync(ctx.PublishedEnvelope(attempt), ctx, CancellationToken.None);
        ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey)
            .PendingOperations.Should().BeEmpty();
        ctx.Published.Select(static publication => publication.Event)
            .OfType<StepCompletedEvent>().Should().ContainSingle()
            .Which.FailureOutcome.Should().Be(WorkflowStepFailureOutcome.OutcomeUncertain);
    }

    [Fact]
    public async Task ToolCallModule_WhenReceiptArrivesDuringSecretStoreOutage_ShouldPersistAndReschedulePoll()
    {
        var store = new TrackingRuntimeSecretStore { ThrowOnResolve = true };
        var operation = DurableOperation(
            status: WorkflowToolPendingOperationStatus.Running,
            etag: "etag-1");
        var tool = new ScriptedDurableOperationTool(
            "durable_tool",
            new WorkflowToolExecutionResult(string.Empty, PendingOperation: operation));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext { RuntimeSecretStore = store };
        var request = ToolRequest(ctx, tool.Name, "call_proxy", "exec-secret-outage");

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);
        var completed = await ctx.WaitForPublishedAsync<WorkflowToolCallAttemptCompletedEvent>(candidate =>
            candidate.ExecutionId == request.ExecutionId);

        await module.HandleAsync(ctx.PublishedEnvelope(completed), ctx, CancellationToken.None);

        store.ResolveCalls.Should().Be(0);
        var firstPending = ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey)
            .PendingOperations.Should().ContainSingle().Subject.Value;
        firstPending.ProtectedMaterialReference.Should().NotBeNull();
        firstPending.ProtectedMaterialDigestSha256.Should().MatchRegex("^[0-9a-f]{64}$");
        var firstPoll = ctx.Scheduled.Select(static callback => callback.Event)
            .OfType<WorkflowToolCallOperationPollFiredEvent>()
            .Should().ContainSingle().Which;

        await module.HandleAsync(OperationPollEnvelope(firstPoll, ctx), ctx, CancellationToken.None);

        store.ResolveCalls.Should().Be(1);
        tool.ReconcileCalls.Should().Be(0);
        var rescheduled = ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey)
            .PendingOperations.Should().ContainSingle().Subject.Value;
        rescheduled.PollAttempt.Should().Be(2);
        rescheduled.PollCallbackId.Should().NotBe(firstPending.PollCallbackId);
        ctx.Published.Select(static publication => publication.Event)
            .OfType<StepCompletedEvent>()
            .Should().BeEmpty();
    }

    [Fact]
    public async Task ToolCallModule_WhenSecretStoreOutageCrossesProviderExpiry_ShouldCompleteOutcomeUncertain()
    {
        var now = new DateTimeOffset(2026, 8, 17, 8, 0, 0, TimeSpan.Zero);
        var store = new TrackingRuntimeSecretStore { ThrowOnResolve = true };
        var operation = DurableOperation(
            status: WorkflowToolPendingOperationStatus.Running,
            etag: "etag-1") with
        {
            ExpiresAtUnixMs = now.AddSeconds(5).ToUnixTimeMilliseconds(),
        };
        var tool = new ScriptedDurableOperationTool(
            "durable_tool",
            new WorkflowToolExecutionResult(string.Empty, PendingOperation: operation));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext
        {
            RuntimeSecretStore = store,
            UtcNowOverride = now,
        };

        await ExecuteToolCallAsync(module, ctx, tool.Name, executionId: "exec-secret-expiry");
        var firstPoll = ctx.Scheduled.Select(static callback => callback.Event)
            .OfType<WorkflowToolCallOperationPollFiredEvent>()
            .Should().ContainSingle().Which;
        ctx.Scheduled.Clear();

        await module.HandleAsync(OperationPollEnvelope(firstPoll, ctx), ctx, CancellationToken.None);

        var secondPoll = ctx.Scheduled.Select(static callback => callback.Event)
            .OfType<WorkflowToolCallOperationPollFiredEvent>()
            .Should().ContainSingle().Which;
        ctx.Scheduled.Clear();
        ctx.UtcNowOverride = now.AddSeconds(6);

        await module.HandleAsync(OperationPollEnvelope(secondPoll, ctx), ctx, CancellationToken.None);

        store.ResolveCalls.Should().Be(2);
        tool.ReconcileCalls.Should().Be(0);
        ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey)
            .PendingOperations.Should().BeEmpty();
        var completion = ctx.Published.Select(static publication => publication.Event)
            .OfType<StepCompletedEvent>()
            .Should().ContainSingle().Subject;
        completion.Success.Should().BeFalse();
        completion.Error.Should().Contain("expired");
        completion.FailureOutcome.Should().Be(WorkflowStepFailureOutcome.OutcomeUncertain);
    }

    [Fact]
    public async Task ToolCallModule_WhenProtectedMaterialIsConfirmedUnavailable_ShouldFailPendingOperationClosed()
    {
        var operation = DurableOperation(
            status: WorkflowToolPendingOperationStatus.Running,
            etag: "etag-1");
        var tool = new ScriptedDurableOperationTool(
            "durable_tool",
            new WorkflowToolExecutionResult(string.Empty, PendingOperation: operation));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name, executionId: "exec-material-unavailable");
        var pending = ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey)
            .PendingOperations.Should().ContainSingle().Subject.Value;
        var poll = ctx.Scheduled.Select(static callback => callback.Event)
            .OfType<WorkflowToolCallOperationPollFiredEvent>()
            .Should().ContainSingle().Which;
        (await ToolCallModule.RevokeProtectedMaterialAsync(
                pending.ProtectedMaterialReference,
                ctx,
                CancellationToken.None))
            .Should().BeTrue();

        await module.HandleAsync(OperationPollEnvelope(poll, ctx), ctx, CancellationToken.None);

        var terminal = ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey);
        terminal.PendingOperations.Should().BeEmpty();
        tool.ReconcileCalls.Should().Be(0);
        var completion = ctx.Published.Select(static publication => publication.Event)
            .OfType<StepCompletedEvent>()
            .Should().ContainSingle().Subject;
        completion.Success.Should().BeFalse();
        completion.FailureOutcome.Should().Be(WorkflowStepFailureOutcome.OutcomeUncertain);
    }

    [Fact]
    public async Task ToolCallModule_WhenPendingOperationSchedulingFails_ShouldPublishTypedSelfContinuation()
    {
        var operation = DurableOperation(
            status: WorkflowToolPendingOperationStatus.Queued,
            etag: "etag-1");
        var tool = new ScriptedDurableOperationTool(
            "durable_tool",
            new WorkflowToolExecutionResult(string.Empty, PendingOperation: operation));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();
        var request = ToolRequest(ctx, tool.Name, "call_proxy", "exec-durable");

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);
        var completed = await ctx.WaitForPublishedAsync<WorkflowToolCallAttemptCompletedEvent>(candidate =>
            candidate.ExecutionId == request.ExecutionId);
        ctx.Scheduled.Clear();
        ctx.FailNextSchedule = true;

        await module.HandleAsync(ctx.PublishedEnvelope(completed), ctx, CancellationToken.None);

        ctx.Scheduled.Should().BeEmpty();
        var continuation = ctx.Published.Should().ContainSingle(publication =>
                publication.Direction == TopologyAudience.Self &&
                publication.Event is WorkflowToolCallOperationPollFiredEvent)
            .Subject.Event.Should().BeOfType<WorkflowToolCallOperationPollFiredEvent>().Subject;
        var pending = ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey)
            .PendingOperations.Should().ContainSingle().Subject.Value;
        continuation.OperationId.Should().Be(pending.OperationId);
        continuation.CallbackId.Should().Be(pending.PollCallbackId);
        tool.ExecuteCalls.Should().Be(1);
    }

    [Fact]
    public async Task ToolCallModule_WhenPollRescheduleTransportsFail_ShouldRecoverCurrentPollFromOriginalEnvelope()
    {
        var submitted = DurableOperation(
            status: WorkflowToolPendingOperationStatus.Running,
            etag: "etag-1");
        var running = submitted with { ETag = "etag-2" };
        var tool = new ScriptedDurableOperationTool(
            "durable_tool",
            new WorkflowToolExecutionResult(string.Empty, PendingOperation: submitted),
            new WorkflowToolExecutionResult(string.Empty, PendingOperation: running));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name, executionId: "exec-poll-redelivery");
        var originalPoll = ctx.Scheduled.Select(static callback => callback.Event)
            .OfType<WorkflowToolCallOperationPollFiredEvent>()
            .Should().ContainSingle().Which;
        ctx.Scheduled.Clear();
        ctx.FailNextSchedule = true;
        ctx.FailNextPublishType = typeof(WorkflowToolCallOperationPollFiredEvent);

        var failedReschedule = () => module.HandleAsync(
            OperationPollEnvelope(originalPoll, ctx),
            ctx,
            CancellationToken.None);

        await failedReschedule.Should().ThrowAsync<WorkflowDurablePublicationPendingException>();
        var persisted = ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey)
            .PendingOperations.Should().ContainSingle().Subject.Value;
        persisted.PollAttempt.Should().Be(originalPoll.PollAttempt + 1);
        persisted.PollCallbackId.Should().NotBe(originalPoll.CallbackId);
        ctx.Scheduled.Should().BeEmpty();
        tool.ReconcileCalls.Should().Be(1);

        var mismatchedPoll = originalPoll.Clone();
        mismatchedPoll.OperationId = "different-operation";
        await module.HandleAsync(OperationPollEnvelope(mismatchedPoll, ctx), ctx, CancellationToken.None);
        ctx.Scheduled.Should().BeEmpty();

        await module.HandleAsync(OperationPollEnvelope(originalPoll, ctx), ctx, CancellationToken.None);

        var recovered = ctx.Scheduled.Select(static callback => callback.Event)
            .OfType<WorkflowToolCallOperationPollFiredEvent>()
            .Should().ContainSingle().Which;
        recovered.PollAttempt.Should().Be(persisted.PollAttempt);
        recovered.CallbackId.Should().Be(persisted.PollCallbackId);
        recovered.OperationId.Should().Be(persisted.OperationId);
        tool.ReconcileCalls.Should().Be(1);
    }

    [Fact]
    public void BuildOperationPollDelay_ShouldClampToMaximumAndOperationDeadline()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(10_000);
        var pending = new PendingToolCallOperationState
        {
            NextPollUnixMs = now.AddMinutes(5).ToUnixTimeMilliseconds(),
            ExpiresAtUnixMs = now.AddSeconds(7).ToUnixTimeMilliseconds(),
        };

        ToolCallModule.BuildOperationPollDelay(pending, now)
            .Should().Be(TimeSpan.FromSeconds(7));

        pending.ExpiresAtUnixMs = now.AddMinutes(10).ToUnixTimeMilliseconds();
        ToolCallModule.BuildOperationPollDelay(pending, now)
            .Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task ToolCallModule_WhenPendingPollIsRedelivered_ShouldRejectStaleAttemptAndKeepReconciling()
    {
        var providerReceipt = DurableOperation(
            status: WorkflowToolPendingOperationStatus.Running,
            etag: "etag-2");
        var originalDeadline = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds();
        var submitted = providerReceipt with
        {
            ProviderOperationId = string.Empty,
            StatusPath = string.Empty,
            ResultPath = string.Empty,
            CancelPath = string.Empty,
            Status = WorkflowToolPendingOperationStatus.SubmissionUncertain,
            ETag = null,
            ExpiresAtUnixMs = originalDeadline,
        };
        var running = providerReceipt with
        {
            Status = WorkflowToolPendingOperationStatus.Running,
            ETag = "etag-2",
            RetryAfterMilliseconds = 25,
            ExpiresAtUnixMs = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(),
        };
        var tool = new ScriptedDurableOperationTool(
            "durable_tool",
            new WorkflowToolExecutionResult(string.Empty, PendingOperation: submitted),
            new WorkflowToolExecutionResult(string.Empty, PendingOperation: running));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name, executionId: "exec-durable");
        var firstPoll = ctx.Scheduled.Select(static callback => callback.Event)
            .OfType<WorkflowToolCallOperationPollFiredEvent>()
            .Should().ContainSingle().Which;

        await module.HandleAsync(OperationPollEnvelope(firstPoll, ctx), ctx, CancellationToken.None);

        var pending = ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey)
            .PendingOperations.Should().ContainSingle().Subject.Value;
        pending.ProviderOperationId.Should().Be(providerReceipt.ProviderOperationId);
        pending.Status.Should().Be(WorkflowToolPendingOperationStatus.Running);
        pending.Etag.Should().Be("etag-2");
        pending.ExpiresAtUnixMs.Should().Be(running.ExpiresAtUnixMs);
        pending.PollAttempt.Should().Be(2);
        pending.PollCallbackId.Should().NotBe(firstPoll.CallbackId);
        tool.ReconcileCalls.Should().Be(1);

        await module.HandleAsync(OperationPollEnvelope(firstPoll, ctx), ctx, CancellationToken.None);

        tool.ReconcileCalls.Should().Be(1);
        ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey)
            .PendingOperations.Should().ContainSingle();
    }

    [Fact]
    public async Task ToolCallModule_WhenPollDeadlineElapsed_ShouldReconcileBeforeCompleting()
    {
        var submitted = DurableOperation(
            status: WorkflowToolPendingOperationStatus.Running,
            etag: "etag-1") with
        {
            ExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds(),
        };
        var tool = new ScriptedDurableOperationTool(
            "durable_tool",
            new WorkflowToolExecutionResult(string.Empty, PendingOperation: submitted),
            WorkflowToolExecutionResult.Failed(
                string.Empty,
                "OPERATION_EXPIRED",
                "The provider operation expired.",
                terminalInvoked: true));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name, executionId: "exec-durable");
        var poll = ctx.Scheduled.Select(static callback => callback.Event)
            .OfType<WorkflowToolCallOperationPollFiredEvent>()
            .Should().ContainSingle().Which;

        await module.HandleAsync(OperationPollEnvelope(poll, ctx), ctx, CancellationToken.None);

        tool.ReconcileCalls.Should().Be(1);
        var state = ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey);
        state.PendingOperations.Should().BeEmpty();
        state.Completions.Should().BeEmpty();
        state.CompletionTombstones.Should().ContainSingle();
        var completion = ctx.Published.Select(static publication => publication.Event)
            .OfType<StepCompletedEvent>()
            .Should().ContainSingle().Subject;
        completion.Success.Should().BeFalse();
        completion.Error.Should().Contain("expired");
    }

    [Fact]
    public async Task ToolCallModule_WhenPendingPollCompletes_ShouldAtomicallyMoveToCompletionOutbox()
    {
        var submitted = DurableOperation(
            status: WorkflowToolPendingOperationStatus.Running,
            etag: "etag-1");
        var tool = new ScriptedDurableOperationTool(
            "durable_tool",
            new WorkflowToolExecutionResult(string.Empty, PendingOperation: submitted),
            WorkflowToolExecutionResult.Success("{\"exitCode\":0}"));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name, executionId: "exec-durable");
        var poll = ctx.Scheduled.Select(static callback => callback.Event)
            .OfType<WorkflowToolCallOperationPollFiredEvent>()
            .Should().ContainSingle().Which;
        var savesBeforeTerminal = ctx.StateSaveCalls;
        ctx.FailNextPublishType = typeof(WorkflowToolCallCompletedEvent);

        var act = () => module.HandleAsync(OperationPollEnvelope(poll, ctx), ctx, CancellationToken.None);

        await act.Should().ThrowAsync<WorkflowDurablePublicationPendingException>();
        var state = ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey);
        state.PendingOperations.Should().BeEmpty();
        state.Completions.Should().ContainSingle();
        state.Completions[0].StepCompletion.Output.Should().Be("{\"exitCode\":0}");
        ctx.StateSaveCalls.Should().Be(savesBeforeTerminal + 1);
        tool.ExecuteCalls.Should().Be(1);
        tool.ReconcileCalls.Should().Be(1);
    }

    [Fact]
    public async Task ToolCallModule_WhenTerminalPollPublicationWakeupsFail_ShouldRedeliverPollAndDrainCompletion()
    {
        var submitted = DurableOperation(
            status: WorkflowToolPendingOperationStatus.Running,
            etag: "etag-1");
        var tool = new ScriptedDurableOperationTool(
            "durable_tool",
            new WorkflowToolExecutionResult(string.Empty, PendingOperation: submitted),
            WorkflowToolExecutionResult.Success("{\"exitCode\":0}"));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name, executionId: "exec-terminal-poll-redelivery");
        var poll = ctx.Scheduled.Select(static callback => callback.Event)
            .OfType<WorkflowToolCallOperationPollFiredEvent>()
            .Should().ContainSingle().Which;
        ctx.FailPublicationRetrySchedulesRemaining = 1;
        ctx.FailPublicationRetryPublishesRemaining = 1;
        ctx.FailToolCompletionPublishesRemaining = 1;

        var firstDelivery = () => module.HandleAsync(OperationPollEnvelope(poll, ctx), ctx, CancellationToken.None);

        await firstDelivery.Should().ThrowAsync<WorkflowDurablePublicationPendingException>();
        var persisted = ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey);
        persisted.PendingOperations.Should().BeEmpty();
        persisted.Completions.Should().ContainSingle();
        persisted.Completions[0].OperationId.Should().Be(poll.OperationId);
        persisted.Completions[0].OperationPollAttempt.Should().Be(poll.PollAttempt);
        persisted.Completions[0].OperationPollCallbackId.Should().Be(poll.CallbackId);

        await module.HandleAsync(OperationPollEnvelope(poll, ctx), ctx, CancellationToken.None);

        tool.ExecuteCalls.Should().Be(1);
        tool.ReconcileCalls.Should().Be(1);
        var recovered = ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey);
        recovered.Completions.Should().BeEmpty();
        recovered.CompletionTombstones.Should().ContainSingle();
        ctx.Published.Select(static publication => publication.Event)
            .OfType<WorkflowToolCallCompletedEvent>().Should().ContainSingle();
        ctx.Published.Select(static publication => publication.Event)
            .OfType<StepCompletedEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task ToolCallModule_WhenTerminalPollRedeliveryEnvelopeIsForged_ShouldNotDrainCompletion()
    {
        var submitted = DurableOperation(
            status: WorkflowToolPendingOperationStatus.Running,
            etag: "etag-1");
        var tool = new ScriptedDurableOperationTool(
            "durable_tool",
            new WorkflowToolExecutionResult(string.Empty, PendingOperation: submitted),
            WorkflowToolExecutionResult.Success("{\"exitCode\":0}"));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name, executionId: "exec-forged-terminal-poll");
        var poll = ctx.Scheduled.Select(static callback => callback.Event)
            .OfType<WorkflowToolCallOperationPollFiredEvent>()
            .Should().ContainSingle().Which;
        ctx.FailPublicationRetrySchedulesRemaining = 1;
        ctx.FailPublicationRetryPublishesRemaining = 1;
        ctx.FailToolCompletionPublishesRemaining = 1;

        await FluentActions.Awaiting(() => module.HandleAsync(
                OperationPollEnvelope(poll, ctx),
                ctx,
                CancellationToken.None))
            .Should().ThrowAsync<WorkflowDurablePublicationPendingException>();

        var forgedPublisher = OperationPollEnvelope(poll, ctx);
        forgedPublisher.Route.PublisherActorId = "forged-publisher";
        await module.HandleAsync(forgedPublisher, ctx, CancellationToken.None);

        var forgedDelivery = OperationPollEnvelope(poll, ctx);
        forgedDelivery.Runtime.DeliveryIdentity.OperationId = "forged-delivery";
        await module.HandleAsync(forgedDelivery, ctx, CancellationToken.None);

        var forgedCallback = OperationPollEnvelope(poll, ctx);
        forgedCallback.Runtime.Callback.CallbackId = "forged-callback";
        await module.HandleAsync(forgedCallback, ctx, CancellationToken.None);

        var forgedPayload = poll.Clone();
        forgedPayload.OperationId = "forged-operation";
        await module.HandleAsync(
            OperationPollEnvelope(forgedPayload, ctx),
            ctx,
            CancellationToken.None);

        var retained = ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey);
        retained.Completions.Should().ContainSingle();
        retained.CompletionTombstones.Should().BeEmpty();
        ctx.Published.Select(static publication => publication.Event)
            .OfType<WorkflowToolCallCompletedEvent>().Should().BeEmpty();
        ctx.Published.Select(static publication => publication.Event)
            .OfType<StepCompletedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task ToolCallModule_WhenTerminalPollProvenanceCannotBeReconstructed_ShouldNotDrainCompletion()
    {
        var submitted = DurableOperation(
            status: WorkflowToolPendingOperationStatus.Running,
            etag: "etag-1");
        var tool = new ScriptedDurableOperationTool(
            "durable_tool",
            new WorkflowToolExecutionResult(string.Empty, PendingOperation: submitted),
            WorkflowToolExecutionResult.Success("{\"exitCode\":0}"));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name, executionId: "exec-corrupt-terminal-poll");
        var poll = ctx.Scheduled.Select(static callback => callback.Event)
            .OfType<WorkflowToolCallOperationPollFiredEvent>()
            .Should().ContainSingle().Which;
        ctx.FailPublicationRetrySchedulesRemaining = 1;
        ctx.FailPublicationRetryPublishesRemaining = 1;
        ctx.FailToolCompletionPublishesRemaining = 1;
        await FluentActions.Awaiting(() => module.HandleAsync(
                OperationPollEnvelope(poll, ctx),
                ctx,
                CancellationToken.None))
            .Should().ThrowAsync<WorkflowDurablePublicationPendingException>();

        var corrupted = ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey);
        corrupted.Completions.Should().ContainSingle();
        corrupted.Completions[0].OperationPollCallbackId = "forged-persisted-callback";
        await ctx.SaveStateAsync(ToolCallModule.ModuleStateKey, corrupted, CancellationToken.None);
        var forgedPoll = poll.Clone();
        forgedPoll.CallbackId = corrupted.Completions[0].OperationPollCallbackId;

        await module.HandleAsync(
            OperationPollEnvelope(forgedPoll, ctx),
            ctx,
            CancellationToken.None);

        var retained = ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey);
        retained.Completions.Should().ContainSingle();
        retained.CompletionTombstones.Should().BeEmpty();
        ctx.Published.Select(static publication => publication.Event)
            .OfType<WorkflowToolCallCompletedEvent>().Should().BeEmpty();
        ctx.Published.Select(static publication => publication.Event)
            .OfType<StepCompletedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task ToolCallModule_WhenWorkflowStopsWithPendingOperation_ShouldGateStopUntilCancellationSettles()
    {
        var submitted = DurableOperation(
            status: WorkflowToolPendingOperationStatus.Running,
            etag: "etag-1");
        var cancelRequested = submitted with
        {
            ETag = "etag-2",
            RetryAfterMilliseconds = 25,
        };
        var tool = new ScriptedDurableOperationTool(
            "durable_tool",
            new WorkflowToolExecutionResult(string.Empty, PendingOperation: submitted));
        tool.CancellationResults.Enqueue(WorkflowToolCancellationResult.Pending(cancelRequested));
        tool.CancellationResults.Enqueue(WorkflowToolCancellationResult.Completed(
            WorkflowToolExecutionResult.Failed(
                string.Empty,
                "code_execution_cancelled",
                "Code execution was cancelled.",
                terminalInvoked: true)));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name, executionId: "exec-durable");
        ctx.Scheduled.Clear();
        var stop = new WorkflowStoppedEvent
        {
            RunId = ctx.RunId,
            WorkflowName = "workflow-alpha",
            Reason = "requested by caller",
        };

        var gate = () => module.HandleAsync(
            Envelope(stop, publisherActorId: ctx.AgentId),
            ctx,
            CancellationToken.None);

        var gated = await gate.Should().ThrowAsync<Exception>();
        gated.Which.Should().BeAssignableTo<IRuntimeEnvelopeRetryableException>();
        var stopping = ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey);
        stopping.StopCancellation.Should().NotBeNull();
        var pending = stopping.PendingOperations.Should().ContainSingle().Subject.Value;
        pending.StopCancellationPhase.Should().Be(WorkflowToolStopCancellationPhase.Requested);
        var firstCancel = ctx.Scheduled.Select(static callback => callback.Event)
            .OfType<WorkflowToolCallStopCancellationFiredEvent>()
            .Should().ContainSingle().Which;

        ctx.Scheduled.Clear();
        await module.HandleAsync(StopCancellationEnvelope(firstCancel, ctx), ctx, CancellationToken.None);

        tool.CancellationRequests.Should().ContainSingle();
        tool.CancellationRequests[0].DeadlineUnixMs.Should().Be(stopping.StopCancellation.ExpiresAtUnixMs);
        var afterAccepted = ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey);
        afterAccepted.PendingOperations.Should().ContainSingle().Subject.Value.Etag.Should().Be("etag-2");
        var secondCancel = ctx.Scheduled.Select(static callback => callback.Event)
            .OfType<WorkflowToolCallStopCancellationFiredEvent>()
            .Should().ContainSingle().Which;

        await module.HandleAsync(StopCancellationEnvelope(secondCancel, ctx), ctx, CancellationToken.None);

        var released = ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey);
        released.PendingOperations.Should().BeEmpty();
        released.StopCancellation.Should().NotBeNull();
        ctx.Published.Select(static publication => publication.Event)
            .OfType<WorkflowStoppedEvent>()
            .Should().ContainSingle()
            .Which.Reason.Should().Be(stop.Reason);
        ctx.Published.Select(static publication => publication.Event)
            .OfType<StepCompletedEvent>()
            .Should().BeEmpty();

        await module.HandleAsync(
            Envelope(
                ctx.Published.Select(static publication => publication.Event)
                    .OfType<WorkflowStoppedEvent>()
                    .Single(),
                publisherActorId: ctx.AgentId),
            ctx,
            CancellationToken.None);

        ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey)
            .StopCancellation.Should().BeNull();
    }

    [Fact]
    public async Task ToolCallModule_WhenStopPublisherIsExternal_ShouldNotStartDurableCancellation()
    {
        var submitted = DurableOperation(
            status: WorkflowToolPendingOperationStatus.Running,
            etag: "etag-1");
        var tool = new ScriptedDurableOperationTool(
            "durable_tool",
            new WorkflowToolExecutionResult(string.Empty, PendingOperation: submitted));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name, executionId: "exec-forged-stop");
        ctx.Scheduled.Clear();

        await module.HandleAsync(
            Envelope(
                new WorkflowStoppedEvent
                {
                    RunId = ctx.RunId,
                    WorkflowName = "workflow-alpha",
                    Reason = "forged external stop",
                },
                publisherActorId: "forged-stop-publisher"),
            ctx,
            CancellationToken.None);

        var state = ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey);
        state.PendingOperations.Should().ContainSingle();
        state.StopCancellation.Should().BeNull();
        ctx.Scheduled.Should().BeEmpty();
        tool.CancellationRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ToolCallModule_WhenProtectedMaterialIsDefinitivelyUnavailableAfterStopDeadline_ShouldFinalizeFrozenAudit()
    {
        var now = new DateTimeOffset(2026, 8, 17, 10, 0, 0, TimeSpan.Zero);
        var submitted = DurableOperation(
            status: WorkflowToolPendingOperationStatus.Running,
            etag: "etag-1");
        var terminal = WorkflowToolExecutionResult.Failed(
            """{"success":false,"code":"code_execution_cancel_outcome_uncertain"}""",
            "code_execution_cancel_outcome_uncertain",
            "The provider terminal outcome could not be confirmed before the workflow stop deadline.",
            terminalInvoked: true,
            retryable: false,
            WorkflowStepFailureOutcome.OutcomeUncertain);
        var recoveryIntent = new WorkflowToolCancellationTerminalAuditIntent(
            terminal,
            ArgumentsSha256: new string('c', 64));
        var tool = new ScriptedDurableOperationTool(
            "durable_tool",
            new WorkflowToolExecutionResult(
                string.Empty,
                PendingOperation: submitted,
                CancellationRecoveryIntent: recoveryIntent));
        tool.CancellationResults.Enqueue(WorkflowToolCancellationResult.Completed(terminal));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext { UtcNowOverride = now };

        await ExecuteToolCallAsync(module, ctx, tool.Name, executionId: "exec-material-lost");
        ctx.Scheduled.Clear();
        var persistedRecoveryIntent = ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey)
            .PendingOperations.Should().ContainSingle().Subject.Value
            .StopCancellationRecoveryIntent;
        persistedRecoveryIntent.Should().NotBeNull();
        persistedRecoveryIntent.FailureCode.Should().Be("code_execution_cancel_outcome_uncertain");
        persistedRecoveryIntent.FailureOutcome.Should().Be(WorkflowStepFailureOutcome.OutcomeUncertain);
        persistedRecoveryIntent.ArgumentsSha256.Should().Be(new string('c', 64));
        var gate = () => module.HandleAsync(
            Envelope(
                new WorkflowRunStoppedEvent
                {
                    RunId = ctx.RunId,
                    Reason = "requested by caller",
                },
                publisherActorId: ctx.AgentId),
            ctx,
            CancellationToken.None);
        await gate.Should().ThrowAsync<Exception>();

        var stopping = ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey);
        var pending = stopping.PendingOperations.Should().ContainSingle().Subject.Value;
        (await ToolCallModule.RevokeProtectedMaterialAsync(
                pending.ProtectedMaterialReference,
                ctx,
                CancellationToken.None))
            .Should().BeTrue();
        var cancellation = ctx.Scheduled.Select(static callback => callback.Event)
            .OfType<WorkflowToolCallStopCancellationFiredEvent>()
            .Should().ContainSingle().Which;
        ctx.UtcNowOverride = DateTimeOffset.FromUnixTimeMilliseconds(
            stopping.StopCancellation.ExpiresAtUnixMs + 1);

        await module.HandleAsync(
            StopCancellationEnvelope(cancellation, ctx),
            ctx,
            CancellationToken.None);

        var released = ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey);
        released.PendingOperations.Should().BeEmpty();
        tool.CancellationRequests.Should().ContainSingle();
        tool.CancellationRequests[0].TerminalIntent.Should().NotBeNull();
        tool.CancellationRequests[0].TerminalIntent!.ArgumentsSha256.Should().Be(new string('c', 64));
        tool.CancellationRequests[0].ExecutionRequest.ArgumentsJson.Should().Be("{}");
        ctx.Published.Select(static publication => publication.Event)
            .OfType<WorkflowRunStoppedEvent>()
            .Should().ContainSingle();
    }

    [Fact]
    public async Task ToolCallModule_WhenCancellationAuditIsPending_ShouldPersistAndRecoverFrozenTerminalIntent()
    {
        var submitted = DurableOperation(
            status: WorkflowToolPendingOperationStatus.Running,
            etag: "etag-1");
        var terminal = WorkflowToolExecutionResult.Failed(
            """{"success":false,"code":"code_execution_cancel_outcome_uncertain"}""",
            "code_execution_cancel_outcome_uncertain",
            "Cancellation outcome is uncertain.",
            terminalInvoked: true,
            retryable: false,
            WorkflowStepFailureOutcome.OutcomeUncertain);
        var tool = new ScriptedDurableOperationTool(
            "durable_tool",
            new WorkflowToolExecutionResult(string.Empty, PendingOperation: submitted));
        tool.CancellationResults.Enqueue(WorkflowToolCancellationResult.Pending(
            submitted,
            terminalIntent: new WorkflowToolCancellationTerminalAuditIntent(
                terminal,
                ArgumentsSha256: new string('a', 64))));
        tool.CancellationResults.Enqueue(WorkflowToolCancellationResult.Completed(terminal));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name, executionId: "exec-durable");
        ctx.Scheduled.Clear();
        var gate = () => module.HandleAsync(
            Envelope(
                new WorkflowRunStoppedEvent
                {
                    RunId = ctx.RunId,
                    Reason = "requested by caller",
                },
                publisherActorId: ctx.AgentId),
            ctx,
            CancellationToken.None);
        await gate.Should().ThrowAsync<Exception>();
        var firstCancellation = ctx.Scheduled.Select(static callback => callback.Event)
            .OfType<WorkflowToolCallStopCancellationFiredEvent>()
            .Should().ContainSingle().Which;
        ctx.Scheduled.Clear();

        await module.HandleAsync(StopCancellationEnvelope(firstCancellation, ctx), ctx, CancellationToken.None);

        tool.CancellationRequests.Should().ContainSingle()
            .Which.TerminalIntent.Should().BeNull();
        var persisted = ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey);
        var pending = persisted.PendingOperations.Should().ContainSingle().Subject.Value;
        pending.StopCancellationPhase.Should().Be(WorkflowToolStopCancellationPhase.FinalizingAudit);
        pending.StopCancellationTerminalIntent.Should().NotBeNull();
        pending.StopCancellationTerminalIntent.FailureCode.Should()
            .Be("code_execution_cancel_outcome_uncertain");
        pending.StopCancellationTerminalIntent.FailureOutcome.Should()
            .Be(WorkflowStepFailureOutcome.OutcomeUncertain);
        ToolCallModule.PreparePendingStopCancellationRecoveries(
            persisted,
            DateTimeOffset.UtcNow,
            out _).Should().ContainSingle()
            .Which.StopCancellationPhase.Should().Be(WorkflowToolStopCancellationPhase.FinalizingAudit);
        var retry = ctx.Scheduled.Select(static callback => callback.Event)
            .OfType<WorkflowToolCallStopCancellationFiredEvent>()
            .Should().ContainSingle().Which;
        var recoveringModule = CreateModule(tool);

        await recoveringModule.HandleAsync(StopCancellationEnvelope(retry, ctx), ctx, CancellationToken.None);

        tool.CancellationRequests.Should().HaveCount(2);
        tool.CancellationRequests[1].TerminalIntent.Should().NotBeNull();
        tool.CancellationRequests[1].TerminalIntent!.Result.Failure!.ErrorCode
            .Should().Be("code_execution_cancel_outcome_uncertain");
        tool.CancellationRequests[1].TerminalIntent!.Result.Failure!.FailureOutcome
            .Should().Be(WorkflowStepFailureOutcome.OutcomeUncertain);
        ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey)
            .PendingOperations.Should().BeEmpty();
    }

    [Fact]
    public async Task ToolCallModule_WhenCompletedCancellationConflictsWithFrozenIntent_ShouldKeepStopGated()
    {
        var submitted = DurableOperation(
            status: WorkflowToolPendingOperationStatus.Running,
            etag: "etag-1");
        var frozen = WorkflowToolExecutionResult.Failed(
            string.Empty,
            "code_execution_cancelled",
            "Code execution was cancelled.",
            terminalInvoked: true);
        var conflicting = WorkflowToolExecutionResult.Failed(
            string.Empty,
            "code_execution_cancel_outcome_uncertain",
            "Cancellation outcome is uncertain.",
            terminalInvoked: true);
        var tool = new ScriptedDurableOperationTool(
            "durable_tool",
            new WorkflowToolExecutionResult(string.Empty, PendingOperation: submitted));
        tool.CancellationResults.Enqueue(WorkflowToolCancellationResult.Pending(
            submitted,
            terminalIntent: new WorkflowToolCancellationTerminalAuditIntent(
                frozen,
                ArgumentsSha256: new string('b', 64))));
        tool.CancellationResults.Enqueue(WorkflowToolCancellationResult.Completed(conflicting));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name, executionId: "exec-durable");
        ctx.Scheduled.Clear();
        var gate = () => module.HandleAsync(
            Envelope(
                new WorkflowRunStoppedEvent { RunId = ctx.RunId, Reason = "requested" },
                publisherActorId: ctx.AgentId),
            ctx,
            CancellationToken.None);
        await gate.Should().ThrowAsync<Exception>();
        var first = ctx.Scheduled.Select(static callback => callback.Event)
            .OfType<WorkflowToolCallStopCancellationFiredEvent>()
            .Should().ContainSingle().Which;
        ctx.Scheduled.Clear();
        await module.HandleAsync(StopCancellationEnvelope(first, ctx), ctx, CancellationToken.None);
        var second = ctx.Scheduled.Select(static callback => callback.Event)
            .OfType<WorkflowToolCallStopCancellationFiredEvent>()
            .Should().ContainSingle().Which;
        ctx.Scheduled.Clear();

        await module.HandleAsync(StopCancellationEnvelope(second, ctx), ctx, CancellationToken.None);

        var state = ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey);
        state.PendingOperations.Should().ContainSingle().Subject.Value.StopCancellationPhase
            .Should().Be(WorkflowToolStopCancellationPhase.FinalizingAudit);
        state.StopCancellation.Should().NotBeNull();
        ctx.Scheduled.Select(static callback => callback.Event)
            .OfType<WorkflowToolCallStopCancellationFiredEvent>()
            .Should().ContainSingle();
        ctx.Published.Select(static publication => publication.Event)
            .OfType<WorkflowRunStoppedEvent>().Should().BeEmpty();
    }

    [Fact]
    public void BuildStopCancellationDelay_AfterDeadline_ShouldPreserveRecoveryBackoff()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var pending = new PendingToolCallOperationState
        {
            NextStopCancellationUnixMs = now.AddSeconds(5).ToUnixTimeMilliseconds(),
        };

        var delay = ToolCallModule.BuildStopCancellationDelay(
            pending,
            now,
            now.AddSeconds(-1).ToUnixTimeMilliseconds());

        delay.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void BuildStopCancellationDelay_WhenDue_ShouldRemainSchedulable()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var pending = new PendingToolCallOperationState
        {
            NextStopCancellationUnixMs = now.ToUnixTimeMilliseconds(),
        };

        var delay = ToolCallModule.BuildStopCancellationDelay(
            pending,
            now,
            now.AddMinutes(1).ToUnixTimeMilliseconds());

        delay.Should().Be(TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void BuildOperationPollDelay_AfterDeadline_ShouldPreserveRecoveryBackoff()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var pending = new PendingToolCallOperationState
        {
            NextPollUnixMs = now.AddSeconds(5).ToUnixTimeMilliseconds(),
            ExpiresAtUnixMs = now.AddSeconds(-1).ToUnixTimeMilliseconds(),
        };

        var delay = ToolCallModule.BuildOperationPollDelay(pending, now);

        delay.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ToolCallModule_WhenCancellationToolIsMissing_ShouldRefreshDiscoveryOnRetry()
    {
        var submitted = DurableOperation(
            status: WorkflowToolPendingOperationStatus.Running,
            etag: "etag-1");
        var tool = new ScriptedDurableOperationTool(
            "durable_tool",
            new WorkflowToolExecutionResult(string.Empty, PendingOperation: submitted));
        tool.CancellationResults.Enqueue(WorkflowToolCancellationResult.Completed(
            WorkflowToolExecutionResult.Failed(
                string.Empty,
                "code_execution_cancelled",
                "Code execution was cancelled.",
                terminalInvoked: true)));
        var initialModule = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(initialModule, ctx, tool.Name, executionId: "exec-durable");
        ctx.Scheduled.Clear();
        var stop = new WorkflowStoppedEvent
        {
            RunId = ctx.RunId,
            WorkflowName = "workflow-alpha",
            Reason = "requested by caller",
        };
        var gate = () => initialModule.HandleAsync(
            Envelope(stop, publisherActorId: ctx.AgentId),
            ctx,
            CancellationToken.None);
        await gate.Should().ThrowAsync<Exception>();
        var firstCancellation = ctx.Scheduled.Select(static callback => callback.Event)
            .OfType<WorkflowToolCallStopCancellationFiredEvent>()
            .Should().ContainSingle().Which;

        var source = new SequencedToolSource([], [tool]);
        var recoveringModule = new ToolCallModule(
            [source],
            NullLogger<ToolCallModule>.Instance);
        ctx.Scheduled.Clear();
        await recoveringModule.HandleAsync(StopCancellationEnvelope(firstCancellation, ctx), ctx, CancellationToken.None);
        var retry = ctx.Scheduled.Select(static callback => callback.Event)
            .OfType<WorkflowToolCallStopCancellationFiredEvent>()
            .Should().ContainSingle().Which;

        ctx.Scheduled.Clear();
        await recoveringModule.HandleAsync(StopCancellationEnvelope(retry, ctx), ctx, CancellationToken.None);

        source.Calls.Should().Be(2);
        tool.CancellationRequests.Should().ContainSingle();
        ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey)
            .PendingOperations.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolCallModule_WhenCurrentRunStopComesFromExternalPublisher_ShouldGate(
        bool useRunStoppedEvent)
    {
        var submitted = DurableOperation(
            status: WorkflowToolPendingOperationStatus.Running,
            etag: "etag-1");
        var tool = new ScriptedDurableOperationTool(
            "durable_tool",
            new WorkflowToolExecutionResult(string.Empty, PendingOperation: submitted));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name, executionId: "exec-durable");
        ctx.Scheduled.Clear();
        IMessage stop = useRunStoppedEvent
            ? new WorkflowRunStoppedEvent
            {
                RunId = ctx.RunId,
                Reason = "requested by external controller",
            }
            : new WorkflowStoppedEvent
            {
                RunId = ctx.RunId,
                WorkflowName = "workflow-alpha",
                Reason = "requested by external controller",
            };

        var gate = () => module.HandleAsync(
            Envelope(stop, publisherActorId: ctx.AgentId),
            ctx,
            CancellationToken.None);

        var gated = await gate.Should().ThrowAsync<Exception>();
        gated.Which.Should().BeAssignableTo<IRuntimeEnvelopeRetryableException>();
        var state = ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey);
        state.StopCancellation.Should().NotBeNull();
        state.PendingOperations.Should().ContainSingle();
        ctx.Scheduled.Select(static scheduled => scheduled.Event)
            .OfType<WorkflowToolCallStopCancellationFiredEvent>()
            .Should().ContainSingle();
    }

    [Fact]
    public async Task ToolCallModule_WhenPersistedStopReleasePublishFails_ShouldDrainOnCallbackRedelivery()
    {
        var submitted = DurableOperation(
            status: WorkflowToolPendingOperationStatus.Running,
            etag: "etag-1");
        var tool = new ScriptedDurableOperationTool(
            "durable_tool",
            new WorkflowToolExecutionResult(string.Empty, PendingOperation: submitted));
        tool.CancellationResults.Enqueue(WorkflowToolCancellationResult.Completed(
            WorkflowToolExecutionResult.Failed(
                string.Empty,
                "code_execution_cancelled",
                "Code execution was cancelled.",
                terminalInvoked: true)));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name, executionId: "exec-durable");
        ctx.Scheduled.Clear();
        var stop = new WorkflowStoppedEvent
        {
            RunId = ctx.RunId,
            WorkflowName = "workflow-alpha",
            Reason = "requested by caller",
        };
        var gate = () => module.HandleAsync(
            Envelope(stop, publisherActorId: ctx.AgentId),
            ctx,
            CancellationToken.None);
        await gate.Should().ThrowAsync<Exception>();
        var cancellation = ctx.Scheduled.Select(static callback => callback.Event)
            .OfType<WorkflowToolCallStopCancellationFiredEvent>()
            .Should().ContainSingle().Which;
        ctx.FailNextPublishType = typeof(WorkflowStoppedEvent);

        var firstTerminalDelivery = () => module.HandleAsync(
            StopCancellationEnvelope(cancellation, ctx),
            ctx,
            CancellationToken.None);

        var publishFailure = await firstTerminalDelivery.Should().ThrowAsync<Exception>();
        publishFailure.Which.Should().BeAssignableTo<IRuntimeEnvelopeRetryableException>();
        var persisted = ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey);
        persisted.PendingOperations.Should().BeEmpty();
        persisted.StopCancellation.Should().NotBeNull();
        ctx.Published.Select(static publication => publication.Event)
            .OfType<WorkflowStoppedEvent>()
            .Should().BeEmpty();

        await module.HandleAsync(
            Envelope(cancellation, publisherActorId: ctx.AgentId),
            ctx,
            CancellationToken.None);
        ctx.Published.Select(static publication => publication.Event)
            .OfType<WorkflowStoppedEvent>()
            .Should().BeEmpty();

        await module.HandleAsync(
            StopCancellationEnvelope(cancellation, ctx),
            ctx,
            CancellationToken.None);

        ctx.Published.Select(static publication => publication.Event)
            .OfType<WorkflowStoppedEvent>()
            .Should().ContainSingle()
            .Which.Reason.Should().Be(stop.Reason);
    }

    [Fact]
    public async Task ToolCallModule_WhenStopCancellationFailsNonRetryably_ShouldNotSettleWithoutAuditProof()
    {
        var submitted = DurableOperation(
            status: WorkflowToolPendingOperationStatus.Running,
            etag: "etag-1");
        var tool = new ScriptedDurableOperationTool(
            "durable_tool",
            new WorkflowToolExecutionResult(string.Empty, PendingOperation: submitted));
        tool.CancellationResults.Enqueue(WorkflowToolCancellationResult.Failed(
            "provider_cancel_rejected",
            "The provider rejected cancellation.",
            retryable: false));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name, executionId: "exec-durable");
        ctx.Scheduled.Clear();
        var gate = () => module.HandleAsync(
            Envelope(
                new WorkflowRunStoppedEvent
                {
                    RunId = ctx.RunId,
                    Reason = "requested by caller",
                },
                publisherActorId: ctx.AgentId),
            ctx,
            CancellationToken.None);
        await gate.Should().ThrowAsync<Exception>();
        var cancellation = ctx.Scheduled.Select(static callback => callback.Event)
            .OfType<WorkflowToolCallStopCancellationFiredEvent>()
            .Should().ContainSingle().Which;
        ctx.Scheduled.Clear();

        await module.HandleAsync(
            StopCancellationEnvelope(cancellation, ctx),
            ctx,
            CancellationToken.None);

        var state = ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey);
        state.PendingOperations.Should().ContainSingle();
        state.StopCancellation.Should().NotBeNull();
        ctx.Scheduled.Select(static scheduled => scheduled.Event)
            .OfType<WorkflowToolCallStopCancellationFiredEvent>()
            .Should().ContainSingle();
        ctx.Published.Select(static publication => publication.Event)
            .OfType<WorkflowRunStoppedEvent>()
            .Should().BeEmpty();
    }

    [Fact]
    public async Task ToolCallModule_WhenStopRunDoesNotMatch_ShouldNotCancelPendingOperation()
    {
        var submitted = DurableOperation(
            status: WorkflowToolPendingOperationStatus.Running,
            etag: "etag-1");
        var tool = new ScriptedDurableOperationTool(
            "durable_tool",
            new WorkflowToolExecutionResult(string.Empty, PendingOperation: submitted));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name, executionId: "exec-durable");
        ctx.Scheduled.Clear();

        await module.HandleAsync(
            Envelope(new WorkflowRunStoppedEvent
            {
                RunId = "another-run",
                Reason = "child stopped",
            }),
            ctx,
            CancellationToken.None);

        var state = ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey);
        state.PendingOperations.Should().ContainSingle();
        state.StopCancellation.Should().BeNull();
        ctx.Scheduled.Should().BeEmpty();
        tool.CancellationRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ToolCallModule_WhenStopCancellationSchedulingFails_ShouldPublishTypedSelfContinuation()
    {
        var submitted = DurableOperation(
            status: WorkflowToolPendingOperationStatus.Running,
            etag: "etag-1");
        var tool = new ScriptedDurableOperationTool(
            "durable_tool",
            new WorkflowToolExecutionResult(string.Empty, PendingOperation: submitted));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name, executionId: "exec-durable");
        ctx.Scheduled.Clear();
        ctx.FailNextSchedule = true;

        var gate = () => module.HandleAsync(
            Envelope(
                new WorkflowRunStoppedEvent
                {
                    RunId = ctx.RunId,
                    Reason = "requested by caller",
                },
                publisherActorId: ctx.AgentId),
            ctx,
            CancellationToken.None);

        var gated = await gate.Should().ThrowAsync<Exception>();
        gated.Which.Should().BeAssignableTo<IRuntimeEnvelopeRetryableException>();
        ctx.Scheduled.Should().BeEmpty();
        ctx.Published.Should().ContainSingle(publication =>
            publication.Direction == TopologyAudience.Self &&
            publication.Event is WorkflowToolCallStopCancellationFiredEvent);
        ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey)
            .StopCancellation.Should().NotBeNull();
    }

    [Fact]
    public async Task ToolCallModule_WhenStopCancellationEnvelopeIsUntrusted_ShouldNotMutateCancellationState()
    {
        var submitted = DurableOperation(
            status: WorkflowToolPendingOperationStatus.Running,
            etag: "etag-1");
        var tool = new ScriptedDurableOperationTool(
            "durable_tool",
            new WorkflowToolExecutionResult(string.Empty, PendingOperation: submitted));
        tool.CancellationResults.Enqueue(WorkflowToolCancellationResult.Completed(
            WorkflowToolExecutionResult.Failed(
                string.Empty,
                "code_execution_cancelled",
                "Code execution was cancelled.",
                terminalInvoked: true)));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name, executionId: "exec-untrusted-cancel");
        ctx.Scheduled.Clear();
        var gate = () => module.HandleAsync(
            Envelope(
                new WorkflowRunStoppedEvent
                {
                    RunId = ctx.RunId,
                    Reason = "requested by caller",
                },
                publisherActorId: ctx.AgentId),
            ctx,
            CancellationToken.None);
        await gate.Should().ThrowAsync<Exception>();
        var fired = ctx.Scheduled.Select(static callback => callback.Event)
            .OfType<WorkflowToolCallStopCancellationFiredEvent>()
            .Should().ContainSingle().Which;
        ctx.Scheduled.Clear();

        var forgedPublisher = StopCancellationEnvelope(fired, ctx);
        forgedPublisher.Route = EnvelopeRouteSemantics.CreateTopologyPublication(
            "forged-agent",
            TopologyAudience.Self);
        var nonSelfAudience = StopCancellationEnvelope(fired, ctx);
        nonSelfAudience.Route = EnvelopeRouteSemantics.CreateTopologyPublication(
            ctx.AgentId,
            TopologyAudience.Children);
        var missingDeliveryIdentity = Envelope(fired, publisherActorId: ctx.AgentId);
        var mismatchedDeliveryIdentity = StopCancellationEnvelope(fired, ctx);
        mismatchedDeliveryIdentity.Runtime!.DeliveryIdentity!.OperationId = "forged-callback";
        var mismatchedCallbackIdentity = StopCancellationEnvelope(fired, ctx);
        mismatchedCallbackIdentity.Runtime!.Callback!.CallbackId = "forged-callback";
        var forgedCallback = fired.Clone();
        forgedCallback.CallbackId = "forged-callback";

        foreach (var untrusted in new[]
                 {
                     forgedPublisher,
                     nonSelfAudience,
                     missingDeliveryIdentity,
                     mismatchedDeliveryIdentity,
                     mismatchedCallbackIdentity,
                     StopCancellationEnvelope(forgedCallback, ctx),
                 })
        {
            await module.HandleAsync(untrusted, ctx, CancellationToken.None);
        }

        var retained = ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey);
        retained.PendingOperations.Should().ContainSingle().Subject.Value.StopCancellationAttempt
            .Should().Be(fired.Attempt);
        tool.CancellationRequests.Should().BeEmpty();
        ctx.Scheduled.Should().BeEmpty();

        await module.HandleAsync(
            StopCancellationEnvelope(fired, ctx),
            ctx,
            CancellationToken.None);

        tool.CancellationRequests.Should().ContainSingle();
        ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey)
            .PendingOperations.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolCallModule_WhenStopCancellationEnvelopeHasOneTrustedIdentity_ShouldCancel(
        bool useDeliveryIdentity)
    {
        var submitted = DurableOperation(
            status: WorkflowToolPendingOperationStatus.Running,
            etag: "etag-1");
        var tool = new ScriptedDurableOperationTool(
            "durable_tool",
            new WorkflowToolExecutionResult(string.Empty, PendingOperation: submitted));
        tool.CancellationResults.Enqueue(WorkflowToolCancellationResult.Completed(
            WorkflowToolExecutionResult.Failed(
                string.Empty,
                "code_execution_cancelled",
                "Code execution was cancelled.",
                terminalInvoked: true)));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(
            module,
            ctx,
            tool.Name,
            executionId: useDeliveryIdentity
                ? "exec-delivery-only-cancel"
                : "exec-callback-only-cancel");
        ctx.Scheduled.Clear();
        ctx.FailNextSchedule = useDeliveryIdentity;
        var gate = () => module.HandleAsync(
            Envelope(
                new WorkflowRunStoppedEvent
                {
                    RunId = ctx.RunId,
                    Reason = "requested by caller",
                },
                publisherActorId: ctx.AgentId),
            ctx,
            CancellationToken.None);
        await gate.Should().ThrowAsync<Exception>();

        EventEnvelope trustedEnvelope;
        if (useDeliveryIdentity)
        {
            var published = ctx.Published.Select(static publication => publication.Event)
                .OfType<WorkflowToolCallStopCancellationFiredEvent>()
                .Should().ContainSingle().Which;
            trustedEnvelope = ctx.PublishedEnvelope(published);
            trustedEnvelope.Runtime!.Callback.Should().BeNull();
        }
        else
        {
            var scheduled = ctx.Scheduled.Select(static callback => callback.Event)
                .OfType<WorkflowToolCallStopCancellationFiredEvent>()
                .Should().ContainSingle().Which;
            trustedEnvelope = StopCancellationEnvelope(scheduled, ctx);
            trustedEnvelope.Runtime!.DeliveryIdentity = null;
        }

        await module.HandleAsync(trustedEnvelope, ctx, CancellationToken.None);

        tool.CancellationRequests.Should().ContainSingle();
        ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey)
            .PendingOperations.Should().BeEmpty();
    }

    [Fact]
    public async Task ToolCallModule_WhenCancellationRescheduleTransportsFail_ShouldRecoverCurrentCallbackFromOriginalEnvelope()
    {
        var submitted = DurableOperation(
            status: WorkflowToolPendingOperationStatus.Running,
            etag: "etag-1");
        var cancelling = submitted with { ETag = "etag-2" };
        var tool = new ScriptedDurableOperationTool(
            "durable_tool",
            new WorkflowToolExecutionResult(string.Empty, PendingOperation: submitted));
        tool.CancellationResults.Enqueue(WorkflowToolCancellationResult.Pending(cancelling));
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext();

        await ExecuteToolCallAsync(module, ctx, tool.Name, executionId: "exec-cancel-redelivery");
        ctx.Scheduled.Clear();
        var startStop = () => module.HandleAsync(
            Envelope(
                new WorkflowRunStoppedEvent
                {
                    RunId = ctx.RunId,
                    Reason = "requested by caller",
                },
                publisherActorId: ctx.AgentId),
            ctx,
            CancellationToken.None);
        await startStop.Should().ThrowAsync<Exception>();
        var originalCancellation = ctx.Scheduled.Select(static callback => callback.Event)
            .OfType<WorkflowToolCallStopCancellationFiredEvent>()
            .Should().ContainSingle().Which;
        ctx.Scheduled.Clear();
        ctx.FailNextSchedule = true;
        ctx.FailNextPublishType = typeof(WorkflowToolCallStopCancellationFiredEvent);

        var failedReschedule = () => module.HandleAsync(
            StopCancellationEnvelope(originalCancellation, ctx),
            ctx,
            CancellationToken.None);

        var failure = await failedReschedule.Should().ThrowAsync<Exception>();
        failure.Which.Should().BeAssignableTo<IRuntimeEnvelopeRetryableException>();
        var persisted = ctx.LoadState<ToolCallModuleState>(ToolCallModule.ModuleStateKey)
            .PendingOperations.Should().ContainSingle().Subject.Value;
        persisted.StopCancellationAttempt.Should().Be(originalCancellation.Attempt + 1);
        persisted.StopCancellationCallbackId.Should().NotBe(originalCancellation.CallbackId);
        ctx.Scheduled.Should().BeEmpty();
        tool.CancellationRequests.Should().ContainSingle();

        var mismatchedCancellation = originalCancellation.Clone();
        mismatchedCancellation.OperationId = "different-operation";
        await module.HandleAsync(
            StopCancellationEnvelope(mismatchedCancellation, ctx),
            ctx,
            CancellationToken.None);
        ctx.Scheduled.Should().BeEmpty();

        await module.HandleAsync(
            StopCancellationEnvelope(originalCancellation, ctx),
            ctx,
            CancellationToken.None);

        var recovered = ctx.Scheduled.Select(static callback => callback.Event)
            .OfType<WorkflowToolCallStopCancellationFiredEvent>()
            .Should().ContainSingle().Which;
        recovered.Attempt.Should().Be(persisted.StopCancellationAttempt);
        recovered.CallbackId.Should().Be(persisted.StopCancellationCallbackId);
        recovered.OperationId.Should().Be(persisted.OperationId);
        tool.CancellationRequests.Should().ContainSingle();
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

    private static WorkflowToolPendingOperation DurableOperation(
        WorkflowToolPendingOperationStatus status,
        string? etag) =>
        new(
            "tool:v1:operation:" + new string('a', 64),
            "provider-operation-1",
            "/executions/provider-operation-1",
            "/executions/provider-operation-1/result",
            "/executions/provider-operation-1/cancel",
            status,
            etag,
            10,
            DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
            "chrono-sandbox",
            "user-service-1",
            WorkflowToolPendingOperationRouteIdentitySource.CodeExecutionContract);

    internal static ToolCallModule CreateModule(
        IWorkflowTool tool,
        IWorkflowCallerAccessTokenProvider? tokenProvider = null,
        ILogger<ToolCallModule>? logger = null) =>
        new(
            [new SingleToolSource(tool)],
            logger ?? NullLogger<ToolCallModule>.Instance,
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
        string operationId = "get_item",
        WorkflowToolResponseProjection? projection = null) =>
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
            ResponseProjection = projection?.Clone(),
        };

    private static ExternalToolInvocationSpec CodeExecutionInvocation(string callSiteId) =>
        new()
        {
            CallSiteId = callSiteId,
            ToolName = WorkflowAuthorizationDependencyEvaluator.CodeExecuteToolName,
            Selector = new ExternalWorkflowCapabilitySelector
            {
                CodeExecution = new CodeExecutionSelector(),
            },
        };

    private static WorkflowCapabilityAdmissionPlan AdmissionPlan(
        string callSiteId,
        string userServiceId = "us-shop-alpha",
        string operationId = "get_item",
        WorkflowToolResponseProjection? projection = null)
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
            ResponseProjection = projection?.Clone(),
        });
        return plan;
    }

    private static WorkflowToolResponseProjection ApprovalDetailProjection()
    {
        var projection = new WorkflowToolResponseProjection();
        projection.Fields.Add(Field("instance_code", Pointer("/data/instance_code")));
        projection.Fields.Add(Field("status", Pointer("/data/status")));
        projection.Fields.Add(Field(
            "vendor",
            Pointer("/data/form"),
            ParseJson(),
            Match("/id", "field-list"),
            Pointer("/value/0"),
            Match("/id", "vendor-widget"),
            Pointer("/value")));
        return projection;
    }

    private static WorkflowToolResponseProjection PayloadProjection()
    {
        var projection = new WorkflowToolResponseProjection();
        projection.Fields.Add(Field("payload", Pointer("/payload")));
        return projection;
    }

    private static WorkflowToolResponseProjectionField Field(
        string outputName,
        params WorkflowToolResponseProjectionOperation[] operations) =>
        new()
        {
            OutputName = outputName,
            Operations = { operations },
        };

    private static WorkflowToolResponseProjectionOperation Pointer(string pointer) =>
        new() { JsonPointer = pointer };

    private static WorkflowToolResponseProjectionOperation ParseJson() =>
        new() { ParseJson = true };

    private static WorkflowToolResponseProjectionOperation Match(string pointer, string expected) =>
        new()
        {
            ArrayMatch = new WorkflowToolResponseProjectionArrayMatch
            {
                ElementJsonPointer = pointer,
                ExpectedString = expected,
            },
        };

    private static async Task ExecuteToolCallAsync(
        ToolCallModule module,
        RecordingWorkflowContext ctx,
        string toolName,
        string stepId = "call_proxy",
        string input = "{}",
        string executionId = "exec-default",
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
        await DrainToolCallContinuationsAsync(module, ctx);
    }

    internal static StepRequestEvent ToolRequest(
        RecordingWorkflowContext ctx,
        string toolName,
        string stepId,
        string executionId,
        ExternalToolInvocationSpec? externalInvocation = null) =>
        new()
        {
            StepId = stepId,
            StepType = "tool_call",
            RunId = ctx.RunId,
            ExecutionId = executionId,
            Input = "{}",
            Parameters = { ["tool"] = toolName },
            ExternalInvocation = externalInvocation,
        };

    private static async Task DrainToolCallContinuationsAsync(
        ToolCallModule module,
        RecordingWorkflowContext ctx)
    {
        while (true)
        {
            var pending = ctx.LoadState<ToolCallModuleState>("tool_call")
                .PendingExecutions.Values.FirstOrDefault(static candidate =>
                    candidate.ExecutionPhase == WorkflowToolCallExecutionPhase.ExecutionPending);
            if (pending == null)
                return;

            var completed = await ctx.WaitForPublishedAsync<WorkflowToolCallAttemptCompletedEvent>(candidate =>
                candidate.CallId == pending.CallId &&
                candidate.ExecutionId == pending.ExecutionId &&
                candidate.Attempt == pending.Attempt &&
                candidate.ContinuationId == pending.ContinuationId);
            ctx.Published.RemoveAll(item => ReferenceEquals(item.Event, completed));

            await module.HandleAsync(
                ctx.PublishedEnvelope(completed),
                ctx,
                CancellationToken.None);
        }
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

    private static ResolveRuntimeSecretRequest ResolveRequest(RuntimeSecretReference reference) =>
        new(
            reference.Ref,
            reference.Purpose,
            reference.OwnerRunId,
            reference.OwnerStepId,
            ToolCallModule.ProtectedMaterialAuditReason);

    private static void AssertProtectedPendingExecution(PendingToolCallExecutionState pending)
    {
        pending.ProtectedMaterialReference.Should().NotBeNull();
        pending.ProtectedMaterialDigestSha256.Should().MatchRegex("^[0-9a-f]{64}$");
        pending.ExecutionPhase.Should().Be(WorkflowToolCallExecutionPhase.ExecutionPending);
        pending.ArgumentsJson.Should().BeEmpty();
        pending.InputFileRefs.Should().BeEmpty();
        pending.IdempotencyKey.Should().BeEmpty();
        pending.ExternalInvocation.Should().BeNull();
        pending.DisplayName.Should().BeEmpty();
    }

    internal static EventEnvelope Envelope(
        IMessage evt,
        DateTimeOffset? issuedAt = null,
        string publisherActorId = "test")
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(issuedAt ?? DateTimeOffset.UtcNow),
            Payload = Any.Pack(evt),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(
                publisherActorId,
                TopologyAudience.Self),
        };
    }

    internal static EventEnvelope CallbackEnvelope(ScheduledToolCallback callback)
    {
        var envelope = Envelope(callback.Event);
        envelope.Runtime = new EnvelopeRuntime
        {
            Callback = new EnvelopeCallbackContext
            {
                CallbackId = callback.Lease.CallbackId,
                Generation = callback.Lease.Generation,
                FiredAtUnixTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                SlotEpoch = callback.Lease.SlotEpoch,
            },
        };
        return envelope;
    }

    private static EventEnvelope OperationPollEnvelope(
        WorkflowToolCallOperationPollFiredEvent poll,
        RecordingWorkflowContext ctx)
    {
        var envelope = Envelope(poll, publisherActorId: ctx.AgentId);
        envelope.Runtime = new EnvelopeRuntime
        {
            DeliveryIdentity = new DeliveryIdentity { OperationId = poll.CallbackId },
            Callback = new EnvelopeCallbackContext
            {
                CallbackId = poll.CallbackId,
                Generation = 1,
                FiredAtUnixTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
        };
        return envelope;
    }

    private static EventEnvelope StopCancellationEnvelope(
        WorkflowToolCallStopCancellationFiredEvent fired,
        RecordingWorkflowContext ctx)
    {
        var envelope = Envelope(fired, publisherActorId: ctx.AgentId);
        envelope.Runtime = new EnvelopeRuntime
        {
            DeliveryIdentity = new DeliveryIdentity { OperationId = fired.CallbackId },
            Callback = new EnvelopeCallbackContext
            {
                CallbackId = fired.CallbackId,
                Generation = 1,
                FiredAtUnixTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
        };
        return envelope;
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

        public int ExecuteCalls { get; private set; }

        public Task<WorkflowToolExecutionResult> ExecuteAsync(
            WorkflowToolExecutionRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ExecuteCalls++;
            return Task.FromResult(result);
        }
    }

    internal sealed class BlockingWorkflowTool(
        string name,
        WorkflowToolRecoverySafety recoverySafety = WorkflowToolRecoverySafety.Unspecified) : IWorkflowTool
    {
        private readonly Channel<BlockingToolInvocation> _invocations =
            Channel.CreateUnbounded<BlockingToolInvocation>();
        private int _executeCalls;

        public string Name { get; } = name;

        public WorkflowToolRecoverySafety RecoverySafety { get; } = recoverySafety;

        public int ExecuteCalls => Volatile.Read(ref _executeCalls);

        public Task<WorkflowToolExecutionResult> ExecuteAsync(
            WorkflowToolExecutionRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _executeCalls);
            var completion = new TaskCompletionSource<WorkflowToolExecutionResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _invocations.Writer.TryWrite(new BlockingToolInvocation(request, completion, ct));
            return completion.Task;
        }

        public ValueTask<BlockingToolInvocation> ReadInvocationAsync() =>
            _invocations.Reader.ReadAsync();
    }

    internal sealed record BlockingToolInvocation(
        WorkflowToolExecutionRequest Request,
        TaskCompletionSource<WorkflowToolExecutionResult> Completion,
        CancellationToken CancellationToken);

    internal sealed record ScheduledToolCallback(
        TimeSpan DueTime,
        IMessage Event,
        RuntimeCallbackLease Lease);

    private sealed class ScriptedDurableOperationTool : IWorkflowDurableOperationTool
    {
        private readonly WorkflowToolExecutionResult _executeResult;
        private readonly Queue<WorkflowToolExecutionResult> _reconciliationResults;

        public ScriptedDurableOperationTool(
            string name,
            WorkflowToolExecutionResult executeResult,
            params WorkflowToolExecutionResult[] reconciliationResults)
        {
            Name = name;
            _executeResult = executeResult;
            _reconciliationResults = new Queue<WorkflowToolExecutionResult>(reconciliationResults);
        }

        public string Name { get; }

        public WorkflowToolRecoverySafety RecoverySafety =>
            WorkflowToolRecoverySafety.DurableStartOnceRedispatch;

        public int ExecuteCalls { get; private set; }

        public int ReconcileCalls { get; private set; }

        public Queue<WorkflowToolCancellationResult> CancellationResults { get; } = [];

        public List<WorkflowToolCancellationRequest> CancellationRequests { get; } = [];

        public Task<WorkflowToolExecutionResult> ExecuteAsync(
            WorkflowToolExecutionRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ExecuteCalls++;
            return Task.FromResult(_executeResult);
        }

        public Task<WorkflowToolExecutionResult> ReconcileAsync(
            WorkflowToolExecutionRequest request,
            WorkflowToolPendingOperation pendingOperation,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ReconcileCalls++;
            _reconciliationResults.Should().NotBeEmpty();
            return Task.FromResult(_reconciliationResults.Dequeue());
        }

        public Task<WorkflowToolCancellationResult> CancelAsync(
            WorkflowToolCancellationRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CancellationRequests.Add(request);
            CancellationResults.Should().NotBeEmpty();
            return Task.FromResult(CancellationResults.Dequeue());
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

        public int ExecuteCalls { get; private set; }

        public Task<WorkflowToolExecutionResult> ExecuteAsync(WorkflowToolExecutionRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ExecuteCalls++;
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

    private sealed class SequencedToolSource(params IReadOnlyList<IWorkflowTool>[] discoveries)
        : IWorkflowToolSource
    {
        private readonly Queue<IReadOnlyList<IWorkflowTool>> _discoveries = new(discoveries);

        public int Calls { get; private set; }

        public Task<IReadOnlyList<IWorkflowTool>> GetToolsAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            _discoveries.Should().NotBeEmpty();
            return Task.FromResult(_discoveries.Dequeue());
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(formatter(state, exception) + (exception?.ToString() ?? string.Empty));
        }
    }

    internal sealed class RecordingToolCallLogger : ILogger<ToolCallModule>
    {
        private readonly object _gate = new();
        private readonly List<LogEntry> _entries = [];
        private readonly List<(Func<LogEntry, bool> Predicate, TaskCompletionSource<LogEntry> Completion)> _waiters = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_gate)
                    return _entries.ToList();
            }
        }

        /// <summary>
        /// Completes when an entry matching <paramref name="predicate"/> has been logged (already
        /// recorded or arriving later, e.g. from a background tool worker). Deterministic sync
        /// point; never polls.
        /// </summary>
        public Task<LogEntry> WaitForEntryAsync(Func<LogEntry, bool> predicate)
        {
            TaskCompletionSource<LogEntry> completion;
            lock (_gate)
            {
                var existing = _entries.FirstOrDefault(predicate);
                if (existing != null)
                    return Task.FromResult(existing);

                completion = new TaskCompletionSource<LogEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((predicate, completion));
            }

            return completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values
                    .Where(static value => !string.Equals(value.Key, "{OriginalFormat}", StringComparison.Ordinal))
                    .ToDictionary(static value => value.Key, static value => value.Value, StringComparer.Ordinal)
                : new Dictionary<string, object?>(StringComparer.Ordinal);
            var entry = new LogEntry(logLevel, formatter(state, exception), properties);
            lock (_gate)
            {
                _entries.Add(entry);
                for (var index = _waiters.Count - 1; index >= 0; index--)
                {
                    if (!_waiters[index].Predicate(entry))
                        continue;

                    _waiters[index].Completion.TrySetResult(entry);
                    _waiters.RemoveAt(index);
                }
            }
        }

        public sealed record LogEntry(
            LogLevel Level,
            string Message,
            IReadOnlyDictionary<string, object?> Properties);
    }

    internal sealed class RecordingWorkflowContext
        : IWorkflowExecutionContext,
          IWorkflowExecutionRuntimeContextAccessor,
          IWorkflowExecutionStateHost,
          ISecretVaultAccessor,
          IRuntimeSecretStoreAccessor
    {
        private readonly Dictionary<string, Any> _states = new(StringComparer.Ordinal);
        private readonly Channel<IMessage> _publishedEvents = Channel.CreateUnbounded<IMessage>();
        private readonly Dictionary<IMessage, EventEnvelopePublishOptions?> _publishedOptions =
            new(ReferenceEqualityComparer.Instance);

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

        public ILogger Logger { get; init; } = NullLogger.Instance;

        public WorkflowExecutionRuntimeContext RuntimeContext { get; } = new();
        public ISecretVault? SecretVault { get; init; }
        public IRuntimeSecretStore? RuntimeSecretStore { get; init; } = new InMemoryRuntimeSecretStore();
        public DateTimeOffset? UtcNowOverride { get; set; }
        public TimeProvider Clock { get; init; } = TimeProvider.System;
        public DateTimeOffset UtcNow => UtcNowOverride ?? Clock.GetUtcNow();

        public long GetTimestamp() => Clock.GetTimestamp();

        public TimeSpan GetElapsedTime(long startingTimestamp) =>
            Clock.GetElapsedTime(startingTimestamp);

        public WorkflowRunExecutionContextState ExecutionContextState { get; } = new();

        public WorkflowRunExecutionContextState ExecutionContextSnapshot => ExecutionContextState.Clone();

        public WorkflowCapabilityAdmissionPlan CapabilityAdmissionPlan { get; init; } = new();

        public WorkflowCapabilityAdmissionPlan CapabilityAdmissionPlanSnapshot => CapabilityAdmissionPlan.Clone();

        public List<(IMessage Event, TopologyAudience Direction)> Published { get; } = [];

        public List<ScheduledToolCallback> Scheduled { get; } = [];

        public System.Type? FailNextPublishType { get; set; }

        public System.Type? FailAfterNextPublishType { get; set; }

        public int FailPublicationRetrySchedulesRemaining { get; set; }

        public int FailPublicationRetryPublishesRemaining { get; set; }

        public int FailToolCompletionPublishesRemaining { get; set; }

        public int FailAttemptCompletionPublishesRemaining { get; set; }

        public int FailAttemptCompletionSchedulesRemaining { get; set; }

        public int FailStateSavesRemaining { get; set; }

        public int FailStatePublicationsAfterCommitRemaining { get; set; }

        public int StateSaveCalls { get; private set; }

        public TaskCompletionSource<bool> AttemptCompletionScheduleFailureObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<TEvent> WaitForPublishedAsync<TEvent>(Func<TEvent, bool>? predicate = null)
            where TEvent : class, IMessage
        {
            while (true)
            {
                var evt = await _publishedEvents.Reader.ReadAsync().AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(5));
                if (evt is TEvent typed && (predicate == null || predicate(typed)))
                    return typed;
            }
        }

        public EventEnvelope PublishedEnvelope(IMessage evt)
        {
            var envelope = Envelope(evt);
            envelope.Route = EnvelopeRouteSemantics.CreateTopologyPublication(AgentId, TopologyAudience.Self);
            if (_publishedOptions.GetValueOrDefault(evt)?.Delivery?.OperationId is { Length: > 0 } operationId)
            {
                envelope.Runtime = new EnvelopeRuntime
                {
                    DeliveryIdentity = new DeliveryIdentity { OperationId = operationId },
                };
            }

            return envelope;
        }

        public string PublishedOperationId(IMessage evt) =>
            _publishedOptions.GetValueOrDefault(evt)?.Delivery?.OperationId ?? string.Empty;

        public bool FailNextSchedule { get; set; }

        /// <summary>
        /// When set, every published <see cref="WorkflowToolCallAttemptCompletedEvent"/> is handed
        /// to this delegate (the actor inbox) BEFORE <see cref="PublishAsync"/> returns to the
        /// producer, modelling a transport that delivers self-publications synchronously. The
        /// producer therefore records its delivery waterline only after the actor has already
        /// reconciled the completion.
        /// </summary>
        public Func<EventEnvelope, Task>? InTransportAttemptCompletionDelivery { get; set; }

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
            StateSaveCalls++;
            if (FailStateSavesRemaining > 0)
            {
                FailStateSavesRemaining--;
                throw new InvalidOperationException("simulated state save failure");
            }

            var packed = Any.Pack(state);
            WorkflowExecutionStateUpsertedEvent? committedUpsert = null;
            if (string.Equals(scopeKey, ToolCallModule.ModuleStateKey, StringComparison.Ordinal) &&
                packed.Is(ToolCallModuleState.Descriptor))
            {
                var authoritative = _states.TryGetValue(scopeKey, out var authoritativeState) &&
                                    authoritativeState.Is(ToolCallModuleState.Descriptor)
                    ? authoritativeState.Unpack<ToolCallModuleState>()
                    : null;
                committedUpsert = new WorkflowExecutionStateUpsertedEvent
                {
                    ScopeKey = scopeKey,
                    State = packed,
                };
                committedUpsert.ToolCallAttemptPersistenceFacts.Add(
                    WorkflowToolCallAttemptPersistence.BuildNewFacts(
                        authoritative,
                        packed.Unpack<ToolCallModuleState>(),
                        ScopeId,
                        UtcNow));
            }

            _states[scopeKey] = packed;
            if (committedUpsert is { ToolCallAttemptPersistenceFacts.Count: > 0 })
            {
                var committed = new StateEvent
                {
                    AgentId = AgentId,
                    EventId = $"recording-workflow-state-{StateSaveCalls}",
                    Timestamp = Timestamp.FromDateTimeOffset(UtcNow),
                    Version = StateSaveCalls,
                    EventType = WorkflowExecutionStateUpsertedEvent.Descriptor.FullName,
                    EventData = Any.Pack(committedUpsert),
                };
                foreach (var observation in WorkflowToolCallAttemptPersistence.BuildCommittedObservations(committed))
                    WorkflowToolCallTelemetry.Record(Logger, observation);
            }

            if (FailStatePublicationsAfterCommitRemaining > 0)
            {
                FailStatePublicationsAfterCommitRemaining--;
                throw new InvalidOperationException("simulated state publication failure");
            }

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
            if (evt is WorkflowToolCallPublicationRetryFiredEvent &&
                FailPublicationRetryPublishesRemaining > 0)
            {
                FailPublicationRetryPublishesRemaining--;
                throw new InvalidOperationException("simulated publication retry publish failure");
            }

            if (evt is WorkflowToolCallCompletedEvent && FailToolCompletionPublishesRemaining > 0)
            {
                FailToolCompletionPublishesRemaining--;
                throw new InvalidOperationException("simulated tool completion publish failure");
            }

            if (evt is WorkflowToolCallAttemptCompletedEvent &&
                FailAttemptCompletionPublishesRemaining > 0)
            {
                FailAttemptCompletionPublishesRemaining--;
                throw new InvalidOperationException("simulated attempt completion publish failure");
            }

            if (FailNextPublishType?.IsInstanceOfType(evt) == true)
            {
                FailNextPublishType = null;
                throw new InvalidOperationException("simulated publish failure");
            }

            Published.Add((evt, direction));
            _publishedOptions[evt] = options?.DeepClone();
            _publishedEvents.Writer.TryWrite(evt);
            if (FailAfterNextPublishType?.IsInstanceOfType(evt) == true)
            {
                FailAfterNextPublishType = null;
                throw new InvalidOperationException("simulated post-publication failure");
            }

            return evt is WorkflowToolCallAttemptCompletedEvent &&
                   InTransportAttemptCompletionDelivery is { } deliverInTransport
                ? deliverInTransport(PublishedEnvelope(evt))
                : Task.CompletedTask;
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
            _ = options;
            if (FailNextSchedule)
            {
                FailNextSchedule = false;
                throw new InvalidOperationException("simulated schedule failure");
            }

            if (evt is WorkflowToolCallPublicationRetryFiredEvent &&
                FailPublicationRetrySchedulesRemaining > 0)
            {
                FailPublicationRetrySchedulesRemaining--;
                throw new InvalidOperationException("simulated publication retry schedule failure");
            }

            if (evt is WorkflowToolCallAttemptCompletedEvent &&
                FailAttemptCompletionSchedulesRemaining > 0)
            {
                FailAttemptCompletionSchedulesRemaining--;
                AttemptCompletionScheduleFailureObserved.TrySetResult(true);
                throw new InvalidOperationException("simulated attempt completion schedule failure");
            }

            var lease = new RuntimeCallbackLease(AgentId, callbackId, 1, RuntimeCallbackBackend.InMemory);
            Scheduled.Add(new ScheduledToolCallback(dueTime, evt, lease));
            return Task.FromResult(lease);
        }

        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _ = lease;
            return Task.CompletedTask;
        }
    }

    private sealed class FailFirstRevokeRuntimeSecretStore : IRuntimeSecretStore
    {
        private readonly InMemoryRuntimeSecretStore _inner = new();
        private int _revokeCalls;

        public int RevokeCalls => Volatile.Read(ref _revokeCalls);

        public Task<StoreRuntimeSecretResult> PutAsync(
            StoreRuntimeSecretRequest request,
            CancellationToken ct = default) =>
            _inner.PutAsync(request, ct);

        public Task<ResolveRuntimeSecretResult> ResolveAsync(
            ResolveRuntimeSecretRequest request,
            CancellationToken ct = default) =>
            _inner.ResolveAsync(request, ct);

        public Task<ConsumeRuntimeSecretResult> ConsumeAsync(
            ConsumeRuntimeSecretRequest request,
            CancellationToken ct = default) =>
            _inner.ConsumeAsync(request, ct);

        public Task<RevokeRuntimeSecretResult> RevokeAsync(
            RevokeRuntimeSecretRequest request,
            CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _revokeCalls) == 1)
                return Task.FromResult(new RevokeRuntimeSecretResult(false));

            return _inner.RevokeAsync(request, ct);
        }
    }

    private sealed class TrackingRuntimeSecretStore : IRuntimeSecretStore
    {
        private readonly InMemoryRuntimeSecretStore _inner = new();
        private int _putCalls;
        private int _resolveCalls;
        private int _revokeCalls;

        public int PutCalls => Volatile.Read(ref _putCalls);

        public int ResolveCalls => Volatile.Read(ref _resolveCalls);

        public int RevokeCalls => Volatile.Read(ref _revokeCalls);

        public bool ThrowOnResolve { get; set; }

        public bool ReturnUnavailableOnResolve { get; set; }

        public RuntimeSecretReference? LastStoredReference { get; private set; }

        public async Task<StoreRuntimeSecretResult> PutAsync(
            StoreRuntimeSecretRequest request,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _putCalls);
            var stored = await _inner.PutAsync(request, ct);
            LastStoredReference = stored.Reference.Clone();
            return stored;
        }

        public Task<ResolveRuntimeSecretResult> ResolveAsync(
            ResolveRuntimeSecretRequest request,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _resolveCalls);
            if (ThrowOnResolve)
                throw new InvalidOperationException("simulated protected material resolve failure");

            if (ReturnUnavailableOnResolve)
                return Task.FromResult(new ResolveRuntimeSecretResult(null, null));

            return _inner.ResolveAsync(request, ct);
        }

        public Task<ConsumeRuntimeSecretResult> ConsumeAsync(
            ConsumeRuntimeSecretRequest request,
            CancellationToken ct = default) =>
            _inner.ConsumeAsync(request, ct);

        public Task<RevokeRuntimeSecretResult> RevokeAsync(
            RevokeRuntimeSecretRequest request,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _revokeCalls);
            return _inner.RevokeAsync(request, ct);
        }
    }

    private sealed class FailingCleanupRuntimeSecretStore : IRuntimeSecretStore
    {
        private readonly InMemoryRuntimeSecretStore _inner = new();

        public Task<StoreRuntimeSecretResult> PutAsync(
            StoreRuntimeSecretRequest request,
            CancellationToken ct = default) =>
            _inner.PutAsync(request, ct);

        public Task<ResolveRuntimeSecretResult> ResolveAsync(
            ResolveRuntimeSecretRequest request,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("simulated protected material resolve failure");

        public Task<ConsumeRuntimeSecretResult> ConsumeAsync(
            ConsumeRuntimeSecretRequest request,
            CancellationToken ct = default) =>
            _inner.ConsumeAsync(request, ct);

        public Task<RevokeRuntimeSecretResult> RevokeAsync(
            RevokeRuntimeSecretRequest request,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("simulated protected material revoke failure");
    }

    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
                throw new InvalidOperationException("simulated cleanup logging failure");
        }
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(System.Type serviceType) => null;
    }
}
