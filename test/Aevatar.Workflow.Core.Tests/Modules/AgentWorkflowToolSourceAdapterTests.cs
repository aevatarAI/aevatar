using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.CodeExecution;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Integration.AI;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Modules;

public sealed class AgentWorkflowToolSourceAdapterTests
{
    [Fact]
    public async Task WorkflowTool_ShouldDeclareDurableStartOnceRedispatchRecovery()
    {
        var adapter = new AgentWorkflowToolSourceAdapter(
            [new SingleAgentToolSource(new CapturingAgentTool())],
            new PassThroughExecutionPort());

        var tool = (await adapter.GetToolsAsync(CancellationToken.None)).Should().ContainSingle().Subject;

        tool.RecoverySafety.Should().Be(WorkflowToolRecoverySafety.DurableStartOnceRedispatch);
    }

    [Fact]
    public async Task GetToolsAsync_WhenOneAgentSourceFails_ShouldKeepHealthySourceTools()
    {
        var healthyTool = new CapturingAgentTool();
        var adapter = new AgentWorkflowToolSourceAdapter(
            [new ThrowingAgentToolSource(), new SingleAgentToolSource(healthyTool)],
            new PassThroughExecutionPort());

        var tools = await adapter.GetToolsAsync(CancellationToken.None);

        tools.Should().ContainSingle().Which.Name.Should().Be(healthyTool.Name);
    }

    [Fact]
    public void OperationAdmissionMapper_ShouldPreservePublishedEndpointIdentity()
    {
        var mapped = WorkflowOperationAdmissionToolContextMapper.Map(
            WriteInvocationAdmission());

        mapped.Should().NotBeNull();
        mapped!.Identity.Should().Be(
            new AgentToolOperationIdentity.PublishedEndpoint("create-event"));
        mapped.AuthorizationBasis.Should().Be(
            AgentToolOperationAuthorizationBasis.PublishedContract);
    }

    [Fact]
    public void OperationAdmissionMapper_ShouldRejectPublishedAdmissionWithoutEndpointIdentity()
    {
        var admission = WriteInvocationAdmission();
        admission.Capability.NyxIdUserService.EndpointId = string.Empty;

        FluentActions.Invoking(() => WorkflowOperationAdmissionToolContextMapper.Map(admission))
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*endpoint identity*");
    }

    [Fact]
    public void OperationAdmissionMapper_ShouldRejectPublishedAdmissionWithInvalidExecutionPolicy()
    {
        var admission = WriteInvocationAdmission();
        admission.Capability.NyxIdUserService.ExecutionPolicy.EnforcementOwner =
            NyxIdOperationEnforcementOwner.NyxId;

        FluentActions.Invoking(() => WorkflowOperationAdmissionToolContextMapper.Map(admission))
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*execution policy*");
    }

    [Fact]
    public async Task WorkflowTool_ShouldMapExplicitRequestAdmissionToProviderNeutralToolContext()
    {
        var agentTool = new CapturingAgentTool();
        var adapter = new AgentWorkflowToolSourceAdapter(
            [new SingleAgentToolSource(agentTool)],
            new PassThroughExecutionPort());
        var tool = (await adapter.GetToolsAsync(CancellationToken.None)).Single();
        var invocationAdmission = ExplicitRequestInvocationAdmission();
        var requestContractDigest = invocationAdmission.NyxIdExplicitRequestGrant.RequestContractDigest;

        await tool.ExecuteAsync(
            new WorkflowToolExecutionRequest(
                ArgumentsJson: "{}",
                RunId: "run-explicit-alpha",
                StepId: "request-alpha",
                ExecutionId: "exec-explicit-alpha",
                CallId: "call-explicit-alpha",
                ScopeId: "scope-explicit-alpha",
                CallerCredential: new WorkflowCallerCredential(),
                RuntimeContext: WorkflowToolRuntimeContext.Empty,
                InvocationAdmission: invocationAdmission),
            CancellationToken.None);

        agentTool.ObservedOperationAdmission.Should().NotBeNull();
        var mapped = agentTool.ObservedOperationAdmission!;
        mapped.Identity.Should().Be(
            new AgentToolOperationIdentity.AuthoredRequest(requestContractDigest));
        mapped.AuthorizationBasis.Should().Be(AgentToolOperationAuthorizationBasis.ExplicitRequest);
        mapped.ServiceInstanceId.Should().Be("usvc-explicit-alpha");
        mapped.ServiceSlug.Should().Be("service-explicit-alpha");
        mapped.HttpMethod.Should().Be("POST");
        mapped.PathTemplate.Should().Be("/api/resources/{resource_id}");
        mapped.PathParameters.Should().ContainSingle().Which.Should().Be(
            new AgentToolOperationParameter(
                "resource_id",
                AgentToolOperationParameterLocation.Path,
                true,
                AgentToolOperationValueSchema.Text));
        mapped.QueryParameters.Should().ContainSingle().Which.Should().Be(
            new AgentToolOperationParameter(
                "page_size",
                AgentToolOperationParameterLocation.Query,
                false,
                AgentToolOperationValueSchema.Text));
        mapped.HeaderParameters.Should().ContainSingle().Which.Should().Be(
            new AgentToolOperationParameter(
                "If-Match",
                AgentToolOperationParameterLocation.Header,
                false,
                AgentToolOperationValueSchema.Text));
        mapped.RequestBody.Should().NotBeNull();
        mapped.RequestBody!.Required.Should().BeTrue();
        mapped.RequestBody.MediaType.Should().Be("application/json");
        mapped.RequestBody.Schema.Kind.Should().Be(AgentToolOperationValueKind.Object);
        mapped.RequestBody.Schema.AdditionalPropertiesAllowed.Should().BeTrue();
        mapped.ResponsePolicy.Should().Be(AgentToolOperationResponsePolicy.TextOnly);
    }

    [Fact]
    public async Task WorkflowTool_ShouldMapActorIssuedUnattendedPermitToExactProviderAuthorization()
    {
        const string argumentsJson = """{"method":"POST","path":"/api/resources/42"}""";
        var agentTool = new CapturingAgentTool(name: "nyxid_proxy");
        var executionPort = new PassThroughExecutionPort();
        var adapter = new AgentWorkflowToolSourceAdapter(
            [new SingleAgentToolSource(agentTool)],
            executionPort);
        var tool = (await adapter.GetToolsAsync(CancellationToken.None)).Single();
        var invocationAdmission = ExplicitRequestInvocationAdmission();
        invocationAdmission.Capability.NyxIdUserRequest.ExecutionPolicy.AllowedExecutionModes.Add(
            ExternalCapabilityExecutionMode.Durable);
        invocationAdmission.NyxIdExplicitRequestGrant.AllowedExecutionModes.Add(
            ExternalCapabilityExecutionMode.Durable);
        RefreshExplicitAdmissionDigests(invocationAdmission);
        var permit = new WorkflowUnattendedInvocationPermit
        {
            AuthorizationId = "sha256:authorization-alpha",
            CallSiteId = invocationAdmission.CallSiteId,
            CapabilityContractDigest = invocationAdmission.Capability.NyxIdUserRequest.ContractDigest,
            ExplicitRequestGrantDigest = invocationAdmission.Capability.NyxIdUserRequest.ExplicitRequestGrantDigest,
        };

        await tool.ExecuteAsync(
            new WorkflowToolExecutionRequest(
                ArgumentsJson: argumentsJson,
                RunId: "run-unattended-alpha",
                StepId: "request-alpha",
                ExecutionId: "exec-unattended-alpha",
                CallId: "call-unattended-alpha",
                ScopeId: "scope-explicit-alpha",
                CallerCredential: new WorkflowCallerCredential
                {
                    BearerToken = "jit-token-alpha",
                    Kind = NyxIdCallerCredentialKind.ProxyDelegation,
                    NyxIdAuthority = new WorkflowCallerNyxIdAuthority
                    {
                        Platform = "nyxid",
                        ExternalUserId = "binder-alpha",
                        Scope = "proxy",
                    },
                },
                RuntimeContext: WorkflowToolRuntimeContext.Empty,
                InvocationAdmission: invocationAdmission,
                UnattendedInvocationPermit: permit),
            CancellationToken.None);

        var mapped = executionPort.Requests.Should().ContainSingle().Subject.UnattendedAuthorization;
        mapped.Should().NotBeNull();
        mapped!.Kind.Should().Be(AgentToolUnattendedAuthorizationKind.WorkflowWebhookExact);
        mapped.AuthorizationId.Should().Be(permit.AuthorizationId);
        mapped.RequestId.Should().Be("run-unattended-alpha");
        mapped.ToolName.Should().Be("nyxid_proxy");
        mapped.ToolCallId.Should().Be("call-unattended-alpha");
        mapped.ArgumentsSha256.Should().Be(AgentToolArgumentsDigest.ComputeSha256(argumentsJson));
        mapped.CallSiteId.Should().Be(invocationAdmission.CallSiteId);
        mapped.OperationSelectorDigest.Should().Be(
            AgentToolOperationSelector.ComputeDigest(
                WorkflowOperationAdmissionToolContextMapper.Map(invocationAdmission)!));
    }

    [Fact]
    public async Task WorkflowTool_ShouldRejectExplicitRequestAdmissionWhenGrantPolicyDoesNotMatch()
    {
        var agentTool = new CapturingAgentTool();
        var adapter = new AgentWorkflowToolSourceAdapter(
            [new SingleAgentToolSource(agentTool)],
            new PassThroughExecutionPort());
        var tool = (await adapter.GetToolsAsync(CancellationToken.None)).Single();
        var invocationAdmission = ExplicitRequestInvocationAdmission();
        invocationAdmission.Capability.NyxIdUserRequest.ExecutionPolicy.Risk =
            NyxIdOperationRisk.Destructive;

        await FluentActions.Awaiting(() => tool.ExecuteAsync(
                new WorkflowToolExecutionRequest(
                    ArgumentsJson: "{}",
                    RunId: "run-explicit-alpha",
                    StepId: "request-alpha",
                    ExecutionId: "exec-explicit-alpha",
                    CallId: "call-explicit-alpha",
                    ScopeId: "scope-explicit-alpha",
                    CallerCredential: new WorkflowCallerCredential(),
                    RuntimeContext: WorkflowToolRuntimeContext.Empty,
                    InvocationAdmission: invocationAdmission),
                CancellationToken.None))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not match its grant*");

        agentTool.ExecuteCount.Should().Be(0);
    }

    [Theory]
    [InlineData("missing_selector")]
    [InlineData("missing_grant")]
    [InlineData("grant_request_digest")]
    public void OperationAdmissionMapper_ShouldRejectInvalidExplicitRequestCorrespondence(
        string mutation)
    {
        var admission = ExplicitRequestInvocationAdmission();
        switch (mutation)
        {
            case "missing_selector":
                admission.Capability.NyxIdUserRequest.Request = null;
                break;
            case "missing_grant":
                admission.NyxIdExplicitRequestGrant = null;
                break;
            case "grant_request_digest":
                admission.NyxIdExplicitRequestGrant.RequestContractDigest = "sha256:wrong-request";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }

        FluentActions.Invoking(() => WorkflowOperationAdmissionToolContextMapper.Map(admission))
            .Should()
            .Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("grant_authority")]
    [InlineData("blank_slug")]
    [InlineData("method_risk_floor")]
    public void OperationAdmissionMapper_ShouldRejectIntrinsicallyInvalidExplicitAdmission(
        string mutation)
    {
        var admission = ExplicitRequestInvocationAdmission();
        switch (mutation)
        {
            case "grant_authority":
                admission.NyxIdExplicitRequestGrant.GrantorAuthority =
                    NyxIdExplicitRequestGrantorAuthority.Unspecified;
                break;
            case "blank_slug":
                admission.Capability.NyxIdUserRequest.ServiceSlugSnapshot = " ";
                break;
            case "method_risk_floor":
                admission.NyxIdExplicitRequestGrant.Risk = NyxIdOperationRisk.ReadOnly;
                admission.Capability.NyxIdUserRequest.ExecutionPolicy.Risk =
                    NyxIdOperationRisk.ReadOnly;
                admission.Capability.NyxIdUserRequest.ExecutionPolicy.Approval =
                    NyxIdOperationApproval.None;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }
        RefreshExplicitAdmissionDigests(admission);

        FluentActions.Invoking(() => WorkflowOperationAdmissionToolContextMapper.Map(admission))
            .Should()
            .Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task WorkflowTool_ShouldMapWorkflowRequestToAgentToolExecutionContext()
    {
        var agentTool = new CapturingAgentTool();
        var adapter = new AgentWorkflowToolSourceAdapter(
            [new SingleAgentToolSource(agentTool)],
            new PassThroughExecutionPort());
        var tool = (await adapter.GetToolsAsync(CancellationToken.None)).Single();

        var result = await tool.ExecuteAsync(
            new WorkflowToolExecutionRequest(
                ArgumentsJson: """{"ok":true}""",
                RunId: "run-1",
                StepId: "step-1",
                ExecutionId: "exec-1",
                CallId: "call-1",
                ScopeId: "scope-1",
                CallerCredential: new WorkflowCallerCredential
                {
                    BearerToken = "token-123",
                    Kind = NyxIdCallerCredentialKind.SourceReadableUserBearer,
                    NyxIdAuthority = new WorkflowCallerNyxIdAuthority
                    {
                        ExternalUserId = " user-audit-alpha ",
                    },
                },
                RuntimeContext: WorkflowToolRuntimeContext.Empty,
                InputFileRefs:
                [
                    new WorkflowFileRef
                    {
                        FileId = "wf-file-1",
                        ArtifactId = "workflow-file://wf-file-1",
                        SourceKind = WorkflowFileSourceKind.ChatInput,
                        FileName = "document.pdf",
                        MediaType = "application/pdf",
                        OwnerRunId = "run-1",
                        OwnerScopeId = "scope-1",
                    },
                ],
                IdempotencyKey: "idem-agent-tool-1",
                ScheduleId: " schedule-1 "),
            CancellationToken.None);

        result.ResultJson.Should().Be("""{"observed":true}""");
        result.ManagedHandoff.Should().BeNull();
        agentTool.ObservedArgumentsJson.Should().Be("""{"ok":true}""");
        agentTool.ObservedAccessToken.Should().Be("token-123");
        agentTool.ObservedOrgToken.Should().Be("token-123");
        agentTool.ObservedScopeId.Should().Be("scope-1");
        agentTool.ObservedOwnerScopeId.Should().Be("scope-1");
        agentTool.ObservedOwnerSubject.Should().Be("user-audit-alpha");
        agentTool.ObservedChat.Should().Be(new AgentChatInvocationContext(
            AgentChatInvocationSurface.WorkflowChat,
            "run-1",
            null,
            null,
            "step-1",
            null));
        agentTool.ObservedCallId.Should().Be("call-1");
        agentTool.ObservedIdempotencyKey.Should().Be("idem-agent-tool-1");
        agentTool.ObservedScheduleId.Should().Be("schedule-1");
        agentTool.ObservedInputFileRefs.Should().ContainSingle().Which.Should().BeEquivalentTo(new ChatFileRef
        {
            FileId = "wf-file-1",
            ArtifactId = "workflow-file://wf-file-1",
            SourceKind = ChatFileSourceKind.ChatInput,
            FileName = "document.pdf",
            MediaType = "application/pdf",
            OwnerRunId = "run-1",
            OwnerScopeId = "scope-1",
        });
        agentTool.ObservedExternalMetadata.Should().NotContainKey("ExecutionId");
        AgentToolRequestContext.Current.Should().BeNull();
    }

    [Fact]
    public async Task WorkflowTool_ShouldExecuteAgentToolThroughAdmissionPort()
    {
        var agentTool = new CapturingAgentTool();
        var executionPort = new PassThroughExecutionPort();
        var adapter = new AgentWorkflowToolSourceAdapter(
            [new SingleAgentToolSource(agentTool)],
            executionPort);
        var tool = (await adapter.GetToolsAsync(CancellationToken.None)).Single();

        var result = await tool.ExecuteAsync(
            new WorkflowToolExecutionRequest(
                ArgumentsJson: """{"original":true}""",
                RunId: "run-1",
                StepId: "step-1",
                ExecutionId: "exec-1",
                CallId: "call-1",
                ScopeId: "scope-1",
                CallerCredential: new WorkflowCallerCredential()),
            CancellationToken.None);

        result.ResultJson.Should().Be("""{"observed":true}""");
        var executionRequest = executionPort.Requests.Should().ContainSingle().Subject;
        executionRequest.ArgumentsJson.Should().Be("""{"original":true}""");
        executionRequest.ApprovalContinuationMode.Should().Be(AgentToolApprovalContinuationMode.ActorOwned);
        executionRequest.ExecutionOwner.Kind.Should().Be(AgentToolExecutionOwnerKind.WorkflowRun);
        executionRequest.ExecutionOwner.OwnerId.Should().Be("run-1");
        AgentToolRequestContext.Current.Should().BeNull();
    }

    [Fact]
    public async Task WorkflowTool_WhenAdmissionDetectsActorRedelivery_ShouldRecoverTheSameToolCall()
    {
        const string recoveredResult = """{"recovered":true}""";
        var agentTool = new CapturingAgentTool();
        var executionPort = new SequencedOutcomeExecutionPort(
            new AgentToolExecutionOutcome(
                AgentToolExecutionOutcomeKind.Failed,
                string.Empty,
                new AgentToolReceipt
                {
                    CallId = "call-presentation-kappa",
                    ToolName = agentTool.Name,
                    Status = AgentToolReceiptStatus.Error,
                },
                IsMutation: false,
                FailureCode: "tool_execution_already_started",
                SafeMessage: "This exact tool call already started and will not be replayed.",
                AgentToolExecutionFailureStage.Admission,
                TerminalInvoked: false,
                Retryable: false,
                AuditCompleted: true),
            new AgentToolExecutionOutcome(
                AgentToolExecutionOutcomeKind.Executed,
                recoveredResult,
                new AgentToolReceipt
                {
                    CallId = "call-presentation-kappa",
                    ToolName = agentTool.Name,
                    Status = AgentToolReceiptStatus.Success,
                    ResultJson = recoveredResult,
                },
                IsMutation: false,
                FailureCode: string.Empty,
                SafeMessage: string.Empty,
                AgentToolExecutionFailureStage.None,
                TerminalInvoked: true,
                Retryable: false,
                AuditCompleted: true));
        var adapter = new AgentWorkflowToolSourceAdapter(
            [new SingleAgentToolSource(agentTool)],
            executionPort);
        var tool = (await adapter.GetToolsAsync(CancellationToken.None)).Single();

        var result = await tool.ExecuteAsync(
            new WorkflowToolExecutionRequest(
                ArgumentsJson: """{"format":"preview"}""",
                RunId: "run-generic-alpha",
                StepId: "present-preview",
                ExecutionId: "exec-recovery-zeta",
                CallId: "call-presentation-kappa",
                ScopeId: "scope-personal-beta",
                CallerCredential: new WorkflowCallerCredential()),
            CancellationToken.None);

        result.ResultJson.Should().Be(recoveredResult);
        result.Failure.Should().BeNull();
        executionPort.Requests.Should().HaveCount(2);
        executionPort.Requests[0].ExecutionAttemptKind.Should().Be(AgentToolExecutionAttemptKind.Initial);
        executionPort.Requests[1].ExecutionAttemptKind.Should().Be(AgentToolExecutionAttemptKind.ActorRecovery);
        executionPort.Requests.Select(request => request.ExecutionContext.Request.RequestId)
            .Should().OnlyContain(requestId => requestId == "run-generic-alpha");
        executionPort.Requests.Select(request => request.ExecutionContext.Request.CallId)
            .Should().OnlyContain(callId => callId == "call-presentation-kappa");
    }

    [Fact]
    public async Task WorkflowDurableTool_ShouldMapPendingReconciliationToActorRecovery()
    {
        var agentTool = new CapturingAgentTool();
        var agentPending = new AgentToolPendingOperation(
            "tool:v1:operation:" + new string('a', 64),
            "provider-operation-alpha",
            "/executions/provider-operation-alpha",
            "/executions/provider-operation-alpha/result",
            "/executions/provider-operation-alpha/cancel",
            AgentToolPendingOperationStatus.Running,
            "\"version-3\"",
            1_500,
            1_900_000_000_000,
            "chrono-sandbox",
            "service-code-alpha",
            CodeExecutionRouteIdentitySource.WorkflowCapabilityAdmission);
        var executionPort = new FixedOutcomeExecutionPort(new AgentToolExecutionOutcome(
            AgentToolExecutionOutcomeKind.Pending,
            string.Empty,
            new AgentToolReceipt
            {
                CallId = "call-durable-alpha",
                ToolName = agentTool.Name,
                Status = AgentToolReceiptStatus.Unspecified,
            },
            IsMutation: false,
            FailureCode: string.Empty,
            SafeMessage: "Tool execution is pending durable provider completion.",
            AgentToolExecutionFailureStage.None,
            TerminalInvoked: false,
            Retryable: false,
            AuditCompleted: true,
            PendingOperation: agentPending));
        var adapter = new AgentWorkflowToolSourceAdapter(
            [new SingleAgentToolSource(agentTool)],
            executionPort);
        var tool = (IWorkflowDurableOperationTool)(await adapter.GetToolsAsync()).Single();
        var workflowPending = new WorkflowToolPendingOperation(
            agentPending.OperationId,
            agentPending.ProviderOperationId,
            agentPending.StatusPath,
            agentPending.ResultPath,
            agentPending.CancelPath,
            WorkflowToolPendingOperationStatus.Running,
            agentPending.ETag,
            agentPending.RetryAfterMilliseconds,
            agentPending.ExpiresAtUnixMs,
            agentPending.ServiceSlug,
            agentPending.UserServiceId,
            WorkflowToolPendingOperationRouteIdentitySource.WorkflowCapabilityAdmission);
        var request = new WorkflowToolExecutionRequest(
            ArgumentsJson: "{}",
            RunId: "run-durable-alpha",
            StepId: "step-durable-alpha",
            ExecutionId: "execution-durable-alpha",
            CallId: "call-durable-alpha",
            ScopeId: "scope-durable-alpha",
            CallerCredential: new WorkflowCallerCredential());

        var result = await tool.ReconcileAsync(request, workflowPending);

        result.PendingOperation.Should().Be(workflowPending);
        result.Failure.Should().BeNull();
        var mapped = executionPort.Requests.Should().ContainSingle().Subject;
        mapped.ExecutionAttemptKind.Should().Be(AgentToolExecutionAttemptKind.ActorRecovery);
        mapped.ExecutionContext.Request.OperationId.Should().Be(agentPending.OperationId);
        mapped.PendingOperation.Should().Be(agentPending);
    }

    [Fact]
    public async Task WorkflowDurableTool_ShouldMapTypedCancellationIdentityAndPendingResult()
    {
        var agentTool = new CapturingAgentTool();
        var operationId = "tool:v1:operation:" + new string('b', 64);
        var agentPending = new AgentToolPendingOperation(
            operationId,
            "provider-operation-cancel",
            "/executions/provider-operation-cancel",
            "/executions/provider-operation-cancel/result",
            "/executions/provider-operation-cancel/cancel",
            AgentToolPendingOperationStatus.Running,
            "\"version-4\"",
            2_000,
            1_900_000_000_000,
            "chrono-sandbox",
            "service-code-cancel",
            CodeExecutionRouteIdentitySource.WorkflowCapabilityAdmission);
        var frozenIntent = new AgentToolCancellationTerminalIntent(
            AgentToolExecutionOutcomeKind.Executed,
            """{"success":false,"code":"code_execution_cancelled"}""",
            new AgentToolReceipt
            {
                CallId = "call-cancel-alpha",
                ToolName = agentTool.Name,
                Status = AgentToolReceiptStatus.Error,
                ResultJson = """{"success":false,"code":"code_execution_cancelled"}""",
                ErrorCode = "code_execution_cancelled",
                ErrorMessage = "cancelled",
                SubjectKind = "nyxid.user-service",
                SubjectId = "service-code-cancel",
                ProviderResourceId = "provider-operation-cancel",
                MutationStage = AgentToolReceiptMutationStage.ReadModelObserved,
            },
            IsMutation: true,
            FailureCode: string.Empty,
            SafeMessage: string.Empty,
            AgentToolExecutionFailureStage.None,
            TerminalInvoked: true,
            Retryable: false,
            new AgentToolCallSafety(false, false, false),
            new string('a', 64));
        var executionPort = new FixedCancellationExecutionPort(
            AgentToolCancellationResult.Pending(agentPending, terminalIntent: frozenIntent));
        var adapter = new AgentWorkflowToolSourceAdapter(
            [new SingleAgentToolSource(agentTool)],
            executionPort);
        var tool = (IWorkflowDurableOperationTool)(await adapter.GetToolsAsync()).Single();
        var workflowPending = new WorkflowToolPendingOperation(
            operationId,
            agentPending.ProviderOperationId,
            agentPending.StatusPath,
            agentPending.ResultPath,
            agentPending.CancelPath,
            WorkflowToolPendingOperationStatus.Running,
            agentPending.ETag,
            agentPending.RetryAfterMilliseconds,
            agentPending.ExpiresAtUnixMs,
            agentPending.ServiceSlug,
            agentPending.UserServiceId,
            WorkflowToolPendingOperationRouteIdentitySource.WorkflowCapabilityAdmission);
        var executionRequest = new WorkflowToolExecutionRequest(
            ArgumentsJson: "{}",
            RunId: "run-cancel-alpha",
            StepId: "step-cancel-alpha",
            ExecutionId: "execution-cancel-alpha",
            CallId: "call-cancel-alpha",
            ScopeId: "scope-cancel-alpha",
            CallerCredential: new WorkflowCallerCredential());

        var result = await tool.CancelAsync(new WorkflowToolCancellationRequest(
            executionRequest,
            workflowPending,
            DeadlineUnixMs: 1_234_567_890));

        result.Disposition.Should().Be(WorkflowToolCancellationDisposition.Pending);
        result.PendingOperation.Should().Be(workflowPending);
        result.PendingTerminalIntent.Should().NotBeNull();
        var mapped = executionPort.CancellationRequests.Should().ContainSingle().Subject;
        mapped.ExecutionAttemptKind.Should().Be(AgentToolExecutionAttemptKind.ActorRecovery);
        mapped.ApprovalContinuationMode.Should().Be(AgentToolApprovalContinuationMode.ActorOwned);
        mapped.ExecutionOwner.Should().BeEquivalentTo(AgentToolExecutionOwners.WorkflowRun("run-cancel-alpha"));
        mapped.ExecutionContext.Request.RequestId.Should().Be("run-cancel-alpha");
        mapped.ExecutionContext.Request.CallId.Should().Be("call-cancel-alpha");
        mapped.ExecutionContext.Request.OperationId.Should().Be(operationId);
        mapped.PendingOperation.Should().Be(agentPending);
        mapped.DeadlineUnixMs.Should().Be(1_234_567_890);

        await tool.CancelAsync(new WorkflowToolCancellationRequest(
            executionRequest,
            workflowPending,
            DeadlineUnixMs: 1_234_567_890,
            TerminalIntent: result.PendingTerminalIntent));

        executionPort.CancellationRequests.Should().HaveCount(2);
        var recovered = executionPort.CancellationRequests[1];
        recovered.TerminalIntent.Should().NotBeNull();
        recovered.TerminalIntent!.Receipt.Should().BeEquivalentTo(frozenIntent.Receipt);
        recovered.TerminalIntent.CallSafety.Should().Be(frozenIntent.CallSafety);
        recovered.TerminalIntent.Kind.Should().Be(frozenIntent.Kind);
    }

    [Fact]
    public async Task WorkflowTool_WhenDuplicateFailureAlreadyInvokedTerminal_ShouldNotRecover()
    {
        var duplicateFailure = new AgentToolExecutionOutcome(
            AgentToolExecutionOutcomeKind.Failed,
            string.Empty,
            new AgentToolReceipt
            {
                CallId = "call-write-lambda",
                ToolName = "capture_context",
                Status = AgentToolReceiptStatus.Error,
                FailureOutcome = AgentToolFailureOutcome.CalleeConfirmed,
            },
            IsMutation: true,
            FailureCode: "tool_execution_already_started",
            SafeMessage: "terminal execution already ran",
            AgentToolExecutionFailureStage.TerminalExecution,
            TerminalInvoked: true,
            Retryable: false,
            AuditCompleted: true);
        var executionPort = new FixedOutcomeExecutionPort(duplicateFailure);
        var adapter = new AgentWorkflowToolSourceAdapter(
            [new SingleAgentToolSource(new CapturingAgentTool())],
            executionPort);
        var tool = (await adapter.GetToolsAsync(CancellationToken.None)).Single();

        var result = await tool.ExecuteAsync(
            new WorkflowToolExecutionRequest(
                ArgumentsJson: """{"operation":"write"}""",
                RunId: "run-write-delta",
                StepId: "submit-write",
                ExecutionId: "exec-write-eta",
                CallId: "call-write-lambda",
                ScopeId: "scope-personal-gamma",
                CallerCredential: new WorkflowCallerCredential()),
            CancellationToken.None);

        result.Failure.Should().Be(new WorkflowToolExecutionFailure(
            "tool_execution_already_started",
            "terminal execution already ran",
            TerminalInvoked: true,
            Retryable: false));
        executionPort.Requests.Should().ContainSingle();
        executionPort.Requests[0].ExecutionAttemptKind.Should().Be(AgentToolExecutionAttemptKind.Initial);
    }

    [Fact]
    public async Task WorkflowTool_ShouldPreserveIssuedTimeInAdmissionIdentity()
    {
        const long issuedAtUnixMs = 1_800_000_000_000;
        var executionPort = new PassThroughExecutionPort();
        var adapter = new AgentWorkflowToolSourceAdapter(
            [new SingleAgentToolSource(new CapturingAgentTool())],
            executionPort);
        var tool = (await adapter.GetToolsAsync(CancellationToken.None)).Single();

        await tool.ExecuteAsync(
            new WorkflowToolExecutionRequest(
                ArgumentsJson: "{}",
                RunId: "run-1",
                StepId: "step-1",
                ExecutionId: "exec-1",
                CallId: "call-1",
                ScopeId: "scope-1",
                CallerCredential: new WorkflowCallerCredential(),
                RuntimeContext: WorkflowToolRuntimeContext.Empty,
                IssuedAtUnixMs: issuedAtUnixMs),
            CancellationToken.None);

        executionPort.Requests.Should().ContainSingle()
            .Which.ExecutionContext.Request.IssuedAtUnixMs.Should().Be(issuedAtUnixMs);
    }

    [Fact]
    public async Task WorkflowTool_WhenApprovalDenied_ShouldFailClosedWithoutExecutingAgentTool()
    {
        var agentTool = new CapturingAgentTool(ToolApprovalMode.AlwaysRequire);
        var executionPort = new FixedOutcomeExecutionPort(CreateOutcome(
            AgentToolExecutionOutcomeKind.Denied,
            AgentToolReceiptStatus.Denied,
            resultJson: """{"error":"blocked"}""",
            failureCode: "approval_denied",
            safeMessage: "blocked"));
        var adapter = new AgentWorkflowToolSourceAdapter(
            [new SingleAgentToolSource(agentTool)],
            executionPort);
        var tool = (await adapter.GetToolsAsync(CancellationToken.None)).Single();

        var result = await tool.ExecuteAsync(
            new WorkflowToolExecutionRequest(
                ArgumentsJson: "{}",
                RunId: "run-1",
                StepId: "step-1",
                ExecutionId: "exec-1",
                CallId: "call-1",
                ScopeId: "scope-1",
                CallerCredential: new WorkflowCallerCredential()),
            CancellationToken.None);

        result.ResultJson.Should().Be("""{"error":"blocked"}""");
        result.Failure.Should().Be(new WorkflowToolExecutionFailure(
            "approval_denied",
            "blocked",
            TerminalInvoked: false,
            Retryable: false));
        agentTool.ExecuteCount.Should().Be(0);
        executionPort.Requests.Should().ContainSingle();
        AgentToolRequestContext.Current.Should().BeNull();
    }

    [Fact]
    public async Task WorkflowTool_WhenAdmissionFailureIsRetryable_ShouldReturnTypedFailure()
    {
        var agentTool = new CapturingAgentTool(ToolApprovalMode.AlwaysRequire);
        var executionPort = new FixedOutcomeExecutionPort(CreateOutcome(
            AgentToolExecutionOutcomeKind.Failed,
            AgentToolReceiptStatus.Error,
            resultJson: """{"error":"tool_admission_unavailable"}""",
            failureCode: "tool_admission_unavailable",
            safeMessage: "The durable tool admission ledger is unavailable.",
            terminalInvoked: false,
            retryable: true));
        var adapter = new AgentWorkflowToolSourceAdapter(
            [new SingleAgentToolSource(agentTool)],
            executionPort);
        var tool = (await adapter.GetToolsAsync(CancellationToken.None)).Single();

        var result = await tool.ExecuteAsync(
            new WorkflowToolExecutionRequest(
                ArgumentsJson: """{"mutation":true}""",
                RunId: "run-1",
                StepId: "step-1",
                ExecutionId: "exec-1",
                CallId: "call-1",
                ScopeId: "scope-1",
                CallerCredential: new WorkflowCallerCredential()),
            CancellationToken.None);

        result.Failure.Should().NotBeNull();
        result.Failure!.ErrorCode.Should().Be("tool_admission_unavailable");
        result.Failure.ErrorMessage.Should().Be("The durable tool admission ledger is unavailable.");
        result.Failure.TerminalInvoked.Should().BeFalse();
        result.Failure.Retryable.Should().BeTrue();
        agentTool.ExecuteCount.Should().Be(0);
    }

    [Fact]
    public async Task WorkflowTool_WhenApprovalPending_ShouldReturnTypedPendingOutcomeWithoutExecutingAgentTool()
    {
        var agentTool = new CapturingAgentTool(ToolApprovalMode.AlwaysRequire);
        var executionPort = new FixedOutcomeExecutionPort(CreateOutcome(
            AgentToolExecutionOutcomeKind.ApprovalRequired,
            AgentToolReceiptStatus.ApprovalRequired,
            approvalRequestId: "approval-1"));
        var adapter = new AgentWorkflowToolSourceAdapter(
            [new SingleAgentToolSource(agentTool)],
            executionPort);
        var tool = (await adapter.GetToolsAsync(CancellationToken.None)).Single();

        var result = await tool.ExecuteAsync(
            new WorkflowToolExecutionRequest(
                ArgumentsJson: """{"danger":true}""",
                RunId: "run-1",
                StepId: "step-1",
                ExecutionId: "exec-1",
                CallId: "call-1",
                ScopeId: "scope-1",
                CallerCredential: new WorkflowCallerCredential()),
            CancellationToken.None);

        agentTool.ExecuteCount.Should().Be(0);
        executionPort.Requests.Should().ContainSingle();
        result.PendingApproval.Should().NotBeNull();
        result.PendingApproval!.ApprovalRequestId.Should().Be("approval-1");
        result.PendingApproval.ToolName.Should().Be("capture_context");
        result.PendingApproval.ToolCallId.Should().Be("call-1");
        result.PendingApproval.ArgumentsJson.Should().Be("""{"danger":true}""");
        result.ResultJson.Should().BeEmpty();
        AgentToolRequestContext.Current.Should().BeNull();
    }

    [Fact]
    public async Task WorkflowTool_ShouldMapDurableApprovalGrantToAdmissionPort()
    {
        var agentTool = new CapturingAgentTool(ToolApprovalMode.AlwaysRequire);
        var executionPort = new PassThroughExecutionPort();
        var adapter = new AgentWorkflowToolSourceAdapter(
            [new SingleAgentToolSource(agentTool)],
            executionPort);
        var tool = (await adapter.GetToolsAsync(CancellationToken.None)).Single();

        var result = await tool.ExecuteAsync(
            new WorkflowToolExecutionRequest(
                ArgumentsJson: """{"mutation":true}""",
                RunId: "run-1",
                StepId: "step-1",
                ExecutionId: "exec-1",
                CallId: "call-1",
                ScopeId: "scope-1",
                CallerCredential: new WorkflowCallerCredential(),
                RuntimeContext: WorkflowToolRuntimeContext.Empty,
                ApprovalGrant: new ToolApprovalGrant("approval-1", "capture_context", "call-1")),
            CancellationToken.None);

        result.ResultJson.Should().Be("""{"observed":true}""");
        agentTool.ExecuteCount.Should().Be(1);
        var grant = executionPort.Requests.Should().ContainSingle().Which.ApprovalGrant;
        grant.Should().NotBeNull();
        grant!.ApprovalRequestId.Should().Be("approval-1");
        grant.RequestId.Should().Be("run-1");
        grant.ToolName.Should().Be("capture_context");
        grant.ToolCallId.Should().Be("call-1");
        grant.ArgumentsSha256.Should().Be(AgentToolArgumentsDigest.ComputeSha256("""{"mutation":true}"""));
        AgentToolRequestContext.Current.Should().BeNull();
    }

    [Fact]
    public async Task WorkflowTool_ShouldMapRuntimeContextToAgentToolExecutionContext()
    {
        var agentTool = new CapturingAgentTool();
        var adapter = new AgentWorkflowToolSourceAdapter(
            [new SingleAgentToolSource(agentTool)],
            new PassThroughExecutionPort());
        var tool = (await adapter.GetToolsAsync(CancellationToken.None)).Single();

        await tool.ExecuteAsync(
            new WorkflowToolExecutionRequest(
                ArgumentsJson: "{}",
                RunId: "run-1",
                StepId: "step-1",
                ExecutionId: "exec-1",
                CallId: "call-1",
                ScopeId: "scope-1",
                CallerCredential: new WorkflowCallerCredential { BearerToken = "token-123" },
                RuntimeContext: new WorkflowToolRuntimeContext(
                    " parent-actor ",
                    " parent-run ",
                    " parent-step ",
                    " root-run ",
                    2)),
            CancellationToken.None);

        agentTool.ObservedWorkflowRuntime.ParentActorId.Should().Be("parent-actor");
        agentTool.ObservedWorkflowRuntime.ParentRunId.Should().Be("parent-run");
        agentTool.ObservedWorkflowRuntime.ParentStepId.Should().Be("parent-step");
        agentTool.ObservedWorkflowRuntime.RootRunId.Should().Be("root-run");
        agentTool.ObservedWorkflowRuntime.Depth.Should().Be(2);
        agentTool.ObservedWorkflowRuntime.HasManagedParent.Should().BeTrue();
        AgentToolRequestContext.Current.Should().BeNull();
    }

    [Theory]
    [InlineData(null, null, null, null)]
    [InlineData("", "", null, null)]
    [InlineData("  ", "\t", null, null)]
    [InlineData(" call-1 ", " scope-1 ", "call-1", "scope-1")]
    public async Task WorkflowTool_ShouldNormalizeWorkflowCallAndScopeIdsForAgentToolContext(
        string? callId,
        string? scopeId,
        string? expectedCallId,
        string? expectedScopeId)
    {
        var agentTool = new CapturingAgentTool();
        var adapter = new AgentWorkflowToolSourceAdapter(
            [new SingleAgentToolSource(agentTool)],
            new PassThroughExecutionPort());
        var tool = (await adapter.GetToolsAsync(CancellationToken.None)).Single();

        await tool.ExecuteAsync(
            new WorkflowToolExecutionRequest(
                ArgumentsJson: "{}",
                RunId: "run-1",
                StepId: "step-1",
                ExecutionId: "exec-1",
                CallId: callId!,
                ScopeId: scopeId!,
                CallerCredential: new WorkflowCallerCredential()),
            CancellationToken.None);

        agentTool.ObservedCallId.Should().Be(expectedCallId);
        agentTool.ObservedScopeId.Should().Be(expectedScopeId);
        AgentToolRequestContext.Current.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task WorkflowTool_ShouldPreserveRuntimeContextWhenWorkflowCredentialIsMissing(string authorization)
    {
        var agentTool = new CapturingAgentTool();
        var adapter = new AgentWorkflowToolSourceAdapter(
            [new SingleAgentToolSource(agentTool)],
            new PassThroughExecutionPort());
        var workflowTool = (await adapter.GetToolsAsync(CancellationToken.None)).Single();

        await workflowTool.ExecuteAsync(
            new WorkflowToolExecutionRequest(
                ArgumentsJson: "{}",
                RunId: "run-1",
                StepId: "step-1",
                ExecutionId: "exec-1",
                CallId: "call-1",
                ScopeId: "scope-1",
                CallerCredential: new WorkflowCallerCredential { BearerToken = authorization },
                RuntimeContext: new WorkflowToolRuntimeContext(
                    " parent-actor ",
                    " parent-run ",
                    " parent-step ",
                    " root-run ",
                    -1)),
            CancellationToken.None);

        agentTool.ObservedAccessToken.Should().BeNull();
        agentTool.ObservedOrgToken.Should().BeNull();
        agentTool.ObservedWorkflowRuntime.ParentActorId.Should().Be("parent-actor");
        agentTool.ObservedWorkflowRuntime.ParentRunId.Should().Be("parent-run");
        agentTool.ObservedWorkflowRuntime.ParentStepId.Should().Be("parent-step");
        agentTool.ObservedWorkflowRuntime.RootRunId.Should().Be("root-run");
        agentTool.ObservedWorkflowRuntime.Depth.Should().Be(0);
        agentTool.ObservedWorkflowRuntime.HasManagedParent.Should().BeTrue();
        AgentToolRequestContext.Current.Should().BeNull();
    }

    [Theory]
    [InlineData("Basic token-123")]
    [InlineData("Bearer token-123")]
    [InlineData("Bearer ")]
    [InlineData("token 123")]
    public async Task WorkflowTool_ShouldRejectMalformedWorkflowCredential(string authorization)
    {
        var agentTool = new CapturingAgentTool();
        var adapter = new AgentWorkflowToolSourceAdapter(
            [new SingleAgentToolSource(agentTool)],
            new PassThroughExecutionPort());
        var workflowTool = (await adapter.GetToolsAsync(CancellationToken.None)).Single();

        await FluentActions.Awaiting(() => workflowTool.ExecuteAsync(
                new WorkflowToolExecutionRequest(
                    ArgumentsJson: "{}",
                    RunId: "run-1",
                    StepId: "step-1",
                    ExecutionId: "exec-1",
                    CallId: "call-1",
                    ScopeId: "scope-1",
                    CallerCredential: new WorkflowCallerCredential { BearerToken = authorization }),
                CancellationToken.None))
            .Should()
            .ThrowAsync<ArgumentException>()
            .WithMessage("*caller credential*invalid*");

        AgentToolRequestContext.Current.Should().BeNull();
    }

    [Fact]
    public async Task WorkflowTool_WhenProviderReceiptIsError_ShouldReturnTypedFailure()
    {
        const string rawResult = """{"error":true,"status":503}""";
        const string safeResult = """{"error":"PROVIDER_HTTP_503","message":"The service request failed."}""";
        var agentTool = new ResultReceiptAgentTool(
            rawResult,
            new AgentToolReceipt
            {
                Status = AgentToolReceiptStatus.Error,
                ErrorCode = "PROVIDER_HTTP_503",
                ErrorMessage = "The service request failed.",
                ResultJson = safeResult,
                FailureOutcome = AgentToolFailureOutcome.CalleeConfirmed,
            });
        var adapter = new AgentWorkflowToolSourceAdapter(
            [new SingleAgentToolSource(agentTool)],
            new PassThroughExecutionPort());
        var workflowTool = (await adapter.GetToolsAsync(CancellationToken.None)).Single();

        var result = await workflowTool.ExecuteAsync(
            new WorkflowToolExecutionRequest(
                ArgumentsJson: "{}",
                RunId: "run-1",
                StepId: "step-1",
                ExecutionId: "exec-1",
                CallId: "call-1",
                ScopeId: "scope-1",
                CallerCredential: new WorkflowCallerCredential()),
            CancellationToken.None);

        result.ResultJson.Should().Be(safeResult);
        result.Failure.Should().NotBeNull();
        result.Failure!.ErrorCode.Should().Be("PROVIDER_HTTP_503");
        result.Failure.ErrorMessage.Should().Be("The service request failed.");
        result.Failure.FailureOutcome.Should().Be(WorkflowStepFailureOutcome.CalleeConfirmed);
    }

    [Fact]
    public async Task WorkflowTool_WhenProviderOutcomeIsUncertain_ShouldPreserveTypedFailureOutcome()
    {
        const string safeResult =
            """{"error":"code_execution_outcome_uncertain","message":"Outcome is uncertain."}""";
        var agentTool = new ResultReceiptAgentTool(
            safeResult,
            new AgentToolReceipt
            {
                Status = AgentToolReceiptStatus.Error,
                ErrorCode = "code_execution_outcome_uncertain",
                ErrorMessage = "Outcome is uncertain.",
                ResultJson = safeResult,
                FailureOutcome = AgentToolFailureOutcome.OutcomeUncertain,
            });
        var adapter = new AgentWorkflowToolSourceAdapter(
            [new SingleAgentToolSource(agentTool)],
            new PassThroughExecutionPort());
        var workflowTool = (await adapter.GetToolsAsync(CancellationToken.None)).Single();

        var result = await workflowTool.ExecuteAsync(
            new WorkflowToolExecutionRequest(
                ArgumentsJson: "{}",
                RunId: "run-uncertain",
                StepId: "step-uncertain",
                ExecutionId: "exec-uncertain",
                CallId: "call-uncertain",
                ScopeId: "scope-uncertain",
                CallerCredential: new WorkflowCallerCredential()),
            CancellationToken.None);

        result.Failure.Should().NotBeNull();
        result.Failure!.FailureOutcome.Should().Be(WorkflowStepFailureOutcome.OutcomeUncertain);
    }

    [Fact]
    public async Task WorkflowTool_WhenResultHasNoReceipt_ShouldFailWithUnknownOutcome()
    {
        const string resultJson = """{"error":true,"status":503,"historical":true}""";
        const string unknownResultJson =
            """{"status":"unknown","message":"The tool outcome could not be verified."}""";
        var agentTool = new ResultReceiptAgentTool(resultJson, receipt: null);
        var adapter = new AgentWorkflowToolSourceAdapter(
            [new SingleAgentToolSource(agentTool)],
            new PassThroughExecutionPort());
        var workflowTool = (await adapter.GetToolsAsync(CancellationToken.None)).Single();

        var result = await workflowTool.ExecuteAsync(
            new WorkflowToolExecutionRequest(
                ArgumentsJson: "{}",
                RunId: "run-1",
                StepId: "step-1",
                ExecutionId: "exec-1",
                CallId: "call-1",
                ScopeId: "scope-1",
                CallerCredential: new WorkflowCallerCredential()),
            CancellationToken.None);

        result.Failure.Should().NotBeNull();
        result.ResultJson.Should().Be(unknownResultJson);
        result.Failure!.ErrorCode.Should().Be("tool_outcome_unknown");
        result.Failure.ErrorMessage.Should().Be("The tool outcome could not be verified.");
        result.Failure.FailureOutcome.Should().Be(WorkflowStepFailureOutcome.OutcomeUncertain);
    }

    private sealed class CapturingAgentTool(
        ToolApprovalMode approvalMode = ToolApprovalMode.NeverRequire,
        string name = "capture_context") : IAgentTool
    {
        public string Name => name;

        public string Description => "Capture tool context";

        public string ParametersSchema => "{}";

        public ToolApprovalMode ApprovalMode { get; } = approvalMode;

        public AgentToolReceipt? CreateResultReceipt(
            string callId,
            string toolName,
            string argumentsJson,
            string resultJson) =>
            new()
            {
                CallId = callId,
                ToolName = toolName,
                Status = AgentToolReceiptStatus.Success,
                ResultJson = resultJson,
            };

        public int ExecuteCount { get; private set; }

        public string? ObservedArgumentsJson { get; private set; }

        public string? ObservedAccessToken { get; private set; }

        public string? ObservedOrgToken { get; private set; }

        public string? ObservedScopeId { get; private set; }

        public string? ObservedOwnerScopeId { get; private set; }

        public string? ObservedOwnerSubject { get; private set; }

        public AgentChatInvocationContext ObservedChat { get; private set; } =
            AgentChatInvocationContext.Empty;

        public string? ObservedCallId { get; private set; }

        public string? ObservedIdempotencyKey { get; private set; }

        public string? ObservedScheduleId { get; private set; }

        public IReadOnlyList<ChatFileRef> ObservedInputFileRefs { get; private set; } = [];

        public IReadOnlyDictionary<string, string> ObservedExternalMetadata { get; private set; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public AgentWorkflowRuntimeContext ObservedWorkflowRuntime { get; private set; } =
            AgentWorkflowRuntimeContext.Empty;

        public AgentToolOperationAdmission? ObservedOperationAdmission { get; private set; }

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ExecuteCount++;
            ObservedArgumentsJson = argumentsJson;
            CaptureContext();
            return ExecuteAsyncCore(ct);
        }

        private async Task<string> ExecuteAsyncCore(CancellationToken ct)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            CaptureContext();
            return """{"observed":true}""";
        }

        private void CaptureContext()
        {
            ObservedAccessToken = AgentToolRequestContext.NyxIdAccessToken;
            ObservedOrgToken = AgentToolRequestContext.NyxIdOrgToken;
            ObservedScopeId = AgentToolRequestContext.ScopeId;
            ObservedOwnerScopeId = AgentToolRequestContext.OwnerScopeId;
            ObservedOwnerSubject = AgentToolRequestContext.OwnerSubject;
            ObservedChat = AgentToolRequestContext.Current?.Chat ?? AgentChatInvocationContext.Empty;
            ObservedCallId = AgentToolRequestContext.CallId;
            ObservedIdempotencyKey = AgentToolRequestContext.IdempotencyKey;
            ObservedScheduleId = AgentToolRequestContext.Current?.Schedule.ScheduleId;
            ObservedInputFileRefs = AgentToolRequestContext.Current?.InputFileRefs ?? [];
            ObservedExternalMetadata = AgentToolRequestContext.Current?.ExternalMetadata
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
            ObservedWorkflowRuntime = AgentToolRequestContext.Current?.WorkflowRuntime
                ?? AgentWorkflowRuntimeContext.Empty;
            ObservedOperationAdmission = AgentToolRequestContext.Current?.OperationAdmission;
        }
    }

    private sealed class ProofPolicyAgentTool : IAgentTool
    {
        public string Name => "nyxid_proxy";

        public string Description => "Proof policy fixture";

        public string ParametersSchema => "{}";

        public ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;

        public int ExecuteCount { get; private set; }

        public AgentToolReceipt? CreateResultReceipt(
            string callId,
            string toolName,
            string argumentsJson,
            string resultJson) =>
            new()
            {
                CallId = callId,
                ToolName = toolName,
                Status = AgentToolReceiptStatus.Success,
                ResultJson = resultJson,
            };

        public AgentToolCallSafety GetCallSafety(string argumentsJson)
        {
            var policy = AgentToolRequestContext.Current?.OperationAdmission?.ExecutionPolicy;
            return new AgentToolCallSafety(
                policy?.Approval == AgentToolOperationApproval.Required,
                policy?.Risk == AgentToolOperationRisk.ReadOnly,
                policy?.Risk == AgentToolOperationRisk.Destructive);
        }

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ExecuteCount++;
            return Task.FromResult("""{"executed":true}""");
        }
    }

    private static WorkflowCapabilityInvocationAdmission WriteInvocationAdmission() =>
        new()
        {
            CallSiteId = "workflow-alpha/write-alpha",
            Capability = new ExternalWorkflowCapabilityRef
            {
                NyxIdUserService = new NyxIdUserServiceCapabilityRef
                {
                    UserServiceId = "us-write-alpha",
                    ServiceSlugSnapshot = "calendar-alpha",
                    EndpointId = "create-event",
                    HttpMethod = "POST",
                    PathTemplate = "/events",
                    ContractDigest = "digest-write-alpha",
                    ExecutionPolicy = new NyxIdOperationExecutionPolicy
                    {
                        Risk = NyxIdOperationRisk.Write,
                        Approval = NyxIdOperationApproval.Required,
                        EnforcementOwner = NyxIdOperationEnforcementOwner.Aevatar,
                        AllowedExecutionModes = { ExternalCapabilityExecutionMode.Interactive },
                    },
                },
            },
        };

    private static WorkflowCapabilityInvocationAdmission ExplicitRequestInvocationAdmission()
    {
        const string callSiteId = "workflow-explicit-alpha/request-alpha";
        const string serviceSlug = "service-explicit-alpha";
        var request = new NyxIdRequestSelector
        {
            UserServiceId = "usvc-explicit-alpha",
            Method = NyxIdRequestMethod.Post,
            PathTemplate = "/api/resources/{resource_id}",
            BodyMode = NyxIdRequestBodyMode.Json,
            BodyRequired = true,
            ResponseMode = NyxIdRequestResponseMode.Text,
        };
        request.QueryParameters.Add("page_size");
        request.HeaderParameters.Add("If-Match");
        var requestContractDigest = WorkflowCapabilityAdmissionPlanIntegrity
            .ComputeNyxIdRequestContractDigest(request);
        var grant = new NyxIdExplicitRequestGrant
        {
            WorkflowId = "wf-explicit-alpha",
            RevisionId = "rev-explicit-alpha",
            CallSiteId = callSiteId,
            RequestContractDigest = requestContractDigest,
            GrantorAuthority = NyxIdExplicitRequestGrantorAuthority.AevatarWorkflowBinder,
            GrantorOwnerKind = ExternalCapabilityAuthorizationOwnerKind.Personal,
            GrantorOwnerSubject = "binder-alpha",
            Risk = NyxIdOperationRisk.Write,
            AllowedExecutionModes = { ExternalCapabilityExecutionMode.Interactive },
        };
        return new WorkflowCapabilityInvocationAdmission
        {
            CallSiteId = callSiteId,
            Capability = new ExternalWorkflowCapabilityRef
            {
                NyxIdUserRequest = new NyxIdUserRequestCapabilityRef
                {
                    Request = request,
                    ServiceSlugSnapshot = serviceSlug,
                    ContractDigest = WorkflowCapabilityAdmissionPlanIntegrity
                        .ComputeNyxIdExplicitRequestProofDigest(requestContractDigest, serviceSlug),
                    ExplicitRequestGrantDigest = WorkflowCapabilityAdmissionPlanIntegrity
                        .ComputeNyxIdExplicitRequestGrantDigest(grant),
                    ExecutionPolicy = new NyxIdOperationExecutionPolicy
                    {
                        Risk = NyxIdOperationRisk.Write,
                        Approval = NyxIdOperationApproval.Required,
                        EnforcementOwner = NyxIdOperationEnforcementOwner.Aevatar,
                        AllowedExecutionModes = { ExternalCapabilityExecutionMode.Interactive },
                    },
                },
            },
            NyxIdExplicitRequestGrant = grant,
        };
    }

    private static void RefreshExplicitAdmissionDigests(
        WorkflowCapabilityInvocationAdmission admission)
    {
        var proof = admission.Capability.NyxIdUserRequest;
        var grant = admission.NyxIdExplicitRequestGrant;
        var requestContractDigest = WorkflowCapabilityAdmissionPlanIntegrity
            .ComputeNyxIdRequestContractDigest(proof.Request);
        grant.RequestContractDigest = requestContractDigest;
        proof.ContractDigest = WorkflowCapabilityAdmissionPlanIntegrity
            .ComputeNyxIdExplicitRequestProofDigest(
                requestContractDigest,
                proof.ServiceSlugSnapshot);
        proof.ExplicitRequestGrantDigest = WorkflowCapabilityAdmissionPlanIntegrity
            .ComputeNyxIdExplicitRequestGrantDigest(grant);
    }

    private sealed class ResultReceiptAgentTool(
        string resultJson,
        AgentToolReceipt? receipt) : IAgentTool
    {
        public string Name => "result_receipt";

        public string Description => "Return a provider-classified result";

        public string ParametersSchema => "{}";

        public AgentToolReceipt? CreateResultReceipt(
            string callId,
            string toolName,
            string argumentsJson,
            string resultJson) =>
            receipt?.Clone();

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(resultJson);
        }
    }

    private sealed class PassThroughExecutionPort : IAgentToolExecutionPort
    {
        public List<AgentToolExecutionRequest> Requests { get; } = [];

        public async Task<AgentToolExecutionOutcome> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            string resultJson;
            using (AgentToolContextScope.Push(request.ExecutionContext))
                resultJson = await request.Tool.ExecuteAsync(request.ArgumentsJson, ct);
            var receipt = request.Tool.CreateResultReceipt(
                    request.ExecutionContext.Request.CallId ?? string.Empty,
                    request.Tool.Name,
                    request.ArgumentsJson,
                    resultJson)
                ?? new AgentToolReceipt
                {
                    CallId = request.ExecutionContext.Request.CallId ?? string.Empty,
                    ToolName = request.Tool.Name,
                    Status = AgentToolReceiptStatus.Unspecified,
                    ResultJson =
                        "{\"status\":\"unknown\",\"message\":\"The tool outcome could not be verified.\"}",
                    ErrorCode = "tool_outcome_unknown",
                    ErrorMessage = "The tool outcome could not be verified.",
                };
            var safeResultJson = receipt.Status == AgentToolReceiptStatus.Unspecified
                ? receipt.ResultJson
                : resultJson;

            return new AgentToolExecutionOutcome(
                AgentToolExecutionOutcomeKind.Executed,
                safeResultJson,
                receipt,
                IsMutation: !request.Tool.IsReadOnly,
                FailureCode: string.Empty,
                SafeMessage: string.Empty,
                AgentToolExecutionFailureStage.None,
                TerminalInvoked: true,
                Retryable: false,
                AuditCompleted: true);
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

    private sealed class FixedCancellationExecutionPort(AgentToolCancellationResult result)
        : IAgentToolExecutionPort
    {
        public List<AgentToolCancellationRequest> CancellationRequests { get; } = [];

        public Task<AgentToolExecutionOutcome> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("Execution must not run during typed cancellation.");

        public Task<AgentToolCancellationResult> CancelAsync(
            AgentToolCancellationRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CancellationRequests.Add(request);
            return Task.FromResult(result);
        }
    }

    private sealed class SequencedOutcomeExecutionPort(params AgentToolExecutionOutcome[] outcomes)
        : IAgentToolExecutionPort
    {
        private readonly Queue<AgentToolExecutionOutcome> _outcomes = new(outcomes);

        public List<AgentToolExecutionRequest> Requests { get; } = [];

        public Task<AgentToolExecutionOutcome> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(_outcomes.Dequeue());
        }
    }

    private static AgentToolExecutionOutcome CreateOutcome(
        AgentToolExecutionOutcomeKind kind,
        AgentToolReceiptStatus status,
        string resultJson = "",
        string failureCode = "",
        string safeMessage = "",
        string approvalRequestId = "",
        bool? terminalInvoked = null,
        bool retryable = false) =>
        new(
            kind,
            resultJson,
            new AgentToolReceipt
            {
                CallId = "call-1",
                ToolName = "capture_context",
                Status = status,
                ApprovalMode = AgentToolReceiptApprovalMode.AlwaysRequire,
                IsDestructive = true,
                ApprovalRequestId = approvalRequestId,
                ResultJson = resultJson,
                FailureOutcome = status is AgentToolReceiptStatus.Error or
                        AgentToolReceiptStatus.Denied or
                        AgentToolReceiptStatus.AuthorizationRequired
                    ? AgentToolFailureOutcome.CalleeConfirmed
                    : AgentToolFailureOutcome.Unspecified,
            },
            IsMutation: true,
            failureCode,
            safeMessage,
            AgentToolExecutionFailureStage.None,
            TerminalInvoked: terminalInvoked ?? kind == AgentToolExecutionOutcomeKind.Executed,
            Retryable: retryable,
            AuditCompleted: true);

    private sealed class SingleAgentToolSource(IAgentTool tool) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<IAgentTool>>([tool]);
        }
    }

    private sealed class ThrowingAgentToolSource : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromException<IReadOnlyList<IAgentTool>>(
                new InvalidOperationException("source unavailable"));
        }
    }
}
