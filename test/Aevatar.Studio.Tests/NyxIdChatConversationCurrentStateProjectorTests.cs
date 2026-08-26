using System.Text;
using Aevatar.AI.Abstractions;
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
    public async Task ProjectAsync_ShouldCopySealedContextAttachmentReferencesWithoutBodies()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var projector = new NyxIdChatConversationCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-08-27T04:00:00Z")));
        var state = BuildState();
        state.ContextAttachments = new ConversationContextAttachmentSet
        {
            Attachments =
            {
                new ConversationContextAttachment
                {
                    ArtifactId = "artifact-follow",
                    RevisionMode = ConversationContextAttachmentRevisionMode.FollowCurrent,
                },
                new ConversationContextAttachment
                {
                    ArtifactId = "artifact-pinned",
                    RevisionMode = ConversationContextAttachmentRevisionMode.PinnedRevision,
                    PinnedRevisionId = "revision-7",
                },
            },
        };

        await projector.ProjectAsync(
            NewContext(),
            WrapCommitted(
                new ConversationContextAttachmentsBoundEvent(),
                state,
                version: 32,
                eventId: "event-alpha-32",
                stateEventTimestamp: DateTimeOffset.Parse("2026-08-27T03:59:00Z")));

        var attachments = dispatcher.Upserts.Should().ContainSingle().Which
            .ContextAttachments;
        attachments.Select(static attachment =>
                (attachment.ArtifactId, attachment.RevisionMode, attachment.PinnedRevisionId))
            .Should().Equal(
                ("artifact-follow", "follow_current", string.Empty),
                ("artifact-pinned", "pinned_revision", "revision-7"));
    }

    [Fact]
    public async Task ProjectAsync_ShouldExposeReloadablePendingAndRecentActionRequests()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var projector = new NyxIdChatConversationCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-08-08T04:00:00Z")));
        var state = BuildState();
        var pending = state.PendingActions.Single();
        pending.Params = new NyxIdAssistantActionParams
        {
            CatalogServiceConnect = new NyxIdCatalogServiceConnectParams
            {
                ServiceSlug = "github",
                RequestedScopes = { "repo:read", "issues:write" },
                ViaNodeId = "node-alpha",
                TargetOrgId = "org-alpha",
            },
        };
        var recent = pending.Clone();
        recent.ActionRequestId = "action-recent";
        recent.PostconditionResult = new NyxIdChatActionPostconditionResult
        {
            ActionRequestId = recent.ActionRequestId,
            Disposition = NyxIdChatActionDisposition.Completed,
            Verified = true,
        };
        state.RecentActions.Add(recent);

        await projector.ProjectAsync(
            NewContext(),
            WrapCommitted(
                new NyxIdChatActionRequestedEvent(),
                state,
                version: 18,
                eventId: "event-alpha-18",
                stateEventTimestamp: DateTimeOffset.Parse("2026-08-08T04:00:00Z")));

        var document = dispatcher.Upserts.Should().ContainSingle().Subject;
        var request = document.PendingActions.Should().ContainSingle().Which.Request;
        request.ActorId.Should().Be(ActorId);
        request.Action.Should().Be("service.connect");
        request.Params.CatalogService.ServiceSlug.Should().Be("github");
        request.Params.CatalogService.RequestedScopes.Should().Equal("repo:read", "issues:write");
        request.Params.CatalogService.ViaNodeId.Should().Be("node-alpha");
        request.Params.CatalogService.TargetOrgId.Should().Be("org-alpha");
        document.RecentActions.Should().ContainSingle().Which.Request
            .ActionRequestId.Should().Be("action-recent");
    }

    [Fact]
    public async Task ProjectAsync_ShouldExposeReloadableServiceAccessReviewParameters()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var projector = new NyxIdChatConversationCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-08-16T04:00:00Z")));
        var state = BuildState();
        var pending = state.PendingActions.Single();
        pending.Action = NyxIdAssistantActionKind.ServiceAccessReview;
        pending.Params = new NyxIdAssistantActionParams
        {
            ServiceAccessReview = new NyxIdServiceAccessReviewParams
            {
                UserServiceId = "user-service-alpha",
                ServiceSlug = "api-github",
                ResourceUri = "https://nyx-api.example/s/api-github",
            },
        };

        await projector.ProjectAsync(
            NewContext(),
            WrapCommitted(
                new NyxIdChatActionRequestedEvent(),
                state,
                version: 19,
                eventId: "event-alpha-19",
                stateEventTimestamp: DateTimeOffset.Parse("2026-08-16T04:00:00Z")));

        var request = dispatcher.Upserts.Should().ContainSingle().Which
            .PendingActions.Should().ContainSingle().Which.Request;
        request.Action.Should().Be("service.access_review");
        request.Params.ServiceAccessReview.UserServiceId.Should().Be("user-service-alpha");
        request.Params.ServiceAccessReview.ServiceSlug.Should().Be("api-github");
        request.Params.ServiceAccessReview.ResourceUri.Should()
            .Be("https://nyx-api.example/s/api-github");
    }

    [Fact]
    public async Task ProjectAsync_ShouldHydrateReloadableKeyActionParameters()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var projector = new NyxIdChatConversationCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-08-12T04:00:00Z")));
        var state = BuildState();
        var keyCreate = state.PendingActions.Single();
        keyCreate.Action = NyxIdAssistantActionKind.KeyCreate;
        keyCreate.Params = new NyxIdAssistantActionParams
        {
            KeyCreate = new NyxIdKeyCreateParams
            {
                Name = "agent-alpha",
                Platform = "codex",
                AllowedServiceIds = { "service-github", "service-lark" },
            },
        };
        var keyRotate = keyCreate.Clone();
        keyRotate.ActionRequestId = "action-key-rotate";
        keyRotate.Action = NyxIdAssistantActionKind.KeyRotate;
        keyRotate.Params = new NyxIdAssistantActionParams
        {
            KeyRotate = new NyxIdKeyRotateParams { KeyId = "key-predecessor" },
        };
        state.RecentActions.Add(keyRotate);

        await projector.ProjectAsync(
            NewContext(),
            WrapCommitted(
                new NyxIdChatActionRequestedEvent(),
                state,
                version: 19,
                eventId: "event-alpha-19",
                stateEventTimestamp: DateTimeOffset.Parse("2026-08-12T04:00:00Z")));

        var document = dispatcher.Upserts.Should().ContainSingle().Subject;
        var createParams = document.PendingActions.Should().ContainSingle().Which.Request.Params.KeyCreate;
        createParams.Name.Should().Be("agent-alpha");
        createParams.Platform.Should().Be("codex");
        createParams.AllowedServiceIds.Should().Equal("service-github", "service-lark");
        document.RecentActions.Should().ContainSingle().Which.Request.Params.KeyRotate.KeyId
            .Should().Be("key-predecessor");
    }

    [Fact]
    public async Task ProjectAsync_ShouldCopyAuthoritativeDeletionTombstone()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var projector = new NyxIdChatConversationCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-08-12T04:30:00Z")));
        var state = BuildState();
        state.Deleted = true;
        state.DeletedAt = Timestamp.FromDateTimeOffset(
            DateTimeOffset.Parse("2026-08-12T04:29:00Z"));

        await projector.ProjectAsync(
            NewContext(),
            WrapCommitted(
                new NyxIdChatConversationHistoryDeletedEvent(),
                state,
                version: 20,
                eventId: "event-alpha-20",
                stateEventTimestamp: DateTimeOffset.Parse("2026-08-12T04:30:00Z")));

        var document = dispatcher.Upserts.Should().ContainSingle().Subject;
        document.Deleted.Should().BeTrue();
        document.DeletedAt.ToDateTimeOffset().Should().Be(
            DateTimeOffset.Parse("2026-08-12T04:29:00Z"));
        document.StateVersion.Should().Be(20);
    }

    [Fact]
    public async Task ProjectAsync_ArmedCanaryEffectFault_ShouldExposeSourceOperationWithoutTarget()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var projector = new NyxIdChatConversationCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-07-25T06:30:00Z")));
        var state = BuildState();
        state.CanaryEffectFault = new NyxIdChatCanaryEffectFaultState
        {
            ArmIntent = new NyxIdChatCanaryEffectFaultArmIntent
            {
                ArmId = "arm-alpha",
                ClientRequestId = "client-arm-alpha",
                SourceOperationKey = new NyxIdChatOperationKey
                {
                    ConversationActorId = ActorId,
                    TurnId = "turn-alpha",
                    TaskId = "task-alpha",
                    StepId = "step-alpha",
                    OperationId = "operation-alpha",
                    OperationGeneration = 1,
                },
                ServiceInstanceId = "canary-service-sensitive",
                OwnerSubject = "canary-owner-sensitive",
                ExpiresAt = Timestamp.FromDateTimeOffset(
                    DateTimeOffset.Parse("2026-07-25T06:30:00Z")),
            },
            Status = NyxIdChatCanaryEffectFaultStatus.Armed,
            ArmedAt = Timestamp.FromDateTimeOffset(
                DateTimeOffset.Parse("2026-07-25T06:10:00Z")),
        };

        await projector.ProjectAsync(
            NewContext(),
            WrapCommitted(
                new NyxIdChatCanaryEffectFaultArmedCommittedEvent(),
                state,
                version: 16,
                eventId: "event-alpha-16",
                stateEventTimestamp: DateTimeOffset.Parse("2026-07-25T06:10:00Z")));

        var fault = dispatcher.Upserts.Should().ContainSingle().Subject.CanaryEffectFault;
        fault.ArmId.Should().Be("arm-alpha");
        fault.Status.Should().Be("armed");
        fault.SourceOperation.OperationId.Should().Be("operation-alpha");
        fault.SourceOperation.OperationGeneration.Should().Be(1);
        fault.TargetOperation.Should().BeNull();
    }

    [Fact]
    public async Task ProjectAsync_ShouldCopySafeQueryStateAndAuthoritativeVersion()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var projector = new NyxIdChatConversationCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-07-25T06:30:00Z")));
        var state = BuildState();
        state.OwnerSubject = "owner-alpha";
        state.CanaryEffectFault = new NyxIdChatCanaryEffectFaultState
        {
            ArmIntent = new NyxIdChatCanaryEffectFaultArmIntent
            {
                ArmId = "arm-alpha",
                ClientRequestId = "client-arm-alpha",
                SourceOperationKey = new NyxIdChatOperationKey
                {
                    ConversationActorId = ActorId,
                    TurnId = "turn-alpha",
                    TaskId = "task-alpha",
                    StepId = "step-alpha",
                    OperationId = "operation-alpha",
                    OperationGeneration = 1,
                },
                ServiceInstanceId = "canary-service-sensitive",
                OwnerSubject = "canary-owner-sensitive",
                ExpiresAt = Timestamp.FromDateTimeOffset(
                    DateTimeOffset.Parse("2026-07-25T06:30:00Z")),
            },
            Directive = new NyxIdChatCanaryEffectFaultDirective
            {
                ArmId = "arm-alpha",
                ClientRequestId = "client-arm-alpha",
                Key = new NyxIdChatOperationKey
                {
                    ConversationActorId = ActorId,
                    TurnId = "turn-alpha",
                    TaskId = "task-alpha",
                    StepId = "step-beta",
                    OperationId = "operation-beta",
                    OperationGeneration = 1,
                },
                ServiceInstanceId = "canary-service-sensitive",
                CatalogDigest = $"sha256:{new string('f', 64)}",
                OwnerSubject = "canary-owner-sensitive",
                ExpiresAt = Timestamp.FromDateTimeOffset(
                    DateTimeOffset.Parse("2026-07-25T06:30:00Z")),
            },
            Status = NyxIdChatCanaryEffectFaultStatus.Forwarded,
            ArmedAt = Timestamp.FromDateTimeOffset(
                DateTimeOffset.Parse("2026-07-25T06:10:00Z")),
            ForwardedAt = Timestamp.FromDateTimeOffset(
                DateTimeOffset.Parse("2026-07-25T06:15:00Z")),
        };

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
        document.LatestStepControlResult.Kind.Should().Be("retry");
        document.LatestStepControlResult.RequestId.Should().Be("retry-alpha");
        document.LatestStepControlResult.StepId.Should().Be("step-beta");
        document.LatestStepControlResult.ExpectedOperationGeneration.Should().Be(2);
        document.LatestStepControlResult.OperationGeneration.Should().Be(3);
        document.LatestStepControlResult.Outcome.Should().Be("accepted");
        document.LatestStepControlResult.ExpectedStateVersion.Should().Be(16);
        document.RecentStepControlResults.Should().ContainSingle().Which.RequestId
            .Should().Be("retry-alpha");
        document.CanaryEffectFault.ArmId.Should().Be("arm-alpha");
        document.CanaryEffectFault.Status.Should().Be("forwarded");
        document.CanaryEffectFault.SourceOperation.OperationId.Should().Be("operation-alpha");
        document.CanaryEffectFault.SourceOperation.OperationGeneration.Should().Be(1);
        document.CanaryEffectFault.TargetOperation.OperationId.Should().Be("operation-beta");
        document.CanaryEffectFault.TargetOperation.OperationGeneration.Should().Be(1);
        document.CanaryEffectFault.ArmedAt.ToDateTimeOffset().Should().Be(
            DateTimeOffset.Parse("2026-07-25T06:10:00Z"));
        document.CanaryEffectFault.ForwardedAt.ToDateTimeOffset().Should().Be(
            DateTimeOffset.Parse("2026-07-25T06:15:00Z"));
        document.CanaryEffectFault.ConsumedAt.Should().BeNull();

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
        document.ActiveTask.SchemaVersion.Should().Be(4);
        document.ActiveTask.ActorId.Should().Be(ActorId);
        document.ActiveTask.PlanId.Should().Be("plan-alpha");
        document.ActiveTask.PlanRevision.Should().Be(3);
        document.ActiveTask.PlanRevisionHistoryStart.Should().Be(1);
        document.ActiveTask.Title.Should().Be("Connect GitHub safely");
        document.ActiveTask.PlanRevisions.Select(static revision =>
                (revision.PlanRevision, revision.RevisionCause))
            .Should().Equal(
                (1, "initial"),
                (2, "scope_resolution"),
                (3, "failure_recovery"));
        document.ActiveTask.PlanRevisions[1].AddedStepIds.Should().Equal("step-beta");
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
        active.Operation.LastProgressAt.Should().Be(state.ActiveTask.Steps[0].Operation.LastProgressAt);
        active.Operation.StalledAt.Should().Be(state.ActiveTask.Steps[0].Operation.StalledAt);
        active.AddedBy.Should().Be("replan");
        active.AddedInPlanRevision.Should().Be(2);
        active.CancelledInPlanRevision.Should().Be(0);
        active.DependsOn.Should().Equal("step-alpha");
        active.Estimate.Kind.Should().Be("duration");
        active.Estimate.Seconds.Should().Be(45);
        active.Substeps.Should().ContainSingle().Which.Should()
            .BeEquivalentTo(new NyxIdChatConversationSubstepDocument
            {
                SubstepId = "substep-beta",
                Title = "Wait for NyxID",
                Status = "running",
            });

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

        var unsafePendingAction = document.PendingActions.Should().ContainSingle().Which;
        unsafePendingAction.Action.Should().Be("service.connect");
        unsafePendingAction.Request.Should().BeNull();

        var serialized = Encoding.UTF8.GetString(document.ToByteArray());
        serialized.Should().NotContain("prompt-secret-alpha");
        serialized.Should().NotContain("https://user:password@example.com");
        serialized.Should().NotContain("owner-subject-alpha");
        serialized.Should().NotContain("owner-alpha");
        serialized.Should().NotContain("canary-owner-sensitive");
        serialized.Should().NotContain("canary-service-sensitive");
        serialized.Should().NotContain(new string('f', 64));
        serialized.Should().NotContain("access-token-alpha");
        serialized.Should().NotContain("history-initialization-outbox-sentinel");
        serialized.Should().NotContain("history-reservation-outbox-sentinel");
        serialized.Should().NotContain("history-terminal-outbox-sentinel");
        serialized.Should().NotContain("credential-outbox-sentinel");
        NyxIdChatConversationCurrentStateDocument.Descriptor.Fields.InFieldNumberOrder()
            .Should().NotContain(field =>
                field.Name == "state_root" || field.Name == "owner_subject");
        var canaryFieldNames = NyxIdChatConversationCanaryEffectFaultDocument.Descriptor.Fields
            .InFieldNumberOrder()
            .Select(static field => field.Name)
            .ToArray();
        canaryFieldNames.Should().NotContain("owner_subject");
        canaryFieldNames.Should().NotContain("service_instance_id");
        canaryFieldNames.Should().NotContain("catalog_digest");
    }

    [Fact]
    public async Task ProjectAsync_ProgressAfterStall_ShouldExposeClearedCurrentAttention()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var projector = new NyxIdChatConversationCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-07-25T06:30:00Z")));
        var state = BuildState();
        var progressedAt = Timestamp.FromDateTimeOffset(
            DateTimeOffset.Parse("2026-07-25T06:12:30Z"));
        var active = state.ActiveTask.Steps.Single(step => step.StepId == "step-beta");
        state.PendingApproval = null;
        active.Status = NyxIdChatStepStatus.Running;
        active.Operation.StalledAt = null;
        active.Operation.LastProgressAt = progressedAt.Clone();
        active.UpdatedAt = progressedAt.Clone();
        state.Attention = new NyxIdChatConversationAttentionState
        {
            TaskStatus = NyxIdChatTaskStatus.Active,
            AttentionKind = NyxIdChatAttentionKind.None,
            ActiveStepSummary = active.Description,
        };

        await projector.ProjectAsync(
            NewContext(),
            WrapCommitted(
                new NyxIdChatOperationProgressedEvent { State = state.Clone() },
                state,
                version: 18,
                eventId: "event-alpha-18",
                stateEventTimestamp: progressedAt.ToDateTimeOffset()));

        var document = dispatcher.Upserts.Should().ContainSingle().Subject;
        document.AttentionKind.Should().Be("none");
        document.AttentionSince.Should().BeNull();
        var current = document.ActiveTask.Steps.Single(step => step.StepId == "step-beta");
        current.Status.Should().Be("running");
        current.UpdatedAt.Should().Be(progressedAt);
        current.Operation.LastProgressAt.Should().Be(progressedAt);
        current.Operation.StalledAt.Should().BeNull();
    }

    [Fact]
    public async Task ProjectAsync_ShouldCopyAuthoritativeToolRecoverySourceWithoutInference()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var projector = new NyxIdChatConversationCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-08-05T10:00:00Z")));
        var state = BuildState();
        var step = state.ActiveTask.Steps.Single(item => item.StepId == "step-alpha");
        step.Source = new NyxIdChatStepSource
        {
            Tool = new NyxIdChatToolStepSource
            {
                ToolName = "repository_update",
                ServiceId = "connected-service-alpha",
                ServiceSlug = "service-slug-alpha",
                ReadinessCapabilityId = "readiness-capability-alpha",
                ProviderResourceId = "repository-alpha",
            },
        };

        await projector.ProjectAsync(
            NewContext(),
            WrapCommitted(
                new NyxIdChatOperationReconciledEvent(),
                state,
                version: 30,
                eventId: "event-alpha-30",
                stateEventTimestamp: DateTimeOffset.Parse("2026-08-05T09:59:00Z")));

        var source = dispatcher.Upserts.Should().ContainSingle().Which.ActiveTask.Steps
            .Single(item => item.StepId == "step-alpha").Source.Tool;
        source.ToolName.Should().Be("repository_update");
        source.ServiceId.Should().Be("connected-service-alpha");
        source.ServiceSlug.Should().Be("service-slug-alpha");
        source.ReadinessCapabilityId.Should().Be("readiness-capability-alpha");
        source.ProviderResourceId.Should().Be("repository-alpha");
    }

    [Fact]
    public async Task ProjectAsync_ShouldCopyTypedPostconditionCheck()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var projector = new NyxIdChatConversationCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-08-05T10:00:00Z")));
        var state = BuildState();
        var step = state.ActiveTask.Steps.Single(item => item.StepId == "step-alpha");
        step.Source = new NyxIdChatStepSource
        {
            Postcondition = new NyxIdChatPostconditionStepSource
            {
                ActionRequestId = "action-alpha",
                Check = "service.connected",
                ProviderResourceId = "connected-service-resource-alpha",
            },
        };

        await projector.ProjectAsync(
            NewContext(),
            WrapCommitted(
                new NyxIdChatOperationReconciledEvent(),
                state,
                version: 30,
                eventId: "event-alpha-30",
                stateEventTimestamp: DateTimeOffset.Parse("2026-08-05T09:59:00Z")));

        var source = dispatcher.Upserts.Should().ContainSingle().Which.ActiveTask.Steps
            .Single(item => item.StepId == "step-alpha").Source.Postcondition;
        source.ActionRequestId.Should().Be("action-alpha");
        source.Check.Should().Be("service.connected");
        source.ProviderResourceId.Should().Be("connected-service-resource-alpha");
    }

    [Fact]
    public async Task ProjectAsync_ShouldPreservePresentEmptyOperationMessage()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var projector = new NyxIdChatConversationCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-08-07T12:30:00Z")));
        var state = BuildState();
        state.ActiveTask.Steps.Single(step => step.StepId == "step-alpha").Operation =
            new NyxIdChatOperationState();

        await projector.ProjectAsync(
            NewContext(),
            WrapCommitted(
                new NyxIdChatOperationReconciledEvent(),
                state,
                version: 31,
                eventId: "event-alpha-31",
                stateEventTimestamp: DateTimeOffset.Parse("2026-08-07T12:29:00Z")));

        var operation = dispatcher.Upserts.Should().ContainSingle().Which.ActiveTask.Steps
            .Single(step => step.StepId == "step-alpha").Operation;
        operation.Should().NotBeNull();
        operation.CalculateSize().Should().Be(0);
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
            Answer = new NyxIdChatInputAnswer
            {
                Selection = new NyxIdChatInputSelectionAnswer
                {
                    OptionIds = { "option-singapore" },
                },
            },
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
        document.LatestInputResolution.Answer.Selection.OptionIds.Should()
            .Equal("option-singapore");
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
                SchemaVersion = 4,
                ActorId = ActorId,
                PlanId = "plan-alpha",
                PlanRevision = 3,
                PlanRevisionHistoryStart = 1,
                Title = "Connect GitHub safely",
                PlanRevisions =
                {
                    new NyxIdChatPlanRevisionRecord
                    {
                        PlanRevision = 1,
                        RevisionCause = NyxIdChatPlanRevisionCause.Initial,
                        CommittedAt = now.Clone(),
                        AddedStepIds = { "step-alpha" },
                    },
                    new NyxIdChatPlanRevisionRecord
                    {
                        PlanRevision = 2,
                        RevisionCause = NyxIdChatPlanRevisionCause.ScopeResolution,
                        CommittedAt = now.Clone(),
                        AddedStepIds = { "step-beta" },
                    },
                    new NyxIdChatPlanRevisionRecord
                    {
                        PlanRevision = 3,
                        RevisionCause = NyxIdChatPlanRevisionCause.FailureRecovery,
                        CommittedAt = now.Clone(),
                    },
                },
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
            LatestStepControlResult = new NyxIdChatStepControlResultState
            {
                Kind = NyxIdChatStepControlKind.Retry,
                RequestId = "retry-alpha",
                ClientRequestId = "client-retry-alpha",
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                StepId = "step-beta",
                ExpectedOperationGeneration = 2,
                OperationGeneration = 3,
                Outcome = NyxIdChatTransitionOutcome.Accepted,
                ReasonCode = "NYXID_CHAT_STEP_RETRY_ACCEPTED",
                SafeMessage = "Retry accepted.",
                CommandId = "command-retry-alpha",
                CorrelationId = "correlation-retry-alpha",
                CommittedAt = now.Clone(),
                ExpectedStateVersion = 16,
                ScopeId = "scope-alpha",
                ConversationActorId = ActorId,
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
        state.RecentStepControlResults.Add(state.LatestStepControlResult.Clone());
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
            AddedBy = NyxIdChatStepAddedBy.Replan,
            AddedInPlanRevision = 2,
            DependsOn = { "step-alpha" },
            Estimate = new NyxIdChatStepEstimate
            {
                Kind = NyxIdChatStepEstimateKind.Duration,
                Seconds = 45,
            },
            Substeps =
            {
                new NyxIdChatSubstepState
                {
                    SubstepId = "substep-beta",
                    Title = "Wait for NyxID",
                    Status = NyxIdChatSubstepStatus.Running,
                },
            },
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
                LastProgressAt = now.Clone(),
                StalledAt = now.Clone(),
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
            AddedBy = NyxIdChatStepAddedBy.Initial,
            AddedInPlanRevision = 1,
            AvailableActions = new NyxIdChatAvailableActions(),
            UpdatedAt = now.Clone(),
        });
        state.PendingActions.Add(new NyxIdChatActionRequestState
        {
            SchemaVersion = 4,
            RegistryRevision = "nyxid-assistant-actions.v5",
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
