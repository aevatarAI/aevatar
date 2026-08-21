using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Credentials;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.Integration.Tests;

[Trait("Category", "Integration")]
[Trait("Feature", "ConnectorCallApproval")]
public sealed class ConnectorCallModuleApprovalTests
{
    [Fact]
    public async Task ApprovedAction_ShouldBindExecutePersistAndRevokeWithoutLeakingMaterial()
    {
        var harness = await ApprovalHarness.CreateAsync();

        await harness.BeginAsync();

        var waiting = harness.Coordination();
        var materialReference = waiting.MaterialReference.Clone();
        waiting.Snapshot.LifecycleStatus.Should().Be(WorkflowExternalActionLifecycleStatus.WaitingApproval);
        waiting.Snapshot.ApprovalStatus.Should().Be(WorkflowExternalActionApprovalStatus.Pending);
        waiting.Snapshot.ExecutionStatus.Should().Be(WorkflowExternalActionExecutionStatus.NotStarted);
        waiting.StatusCheckLease.Should().NotBeNull();
        harness.Connector.Requests.Should().BeEmpty();
        harness.ApprovalPort.Submissions.Should().ContainSingle();
        var remoteRequest = harness.ApprovalPort.Submissions.Single();
        remoteRequest.RequestId.Should().Be(waiting.Snapshot.Plan.ActionId);
        remoteRequest.ToolCallId.Should().Be(waiting.Snapshot.Plan.ActionId);
        remoteRequest.ArgumentsJson.Should().Contain("service-alpha");
        remoteRequest.ArgumentsJson.Should().Contain("node-alpha");
        remoteRequest.ArgumentsJson.Should().Contain(waiting.Snapshot.Plan.MaterialDigestSha256);
        remoteRequest.ArgumentsJson.Should().NotContain(ApprovalHarness.RawPayload);
        remoteRequest.ArgumentsJson.Should().NotContain(ApprovalHarness.RawParameter);
        remoteRequest.ArgumentsJson.Should().NotContain(ApprovalHarness.BearerToken);

        var originalCallback = harness.LatestApprovalStatusCallback();
        harness.ApprovalPort.NextStatus = new RemoteToolApprovalStatusSnapshot(
            RemoteToolApprovalStatus.Approved,
            ExpiresAt: harness.RemoteExpiresAt);
        await harness.FireAsync(originalCallback);
        await harness.DrainConnectorCompletionsAsync();

        harness.Connector.Requests.Should().ContainSingle();
        var connectorRequest = harness.Connector.Requests.Single();
        connectorRequest.Payload.Should().Be(ApprovalHarness.RawPayload);
        connectorRequest.Parameters["api_secret"].Should().Be(ApprovalHarness.RawParameter);
        connectorRequest.IdempotencyKey.Should().Be(ApprovalHarness.IdempotencyKey);
        connectorRequest.HttpAuthorization.Should().Be($"Bearer {ApprovalHarness.BearerToken}");

        var completed = harness.StepCompletions().Should().ContainSingle().Subject;
        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("connector-ok");
        completed.Annotations["connector.approval.action_id"].Should().Be(waiting.Snapshot.Plan.ActionId);
        var terminal = harness.Coordination();
        terminal.Snapshot.ApprovalStatus.Should().Be(WorkflowExternalActionApprovalStatus.Approved);
        terminal.Snapshot.ExecutionStatus.Should().Be(WorkflowExternalActionExecutionStatus.Succeeded);
        terminal.Snapshot.LifecycleStatus.Should().Be(WorkflowExternalActionLifecycleStatus.Succeeded);
        terminal.MaterialReference.Should().BeNull();
        terminal.StepCompletionPublished.Should().BeTrue();
        harness.State().PendingByOperationId.Should().BeEmpty();
        await AssertReferenceRevokedAsync(harness.SecretStore, materialReference);

        await harness.FireAsync(originalCallback);
        await harness.DrainConnectorCompletionsAsync();
        harness.Connector.Requests.Should().ContainSingle();
        harness.StepCompletions().Should().ContainSingle();
    }

    [Fact]
    public async Task RemoteApproval_WithAgentKey_ShouldPreserveTypedCredential()
    {
        const string agentKey = "nyxid_ag_connector_approval_key";
        var harness = await ApprovalHarness.CreateAsync();
        await WorkflowCallerCredentialRuntimeContextAccess.SetCredentialAsync(
            harness.Agent,
            new WorkflowCallerCredential
            {
                BearerToken = agentKey,
                Kind = NyxIdCallerCredentialKind.AgentKey,
                NyxIdAuthority = new WorkflowCallerNyxIdAuthority
                {
                    Platform = "nyxid",
                    Tenant = "tenant-alpha",
                    ExternalUserId = "user-alpha",
                    Scope = "proxy",
                },
            });
        harness.ApprovalPort.ExpectedToken = agentKey;

        await harness.BeginAsync();

        harness.ApprovalPort.Credentials.Should().ContainSingle();
        harness.ApprovalPort.Credentials.Single().Token.Should().Be(agentKey);
        harness.ApprovalPort.Credentials.Single().Kind.Should()
            .Be(AgentToolNyxIdCredentialKind.AgentKey);
    }

    [Fact]
    public async Task PendingApproval_ShouldResumeAfterContextAndModuleRecreation()
    {
        var harness = await ApprovalHarness.CreateAsync();
        await harness.BeginAsync();
        harness.ApprovalPort.NextStatus = new RemoteToolApprovalStatusSnapshot(
            RemoteToolApprovalStatus.Pending,
            ExpiresAt: harness.RemoteExpiresAt);

        await harness.FireAsync(harness.LatestApprovalStatusCallback());

        harness.Coordination().StatusCheckCount.Should().Be(2);
        var resumedCallback = harness.LatestApprovalStatusCallback();
        harness.RecreateModuleAndContext();
        harness.ApprovalPort.NextStatus = new RemoteToolApprovalStatusSnapshot(
            RemoteToolApprovalStatus.Approved,
            ExpiresAt: harness.RemoteExpiresAt);
        await harness.FireAsync(resumedCallback);
        await harness.DrainConnectorCompletionsAsync();

        harness.ApprovalPort.Submissions.Should().ContainSingle();
        harness.ApprovalPort.StatusQueries.Should().HaveCount(2);
        harness.Connector.Requests.Should().ContainSingle();
        harness.Coordination().Snapshot.LifecycleStatus
            .Should().Be(WorkflowExternalActionLifecycleStatus.Succeeded);
    }

    [Fact]
    public async Task UndispatchedExecutingState_ShouldReplayWithTheSameLogicalIdempotencyKey()
    {
        var harness = await ApprovalHarness.CreateAsync();
        await harness.BeginAsync();
        harness.ApprovalPort.NextStatus = new RemoteToolApprovalStatusSnapshot(
            RemoteToolApprovalStatus.Approved,
            ExpiresAt: harness.RemoteExpiresAt);

        await harness.FireAsync(harness.LatestApprovalStatusCallback());

        var state = harness.State();
        var pending = state.PendingByOperationId.Values.Should().ContainSingle().Subject;
        pending.RequestDispatched.Should().BeTrue();
        pending.RequestDispatched = false;
        await harness.SaveStateAsync(state);
        harness.RecreateModuleAndContext();

        await harness.BeginAsync();
        await harness.DrainConnectorCompletionsAsync();

        harness.Connector.Requests.Should().HaveCount(2);
        harness.Connector.Requests.Select(static request => request.IdempotencyKey)
            .Should().OnlyContain(key => key == ApprovalHarness.IdempotencyKey);
        harness.StepCompletions().Should().ContainSingle().Which.Output.Should().Be("connector-ok");
        harness.Coordination().Snapshot.LifecycleStatus
            .Should().Be(WorkflowExternalActionLifecycleStatus.Succeeded);
    }

    [Fact]
    public async Task PersistedSuccessfulOutcome_ShouldRecoverExactCompletionAfterPublicationFailure()
    {
        var harness = await ApprovalHarness.CreateAsync();
        await harness.BeginAsync();
        harness.ApprovalPort.NextStatus = new RemoteToolApprovalStatusSnapshot(
            RemoteToolApprovalStatus.Approved,
            ExpiresAt: harness.RemoteExpiresAt);
        await harness.FireAsync(harness.LatestApprovalStatusCallback());
        harness.Context.OnPublish = static (evt, _) =>
        {
            if (evt is StepCompletedEvent)
                throw new InvalidOperationException("simulated publication failure");
        };

        var drain = harness.DrainConnectorCompletionsAsync;
        await drain.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("simulated publication failure");

        var persisted = harness.Coordination();
        persisted.Snapshot.ExecutionStatus.Should().Be(WorkflowExternalActionExecutionStatus.Succeeded);
        persisted.StepCompletionPublished.Should().BeFalse();
        var materialReference = persisted.MaterialReference.Clone();
        var completionReference = persisted.CompletionReference.Clone();
        harness.RecreateModuleAndContext();

        await harness.BeginAsync();

        var completion = harness.StepCompletions().Should().ContainSingle().Subject;
        completion.Success.Should().BeTrue();
        completion.Output.Should().Be("connector-ok");
        harness.Coordination().StepCompletionPublished.Should().BeTrue();
        harness.Coordination().MaterialReference.Should().BeNull();
        harness.Coordination().CompletionReference.Should().BeNull();
        harness.State().PendingByOperationId.Should().BeEmpty();
        await AssertReferenceRevokedAsync(harness.SecretStore, materialReference);
        await AssertReferenceRevokedAsync(harness.SecretStore, completionReference);
    }

    [Theory]
    [InlineData("GET", "/resources/alpha")]
    [InlineData("POST", "/resources/beta")]
    public async Task HttpApprovalPlanMismatch_ShouldFailBeforeRemoteSubmission(
        string approvedVerb,
        string approvedResource)
    {
        var connector = new RecordingConnector(
            new ConnectorResponse { Success = true, Output = "connector-ok" })
        {
            Type = "http",
        };
        var harness = await ApprovalHarness.CreateAsync(connector: connector);

        await harness.BeginAsync(httpVerb: approvedVerb, resource: approvedResource);

        harness.ApprovalPort.Submissions.Should().BeEmpty();
        harness.Connector.Requests.Should().BeEmpty();
        harness.State().ApprovalsByActionId.Should().BeEmpty();
        harness.StepCompletions().Should().ContainSingle().Which.Success.Should().BeFalse();
    }

    [Theory]
    [InlineData(
        RemoteToolApprovalStatus.Rejected,
        WorkflowExternalActionApprovalStatus.Denied,
        WorkflowExternalActionLifecycleStatus.Denied,
        "approval_denied")]
    [InlineData(
        RemoteToolApprovalStatus.Expired,
        WorkflowExternalActionApprovalStatus.Expired,
        WorkflowExternalActionLifecycleStatus.Expired,
        "approval_expired")]
    [InlineData(
        RemoteToolApprovalStatus.Cancelled,
        WorkflowExternalActionApprovalStatus.Cancelled,
        WorkflowExternalActionLifecycleStatus.Cancelled,
        "approval_cancelled")]
    public async Task TerminalApprovalDecision_ShouldNeverExecuteConnector(
        RemoteToolApprovalStatus remoteStatus,
        WorkflowExternalActionApprovalStatus approvalStatus,
        WorkflowExternalActionLifecycleStatus lifecycleStatus,
        string reasonCode)
    {
        var harness = await ApprovalHarness.CreateAsync();
        await harness.BeginAsync();
        var reference = harness.Coordination().MaterialReference.Clone();
        harness.ApprovalPort.NextStatus = new RemoteToolApprovalStatusSnapshot(
            remoteStatus,
            ExpiresAt: harness.RemoteExpiresAt);

        await harness.FireAsync(harness.LatestApprovalStatusCallback());

        harness.Connector.Requests.Should().BeEmpty();
        var coordination = harness.Coordination();
        coordination.Snapshot.ApprovalStatus.Should().Be(approvalStatus);
        coordination.Snapshot.LifecycleStatus.Should().Be(lifecycleStatus);
        coordination.Snapshot.ApprovalReasonCode.Should().Be(reasonCode);
        coordination.Snapshot.ExecutionStatus.Should().Be(WorkflowExternalActionExecutionStatus.NotStarted);
        coordination.MaterialReference.Should().BeNull();
        harness.StepCompletions().Should().ContainSingle().Which.Success.Should().BeFalse();
        await AssertReferenceRevokedAsync(harness.SecretStore, reference);
    }

    [Theory]
    [InlineData("action")]
    [InlineData("remote")]
    [InlineData("digest")]
    [InlineData("principal")]
    [InlineData("scope")]
    [InlineData("node")]
    [InlineData("service")]
    [InlineData("permission")]
    public async Task TamperedStatusCallback_ShouldBeIgnoredWithoutRemoteReadOrExecution(string field)
    {
        var harness = await ApprovalHarness.CreateAsync();
        await harness.BeginAsync();
        var callback = harness.LatestApprovalStatusCallback();
        var fired = ((WorkflowConnectorApprovalStatusCheckFiredEvent)callback.Event).Clone();
        switch (field)
        {
            case "action": fired.ActionId += "-tampered"; break;
            case "remote": fired.RemoteApprovalId += "-tampered"; break;
            case "digest": fired.MaterialDigestSha256 = new string('0', 64); break;
            case "principal": fired.PrincipalSubject += "-tampered"; break;
            case "scope": fired.ScopeId += "-tampered"; break;
            case "node": fired.NodeId += "-tampered"; break;
            case "service": fired.ServiceRef += "-tampered"; break;
            case "permission": fired.PermissionScope += "-tampered"; break;
        }

        await harness.FireAsync(callback, fired);

        harness.ApprovalPort.StatusQueries.Should().BeEmpty();
        harness.Connector.Requests.Should().BeEmpty();
        harness.Coordination().Snapshot.LifecycleStatus
            .Should().Be(WorkflowExternalActionLifecycleStatus.WaitingApproval);
    }

    [Fact]
    public async Task TamperedProtectedPayload_ShouldFailDigestCheckBeforeConnectorDispatch()
    {
        var secretStore = new TamperingRuntimeSecretStore();
        var harness = await ApprovalHarness.CreateAsync(secretStore: secretStore);
        await harness.BeginAsync();
        secretStore.TamperConnectorMaterialOnResolve = true;
        harness.ApprovalPort.NextStatus = new RemoteToolApprovalStatusSnapshot(
            RemoteToolApprovalStatus.Approved,
            ExpiresAt: harness.RemoteExpiresAt);

        await harness.FireAsync(harness.LatestApprovalStatusCallback());

        harness.Connector.Requests.Should().BeEmpty();
        var coordination = harness.Coordination();
        coordination.Snapshot.ApprovalStatus.Should().Be(WorkflowExternalActionApprovalStatus.Approved);
        coordination.Snapshot.ExecutionStatus.Should().Be(WorkflowExternalActionExecutionStatus.Failed);
        coordination.Snapshot.ExecutionReasonCode.Should().Be("approval_material_digest_mismatch");
        coordination.Snapshot.LifecycleStatus.Should().Be(WorkflowExternalActionLifecycleStatus.Failed);
        coordination.MaterialReference.Should().BeNull();
    }

    [Fact]
    public async Task RemoteExpiryMismatch_ShouldFailClosedBeforeConnectorDispatch()
    {
        var harness = await ApprovalHarness.CreateAsync();
        await harness.BeginAsync();
        harness.ApprovalPort.NextStatus = new RemoteToolApprovalStatusSnapshot(
            RemoteToolApprovalStatus.Approved,
            ExpiresAt: harness.RemoteExpiresAt.AddSeconds(1));

        await harness.FireAsync(harness.LatestApprovalStatusCallback());

        harness.Connector.Requests.Should().BeEmpty();
        var coordination = harness.Coordination();
        coordination.Snapshot.ApprovalStatus.Should().Be(WorkflowExternalActionApprovalStatus.Failed);
        coordination.Snapshot.ApprovalReasonCode.Should().Be("approval_remote_expiry_mismatch");
        coordination.Snapshot.LifecycleStatus.Should().Be(WorkflowExternalActionLifecycleStatus.Failed);
    }

    [Fact]
    public async Task UnavailableRemoteStatus_ShouldFailClosedWithoutConnectorExecution()
    {
        var harness = await ApprovalHarness.CreateAsync();
        await harness.BeginAsync();
        harness.ApprovalPort.ThrowOnStatus = true;

        await harness.FireAsync(harness.LatestApprovalStatusCallback());

        harness.Connector.Requests.Should().BeEmpty();
        var coordination = harness.Coordination();
        coordination.Snapshot.ApprovalStatus.Should().Be(WorkflowExternalActionApprovalStatus.Failed);
        coordination.Snapshot.ApprovalReasonCode.Should().Be("approval_status_unavailable");
        coordination.Snapshot.LifecycleStatus.Should().Be(WorkflowExternalActionLifecycleStatus.Failed);
        coordination.MaterialReference.Should().BeNull();
        harness.StepCompletions().Should().ContainSingle().Which.Success.Should().BeFalse();
    }

    [Fact]
    public async Task ChangedCallerAuthority_ShouldFailClosedBeforeRemoteReadOrConnectorExecution()
    {
        var harness = await ApprovalHarness.CreateAsync();
        await harness.BeginAsync();
        await harness.SetCallerAuthorityAsync("user-beta");

        await harness.FireAsync(harness.LatestApprovalStatusCallback());

        harness.ApprovalPort.StatusQueries.Should().BeEmpty();
        harness.Connector.Requests.Should().BeEmpty();
        var coordination = harness.Coordination();
        coordination.Snapshot.ApprovalStatus.Should().Be(WorkflowExternalActionApprovalStatus.Failed);
        coordination.Snapshot.ApprovalReasonCode.Should().Be("approval_authority_mismatch");
        coordination.Snapshot.LifecycleStatus.Should().Be(WorkflowExternalActionLifecycleStatus.Failed);
    }

    [Fact]
    public async Task PersistedRemoteBindingWithoutCallbackLease_ShouldRescheduleWithoutResubmission()
    {
        var harness = await ApprovalHarness.CreateAsync();
        await harness.BeginAsync();
        var state = harness.State();
        state.ApprovalsByActionId.Values.Single().StatusCheckLease = null;
        await harness.SaveStateAsync(state);
        harness.Context.Scheduled.Clear();

        await harness.BeginAsync();

        harness.ApprovalPort.Submissions.Should().ContainSingle();
        harness.Context.Scheduled.Should().ContainSingle();
        harness.Coordination().StatusCheckLease.Should().NotBeNull();
        harness.Connector.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ScheduledCallbackWithoutPersistedLease_ShouldUsePersistedCallbackIdentity()
    {
        var harness = await ApprovalHarness.CreateAsync();
        await harness.BeginAsync();
        var callback = harness.LatestApprovalStatusCallback();
        var state = harness.State();
        state.ApprovalsByActionId.Values.Single().StatusCheckLease = null;
        await harness.SaveStateAsync(state);
        harness.ApprovalPort.NextStatus = new RemoteToolApprovalStatusSnapshot(
            RemoteToolApprovalStatus.Approved,
            ExpiresAt: harness.RemoteExpiresAt);

        await harness.FireAsync(callback);
        await harness.DrainConnectorCompletionsAsync();

        harness.Connector.Requests.Should().ContainSingle();
        harness.Coordination().Snapshot.LifecycleStatus
            .Should().Be(WorkflowExternalActionLifecycleStatus.Succeeded);
    }

    [Fact]
    public async Task PersistedTerminalWithoutCompletion_ShouldPublishAndRevokeOnRequestRedelivery()
    {
        var harness = await ApprovalHarness.CreateAsync();
        await harness.BeginAsync();
        var state = harness.State();
        var coordination = state.ApprovalsByActionId.Values.Single();
        var reference = coordination.MaterialReference.Clone();
        coordination.StatusCheckLease = null;
        coordination.Snapshot.ApprovalStatus = WorkflowExternalActionApprovalStatus.Denied;
        coordination.Snapshot.ApprovalReasonCode = "approval_denied";
        coordination.Snapshot.ApprovalResolvedAt = Timestamp.FromDateTimeOffset(ApprovalHarness.Now);
        coordination.Snapshot.LifecycleStatus = WorkflowExternalActionLifecycleStatus.Denied;
        coordination.StepCompletionPublished = false;
        await harness.SaveStateAsync(state);
        harness.Context.Published.Clear();

        await harness.BeginAsync();

        harness.StepCompletions().Should().ContainSingle().Which.Success.Should().BeFalse();
        harness.Coordination().StepCompletionPublished.Should().BeTrue();
        harness.Coordination().MaterialReference.Should().BeNull();
        await AssertReferenceRevokedAsync(harness.SecretStore, reference);
    }

    [Fact]
    public async Task PublishedTerminalWithProtectedMaterial_ShouldRevokeWithoutRepublishing()
    {
        var harness = await ApprovalHarness.CreateAsync();
        await harness.BeginAsync();
        var state = harness.State();
        var coordination = state.ApprovalsByActionId.Values.Single();
        var reference = coordination.MaterialReference.Clone();
        coordination.StatusCheckLease = null;
        coordination.Snapshot.ApprovalStatus = WorkflowExternalActionApprovalStatus.Denied;
        coordination.Snapshot.ApprovalReasonCode = "approval_denied";
        coordination.Snapshot.ApprovalResolvedAt = Timestamp.FromDateTimeOffset(ApprovalHarness.Now);
        coordination.Snapshot.LifecycleStatus = WorkflowExternalActionLifecycleStatus.Denied;
        coordination.StepCompletionPublished = true;
        await harness.SaveStateAsync(state);
        harness.Context.Published.Clear();

        await harness.BeginAsync();

        harness.StepCompletions().Should().BeEmpty();
        harness.Coordination().MaterialReference.Should().BeNull();
        await AssertReferenceRevokedAsync(harness.SecretStore, reference);
    }

    [Fact]
    public async Task SupersededPlan_ShouldIgnoreDelayedCallbackAndExecuteOnlyReplacement()
    {
        var harness = await ApprovalHarness.CreateAsync();
        await harness.BeginAsync();
        var originalCallback = harness.LatestApprovalStatusCallback();
        var originalState = harness.State();
        var originalCoordination = originalState.ApprovalsByActionId.Values.Single();
        var originalReference = originalCoordination.MaterialReference.Clone();
        var originalActionId = originalCoordination.Snapshot.Plan.ActionId;
        harness.ApprovalPort.Submission = new RemoteToolApprovalSubmission(
            "remote-approval-beta",
            ApprovalHarness.Now.AddMinutes(2));

        await harness.BeginAsync(
            executionId: "execution-beta",
            idempotencyKey: "connector-idempotency-beta",
            input: "replacement-payload");

        var superseded = harness.State().ApprovalsByActionId[originalActionId];
        superseded.Snapshot.LifecycleStatus.Should().Be(WorkflowExternalActionLifecycleStatus.Cancelled);
        superseded.MaterialReference.Should().BeNull();
        await AssertReferenceRevokedAsync(harness.SecretStore, originalReference);

        await harness.FireAsync(originalCallback);
        harness.ApprovalPort.StatusQueries.Should().BeEmpty();
        harness.Connector.Requests.Should().BeEmpty();

        harness.ApprovalPort.NextStatus = new RemoteToolApprovalStatusSnapshot(
            RemoteToolApprovalStatus.Approved,
            ExpiresAt: harness.RemoteExpiresAt);
        await harness.FireAsync(harness.LatestApprovalStatusCallback());
        await harness.DrainConnectorCompletionsAsync();

        var connectorRequest = harness.Connector.Requests.Should().ContainSingle().Subject;
        connectorRequest.IdempotencyKey.Should().Be("connector-idempotency-beta");
        connectorRequest.Payload.Should().Be("replacement-payload");
    }

    [Fact]
    public async Task ApprovedPhysicalRetry_ShouldReuseStableIdempotencyKey()
    {
        var connector = new RecordingConnector(
            new ConnectorResponse
            {
                Success = false,
                Error = "transient",
                TerminalInvoked = false,
                Retryable = true,
            },
            new ConnectorResponse { Success = true, Output = "retried-ok" });
        var harness = await ApprovalHarness.CreateAsync(connector: connector, retry: 1);
        await harness.BeginAsync();
        harness.ApprovalPort.NextStatus = new RemoteToolApprovalStatusSnapshot(
            RemoteToolApprovalStatus.Approved,
            ExpiresAt: harness.RemoteExpiresAt);

        await harness.FireAsync(harness.LatestApprovalStatusCallback());
        await harness.DrainConnectorCompletionsAsync();

        connector.Requests.Should().HaveCount(2);
        connector.Requests.Select(static request => request.IdempotencyKey)
            .Should().OnlyContain(key => key == ApprovalHarness.IdempotencyKey);
        harness.StepCompletions().Should().ContainSingle().Which.Output.Should().Be("retried-ok");
        harness.Coordination().Snapshot.ExecutionStatus
            .Should().Be(WorkflowExternalActionExecutionStatus.Succeeded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableApprovalPath_ShouldFailClosedWithoutConnectorExecution(bool throwOnSubmit)
    {
        var harness = await ApprovalHarness.CreateAsync(
            configurePort: throwOnSubmit,
            throwOnSubmit: throwOnSubmit);

        await harness.BeginAsync();

        harness.Connector.Requests.Should().BeEmpty();
        var coordination = harness.Coordination();
        coordination.Snapshot.ApprovalStatus.Should().Be(WorkflowExternalActionApprovalStatus.Failed);
        coordination.Snapshot.LifecycleStatus.Should().Be(WorkflowExternalActionLifecycleStatus.Failed);
        coordination.Snapshot.ApprovalReasonCode.Should().Be(
            throwOnSubmit ? "approval_submission_indeterminate" : "approval_path_unavailable");
        coordination.MaterialReference.Should().BeNull();
        harness.StepCompletions().Should().ContainSingle().Which.Success.Should().BeFalse();
    }

    [Fact]
    public async Task RunTermination_ShouldCancelPendingApprovalAndRevokeMaterial()
    {
        var harness = await ApprovalHarness.CreateAsync();
        await harness.BeginAsync();
        var reference = harness.Coordination().MaterialReference.Clone();

        await harness.Module.HandleAsync(
            Envelope(new WorkflowRunStoppedEvent
            {
                RunId = ApprovalHarness.RunId,
                Reason = "cancelled",
            }),
            harness.Context,
            CancellationToken.None);

        var coordination = harness.Coordination();
        coordination.Snapshot.ApprovalStatus.Should().Be(WorkflowExternalActionApprovalStatus.Cancelled);
        coordination.Snapshot.LifecycleStatus.Should().Be(WorkflowExternalActionLifecycleStatus.Cancelled);
        coordination.MaterialReference.Should().BeNull();
        coordination.StepCompletionPublished.Should().BeTrue();
        harness.Connector.Requests.Should().BeEmpty();
        await AssertReferenceRevokedAsync(harness.SecretStore, reference);
    }

    private static async Task AssertReferenceRevokedAsync(
        IRuntimeSecretStore secretStore,
        RuntimeSecretReference reference)
    {
        var resolved = await secretStore.ResolveAsync(new ResolveRuntimeSecretRequest(
            reference.Ref,
            reference.Purpose,
            reference.OwnerRunId,
            reference.OwnerStepId,
            "test-verify-revoked"));
        resolved.Resolved.Should().BeFalse();
    }

    internal static EventEnvelope Envelope(IMessage evt) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(ApprovalHarness.Now),
            Payload = Any.Pack(evt),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
        };

    internal sealed class ApprovalHarness
    {
        public const string RunId = "run-approval";
        public const string StepId = "connector-approval";
        public const string IdempotencyKey = "connector-idempotency-alpha";
        public const string BearerToken = "nyx-bearer-secret";
        public const string RawPayload = "raw-payload-secret";
        public const string RawParameter = "parameter-secret";
        public static readonly DateTimeOffset Now = new(2026, 7, 17, 8, 0, 0, TimeSpan.Zero);

        private ApprovalHarness(
            TestAgent agent,
            RecordingConnector connector,
            RecordingApprovalPort approvalPort,
            IRuntimeSecretStore secretStore,
            bool configurePort,
            int retry)
        {
            Agent = agent;
            Connector = connector;
            ConnectorResolver = new MutableConnectorResolver(connector);
            ApprovalPort = approvalPort;
            SecretStore = secretStore;
            ConfigurePort = configurePort;
            Retry = retry;
            Logger = new RecordingLogger();
            Services = new ServiceCollection().BuildServiceProvider();
            Context = NewContext();
            Module = NewModule();
        }

        public TestAgent Agent { get; }
        public RecordingConnector Connector { get; }
        public RecordingApprovalPort ApprovalPort { get; }
        public MutableConnectorResolver ConnectorResolver { get; }
        public IRuntimeSecretStore SecretStore { get; }
        private RecordingLogger Logger { get; }
        private IServiceProvider Services { get; }
        public DateTimeOffset RemoteExpiresAt { get; } = Now.AddMinutes(2);
        public TestEventHandlerContext Context { get; private set; }
        public ConnectorCallModule Module { get; private set; }
        private bool ConfigurePort { get; set; }
        private int Retry { get; }

        public static async Task<ApprovalHarness> CreateAsync(
            RecordingConnector? connector = null,
            IRuntimeSecretStore? secretStore = null,
            bool configurePort = true,
            bool throwOnSubmit = false,
            int retry = 0)
        {
            secretStore ??= new InMemoryRuntimeSecretStore();
            connector ??= new RecordingConnector(
                new ConnectorResponse { Success = true, Output = "connector-ok" });
            var approvalPort = new RecordingApprovalPort
            {
                Submission = new RemoteToolApprovalSubmission("remote-approval-alpha", Now.AddMinutes(2)),
                ThrowOnSubmit = throwOnSubmit,
            };
            var agent = new TestAgent("workflow-actor-alpha", RunId, "scope-alpha", secretStore);
            var harness = new ApprovalHarness(agent, connector, approvalPort, secretStore, configurePort, retry);
            await WorkflowCallerCredentialRuntimeContextAccess.SetCredentialAsync(
                agent,
                new WorkflowCallerCredential
                {
                    BearerToken = BearerToken,
                    NyxIdAuthority = new WorkflowCallerNyxIdAuthority
                    {
                        Platform = "nyxid",
                        Tenant = "tenant-alpha",
                        ExternalUserId = "user-alpha",
                        Scope = "proxy",
                    },
                });
            return harness;
        }

        public Task BeginAsync(
            string executionId = "execution-alpha",
            string idempotencyKey = IdempotencyKey,
            string input = RawPayload,
            string httpVerb = "POST",
            string resource = "/resources/alpha") =>
            BeginAsync(CreateRequest(executionId, idempotencyKey, input, httpVerb, resource));

        public Task BeginAsync(StepRequestEvent request) =>
            Module.HandleAsync(
                Envelope(request),
                Context,
                CancellationToken.None);

        public Task SaveStateAsync(ConnectorCallModuleState state) =>
            Context.SaveStateAsync("connector_call", state, CancellationToken.None);

        public Task SetCallerAuthorityAsync(string externalUserId) =>
            WorkflowCallerCredentialRuntimeContextAccess.SetCredentialAsync(
                Agent,
                new WorkflowCallerCredential
                {
                    BearerToken = BearerToken,
                    NyxIdAuthority = new WorkflowCallerNyxIdAuthority
                    {
                        Platform = "nyxid",
                        Tenant = "tenant-alpha",
                        ExternalUserId = externalUserId,
                        Scope = "proxy",
                    },
                });

        public async Task FireAsync(ScheduledCallback callback, IMessage? replacement = null)
        {
            var envelope = Context.CreateScheduledEnvelope(callback);
            if (replacement != null)
                envelope.Payload = Any.Pack(replacement);
            await Module.HandleAsync(envelope, Context, CancellationToken.None);
        }

        public async Task DrainConnectorCompletionsAsync()
        {
            while (true)
            {
                var index = Context.Published.FindIndex(static published =>
                    published.evt is WorkflowConnectorAttemptCompletedEvent);
                if (index < 0)
                    return;

                var completed = (WorkflowConnectorAttemptCompletedEvent)Context.Published[index].evt;
                Context.Published.RemoveAt(index);
                await Module.HandleAsync(Envelope(completed), Context, CancellationToken.None);
            }
        }

        public void RecreateModuleAndContext(bool? configurePort = null)
        {
            if (configurePort.HasValue)
                ConfigurePort = configurePort.Value;
            Context = NewContext();
            Module = NewModule();
        }

        public ConnectorCallModuleState State() =>
            Context.LoadState<ConnectorCallModuleState>("connector_call");

        public ConnectorApprovalCoordinationState Coordination()
        {
            var state = State();
            state.ApprovalsByActionId.Values.Should().ContainSingle(
                "logs={0}; pending={1}; connector_requests={2}; published={3}",
                string.Join(" | ", Logger.Messages),
                state.PendingByOperationId.Count,
                Connector.Requests.Count,
                string.Join(",", Context.Published.Select(static item => item.evt is StepCompletedEvent completed
                    ? $"{StepCompletedEvent.Descriptor.Name}:{completed.Error}"
                    : item.evt.Descriptor.Name)));
            return state.ApprovalsByActionId.Values.Single();
        }

        public ScheduledCallback LatestApprovalStatusCallback() =>
            Context.Scheduled.Last(callback =>
                callback.Event is WorkflowConnectorApprovalStatusCheckFiredEvent);

        public ScheduledCallback LatestConnectorTimeoutCallback() =>
            Context.Scheduled.Last(callback =>
                callback.Event is WorkflowConnectorTimeoutFiredEvent);

        public IReadOnlyList<StepCompletedEvent> StepCompletions() =>
            Context.Published.Select(static published => published.evt).OfType<StepCompletedEvent>().ToList();

        private TestEventHandlerContext NewContext() =>
            new(Services, Agent, Logger) { UtcNow = Now };

        private ConnectorCallModule NewModule() =>
            new(
                ConnectorResolver,
                remoteToolApprovalPort: ConfigurePort ? ApprovalPort : null);

        public StepRequestEvent CreateRequest(
            string executionId,
            string idempotencyKey,
            string input,
            string httpVerb,
            string resource) =>
            new()
            {
                StepId = StepId,
                StepType = "connector_call",
                RunId = RunId,
                ExecutionId = executionId,
                IdempotencyKey = idempotencyKey,
                Input = input,
                StepParameters = new WorkflowStepParameters
                {
                    Parameters =
                    {
                        ["connector"] = Connector.Name,
                        ["operation"] = "create_resource",
                        ["method"] = "POST",
                        ["path"] = "/resources/alpha",
                        ["api_secret"] = RawParameter,
                        ["retry"] = Retry.ToString(),
                    },
                    ConnectorApproval = new WorkflowConnectorApprovalOptions
                    {
                        Policy = WorkflowExternalActionApprovalPolicy.Required,
                        ServiceRef = "service-alpha",
                        NodeId = "node-alpha",
                        HttpVerb = httpVerb,
                        Resource = resource,
                        PermissionScope = "resources.write",
                        ExpirationSeconds = 300,
                        StatusCheckIntervalSeconds = 2,
                        Destructive = true,
                        TeamId = "team-alpha",
                        MemberId = "member-alpha",
                        WorkflowId = "workflow-alpha",
                        PublishedServiceId = "published-service-alpha",
                        PolicyReason = "external-write",
                    },
                },
            };
    }

    internal sealed class MutableConnectorResolver(IConnector connector) : IWorkflowConnectorResolver
    {
        public IConnector? Connector { get; set; } = connector;
        public Queue<IConnector?> Resolutions { get; } = [];

        public ValueTask<IConnector?> ResolveAsync(
            IWorkflowExecutionContext context,
            string connectorName,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var resolved = Resolutions.Count > 0 ? Resolutions.Dequeue() : Connector;
            return ValueTask.FromResult<IConnector?>(
                resolved != null && string.Equals(resolved.Name, connectorName, StringComparison.Ordinal)
                    ? resolved
                    : null);
        }
    }

    internal sealed class RecordingConnector(params ConnectorResponse[] responses) : IConnector
    {
        private readonly Queue<ConnectorResponse> _responses = new(responses);

        public string Name => "connector-alpha";
        public string Type { get; init; } = "test";
        public List<ConnectorRequest> Requests { get; } = [];

        public Task<ConnectorResponse> ExecuteAsync(ConnectorRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(_responses.Count == 0
                ? new ConnectorResponse { Success = true, Output = "connector-ok" }
                : _responses.Dequeue());
        }
    }

    internal sealed class RecordingApprovalPort : IRemoteToolApprovalPort
    {
        public RemoteToolApprovalSubmission Submission { get; set; } = new("remote", ApprovalHarness.Now.AddMinutes(1));
        public RemoteToolApprovalStatusSnapshot NextStatus { get; set; } = new(
            RemoteToolApprovalStatus.Pending,
            ExpiresAt: ApprovalHarness.Now.AddMinutes(2));
        public bool ThrowOnSubmit { get; init; }
        public bool ThrowOnStatus { get; set; }
        public string ExpectedToken { get; set; } = ApprovalHarness.BearerToken;
        public List<RemoteToolApprovalRequest> Submissions { get; } = [];
        public List<RemoteToolApprovalStatusQuery> StatusQueries { get; } = [];
        public List<(string? Token, AgentToolNyxIdCredentialKind Kind)> Credentials { get; } = [];

        public Task<RemoteToolApprovalSubmission> SubmitAsync(
            RemoteToolApprovalRequest request,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var context = AgentToolRequestContext.Current;
            context.Should().NotBeNull();
            context!.Credentials.NyxIdAccessToken.Should().Be(ExpectedToken);
            Credentials.Add((
                context.Credentials.NyxIdAccessToken,
                context.Credentials.NyxIdCredentialKind));
            Submissions.Add(request);
            return ThrowOnSubmit
                ? Task.FromException<RemoteToolApprovalSubmission>(new HttpRequestException("unavailable"))
                : Task.FromResult(Submission);
        }

        public Task<RemoteToolApprovalStatusSnapshot> GetStatusAsync(
            RemoteToolApprovalStatusQuery query,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var context = AgentToolRequestContext.Current;
            context.Should().NotBeNull();
            context!.Credentials.NyxIdAccessToken.Should().Be(ExpectedToken);
            Credentials.Add((
                context.Credentials.NyxIdAccessToken,
                context.Credentials.NyxIdCredentialKind));
            StatusQueries.Add(query);
            return ThrowOnStatus
                ? Task.FromException<RemoteToolApprovalStatusSnapshot>(new HttpRequestException("unavailable"))
                : Task.FromResult(NextStatus);
        }

        public Task<RemoteToolApprovalDecisionResult> DecideAsync(
            RemoteToolApprovalDecision decision,
            CancellationToken ct) =>
            Task.FromResult(new RemoteToolApprovalDecisionResult(true));
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

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
            var message = formatter(state, exception);
            if (exception != null)
                message += $" exception={exception.GetType().Name}: {exception.Message}";
            Messages.Add(message);
        }
    }

    internal sealed class TamperingRuntimeSecretStore : IRuntimeSecretStore
    {
        private readonly InMemoryRuntimeSecretStore _inner = new();
        private int _materialResolveCount;

        public bool TamperConnectorMaterialOnResolve { get; set; }
        public int? TamperConnectorMaterialOnResolveNumber { get; set; }

        public Task<StoreRuntimeSecretResult> PutAsync(
            StoreRuntimeSecretRequest request,
            CancellationToken ct = default) =>
            _inner.PutAsync(request, ct);

        public async Task<ResolveRuntimeSecretResult> ResolveAsync(
            ResolveRuntimeSecretRequest request,
            CancellationToken ct = default)
        {
            var result = await _inner.ResolveAsync(request, ct);
            if (request.Purpose != CredentialSecretPurposes.WorkflowConnectorExternalActionMaterial ||
                !result.Resolved ||
                string.IsNullOrWhiteSpace(result.Secret))
            {
                return result;
            }

            _materialResolveCount++;
            if (!TamperConnectorMaterialOnResolve &&
                TamperConnectorMaterialOnResolveNumber != _materialResolveCount)
            {
                return result;
            }

            var material = ConnectorCallProtectedMaterial.Parser.ParseFrom(
                Convert.FromBase64String(result.Secret));
            material.Payload += "-tampered";
            return new ResolveRuntimeSecretResult(
                result.Reference,
                Convert.ToBase64String(material.ToByteArray()));
        }

        public Task<ConsumeRuntimeSecretResult> ConsumeAsync(
            ConsumeRuntimeSecretRequest request,
            CancellationToken ct = default) =>
            _inner.ConsumeAsync(request, ct);

        public Task<RevokeRuntimeSecretResult> RevokeAsync(
            RevokeRuntimeSecretRequest request,
            CancellationToken ct = default) =>
            _inner.RevokeAsync(request, ct);
    }
}
