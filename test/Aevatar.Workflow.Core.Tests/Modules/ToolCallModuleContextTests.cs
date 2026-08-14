using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
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
        resolved.ErrorCode.Should().Be(
            ToolCallModule.ToolCallProtectedMaterialErrorCodes.Unavailable);
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
        var tool = new CountingAgentTool("tool-alpha", static _ => "{}");
        var store = new TrackingRuntimeSecretStore();
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext
        {
            RuntimeSecretStore = store,
            FailStateSavesRemaining = 1,
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
    }

    [Fact]
    public async Task ToolCallModule_WhenInitialPendingCommitSucceedsButStatePublicationFails_ShouldRetainOwnedProtectedMaterial()
    {
        var tool = new CountingAgentTool("tool-alpha", static _ => "{}");
        var store = new TrackingRuntimeSecretStore();
        var module = CreateModule(tool);
        var ctx = new RecordingWorkflowContext
        {
            RuntimeSecretStore = store,
            FailStatePublicationsAfterCommitRemaining = 1,
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
        pending.ProtectedMaterialReference.ToByteArray().Should().Equal(reference.ToByteArray());
        var resolved = await store.ResolveAsync(ResolveRequest(reference), CancellationToken.None);
        resolved.Resolved.Should().BeTrue();
        ctx.Scheduled.Should().BeEmpty();
        tool.ExecuteCalls.Should().Be(0);
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
        pendingCleanup.ToolCompletionPublished.Should().BeTrue();
        pendingCleanup.StepCompletionPublished.Should().BeTrue();

        await ExecuteToolCallAsync(module, ctx, tool.Name, executionId: "exec-cleanup");

        store.RevokeCalls.Should().Be(2);
        tool.ExecuteCalls.Should().Be(1);
        ctx.LoadState<ToolCallModuleState>("tool_call").CompletionTombstones.Should().ContainSingle();
        ctx.Published.Select(static item => item.Event)
            .OfType<WorkflowToolCallCompletedEvent>().Should().ContainSingle();
        ctx.Published.Select(static item => item.Event)
            .OfType<StepCompletedEvent>().Should().ContainSingle();
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
        var module = CreateModule(tool);
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
        var module = CreateModule(tool);
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

    private static StepRequestEvent ToolRequest(
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

    private static EventEnvelope CallbackEnvelope(ScheduledToolCallback callback)
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

    private sealed class BlockingWorkflowTool(
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

    private sealed record BlockingToolInvocation(
        WorkflowToolExecutionRequest Request,
        TaskCompletionSource<WorkflowToolExecutionResult> Completion,
        CancellationToken CancellationToken);

    private sealed record ScheduledToolCallback(
        TimeSpan DueTime,
        IMessage Event,
        RuntimeCallbackLease Lease);

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

    private sealed class RecordingWorkflowContext
        : IWorkflowExecutionContext,
          IWorkflowExecutionRuntimeContextAccessor,
          IWorkflowExecutionStateHost,
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
        public IRuntimeSecretStore? RuntimeSecretStore { get; init; } = new InMemoryRuntimeSecretStore();

        public WorkflowRunExecutionContextState ExecutionContextState { get; } = new();

        public WorkflowRunExecutionContextState ExecutionContextSnapshot => ExecutionContextState.Clone();

        public WorkflowCapabilityAdmissionPlan CapabilityAdmissionPlan { get; init; } = new();

        public WorkflowCapabilityAdmissionPlan CapabilityAdmissionPlanSnapshot => CapabilityAdmissionPlan.Clone();

        public List<(IMessage Event, TopologyAudience Direction)> Published { get; } = [];

        public List<ScheduledToolCallback> Scheduled { get; } = [];

        public System.Type? FailNextPublishType { get; set; }

        public int FailPublicationRetrySchedulesRemaining { get; set; }

        public int FailPublicationRetryPublishesRemaining { get; set; }

        public int FailToolCompletionPublishesRemaining { get; set; }

        public int FailAttemptCompletionPublishesRemaining { get; set; }

        public int FailAttemptCompletionSchedulesRemaining { get; set; }

        public int FailStateSavesRemaining { get; set; }

        public int FailStatePublicationsAfterCommitRemaining { get; set; }

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
            if (FailStateSavesRemaining > 0)
            {
                FailStateSavesRemaining--;
                throw new InvalidOperationException("simulated state save failure");
            }

            _states[scopeKey] = Any.Pack(state);
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
            _ = options;
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
        private int _revokeCalls;

        public int PutCalls => Volatile.Read(ref _putCalls);

        public int RevokeCalls => Volatile.Read(ref _revokeCalls);

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
