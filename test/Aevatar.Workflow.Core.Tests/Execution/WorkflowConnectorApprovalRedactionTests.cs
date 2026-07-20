using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Execution;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Core.Tests.Execution;

public sealed class WorkflowConnectorApprovalRedactionTests
{
    [Fact]
    public async Task BeforePublishAsync_ShouldRemoveProtectedMaterialReferenceFromEventAndStateRoot()
    {
        const string materialReference = "runtime-secret://connector-material-alpha";
        const string completionReference = "runtime-secret://connector-completion-alpha";
        var connectorState = CreateConnectorState(materialReference, completionReference);
        var published = new CommittedStateEventPublished
        {
            StateEvent = new StateEvent
            {
                EventId = "evt-connector-approval",
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                Version = 7,
                AgentId = "run-approval",
                EventType = nameof(WorkflowExecutionStateUpsertedEvent),
                EventData = Any.Pack(new WorkflowExecutionStateUpsertedEvent
                {
                    ScopeKey = "connector_call",
                    State = Any.Pack(connectorState),
                }),
            },
            StateRoot = Any.Pack(new WorkflowRunState
            {
                RunId = "run-approval",
                ExecutionStates =
                {
                    ["connector_call"] = Any.Pack(connectorState),
                },
            }),
        };
        var hook = new WorkflowRunCommittedStateRedactionHook();

        await hook.BeforePublishAsync(new CommittedStatePublicationContext
        {
            ActorId = "run-approval",
            ActorType = typeof(WorkflowRunGAgent),
            Published = published,
        }, CancellationToken.None);

        var upserted = published.StateEvent.EventData.Unpack<WorkflowExecutionStateUpsertedEvent>();
        var eventState = upserted.State.Unpack<ConnectorCallModuleState>();
        var eventApproval = eventState.ApprovalsByActionId.Values.Should().ContainSingle().Subject;
        eventApproval.MaterialReference.Should().BeNull();
        eventApproval.CompletionReference.Should().BeNull();
        eventApproval.Snapshot.Plan.ActionId.Should().Be("action-alpha");

        var stateRoot = published.StateRoot.Unpack<WorkflowRunState>();
        var rootState = stateRoot.ExecutionStates["connector_call"].Unpack<ConnectorCallModuleState>();
        var rootApproval = rootState.ApprovalsByActionId.Values.Should().ContainSingle().Subject;
        rootApproval.MaterialReference.Should().BeNull();
        rootApproval.CompletionReference.Should().BeNull();
        rootApproval.Snapshot.Plan.Summary.Should().Be("POST /resources/alpha");

        published.ToString().Should().NotContain(materialReference);
        published.ToString().Should().NotContain(completionReference);
        published.ToString().Should().NotContain("raw-payload-secret");
        connectorState.ApprovalsByActionId.Values.Single().MaterialReference.Ref
            .Should().Be(materialReference);
    }

    private static ConnectorCallModuleState CreateConnectorState(
        string materialReference,
        string completionReference)
    {
        var state = new ConnectorCallModuleState();
        state.ApprovalsByActionId["action-alpha"] = new ConnectorApprovalCoordinationState
        {
            Snapshot = new WorkflowExternalActionApprovalSnapshot
            {
                Plan = new WorkflowExternalActionPlan
                {
                    ActionId = "action-alpha",
                    Summary = "POST /resources/alpha",
                    MaterialDigestSha256 = new string('a', 64),
                    Provenance = new WorkflowExternalActionProvenance
                    {
                        RunId = "run-approval",
                        StepId = "connector-approval",
                    },
                },
                LifecycleStatus = WorkflowExternalActionLifecycleStatus.WaitingApproval,
                ApprovalStatus = WorkflowExternalActionApprovalStatus.Pending,
                ExecutionStatus = WorkflowExternalActionExecutionStatus.NotStarted,
            },
            MaterialReference = new RuntimeSecretReference
            {
                Ref = materialReference,
                Purpose = CredentialSecretPurposes.WorkflowConnectorExternalActionMaterial,
                OwnerRunId = "run-approval",
                OwnerStepId = "connector-approval",
            },
            CompletionReference = new RuntimeSecretReference
            {
                Ref = completionReference,
                Purpose = CredentialSecretPurposes.WorkflowConnectorExternalActionCompletion,
                OwnerRunId = "run-approval",
                OwnerStepId = "connector-approval",
            },
        };
        return state;
    }
}
