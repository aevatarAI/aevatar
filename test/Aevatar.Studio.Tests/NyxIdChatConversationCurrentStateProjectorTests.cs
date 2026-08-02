using System.Text;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.NyxidChat;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.Projectors;
using Aevatar.Studio.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

public sealed class NyxIdChatConversationCurrentStateProjectorTests
{
    private const string ActorId = "conversation-alpha";

    [Fact]
    public async Task ProjectAsync_ShouldCopySafeQueryStateAndAuthoritativeVersion()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var projector = new NyxIdChatConversationCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-07-25T06:30:00Z")));
        var state = BuildState();
        state.OwnerSubject = "owner-alpha";

        await projector.ProjectAsync(
            NewContext(),
            WrapCommitted(
                new NyxIdChatOperationProgressedEvent(),
                state,
                version: 17,
                eventId: "event-alpha-17",
                stateEventTimestamp: DateTimeOffset.Parse("2026-07-25T06:15:00Z")));

        var document = dispatcher.Upserts.Should().ContainSingle().Subject;
        document.Id.Should().Be(ActorId);
        document.ActorId.Should().Be(ActorId);
        document.ConversationActorId.Should().Be(ActorId);
        document.ScopeId.Should().Be("scope-alpha");
        document.StateVersion.Should().Be(17);
        document.LastEventId.Should().Be("event-alpha-17");
        document.UpdatedAt.ToDateTimeOffset().Should()
            .Be(DateTimeOffset.Parse("2026-07-25T06:15:00Z"));
        document.ProgressSequence.Should().Be(29);

        document.ActiveTurn.TurnId.Should().Be("turn-alpha");
        document.ActiveTurn.TaskId.Should().Be("task-alpha");
        document.ActiveTurn.CommandId.Should().Be("command-alpha");
        document.ActiveTurn.Status.Should().Be("active");
        document.LatestTurn.TurnId.Should().Be("turn-alpha");
        document.LatestTurn.CommandId.Should().Be("command-alpha");
        document.RecentTerminalTurns.Should().ContainSingle(turn =>
            turn.TurnId == "turn-before" && turn.Status == "failed");

        document.ActiveTask.TaskId.Should().Be("task-alpha");
        document.ActiveTask.Status.Should().Be("active");
        document.ActiveTask.ActiveStepId.Should().Be("step-beta");
        document.ActiveTask.ActiveOperationId.Should().Be("operation-beta");
        document.ActiveTask.Steps.Select(static step => step.StepId)
            .Should().Equal("step-alpha", "step-beta");

        var first = document.ActiveTask.Steps[0];
        first.Order.Should().Be(1);
        first.Kind.Should().Be("llm");
        first.Status.Should().Be("done");
        first.ExternalEffect.Should().Be("not_started");
        first.AvailableActions.Retry.Should().BeFalse();

        var active = document.ActiveTask.Steps[1];
        active.Kind.Should().Be("browser_action");
        active.Status.Should().Be("waiting");
        active.ExternalEffect.Should().Be("not_applied");
        active.ActionRequestId.Should().Be("action-alpha");
        active.AvailableActions.Should().NotBeNull();
        active.AvailableActions.Stop.Should().BeTrue();
        active.Operation.ConversationActorId.Should().Be(ActorId);
        active.Operation.TurnId.Should().Be("turn-alpha");
        active.Operation.TaskId.Should().Be("task-alpha");
        active.Operation.StepId.Should().Be("step-beta");
        active.Operation.OperationId.Should().Be("operation-beta");
        active.Operation.OperationGeneration.Should().Be(3);
        active.Operation.Phase.Should().Be("running");

        document.PendingApproval.ApprovalRequestId.Should().Be("approval-alpha");
        document.PendingApproval.StepId.Should().Be("step-beta");
        document.PendingApproval.Action.Should().Be("delete");
        document.PendingApproval.Target.Should().Be("repository:repo-alpha");
        document.PendingApproval.ActorLabel.Should().Be("Aevatar Assistant");
        document.PendingApproval.Reversibility.Should().Be("irreversible");
        document.PendingApproval.GrantBoundary.Should().Be("within_grant");
        document.TaskStatus.Should().Be("active");
        document.AttentionKind.Should().Be("approval");
        document.AttentionSince.Should().Be(state.Attention.AttentionSince);
        document.ActiveStepSummary.Should().Be("Connect a service.");
        document.ControlFence.Kind.Should().Be("steering");
        document.ControlFence.RequestId.Should().Be("steering-alpha");
        document.ControlFence.Outcome.Should().Be("accepted");
        document.LatestControlResult.RequestId.Should().Be("steering-alpha");
        document.ContinuationAdmission.Kind.Should().Be("steering");
        document.ContinuationAdmission.ContinuationTurnId.Should().Be("turn-beta");
        document.ContinuationAdmission.Status.Should().Be("accepted_for_later");

        var action = document.PendingActions.Should().ContainSingle().Subject;
        action.ActionRequestId.Should().Be("action-alpha");
        action.OriginTurnId.Should().Be("turn-alpha");
        action.TaskId.Should().Be("task-alpha");
        action.StepId.Should().Be("step-beta");
        action.Action.Should().Be("service.connect");
        var report = action.Reports.Should().ContainSingle().Subject;
        report.Disposition.Should().Be("completed");
        report.Resource.UserServiceId.Should().Be("user-service-alpha");
        action.PostconditionResult.Verified.Should().BeFalse();
        action.PostconditionResult.FailureCode.Should().Be("READ_MODEL_STALE");

        var serialized = Encoding.UTF8.GetString(document.ToByteArray());
        serialized.Should().NotContain("prompt-secret-alpha");
        serialized.Should().NotContain("https://user:password@example.com");
        serialized.Should().NotContain("owner-subject-alpha");
        serialized.Should().NotContain("owner-alpha");
        serialized.Should().NotContain("access-token-alpha");
        serialized.Should().NotContain("history-initialization-outbox-sentinel");
        serialized.Should().NotContain("history-reservation-outbox-sentinel");
        serialized.Should().NotContain("history-terminal-outbox-sentinel");
        serialized.Should().NotContain("credential-outbox-sentinel");
        NyxIdChatConversationCurrentStateDocument.Descriptor.Fields.InFieldNumberOrder()
            .Should().NotContain(field =>
                field.Name == "state_root" || field.Name == "owner_subject");
    }

    [Fact]
    public async Task ProjectAsync_ShouldIgnoreNonControllerCommittedState()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var projector = new NyxIdChatConversationCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.UtcNow));

        await projector.ProjectAsync(NewContext(), new EventEnvelope
        {
            Id = "raw-alpha",
            Payload = Any.Pack(new StringValue { Value = "not committed controller state" }),
        });

        dispatcher.Upserts.Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_ShouldMaterializeConversationBeforeFirstTurn()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var projector = new NyxIdChatConversationCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-07-25T06:30:00Z")));
        var state = new NyxIdChatConversationGAgentState
        {
            ConversationActorId = ActorId,
            ScopeId = "scope-alpha",
            ProgressSequence = 0,
        };

        await projector.ProjectAsync(
            NewContext(),
            WrapCommitted(
                new NyxIdChatConversationCreationStartedEvent
                {
                    ActorId = ActorId,
                    ScopeId = "scope-alpha",
                },
                state,
                version: 1,
                eventId: "event-alpha-created",
                stateEventTimestamp: DateTimeOffset.Parse("2026-07-25T06:25:00Z")));

        var document = dispatcher.Upserts.Should().ContainSingle().Subject;
        document.StateVersion.Should().Be(1);
        document.ActiveTurn.Should().BeNull();
        document.LatestTurn.Should().BeNull();
        document.ActiveTask.Should().BeNull();
        document.PendingApproval.Should().BeNull();
        document.PendingActions.Should().BeEmpty();
        document.ControlFence.Should().BeNull();
        document.LatestControlResult.Should().BeNull();
        document.ContinuationAdmission.Should().BeNull();
    }

    [Fact]
    public async Task ProjectAsync_ShouldCopyPendingInputAndLatestResolutionFacts()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var projector = new NyxIdChatConversationCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-08-01T12:05:00Z")));
        var state = BuildState();
        var askedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-01T12:00:00Z"));
        state.PendingApproval = null;
        state.PendingInput = new NyxIdChatPendingInputState
        {
            RequestId = "input-alpha",
            TurnId = "turn-alpha",
            TaskId = "task-alpha",
            StepId = "step-beta",
            Prompt = "Choose a deployment region.",
            AskedAt = askedAt.Clone(),
            AllowFreeText = false,
            MultiSelect = false,
            Options =
            {
                new NyxIdChatInputOption
                {
                    OptionId = "option-singapore",
                    Label = "Singapore",
                    Description = "Use the Singapore region.",
                },
                new NyxIdChatInputOption
                {
                    OptionId = "option-frankfurt",
                    Label = "Frankfurt",
                    Description = "Use the Frankfurt region.",
                },
            },
        };
        state.LatestInputResolution = new NyxIdChatInputResolutionState
        {
            RequestId = "input-before",
            ClientRequestId = "client-input-before",
            Outcome = NyxIdChatNeedsYouResolutionOutcome.Accepted,
            CommittedAt = askedAt.Clone(),
        };
        state.LatestApprovalResolution = new NyxIdChatApprovalResolutionState
        {
            RequestId = "approval-before",
            ClientRequestId = "client-approval-before",
            Outcome = NyxIdChatNeedsYouResolutionOutcome.Accepted,
            Approved = false,
            CommittedAt = askedAt.Clone(),
        };
        state.Attention = new NyxIdChatConversationAttentionState
        {
            TaskStatus = NyxIdChatTaskStatus.Active,
            AttentionKind = NyxIdChatAttentionKind.Input,
            AttentionSince = askedAt.Clone(),
            ActiveStepSummary = "Choose a deployment region.",
        };

        await projector.ProjectAsync(
            NewContext(),
            WrapCommitted(
                new NyxIdChatInputRequestedEvent
                {
                    PendingInput = state.PendingInput.Clone(),
                    State = state.Clone(),
                },
                state,
                version: 23,
                eventId: "event-alpha-input-23",
                stateEventTimestamp: DateTimeOffset.Parse("2026-08-01T12:00:00Z")));

        var document = dispatcher.Upserts.Should().ContainSingle().Subject;
        document.StateVersion.Should().Be(23);
        document.PendingApproval.Should().BeNull();
        document.PendingInput.RequestId.Should().Be("input-alpha");
        document.PendingInput.Prompt.Should().Be("Choose a deployment region.");
        document.PendingInput.Options.Select(static option => option.Label).Should()
            .Equal("Singapore", "Frankfurt");
        document.PendingInput.AskedAt.Should().Be(askedAt);
        document.LatestInputResolution.RequestId.Should().Be("input-before");
        document.LatestInputResolution.Outcome.Should().Be("accepted");
        document.LatestApprovalResolution.RequestId.Should().Be("approval-before");
        document.LatestApprovalResolution.Approved.Should().BeFalse();
        document.AttentionKind.Should().Be("input");
        document.AttentionSince.Should().Be(askedAt);
        document.ActiveStepSummary.Should().Be("Choose a deployment region.");
    }

    [Fact]
    public async Task ProjectAsync_ShouldFailWhenStoreRejectsSameVersionConflict()
    {
        var dispatcher = new RecordingWriteDispatcher
        {
            Result = ProjectionWriteResult.Conflict(),
        };
        var projector = new NyxIdChatConversationCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.UtcNow));

        var act = () => projector.ProjectAsync(
            NewContext(),
            WrapCommitted(
                new NyxIdChatTurnStartedEvent(),
                BuildState(),
                version: 17,
                eventId: "event-alpha-17",
                stateEventTimestamp: DateTimeOffset.UtcNow)).AsTask();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*rejected state version 17*Conflict*");
    }

    [Fact]
    public async Task StandardStore_ShouldEnforceMonotonicOverwriteAndExactDuplicateRules()
    {
        var store = new InMemoryProjectionDocumentStore<
            NyxIdChatConversationCurrentStateDocument,
            string>(static document => document.ActorId);
        var versionSeven = MinimalDocument(7, "event-alpha-7", "turn-alpha");
        var versionEight = MinimalDocument(8, "event-alpha-8", "turn-beta");

        (await store.UpsertAsync(versionSeven)).Disposition.Should()
            .Be(ProjectionWriteDisposition.Applied);
        (await store.UpsertAsync(versionEight)).Disposition.Should()
            .Be(ProjectionWriteDisposition.Applied);
        (await store.UpsertAsync(versionSeven)).Disposition.Should()
            .Be(ProjectionWriteDisposition.Stale);
        (await store.UpsertAsync(versionEight.Clone())).Disposition.Should()
            .Be(ProjectionWriteDisposition.Duplicate);

        var conflict = versionEight.Clone();
        conflict.ActiveTurn.Status = "failed";
        (await store.UpsertAsync(conflict)).Disposition.Should()
            .Be(ProjectionWriteDisposition.Conflict);

        var stored = await store.GetAsync(ActorId);
        stored.Should().NotBeNull();
        stored!.StateVersion.Should().Be(8);
        stored.ActiveTurn.TurnId.Should().Be("turn-beta");
        stored.ActiveTurn.Status.Should().Be("active");
    }

    private static NyxIdChatConversationGAgentState BuildState()
    {
        var now = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-25T06:10:00Z"));
        var state = new NyxIdChatConversationGAgentState
        {
            ConversationActorId = ActorId,
            ScopeId = "scope-alpha",
            ProgressSequence = 29,
            UpdatedAt = now.Clone(),
            ActiveTurn = new NyxIdChatTurnState
            {
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                ClientRequestId = "client-alpha",
                CommandId = "command-alpha",
                Status = NyxIdChatTurnStatus.Active,
                Prompt = "prompt-secret-alpha",
                CreatedAt = now.Clone(),
            },
            LatestTurn = new NyxIdChatTurnState
            {
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                CommandId = "command-alpha",
                Status = NyxIdChatTurnStatus.Active,
                Prompt = "prompt-secret-alpha",
                CreatedAt = now.Clone(),
            },
            ActiveTask = new NyxIdChatTaskState
            {
                TaskId = "task-alpha",
                TurnId = "turn-alpha",
                Status = NyxIdChatTaskStatus.Active,
                ActiveStepId = "step-beta",
                ActiveOperationId = "operation-beta",
                CreatedAt = now.Clone(),
                UpdatedAt = now.Clone(),
            },
            PendingApproval = new NyxIdChatPendingApprovalState
            {
                ApprovalRequestId = "approval-alpha",
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                StepId = "step-beta",
                ToolName = "safe-tool-name",
                ExpiresAt = now.Clone(),
                AskedAt = now.Clone(),
                Presentation = new NyxIdChatApprovalPresentation
                {
                    Action = "delete",
                    Target = "repository:repo-alpha",
                    ActorLabel = "Aevatar Assistant",
                    Reversibility = NyxIdChatApprovalReversibility.Irreversible,
                    GrantBoundary = "within_grant",
                },
            },
            Attention = new NyxIdChatConversationAttentionState
            {
                TaskStatus = NyxIdChatTaskStatus.Active,
                AttentionKind = NyxIdChatAttentionKind.Approval,
                AttentionSince = now.Clone(),
                ActiveStepSummary = "Connect a service.",
            },
            ControlFence = new NyxIdChatControlFenceState
            {
                Kind = NyxIdChatControlKind.Steering,
                RequestId = "steering-alpha",
                ClientRequestId = "client-steering-alpha",
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                OperationGeneration = 3,
                Outcome = NyxIdChatControlOutcome.Accepted,
                ReasonCode = "STEERING_ACCEPTED",
                SafeMessage = "Steering accepted.",
                CommittedAt = now.Clone(),
            },
            LatestControlResult = new NyxIdChatControlFenceState
            {
                Kind = NyxIdChatControlKind.Steering,
                RequestId = "steering-alpha",
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                Outcome = NyxIdChatControlOutcome.Accepted,
                CommittedAt = now.Clone(),
            },
            ContinuationAdmission = new NyxIdChatContinuationAdmissionState
            {
                Kind = NyxIdChatContinuationKind.Steering,
                RequestId = "steering-alpha",
                ClientRequestId = "client-steering-alpha",
                OriginTurnId = "turn-alpha",
                ContinuationTurnId = "turn-beta",
                Status = NyxIdChatContinuationAdmissionStatus.AcceptedForLater,
                ReasonCode = "SAFE_CHECKPOINT_PENDING",
                SafeMessage = "Continuation accepted for later.",
                CommittedAt = now.Clone(),
                Instruction = "owner-subject-alpha access-token-alpha",
            },
            PendingHistoryInitialization = new NyxIdChatHistoryInitializationOutbox
            {
                OperationId = "history-initialization-outbox-sentinel",
                ScopeId = "scope-alpha",
                ConversationId = ActorId,
                ServiceId = ActorId,
                ServiceKind = "nyxid.chat",
                InitialTitle = "credential-outbox-sentinel",
                CreatedAt = now.Clone(),
                Attempt = 3,
            },
            HistoryInitializationOperationId =
                "history-initialization-outbox-sentinel",
            HistoryDeliveryReservation = new NyxIdChatHistoryDeliveryReservationState
            {
                DeliveryId = "history-reservation-outbox-sentinel",
                ScopeId = "scope-alpha",
                ConversationId = ActorId,
                TurnId = "turn-alpha",
                UserText = "credential-outbox-sentinel",
                SourceActorId = ActorId,
                SourceCommandId = "command-history-alpha",
                SourceCorrelationId = "correlation-history-alpha",
                RequestFingerprint = "fingerprint-history-alpha",
                CreateConversationIfMissing = true,
                Dispatched = true,
                DispatchedAt = now.Clone(),
            },
            PendingHistoryTerminal = new NyxIdChatHistoryTerminalOutbox
            {
                DeliveryId = "history-reservation-outbox-sentinel",
                TurnId = "turn-alpha",
                SourceActorId = ActorId,
                SourceCommandId = "command-history-alpha",
                Status = NyxIdChatTurnStatus.Blocked,
                Text = "history-terminal-outbox-sentinel credential-outbox-sentinel",
                ErrorCode = "SAFE_BLOCKED",
                ObservedAt = now.Clone(),
                Attempt = 2,
            },
        };
        state.RecentTerminalTurns.Add(new NyxIdChatTurnSummary
        {
            TurnId = "turn-before",
            TaskId = "task-before",
            Status = NyxIdChatTurnStatus.Failed,
            FailureCode = "SAFE_FAILURE",
            SafeMessage = "Previous turn failed.",
            TerminalAt = now.Clone(),
        });
        state.ActiveTask.Steps.Add(new NyxIdChatTaskStepState
        {
            StepId = "step-beta",
            Order = 2,
            Kind = NyxIdChatStepKind.BrowserAction,
            Status = NyxIdChatStepStatus.Waiting,
            Required = true,
            Description = "Connect a service.",
            ExternalEffect = NyxIdChatEffectEvidence.NotApplied,
            ActionRequestId = "action-alpha",
            AvailableActions = new NyxIdChatAvailableActions { Stop = true },
            UpdatedAt = now.Clone(),
            Operation = new NyxIdChatOperationState
            {
                Key = new NyxIdChatOperationKey
                {
                    ConversationActorId = ActorId,
                    TurnId = "turn-alpha",
                    TaskId = "task-alpha",
                    StepId = "step-beta",
                    OperationId = "operation-beta",
                    OperationGeneration = 3,
                },
                Kind = NyxIdChatStepKind.BrowserAction,
                Phase = NyxIdChatOperationPhase.Running,
                LatestProgressSequence = 4,
                RequestedAt = now.Clone(),
            },
        });
        state.ActiveTask.Steps.Add(new NyxIdChatTaskStepState
        {
            StepId = "step-alpha",
            Order = 1,
            Kind = NyxIdChatStepKind.Llm,
            Status = NyxIdChatStepStatus.Done,
            Required = true,
            Description = "Plan the work.",
            ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
            AvailableActions = new NyxIdChatAvailableActions(),
            UpdatedAt = now.Clone(),
        });
        state.PendingActions.Add(new NyxIdChatActionRequestState
        {
            SchemaVersion = 4,
            RegistryRevision = "nyxid-assistant-actions.v4",
            ConversationActorId = ActorId,
            OriginTurnId = "turn-alpha",
            TaskId = "task-alpha",
            StepId = "step-beta",
            ActionRequestId = "action-alpha",
            Action = NyxIdAssistantActionKind.ServiceConnect,
            Params = new NyxIdAssistantActionParams
            {
                CustomServiceConnect = new NyxIdCustomServiceConnectParams
                {
                    Name = "unsafe-input-must-not-project",
                    EndpointUrl = "https://user:password@example.com",
                },
            },
            RequestedAt = now.Clone(),
            PostconditionResult = new NyxIdChatActionPostconditionResult
            {
                ActionRequestId = "action-alpha",
                Disposition = NyxIdChatActionDisposition.Completed,
                Verified = false,
                FailureCode = "READ_MODEL_STALE",
                SafeMessage = "Awaiting fresh authorization evidence.",
            },
            Reports =
            {
                new NyxIdChatActionReport
                {
                    ActionRequestId = "action-alpha",
                    OriginTurnId = "turn-alpha",
                    Disposition = NyxIdChatActionDisposition.Completed,
                    Resource = new NyxIdChatSafeResourceRef
                    {
                        UserService = new NyxIdChatUserServiceRef
                        {
                            UserServiceId = "user-service-alpha",
                        },
                    },
                    SafeMessage = "Browser reported completion.",
                    ReportedAt = now.Clone(),
                },
            },
        });
        return state;
    }

    private static NyxIdChatConversationCurrentStateDocument MinimalDocument(
        long version,
        string eventId,
        string turnId) => new()
    {
        Id = ActorId,
        ActorId = ActorId,
        ConversationActorId = ActorId,
        ScopeId = "scope-alpha",
        StateVersion = version,
        LastEventId = eventId,
        UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-25T06:20:00Z")),
        ActiveTurn = new NyxIdChatConversationTurnDocument
        {
            TurnId = turnId,
            TaskId = $"task-{turnId}",
            Status = "active",
        },
    };

    private static StudioMaterializationContext NewContext() => new()
    {
        RootActorId = ActorId,
        ProjectionKind = "nyxid-chat-conversation",
    };

    private static EventEnvelope WrapCommitted(
        IMessage payload,
        NyxIdChatConversationGAgentState state,
        long version,
        string eventId,
        DateTimeOffset stateEventTimestamp) => new()
    {
        Id = eventId,
        Timestamp = Timestamp.FromDateTimeOffset(stateEventTimestamp.AddMinutes(-1)),
        Route = EnvelopeRouteSemantics.CreateObserverPublication(ActorId),
        Payload = Any.Pack(new CommittedStateEventPublished
        {
            StateEvent = new StateEvent
            {
                EventId = eventId,
                Version = version,
                EventData = Any.Pack(payload),
                Timestamp = Timestamp.FromDateTimeOffset(stateEventTimestamp),
            },
            StateRoot = Any.Pack(state),
        }),
    };

    private sealed class RecordingWriteDispatcher
        : IProjectionWriteDispatcher<NyxIdChatConversationCurrentStateDocument>
    {
        public List<NyxIdChatConversationCurrentStateDocument> Upserts { get; } = [];
        public ProjectionWriteResult Result { get; init; } = ProjectionWriteResult.Applied();

        public Task<ProjectionWriteResult> UpsertAsync(
            NyxIdChatConversationCurrentStateDocument readModel,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Upserts.Add(readModel);
            return Task.FromResult(Result);
        }

        public Task<ProjectionWriteResult> DeleteAsync(
            string id,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FixedProjectionClock(DateTimeOffset utcNow) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
