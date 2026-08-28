using System.Reflection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Infrastructure.ActorBacked;
using Aevatar.Studio.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

public sealed class ProjectionNyxIdChatConversationStateQueryPortTests
{
    [Fact]
    public void Constructor_ShouldDependOnlyOnProjectionDocumentReader()
    {
        var constructor = typeof(ProjectionNyxIdChatConversationStateQueryPort)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Should().ContainSingle().Subject;

        constructor.GetParameters().Select(static parameter => parameter.ParameterType)
            .Should().Equal(typeof(IProjectionDocumentReader<
                NyxIdChatConversationCurrentStateDocument,
                string>));
    }

    [Fact]
    public async Task GetAsync_ShouldReturnCurrentSnapshotWhenServerIsNewer()
    {
        var reader = new RecordingReader { Document = BuildDocument(stateVersion: 8) };
        var port = new ProjectionNyxIdChatConversationStateQueryPort(reader);

        var result = await port.GetAsync(new NyxIdChatConversationStateQuery(
            " scope-alpha ",
            " conversation-alpha ",
            AfterStateVersion: 7,
            TurnId: " turn-alpha "));

        result.Status.Should().Be(NyxIdChatConversationStateQueryStatus.Current);
        result.StateVersion.Should().Be(8);
        result.TurnId.Should().Be("turn-alpha");
        result.Snapshot.Should().NotBeNull();
        result.Snapshot!.ActorId.Should().Be("conversation-alpha");
        result.Snapshot.ScopeId.Should().Be("scope-alpha");
        result.Snapshot.ProgressSequence.Should().Be(34);
        result.Snapshot.ActiveTurn!.CommandId.Should().Be("command-alpha");
        result.Snapshot.ActiveTask!.Steps.Should().ContainSingle().Which.Operation!
            .OperationId.Should().Be("operation-alpha");
        result.Snapshot.ActiveTask.SchemaVersion.Should().Be(4);
        result.Snapshot.ActiveTask.ActorId.Should().Be("conversation-alpha");
        result.Snapshot.ActiveTask.PlanId.Should().Be("plan-alpha");
        result.Snapshot.ActiveTask.PlanRevision.Should().Be(2);
        result.Snapshot.ActiveTask.PlanRevisionHistoryStart.Should().Be(1);
        result.Snapshot.ActiveTask.Title.Should().Be("Update GitHub safely");
        result.Snapshot.ActiveTask.PlanRevisions.Should().NotBeNull();
        result.Snapshot.ActiveTask.PlanRevisions!.Select(static revision =>
                (revision.PlanRevision, revision.RevisionCause))
            .Should().Equal((1, "initial"), (2, "scope_resolution"));
        result.Snapshot.ActiveTask.PlanRevisions[1].AddedStepIds.Should()
            .Equal("step-alpha");
        var source = result.Snapshot.ActiveTask.Steps.Single().Source!.Tool!;
        source.ToolName.Should().Be("repository_update");
        source.ServiceId.Should().Be("connected-service-alpha");
        source.ServiceSlug.Should().Be("service-slug-alpha");
        source.ReadinessCapabilityId.Should().Be("readiness-capability-alpha");
        var step = result.Snapshot.ActiveTask.Steps.Single();
        step.AddedBy.Should().Be("replan");
        step.AddedInPlanRevision.Should().Be(2);
        step.CancelledInPlanRevision.Should().Be(0);
        step.DependsOn.Should().Equal("step-plan");
        step.Estimate.Should().BeEquivalentTo(
            new NyxIdChatConversationStepEstimateSnapshot("duration", 20));
        step.Substeps.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new NyxIdChatConversationSubstepSnapshot(
                "substep-alpha",
                "Validate repository",
                "done"));
        result.Snapshot.PendingActions.Should().ContainSingle().Which.Reports
            .Should().ContainSingle().Which.Resource!.UserServiceId.Should()
            .Be("user-service-alpha");
        var actionRequest = result.Snapshot.PendingActions.Single().Request;
        actionRequest.Should().NotBeNull();
        actionRequest!.Params.CatalogService.Should().NotBeNull();
        actionRequest.Params.CatalogService!.ServiceSlug.Should().Be("github");
        actionRequest.Params.CatalogService.RequestedScopes.Should().Equal("repo:read");
        result.Snapshot.RecentActions.Should().ContainSingle().Which.Request!
            .ActionRequestId.Should().Be("action-recent");
        result.Snapshot.PendingInput.Should().NotBeNull();
        result.Snapshot.PendingInput!.RequestId.Should().Be("input-alpha");
        result.Snapshot.PendingInput.Options.Select(static option => option.Label).Should()
            .Equal("Singapore", "Frankfurt");
        result.Snapshot.LatestInputResolution!.RequestId.Should().Be("input-before");
        result.Snapshot.LatestInputResolution.Answer!.Selection!.OptionIds.Should()
            .Equal("option-singapore");
        result.Snapshot.LatestApprovalResolution!.Approved.Should().BeFalse();
        result.Snapshot.TaskStatus.Should().Be("active");
        result.Snapshot.AttentionKind.Should().Be("input");
        result.Snapshot.ActiveStepSummary.Should().Be("Choose a deployment region.");
        result.Snapshot.LatestStepControlResult.Should().NotBeNull();
        result.Snapshot.LatestStepControlResult!.RequestId.Should().Be("retry-alpha");
        result.Snapshot.LatestStepControlResult.Kind.Should().Be("retry");
        result.Snapshot.LatestStepControlResult.StepId.Should().Be("step-alpha");
        result.Snapshot.LatestStepControlResult.OperationGeneration.Should().Be(3);
        result.Snapshot.LatestStepControlResult.ExpectedStateVersion.Should().Be(10);
        result.Snapshot.RecentStepControlResults.Should().ContainSingle().Which.RequestId
            .Should().Be("retry-alpha");
        result.Snapshot.CanaryEffectFault.Should().BeEquivalentTo(
            new NyxIdChatCanaryEffectFaultSnapshot(
                "arm-alpha",
                "forwarded",
                new NyxIdChatCanaryOperationSnapshot(
                    "conversation-alpha",
                    "turn-source",
                    "task-source",
                    "step-source",
                    "operation-source",
                    1),
                new NyxIdChatCanaryOperationSnapshot(
                    "conversation-alpha",
                    "turn-alpha",
                    "task-alpha",
                    "step-alpha",
                    "operation-alpha",
                    1),
                DateTimeOffset.Parse("2026-08-01T12:15:00Z"),
                DateTimeOffset.Parse("2026-08-01T12:00:00Z"),
                DateTimeOffset.Parse("2026-08-01T12:01:00Z"),
                null));
        reader.Keys.Should().ContainSingle("conversation-alpha");
    }

    // Issue #3543: the sealed create-time attachment set must survive the
    // document → snapshot projection so consumers can reconcile what a
    // conversation is actually bound to.
    [Fact]
    public async Task GetAsync_ShouldProjectSealedContextAttachments()
    {
        var document = BuildDocument(stateVersion: 8);
        document.ContextAttachments.Add(new NyxIdChatConversationContextAttachmentDocument
        {
            ArtifactId = "artifact-chart",
            RevisionMode = "PINNED_REVISION",
            PinnedRevisionId = "artifact-chart-revision-2",
        });
        document.ContextAttachments.Add(new NyxIdChatConversationContextAttachmentDocument
        {
            ArtifactId = "artifact-journal",
            RevisionMode = "FOLLOW_CURRENT",
            PinnedRevisionId = string.Empty,
        });
        var port = new ProjectionNyxIdChatConversationStateQueryPort(
            new RecordingReader { Document = document });

        var result = await port.GetAsync(new NyxIdChatConversationStateQuery(
            "scope-alpha",
            "conversation-alpha"));

        result.Status.Should().Be(NyxIdChatConversationStateQueryStatus.Current);
        result.Snapshot!.ContextAttachments.Should().Equal(
            new NyxIdChatContextAttachmentSnapshot(
                "artifact-chart",
                "PINNED_REVISION",
                "artifact-chart-revision-2"),
            new NyxIdChatContextAttachmentSnapshot("artifact-journal", "FOLLOW_CURRENT"));
    }

    [Fact]
    public async Task GetAsync_ShouldOmitContextAttachmentsWhenConversationHasNone()
    {
        var port = new ProjectionNyxIdChatConversationStateQueryPort(
            new RecordingReader { Document = BuildDocument(stateVersion: 8) });

        var result = await port.GetAsync(new NyxIdChatConversationStateQuery(
            "scope-alpha",
            "conversation-alpha"));

        result.Status.Should().Be(NyxIdChatConversationStateQueryStatus.Current);
        result.Snapshot!.ContextAttachments.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_ShouldExposeTypedPostconditionCheck()
    {
        var document = BuildDocument(stateVersion: 8);
        var step = document.ActiveTask.Steps.Single();
        step.Source = new NyxIdChatConversationStepSourceDocument
        {
            Postcondition = new NyxIdChatConversationPostconditionStepSourceDocument
            {
                ActionRequestId = "action-alpha",
                Check = "service.connected",
            },
        };
        var port = new ProjectionNyxIdChatConversationStateQueryPort(
            new RecordingReader { Document = document });

        var result = await port.GetAsync(new NyxIdChatConversationStateQuery(
            "scope-alpha",
            "conversation-alpha"));

        var source = result.Snapshot!.ActiveTask!.Steps.Single().Source!.Postcondition!;
        source.ActionRequestId.Should().Be("action-alpha");
        source.Check.Should().Be("service.connected");
    }

    [Fact]
    public async Task GetAsync_ShouldExposeFlatReloadableKeyActionParameters()
    {
        var document = BuildDocument(stateVersion: 8);
        var keyCreate = document.PendingActions.Single();
        keyCreate.Action = "key.create";
        keyCreate.Request.Action = "key.create";
        keyCreate.Request.Params = new NyxIdChatConversationActionParamsDocument
        {
            KeyCreate = new NyxIdChatConversationKeyCreateDocument
            {
                Name = "agent-alpha",
                Platform = "codex",
                AllowedServiceIds = { "service-github", "service-lark" },
            },
        };
        var keyRotate = document.RecentActions.Single();
        keyRotate.Action = "key.rotate";
        keyRotate.Request.Action = "key.rotate";
        keyRotate.Request.Params = new NyxIdChatConversationActionParamsDocument
        {
            KeyRotate = new NyxIdChatConversationKeyRotateDocument
            {
                KeyId = "key-predecessor",
            },
        };
        var port = new ProjectionNyxIdChatConversationStateQueryPort(
            new RecordingReader { Document = document });

        var result = await port.GetAsync(new NyxIdChatConversationStateQuery(
            "scope-alpha",
            "conversation-alpha"));

        var createParams = result.Snapshot!.PendingActions.Single().Request!.Params;
        createParams.Name.Should().Be("agent-alpha");
        createParams.Platform.Should().Be("codex");
        createParams.AllowedServiceIds.Should().Equal("service-github", "service-lark");
        createParams.KeyId.Should().BeNull();
        var rotateParams = result.Snapshot.RecentActions!.Single().Request!.Params;
        rotateParams.KeyId.Should().Be("key-predecessor");
        rotateParams.Name.Should().BeNull();
        rotateParams.AllowedServiceIds.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_ShouldExposeReloadableServiceAccessReviewParameters()
    {
        var document = BuildDocument(stateVersion: 8);
        var review = document.PendingActions.Single();
        review.Action = "service.access_review";
        review.Request.Action = "service.access_review";
        review.Request.Params = new NyxIdChatConversationActionParamsDocument
        {
            ServiceAccessReview = new NyxIdChatConversationServiceAccessReviewDocument
            {
                UserServiceId = "user-service-alpha",
                ServiceSlug = "api-github",
                ResourceUri = "https://nyx-api.example/s/api-github",
            },
        };
        var port = new ProjectionNyxIdChatConversationStateQueryPort(
            new RecordingReader { Document = document });

        var result = await port.GetAsync(new NyxIdChatConversationStateQuery(
            "scope-alpha",
            "conversation-alpha"));

        var request = result.Snapshot!.PendingActions.Single().Request!;
        request.Action.Should().Be("service.access_review");
        request.Params.ServiceAccessReview.Should().BeEquivalentTo(
            new NyxIdChatServiceAccessReviewSnapshot(
                "user-service-alpha",
                "api-github",
                "https://nyx-api.example/s/api-github"));
    }

    [Fact]
    public async Task GetAsync_ShouldPreserveAvailableActionsMessagePresence()
    {
        var document = BuildDocument(stateVersion: 8);
        var absent = document.ActiveTask.Steps.Single();
        absent.AvailableActions = null;
        var present = absent.Clone();
        present.StepId = "step-present-actions";
        present.Order = 2;
        present.AvailableActions = new NyxIdChatConversationAvailableActionsDocument();
        document.ActiveTask.Steps.Add(present);
        var port = new ProjectionNyxIdChatConversationStateQueryPort(
            new RecordingReader { Document = document });

        var result = await port.GetAsync(new NyxIdChatConversationStateQuery(
            "scope-alpha",
            "conversation-alpha"));

        var steps = result.Snapshot!.ActiveTask!.Steps;
        steps.Single(step => step.StepId == "step-alpha").AvailableActions.Should().BeNull();
        steps.Single(step => step.StepId == "step-present-actions").AvailableActions
            .Should().BeEquivalentTo(new NyxIdChatAvailableActionsSnapshot(false, false, false));
    }

    [Fact]
    public async Task GetAttentionSummariesAsync_ShouldBatchReadActorScopedProjectionTruth()
    {
        var valid = BuildDocument(stateVersion: 23);
        var deleted = BuildDocument(stateVersion: 25);
        deleted.Id = "conversation-deleted";
        deleted.ActorId = "conversation-deleted";
        deleted.ConversationActorId = "conversation-deleted";
        deleted.Deleted = true;
        var foreign = BuildDocument(stateVersion: 24);
        foreign.Id = "conversation-foreign";
        foreign.ActorId = "conversation-foreign";
        foreign.ConversationActorId = "conversation-foreign";
        foreign.ScopeId = "scope-other";
        var reader = new RecordingReader { QueryDocuments = [valid, deleted, foreign] };
        var port = new ProjectionNyxIdChatConversationStateQueryPort(reader);

        var result = await port.GetAttentionSummariesAsync(
            " scope-alpha ",
            [
                " conversation-alpha ",
                "conversation-alpha",
                "conversation-deleted",
                "conversation-missing",
            ]);

        var summary = result.Should().ContainSingle().Which;
        summary.Key.Should().Be("conversation-alpha");
        summary.Value.TaskStatus.Should().Be("active");
        summary.Value.AttentionKind.Should().Be("input");
        summary.Value.AttentionSince.Should().Be(
            DateTimeOffset.Parse("2026-08-01T12:00:00Z"));
        summary.Value.ActiveStepSummary.Should().Be("Choose a deployment region.");
        summary.Value.StateVersion.Should().Be(23);
        reader.Queries.Should().ContainSingle().Which.Should().Match<ProjectionDocumentQuery>(query =>
            query.Take == 3 &&
            query.Filters.Any(filter =>
                filter.FieldPath == nameof(NyxIdChatConversationCurrentStateDocument.ScopeId) &&
                filter.Operator == ProjectionDocumentFilterOperator.Eq) &&
            query.Filters.Any(filter =>
                filter.FieldPath == nameof(NyxIdChatConversationCurrentStateDocument.ConversationActorId) &&
                filter.Operator == ProjectionDocumentFilterOperator.In));
    }

    [Fact]
    public async Task GetAsync_ShouldReturnNotModifiedOnlyForEqualMatchingCursor()
    {
        var port = new ProjectionNyxIdChatConversationStateQueryPort(
            new RecordingReader { Document = BuildDocument(stateVersion: 8) });

        var result = await port.GetAsync(new NyxIdChatConversationStateQuery(
            "scope-alpha",
            "conversation-alpha",
            AfterStateVersion: 8,
            TurnId: "turn-alpha"));

        result.Status.Should().Be(NyxIdChatConversationStateQueryStatus.NotModified);
        result.StateVersion.Should().Be(8);
        result.TurnId.Should().Be("turn-alpha");
        result.Snapshot.Should().BeNull();
    }

    [Theory]
    [InlineData(-1, "turn-alpha", "invalid_state_version")]
    [InlineData(9, "turn-alpha", "future_state_version")]
    [InlineData(8, "turn-other", "turn_mismatch")]
    public async Task GetAsync_ShouldReturnReloadRequiredForUnsafeCursor(
        long afterStateVersion,
        string turnId,
        string reasonCode)
    {
        var port = new ProjectionNyxIdChatConversationStateQueryPort(
            new RecordingReader { Document = BuildDocument(stateVersion: 8) });

        var result = await port.GetAsync(new NyxIdChatConversationStateQuery(
            "scope-alpha",
            "conversation-alpha",
            afterStateVersion,
            turnId));

        result.Status.Should().Be(NyxIdChatConversationStateQueryStatus.ReloadRequired);
        result.ReasonCode.Should().Be(reasonCode);
        result.Snapshot.Should().BeNull();
    }

    [Theory]
    [InlineData("scope-other", "conversation-alpha")]
    [InlineData("scope-alpha", "conversation-other")]
    public async Task GetAsync_ShouldFailClosedForDocumentIdentityMismatch(
        string documentScopeId,
        string documentActorId)
    {
        var document = BuildDocument(stateVersion: 8);
        document.ScopeId = documentScopeId;
        document.ConversationActorId = documentActorId;
        var port = new ProjectionNyxIdChatConversationStateQueryPort(
            new RecordingReader { Document = document });

        var result = await port.GetAsync(new NyxIdChatConversationStateQuery(
            "scope-alpha",
            "conversation-alpha",
            AfterStateVersion: 7,
            TurnId: "turn-alpha"));

        result.Status.Should().Be(NyxIdChatConversationStateQueryStatus.NotFound);
        result.ReasonCode.Should().BeNull();
        result.Snapshot.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_ShouldReturnNotFoundWhenDocumentIsMissing()
    {
        var port = new ProjectionNyxIdChatConversationStateQueryPort(new RecordingReader());

        var result = await port.GetAsync(new NyxIdChatConversationStateQuery(
            "scope-alpha",
            "conversation-missing"));

        result.Status.Should().Be(NyxIdChatConversationStateQueryStatus.NotFound);
        result.Snapshot.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_ShouldReturnNotFoundWhenDocumentIsDeleted()
    {
        var document = BuildDocument(stateVersion: 9);
        document.Deleted = true;
        document.DeletedAt = Timestamp.FromDateTimeOffset(
            DateTimeOffset.Parse("2026-08-12T04:29:00Z"));
        var port = new ProjectionNyxIdChatConversationStateQueryPort(
            new RecordingReader { Document = document });

        var result = await port.GetAsync(new NyxIdChatConversationStateQuery(
            "scope-alpha",
            "conversation-alpha"));

        result.Status.Should().Be(NyxIdChatConversationStateQueryStatus.NotFound);
        result.Snapshot.Should().BeNull();
    }

    private static NyxIdChatConversationCurrentStateDocument BuildDocument(long stateVersion) => new()
    {
        Id = "conversation-alpha",
        ActorId = "conversation-alpha",
        ConversationActorId = "conversation-alpha",
        ScopeId = "scope-alpha",
        StateVersion = stateVersion,
        LastEventId = $"event-alpha-{stateVersion}",
        UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-25T06:20:00Z")),
        ProgressSequence = 34,
        PendingInput = new NyxIdChatConversationPendingInputDocument
        {
            RequestId = "input-alpha",
            TurnId = "turn-alpha",
            TaskId = "task-alpha",
            StepId = "step-alpha",
            Prompt = "Choose a deployment region.",
            AskedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-01T12:00:00Z")),
            Options =
            {
                new NyxIdChatConversationInputOptionDocument { Label = "Singapore" },
                new NyxIdChatConversationInputOptionDocument { Label = "Frankfurt" },
            },
        },
        LatestInputResolution = new NyxIdChatConversationInputResolutionDocument
        {
            RequestId = "input-before",
            ClientRequestId = "client-input-before",
            Outcome = "accepted",
            CommittedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-01T11:55:00Z")),
            Answer = new NyxIdChatConversationInputAnswerDocument
            {
                Selection = new NyxIdChatConversationInputSelectionAnswerDocument
                {
                    OptionIds = { "option-singapore" },
                },
            },
        },
        LatestApprovalResolution = new NyxIdChatConversationApprovalResolutionDocument
        {
            RequestId = "approval-before",
            ClientRequestId = "client-approval-before",
            Outcome = "accepted",
            Approved = false,
            CommittedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-01T11:50:00Z")),
        },
        TaskStatus = "active",
        AttentionKind = "input",
        AttentionSince = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-01T12:00:00Z")),
        ActiveStepSummary = "Choose a deployment region.",
        CanaryEffectFault = new NyxIdChatConversationCanaryEffectFaultDocument
        {
            ArmId = "arm-alpha",
            Status = "forwarded",
            SourceOperation = new NyxIdChatConversationCanaryOperationDocument
            {
                ConversationActorId = "conversation-alpha",
                TurnId = "turn-source",
                TaskId = "task-source",
                StepId = "step-source",
                OperationId = "operation-source",
                OperationGeneration = 1,
            },
            TargetOperation = new NyxIdChatConversationCanaryOperationDocument
            {
                ConversationActorId = "conversation-alpha",
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                StepId = "step-alpha",
                OperationId = "operation-alpha",
                OperationGeneration = 1,
            },
            ExpiresAt = Timestamp.FromDateTimeOffset(
                DateTimeOffset.Parse("2026-08-01T12:15:00Z")),
            ArmedAt = Timestamp.FromDateTimeOffset(
                DateTimeOffset.Parse("2026-08-01T12:00:00Z")),
            ForwardedAt = Timestamp.FromDateTimeOffset(
                DateTimeOffset.Parse("2026-08-01T12:01:00Z")),
        },
        ActiveTurn = new NyxIdChatConversationTurnDocument
        {
            TurnId = "turn-alpha",
            TaskId = "task-alpha",
            CommandId = "command-alpha",
            Status = "active",
        },
        LatestTurn = new NyxIdChatConversationTurnDocument
        {
            TurnId = "turn-alpha",
            TaskId = "task-alpha",
            CommandId = "command-alpha",
            Status = "active",
        },
        ActiveTask = new NyxIdChatConversationTaskDocument
        {
            TaskId = "task-alpha",
            TurnId = "turn-alpha",
            Status = "active",
            ActiveStepId = "step-alpha",
            ActiveOperationId = "operation-alpha",
            SchemaVersion = 4,
            ActorId = "conversation-alpha",
            PlanId = "plan-alpha",
            PlanRevision = 2,
            PlanRevisionHistoryStart = 1,
            Title = "Update GitHub safely",
            PlanRevisions =
            {
                new NyxIdChatConversationPlanRevisionDocument
                {
                    PlanRevision = 1,
                    RevisionCause = "initial",
                    AddedStepIds = { "step-plan" },
                },
                new NyxIdChatConversationPlanRevisionDocument
                {
                    PlanRevision = 2,
                    RevisionCause = "scope_resolution",
                    AddedStepIds = { "step-alpha" },
                },
            },
            Steps =
            {
                new NyxIdChatConversationStepDocument
                {
                    StepId = "step-alpha",
                    Order = 1,
                    Kind = "tool",
                    Status = "running",
                    ExternalEffect = "not_started",
                    AddedBy = "replan",
                    AddedInPlanRevision = 2,
                    DependsOn = { "step-plan" },
                    Estimate = new NyxIdChatConversationStepEstimateDocument
                    {
                        Kind = "duration",
                        Seconds = 20,
                    },
                    Substeps =
                    {
                        new NyxIdChatConversationSubstepDocument
                        {
                            SubstepId = "substep-alpha",
                            Title = "Validate repository",
                            Status = "done",
                        },
                    },
                    Source = new NyxIdChatConversationStepSourceDocument
                    {
                        Tool = new NyxIdChatConversationToolStepSourceDocument
                        {
                            ToolName = "repository_update",
                            ServiceId = "connected-service-alpha",
                            ServiceSlug = "service-slug-alpha",
                            ReadinessCapabilityId = "readiness-capability-alpha",
                        },
                    },
                    Operation = new NyxIdChatConversationOperationDocument
                    {
                        ConversationActorId = "conversation-alpha",
                        TurnId = "turn-alpha",
                        TaskId = "task-alpha",
                        StepId = "step-alpha",
                        OperationId = "operation-alpha",
                        OperationGeneration = 2,
                        Phase = "running",
                    },
                },
            },
        },
        PendingActions =
        {
            new NyxIdChatConversationActionDocument
            {
                ActionRequestId = "action-alpha",
                OriginTurnId = "turn-alpha",
                TaskId = "task-alpha",
                StepId = "step-alpha",
                Action = "service.connect",
                Request = new NyxIdChatConversationActionRequestDocument
                {
                    SchemaVersion = 4,
                    ActorId = "conversation-alpha",
                    OriginTurnId = "turn-alpha",
                    TaskId = "task-alpha",
                    StepId = "step-alpha",
                    ActionRequestId = "action-alpha",
                    Action = "service.connect",
                    Params = new NyxIdChatConversationActionParamsDocument
                    {
                        CatalogService = new NyxIdChatConversationCatalogServiceConnectDocument
                        {
                            ServiceSlug = "github",
                            RequestedScopes = { "repo:read" },
                        },
                    },
                },
                Reports =
                {
                    new NyxIdChatConversationActionReportDocument
                    {
                        ActionRequestId = "action-alpha",
                        OriginTurnId = "turn-alpha",
                        Disposition = "completed",
                        Resource = new NyxIdChatConversationResourceDocument
                        {
                            UserServiceId = "user-service-alpha",
                        },
                    },
                },
            },
        },
        LatestStepControlResult = new NyxIdChatConversationStepControlResultDocument
        {
            Kind = "retry",
            RequestId = "retry-alpha",
            ClientRequestId = "client-retry-alpha",
            TurnId = "turn-alpha",
            TaskId = "task-alpha",
            StepId = "step-alpha",
            ExpectedOperationGeneration = 2,
            OperationGeneration = 3,
            Outcome = "accepted",
            ReasonCode = "NYXID_CHAT_STEP_RETRY_ACCEPTED",
            SafeMessage = "Retry accepted.",
            CommandId = "command-retry-alpha",
            CorrelationId = "correlation-retry-alpha",
            CommittedAt = Timestamp.FromDateTimeOffset(
                DateTimeOffset.Parse("2026-07-25T06:10:00Z")),
            ExpectedStateVersion = 10,
            ScopeId = "scope-alpha",
            ConversationActorId = "conversation-alpha",
        },
        RecentStepControlResults =
        {
            new NyxIdChatConversationStepControlResultDocument
            {
                Kind = "retry",
                RequestId = "retry-alpha",
                ClientRequestId = "client-retry-alpha",
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                StepId = "step-alpha",
                ExpectedOperationGeneration = 2,
                OperationGeneration = 3,
                Outcome = "accepted",
                ReasonCode = "NYXID_CHAT_STEP_RETRY_ACCEPTED",
                SafeMessage = "Retry accepted.",
                CommandId = "command-retry-alpha",
                CorrelationId = "correlation-retry-alpha",
                ExpectedStateVersion = 10,
                ScopeId = "scope-alpha",
                ConversationActorId = "conversation-alpha",
            },
        },
        RecentActions =
        {
            new NyxIdChatConversationActionDocument
            {
                SchemaVersion = 4,
                ActionRequestId = "action-recent",
                OriginTurnId = "turn-alpha",
                TaskId = "task-alpha",
                StepId = "step-alpha",
                Action = "service.connect",
                Request = new NyxIdChatConversationActionRequestDocument
                {
                    SchemaVersion = 4,
                    ActorId = "conversation-alpha",
                    OriginTurnId = "turn-alpha",
                    TaskId = "task-alpha",
                    StepId = "step-alpha",
                    ActionRequestId = "action-recent",
                    Action = "service.connect",
                    Params = new NyxIdChatConversationActionParamsDocument
                    {
                        CatalogService = new NyxIdChatConversationCatalogServiceConnectDocument
                        {
                            ServiceSlug = "github",
                            RequestedScopes = { "repo:read" },
                        },
                    },
                },
            },
        },
    };

    private sealed class RecordingReader
        : IProjectionDocumentReader<NyxIdChatConversationCurrentStateDocument, string>
    {
        public NyxIdChatConversationCurrentStateDocument? Document { get; init; }
        public IReadOnlyList<NyxIdChatConversationCurrentStateDocument> QueryDocuments { get; init; } = [];
        public List<string> Keys { get; } = [];
        public List<ProjectionDocumentQuery> Queries { get; } = [];

        public Task<NyxIdChatConversationCurrentStateDocument?> GetAsync(
            string key,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Keys.Add(key);
            return Task.FromResult(Document?.Clone());
        }

        public Task<ProjectionDocumentQueryResult<NyxIdChatConversationCurrentStateDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Queries.Add(query);
            return Task.FromResult(new ProjectionDocumentQueryResult<NyxIdChatConversationCurrentStateDocument>
            {
                Items = QueryDocuments.Select(static document => document.Clone()).ToArray(),
            });
        }
    }
}
