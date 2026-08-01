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
        result.Snapshot.PendingActions.Should().ContainSingle().Which.Reports
            .Should().ContainSingle().Which.Resource!.UserServiceId.Should()
            .Be("user-service-alpha");
        result.Snapshot.PendingInput.Should().NotBeNull();
        result.Snapshot.PendingInput!.RequestId.Should().Be("input-alpha");
        result.Snapshot.PendingInput.Options.Select(static option => option.Label).Should()
            .Equal("Singapore", "Frankfurt");
        result.Snapshot.LatestInputResolution!.RequestId.Should().Be("input-before");
        result.Snapshot.LatestApprovalResolution!.Approved.Should().BeFalse();
        result.Snapshot.TaskStatus.Should().Be("active");
        result.Snapshot.AttentionKind.Should().Be("input");
        result.Snapshot.ActiveStepSummary.Should().Be("Choose a deployment region.");
        reader.Keys.Should().ContainSingle("conversation-alpha");
    }

    [Fact]
    public async Task GetAttentionSummariesAsync_ShouldBatchReadActorScopedProjectionTruth()
    {
        var valid = BuildDocument(stateVersion: 23);
        var foreign = BuildDocument(stateVersion: 24);
        foreign.Id = "conversation-foreign";
        foreign.ActorId = "conversation-foreign";
        foreign.ConversationActorId = "conversation-foreign";
        foreign.ScopeId = "scope-other";
        var reader = new RecordingReader { QueryDocuments = [valid, foreign] };
        var port = new ProjectionNyxIdChatConversationStateQueryPort(reader);

        var result = await port.GetAttentionSummariesAsync(
            " scope-alpha ",
            [" conversation-alpha ", "conversation-alpha", "conversation-missing"]);

        var summary = result.Should().ContainSingle().Which;
        summary.Key.Should().Be("conversation-alpha");
        summary.Value.TaskStatus.Should().Be("active");
        summary.Value.AttentionKind.Should().Be("input");
        summary.Value.AttentionSince.Should().Be(
            DateTimeOffset.Parse("2026-08-01T12:00:00Z"));
        summary.Value.ActiveStepSummary.Should().Be("Choose a deployment region.");
        summary.Value.StateVersion.Should().Be(23);
        reader.Queries.Should().ContainSingle().Which.Should().Match<ProjectionDocumentQuery>(query =>
            query.Take == 2 &&
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
    [InlineData("scope-other", "conversation-alpha", "scope_mismatch")]
    [InlineData("scope-alpha", "conversation-other", "conversation_mismatch")]
    public async Task GetAsync_ShouldReturnReloadRequiredForDocumentIdentityMismatch(
        string documentScopeId,
        string documentActorId,
        string reasonCode)
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

        result.Status.Should().Be(NyxIdChatConversationStateQueryStatus.ReloadRequired);
        result.ReasonCode.Should().Be(reasonCode);
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
            Steps =
            {
                new NyxIdChatConversationStepDocument
                {
                    StepId = "step-alpha",
                    Order = 1,
                    Kind = "tool",
                    Status = "running",
                    ExternalEffect = "not_started",
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
