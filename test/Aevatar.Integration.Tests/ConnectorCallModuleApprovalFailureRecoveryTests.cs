using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using FluentAssertions;
using ApprovalHarness = Aevatar.Integration.Tests.ConnectorCallModuleApprovalTests.ApprovalHarness;
using RecordingConnector = Aevatar.Integration.Tests.ConnectorCallModuleApprovalTests.RecordingConnector;
using TamperingRuntimeSecretStore = Aevatar.Integration.Tests.ConnectorCallModuleApprovalTests.TamperingRuntimeSecretStore;

namespace Aevatar.Integration.Tests;

[Trait("Category", "Integration")]
[Trait("Feature", "ConnectorCallApproval")]
public sealed class ConnectorCallModuleApprovalFailureRecoveryTests
{
    [Theory]
    [InlineData("identity")]
    [InlineData("authority")]
    [InlineData("action")]
    [InlineData("timing")]
    public async Task InvalidApprovalConfiguration_ShouldFailBeforeSubmission(string invalidField)
    {
        var harness = await ApprovalHarness.CreateAsync();
        var request = harness.CreateRequest(
            "execution-alpha",
            ApprovalHarness.IdempotencyKey,
            ApprovalHarness.RawPayload,
            "POST",
            "/resources/alpha");
        switch (invalidField)
        {
            case "identity":
                request.IdempotencyKey = string.Empty;
                break;
            case "authority":
                await harness.Agent.ClearExecutionContextAsync();
                break;
            case "action":
                request.StepParameters.ConnectorApproval.ServiceRef = string.Empty;
                break;
            case "timing":
                request.StepParameters.ConnectorApproval.ExpirationSeconds = 0;
                break;
        }

        await harness.BeginAsync(request);

        harness.ApprovalPort.Submissions.Should().BeEmpty();
        harness.Connector.Requests.Should().BeEmpty();
        harness.State().ApprovalsByActionId.Should().BeEmpty();
        harness.StepCompletions().Should().ContainSingle().Which.Success.Should().BeFalse();
    }

    [Fact]
    public async Task InvalidRemoteApprovalBinding_ShouldFailClosed()
    {
        var harness = await ApprovalHarness.CreateAsync();
        harness.ApprovalPort.Submission = new RemoteToolApprovalSubmission(string.Empty, harness.RemoteExpiresAt);

        await harness.BeginAsync();

        harness.Coordination().Snapshot.ApprovalReasonCode.Should().Be("approval_remote_binding_invalid");
        harness.Coordination().Snapshot.LifecycleStatus.Should().Be(WorkflowExternalActionLifecycleStatus.Failed);
        harness.Connector.Requests.Should().BeEmpty();
        harness.StepCompletions().Should().ContainSingle().Which.Success.Should().BeFalse();
    }

    [Fact]
    public async Task WaitingApprovalWithoutRemoteBinding_ShouldFailOnRedelivery()
    {
        var harness = await ApprovalHarness.CreateAsync();
        await harness.BeginAsync();
        var state = harness.State();
        var coordination = state.ApprovalsByActionId.Values.Single();
        coordination.Snapshot.RemoteApprovalId = string.Empty;
        await harness.SaveStateAsync(state);

        await harness.BeginAsync();

        harness.ApprovalPort.Submissions.Should().ContainSingle();
        harness.Coordination().Snapshot.ApprovalReasonCode.Should().Be("approval_submission_indeterminate");
        harness.Coordination().Snapshot.LifecycleStatus.Should().Be(WorkflowExternalActionLifecycleStatus.Failed);
    }

    [Fact]
    public async Task ApprovedState_ShouldStartExecutionOnRequestRedelivery()
    {
        var harness = await ApprovalHarness.CreateAsync();
        await harness.BeginAsync();
        var state = harness.State();
        var coordination = state.ApprovalsByActionId.Values.Single();
        coordination.StatusCheckLease = null;
        coordination.Snapshot.ApprovalStatus = WorkflowExternalActionApprovalStatus.Approved;
        coordination.Snapshot.ApprovalReasonCode = "approval_approved";
        coordination.Snapshot.LifecycleStatus = WorkflowExternalActionLifecycleStatus.Approved;
        await harness.SaveStateAsync(state);

        await harness.BeginAsync();
        await harness.DrainConnectorCompletionsAsync();

        harness.Connector.Requests.Should().ContainSingle();
        harness.Coordination().Snapshot.LifecycleStatus.Should().Be(WorkflowExternalActionLifecycleStatus.Succeeded);
    }

    [Fact]
    public async Task ExecutingStateWithoutPendingDispatch_ShouldRestartExecutionOnRedelivery()
    {
        var harness = await ApprovalHarness.CreateAsync();
        await harness.BeginAsync();
        var state = harness.State();
        var coordination = state.ApprovalsByActionId.Values.Single();
        coordination.StatusCheckLease = null;
        coordination.CurrentAttempt = 1;
        coordination.Snapshot.ApprovalStatus = WorkflowExternalActionApprovalStatus.Approved;
        coordination.Snapshot.LifecycleStatus = WorkflowExternalActionLifecycleStatus.Executing;
        coordination.Snapshot.ExecutionStatus = WorkflowExternalActionExecutionStatus.Executing;
        await harness.SaveStateAsync(state);

        await harness.BeginAsync();
        await harness.DrainConnectorCompletionsAsync();

        harness.Connector.Requests.Should().ContainSingle();
        harness.Coordination().Snapshot.LifecycleStatus.Should().Be(WorkflowExternalActionLifecycleStatus.Succeeded);
    }

    [Fact]
    public async Task UnknownRemoteApprovalStatus_ShouldFailClosed()
    {
        var harness = await ApprovalHarness.CreateAsync();
        await harness.BeginAsync();
        harness.ApprovalPort.NextStatus = new RemoteToolApprovalStatusSnapshot(
            RemoteToolApprovalStatus.Unknown,
            ExpiresAt: harness.RemoteExpiresAt);

        await harness.FireAsync(harness.LatestApprovalStatusCallback());

        harness.Coordination().Snapshot.ApprovalReasonCode.Should().Be("approval_status_unknown");
        harness.Coordination().Snapshot.LifecycleStatus.Should().Be(WorkflowExternalActionLifecycleStatus.Failed);
        harness.Connector.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task LocallyExpiredApproval_ShouldFailBeforeRemoteStatusRead()
    {
        var harness = await ApprovalHarness.CreateAsync();
        await harness.BeginAsync();
        harness.Context.UtcNow = harness.RemoteExpiresAt;

        await harness.FireAsync(harness.LatestApprovalStatusCallback());

        harness.ApprovalPort.StatusQueries.Should().BeEmpty();
        harness.Coordination().Snapshot.ApprovalReasonCode.Should().Be("approval_expired");
        harness.Coordination().Snapshot.LifecycleStatus.Should().Be(WorkflowExternalActionLifecycleStatus.Expired);
    }

    [Fact]
    public async Task ApprovalPortLostAfterRestart_ShouldFailClosedOnStatusCheck()
    {
        var harness = await ApprovalHarness.CreateAsync();
        await harness.BeginAsync();
        var callback = harness.LatestApprovalStatusCallback();
        harness.RecreateModuleAndContext(configurePort: false);

        await harness.FireAsync(callback);

        harness.ApprovalPort.StatusQueries.Should().BeEmpty();
        harness.Coordination().Snapshot.ApprovalReasonCode.Should().Be("approval_path_unavailable");
        harness.Coordination().Snapshot.LifecycleStatus.Should().Be(WorkflowExternalActionLifecycleStatus.Failed);
    }

    [Fact]
    public async Task DenialWithContinueOnError_ShouldPublishOriginalInput()
    {
        var harness = await ApprovalHarness.CreateAsync();
        var request = harness.CreateRequest(
            "execution-alpha",
            ApprovalHarness.IdempotencyKey,
            ApprovalHarness.RawPayload,
            "POST",
            "/resources/alpha");
        request.Parameters["on_error"] = "continue";
        await harness.BeginAsync(request);
        harness.ApprovalPort.NextStatus = new RemoteToolApprovalStatusSnapshot(
            RemoteToolApprovalStatus.Rejected,
            ExpiresAt: harness.RemoteExpiresAt);

        await harness.FireAsync(harness.LatestApprovalStatusCallback());

        var completion = harness.StepCompletions().Should().ContainSingle().Subject;
        completion.Success.Should().BeTrue();
        completion.Output.Should().Be(ApprovalHarness.RawPayload);
        completion.Annotations["connector.continued_on_error"].Should().Be("true");
        completion.Annotations["connector.error"].Should().Contain("denied");
    }

    [Theory]
    [InlineData(true, "connector_unavailable")]
    [InlineData(false, "connector_binding_mismatch")]
    public async Task ChangedConnectorBinding_ShouldFailBeforeApprovedDispatch(
        bool removeConnector,
        string reasonCode)
    {
        var harness = await ApprovalHarness.CreateAsync();
        await harness.BeginAsync();
        harness.ConnectorResolver.Connector = removeConnector
            ? null
            : new RecordingConnector(new ConnectorResponse { Success = true, Output = "unexpected" })
            {
                Type = "different",
            };

        await ApproveAsync(harness, drainConnectorCompletions: false);

        harness.Connector.Requests.Should().BeEmpty();
        harness.Coordination().Snapshot.ExecutionReasonCode.Should().Be(reasonCode);
        harness.Coordination().Snapshot.LifecycleStatus.Should().Be(WorkflowExternalActionLifecycleStatus.Failed);
    }

    [Fact]
    public async Task MaterialChangedBetweenPendingRegistrationAndDispatch_ShouldFailSecondVerification()
    {
        var secretStore = new TamperingRuntimeSecretStore
        {
            TamperConnectorMaterialOnResolveNumber = 2,
        };
        var harness = await ApprovalHarness.CreateAsync(secretStore: secretStore);
        await harness.BeginAsync();

        await ApproveAsync(harness, drainConnectorCompletions: false);

        harness.Connector.Requests.Should().BeEmpty();
        harness.State().PendingByOperationId.Should().BeEmpty();
        harness.Coordination().Snapshot.ExecutionReasonCode.Should().Be("approval_material_digest_mismatch");
        harness.Coordination().Snapshot.LifecycleStatus.Should().Be(WorkflowExternalActionLifecycleStatus.Failed);
    }

    [Fact]
    public async Task UndispatchedExecutionWithRevokedMaterial_ShouldFailOnRedelivery()
    {
        var harness = await ApprovalHarness.CreateAsync();
        await harness.BeginAsync();
        await ApproveAsync(harness, drainConnectorCompletions: false);
        var state = harness.State();
        var coordination = state.ApprovalsByActionId.Values.Single();
        var materialReference = coordination.MaterialReference.Clone();
        state.PendingByOperationId.Values.Single().RequestDispatched = false;
        await harness.SaveStateAsync(state);
        await RevokeAsync(harness, materialReference);
        harness.RecreateModuleAndContext();

        await harness.BeginAsync();

        harness.Connector.Requests.Should().ContainSingle();
        harness.Coordination().Snapshot.ExecutionReasonCode.Should().Be("approval_material_unavailable");
        harness.Coordination().Snapshot.LifecycleStatus.Should().Be(WorkflowExternalActionLifecycleStatus.Failed);
    }

    [Fact]
    public async Task UndispatchedExecutionWithChangedConnector_ShouldFailOnRedelivery()
    {
        var harness = await ApprovalHarness.CreateAsync();
        await harness.BeginAsync();
        await ApproveAsync(harness, drainConnectorCompletions: false);
        var state = harness.State();
        state.PendingByOperationId.Values.Single().RequestDispatched = false;
        await harness.SaveStateAsync(state);
        harness.RecreateModuleAndContext();
        harness.ConnectorResolver.Resolutions.Enqueue(harness.Connector);
        harness.ConnectorResolver.Resolutions.Enqueue(null);

        await harness.BeginAsync();

        harness.Connector.Requests.Should().ContainSingle();
        harness.Coordination().Snapshot.ExecutionReasonCode.Should().Be("connector_unavailable");
        harness.Coordination().Snapshot.LifecycleStatus.Should().Be(WorkflowExternalActionLifecycleStatus.Failed);
    }

    [Fact]
    public async Task FailedConnectorWithoutRetry_ShouldPersistExecutionFailure()
    {
        var connector = new RecordingConnector(new ConnectorResponse { Success = false, Error = "connector-boom" });
        var harness = await ApprovalHarness.CreateAsync(connector: connector);
        await harness.BeginAsync();

        await ApproveAsync(harness);

        harness.StepCompletions().Should().ContainSingle().Which.Error.Should().Be("connector-boom");
        harness.Coordination().Snapshot.ExecutionReasonCode.Should().Be("connector_failed");
        harness.Coordination().Snapshot.ExecutionStatus.Should().Be(WorkflowExternalActionExecutionStatus.Failed);
    }

    [Theory]
    [InlineData("", "valid", false)]
    [InlineData("not-json", "valid", false)]
    [InlineData("{}", "valid", false)]
    [InlineData("{\"items\":[true]}", "items.0", true)]
    [InlineData("{\"value\":0}", "value", false)]
    [InlineData("{\"value\":\"false\"}", "value", false)]
    [InlineData("{\"value\":null}", "value", false)]
    [InlineData("{\"value\":[]}", "value", true)]
    [InlineData("{\"value\":{}}", ".", true)]
    public async Task ApprovedResponseAssertion_ShouldFollowJsonPathTruthiness(
        string responseOutput,
        string responsePath,
        bool expectedSuccess)
    {
        var connector = new RecordingConnector(new ConnectorResponse { Success = true, Output = responseOutput });
        var harness = await ApprovalHarness.CreateAsync(connector: connector);
        var request = harness.CreateRequest(
            "execution-alpha",
            ApprovalHarness.IdempotencyKey,
            ApprovalHarness.RawPayload,
            "POST",
            "/resources/alpha");
        request.Parameters["assert_response_path"] = responsePath;
        await harness.BeginAsync(request);

        await ApproveAsync(harness);

        var completion = harness.StepCompletions().Should().ContainSingle().Subject;
        completion.Success.Should().Be(expectedSuccess);
        if (expectedSuccess)
            completion.Error.Should().BeEmpty();
        else
            completion.Error.Should().Contain("assertion failed");
    }

    [Fact]
    public async Task ApprovedPassThroughInput_ShouldPublishOriginalInput()
    {
        var connector = new RecordingConnector(
            new ConnectorResponse { Success = true, Output = "{\"valid\":true}" });
        var harness = await ApprovalHarness.CreateAsync(connector: connector);
        var request = harness.CreateRequest(
            "execution-alpha",
            ApprovalHarness.IdempotencyKey,
            ApprovalHarness.RawPayload,
            "POST",
            "/resources/alpha");
        request.Parameters["assert_response_path"] = "valid";
        request.Parameters["pass_through_input"] = "true";
        await harness.BeginAsync(request);

        await ApproveAsync(harness);

        harness.StepCompletions().Should().ContainSingle().Which.Output.Should().Be(ApprovalHarness.RawPayload);
    }

    [Fact]
    public async Task ConnectorCompletionWithRevokedMaterial_ShouldPersistFailure()
    {
        var harness = await ApprovalHarness.CreateAsync();
        await harness.BeginAsync();
        await ApproveAsync(harness, drainConnectorCompletions: false);
        var materialReference = harness.Coordination().MaterialReference.Clone();
        await RevokeAsync(harness, materialReference);

        await harness.DrainConnectorCompletionsAsync();

        harness.Coordination().Snapshot.ExecutionReasonCode.Should().Be("approval_material_unavailable");
        harness.Coordination().Snapshot.ExecutionStatus.Should().Be(WorkflowExternalActionExecutionStatus.Failed);
        harness.StepCompletions().Should().ContainSingle().Which.Success.Should().BeFalse();
    }

    [Fact]
    public async Task ApprovedConnectorTimeout_ShouldPersistTerminalFailure()
    {
        var harness = await ApprovalHarness.CreateAsync();
        await harness.BeginAsync();
        await ApproveAsync(harness, drainConnectorCompletions: false);

        await harness.FireAsync(harness.LatestConnectorTimeoutCallback());

        var completion = harness.StepCompletions().Should().ContainSingle().Subject;
        completion.Success.Should().BeFalse();
        completion.Error.Should().Contain("timed out");
        completion.Annotations["connector.timeout_fired"].Should().Be("true");
        harness.Coordination().Snapshot.ExecutionReasonCode.Should().Be("connector_timeout");
        harness.State().PendingByOperationId.Should().BeEmpty();
    }

    [Fact]
    public async Task ApprovedConnectorTimeoutWithRetry_ShouldReuseLogicalIdempotencyKey()
    {
        var connector = new RecordingConnector(
            new ConnectorResponse { Success = true, Output = "first" },
            new ConnectorResponse { Success = true, Output = "second" });
        var harness = await ApprovalHarness.CreateAsync(connector: connector, retry: 1);
        await harness.BeginAsync();
        await ApproveAsync(harness, drainConnectorCompletions: false);

        await harness.FireAsync(harness.LatestConnectorTimeoutCallback());

        connector.Requests.Should().HaveCount(2);
        connector.Requests.Select(static request => request.IdempotencyKey)
            .Should().OnlyContain(key => key == ApprovalHarness.IdempotencyKey);
        harness.Coordination().CurrentAttempt.Should().Be(2);
        harness.Coordination().Snapshot.ExecutionStatus.Should().Be(WorkflowExternalActionExecutionStatus.Executing);
    }

    [Fact]
    public async Task UndispatchedApprovedTimeout_ShouldRedriveTheSameAttempt()
    {
        var harness = await ApprovalHarness.CreateAsync();
        await harness.BeginAsync();
        await ApproveAsync(harness, drainConnectorCompletions: false);
        var timeout = harness.LatestConnectorTimeoutCallback();
        var state = harness.State();
        state.PendingByOperationId.Values.Single().RequestDispatched = false;
        await harness.SaveStateAsync(state);

        await harness.FireAsync(timeout);

        harness.Connector.Requests.Should().HaveCount(2);
        harness.Connector.Requests.Select(static request => request.IdempotencyKey)
            .Should().OnlyContain(key => key == ApprovalHarness.IdempotencyKey);
        harness.Coordination().CurrentAttempt.Should().Be(1);
    }

    private static async Task ApproveAsync(
        ApprovalHarness harness,
        bool drainConnectorCompletions = true)
    {
        harness.ApprovalPort.NextStatus = new RemoteToolApprovalStatusSnapshot(
            RemoteToolApprovalStatus.Approved,
            ExpiresAt: harness.RemoteExpiresAt);
        await harness.FireAsync(harness.LatestApprovalStatusCallback());
        if (drainConnectorCompletions)
            await harness.DrainConnectorCompletionsAsync();
    }

    private static Task<RevokeRuntimeSecretResult> RevokeAsync(
        ApprovalHarness harness,
        RuntimeSecretReference reference) =>
        harness.SecretStore.RevokeAsync(new RevokeRuntimeSecretRequest(
            reference.Ref,
            reference.Purpose,
            reference.OwnerRunId,
            reference.OwnerStepId,
            "connector-approval-coverage-test"));
}
