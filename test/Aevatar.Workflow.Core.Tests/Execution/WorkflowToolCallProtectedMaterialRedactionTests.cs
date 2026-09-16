using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Execution;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Core.Tests.Execution;

#pragma warning disable CS0612 // Redaction coverage intentionally populates and inspects legacy fields.
public sealed class WorkflowToolCallProtectedMaterialRedactionTests
{
    [Fact]
    public async Task BeforePublishAsync_ShouldRemoveToolMaterialHandlesAndLegacyPayloads()
    {
        const string materialHandle = "runtime-secret://tool-material-alpha";
        const string secretMarker = "tool-material-secret-marker";
        var toolCallState = CreateToolCallState(materialHandle, secretMarker);
        var published = new CommittedStateEventPublished
        {
            StateEvent = new StateEvent
            {
                EventId = "evt-tool-call-material",
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                Version = 11,
                AgentId = "run-alpha",
                EventType = nameof(WorkflowExecutionStateUpsertedEvent),
                EventData = Any.Pack(new WorkflowExecutionStateUpsertedEvent
                {
                    ScopeKey = "tool_call",
                    State = Any.Pack(toolCallState),
                }),
            },
            StateRoot = Any.Pack(new WorkflowRunState
            {
                RunId = "run-alpha",
                ExecutionStates =
                {
                    ["tool_call"] = Any.Pack(toolCallState),
                },
            }),
        };
        var hook = new WorkflowRunCommittedStateRedactionHook();

        await hook.BeforePublishAsync(new CommittedStatePublicationContext
        {
            ActorId = "run-alpha",
            ActorType = typeof(WorkflowRunGAgent),
            Published = published,
        }, CancellationToken.None);

        var upserted = published.StateEvent.EventData.Unpack<WorkflowExecutionStateUpsertedEvent>();
        AssertRedacted(upserted.State.Unpack<ToolCallModuleState>());

        var stateRoot = published.StateRoot.Unpack<WorkflowRunState>();
        AssertRedacted(stateRoot.ExecutionStates["tool_call"].Unpack<ToolCallModuleState>());

        published.ToString().Should().NotContain(materialHandle);
        published.ToString().Should().NotContain(secretMarker);
        toolCallState.PendingApprovals["approval-alpha"].ProtectedMaterialReference.Ref
            .Should().Be(materialHandle);
        toolCallState.PendingExecutions["execution-alpha"].ArgumentsJson
            .Should().Contain(secretMarker);
        toolCallState.Completions.Should().ContainSingle()
            .Which.ProtectedMaterialReference.Ref.Should().Be(materialHandle);
    }

    private static void AssertRedacted(ToolCallModuleState state)
    {
        var approval = state.PendingApprovals["approval-alpha"];
        approval.ProtectedMaterialReference.Should().BeNull();
        approval.ProtectedMaterialDigestSha256.Should().BeEmpty();
        approval.ExecutionPhase.Should().Be(WorkflowToolCallExecutionPhase.ApprovalPending);
        approval.ArgumentsJson.Should().BeEmpty();
        approval.Input.Should().BeEmpty();
        approval.InputFileRefs.Should().BeEmpty();
        approval.IdempotencyKey.Should().BeEmpty();
        approval.ExternalInvocation.Should().BeNull();
        approval.DisplayName.Should().BeEmpty();

        var execution = state.PendingExecutions["execution-alpha"];
        execution.ProtectedMaterialReference.Should().BeNull();
        execution.ProtectedMaterialDigestSha256.Should().BeEmpty();
        execution.ExecutionPhase.Should().Be(WorkflowToolCallExecutionPhase.ExecutionPending);
        execution.ArgumentsJson.Should().BeEmpty();
        execution.InputFileRefs.Should().BeEmpty();
        execution.IdempotencyKey.Should().BeEmpty();
        execution.ExternalInvocation.Should().BeNull();
        execution.DisplayName.Should().BeEmpty();

        state.Completions.Should().ContainSingle()
            .Which.ProtectedMaterialReference.Should().BeNull();
    }

    private static ToolCallModuleState CreateToolCallState(string materialHandle, string secretMarker)
    {
        var state = new ToolCallModuleState();
        var approval = new PendingToolCallApprovalState
        {
            RunId = "run-alpha",
            StepId = "approve-tool",
            ExecutionId = "execution-alpha",
            ToolName = "tool-alpha",
            ToolCallId = "call-alpha",
            ApprovalRequestId = "approval-alpha",
            ArgumentsJson = $"{{\"token\":\"{secretMarker}\"}}",
            Input = secretMarker,
            IdempotencyKey = secretMarker,
            ExternalInvocation = new ExternalToolInvocationSpec { CallSiteId = secretMarker },
            DisplayName = secretMarker,
            ProtectedMaterialReference = CreateReference(materialHandle, "approve-tool"),
            ProtectedMaterialDigestSha256 = new string('a', 64),
            ExecutionPhase = WorkflowToolCallExecutionPhase.ApprovalPending,
        };
        approval.InputFileRefs.Add(new WorkflowFileRef { FileId = secretMarker });
        state.PendingApprovals["approval-alpha"] = approval;

        var execution = new PendingToolCallExecutionState
        {
            RunId = "run-alpha",
            StepId = "execute-tool",
            ExecutionId = "execution-alpha",
            ToolName = "tool-alpha",
            CallId = "call-alpha",
            ArgumentsJson = $"{{\"token\":\"{secretMarker}\"}}",
            IdempotencyKey = secretMarker,
            ExternalInvocation = new ExternalToolInvocationSpec { CallSiteId = secretMarker },
            DisplayName = secretMarker,
            ProtectedMaterialReference = CreateReference(materialHandle, "execute-tool"),
            ProtectedMaterialDigestSha256 = new string('b', 64),
            ExecutionPhase = WorkflowToolCallExecutionPhase.ExecutionPending,
        };
        execution.InputFileRefs.Add(new WorkflowFileRef { FileId = secretMarker });
        state.PendingExecutions["execution-alpha"] = execution;
        state.Completions.Add(new WorkflowToolCallCompletionOutboxEntry
        {
            RunId = "run-alpha",
            StepId = "complete-tool",
            ExecutionId = "execution-complete-alpha",
            CallId = "call-complete-alpha",
            ProtectedMaterialReference = CreateReference(materialHandle, "complete-tool"),
        });
        return state;
    }

    private static RuntimeSecretReference CreateReference(string materialHandle, string stepId) =>
        new()
        {
            Ref = materialHandle,
            Purpose = CredentialSecretPurposes.WorkflowToolCallProtectedMaterial,
            OwnerRunId = "run-alpha",
            OwnerStepId = stepId,
        };
}
#pragma warning restore CS0612
