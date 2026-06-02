using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Workflow.Application.Abstractions.Schedules;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.Metadata;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.Projectors;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowScheduleProjectionTests
{
    [Fact]
    public async Task CurrentStateProjector_ShouldMapScheduledDispatchStateAndStripAdapterHeaders()
    {
        var observedAt = DateTimeOffset.Parse("2026-05-29T09:15:00+00:00");
        var createdAt = observedAt.AddHours(-2);
        var nextFireAt = observedAt.AddMinutes(45);
        var lastFireAt = observedAt.AddMinutes(-15);
        var olderFireAt = observedAt.AddHours(-1);
        var newerFireAt = observedAt.AddMinutes(-5);
        var dispatcher = new RecordingScheduleWriteDispatcher();
        var projector = new WorkflowScheduleCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-05-29T10:00:00+00:00")));
        var state = new ScheduledDispatchState
        {
            ScheduleId = "schedule-1",
            DisplayName = "Daily report",
            TargetActorId = "target-actor-1",
            CronExpression = "*/15 * * * *",
            Timezone = "UTC",
            Enabled = true,
            CreatedAt = createdAt,
            NextFireAt = nextFireAt,
            LastFireAt = lastFireAt,
            LastTargetActorId = "run-actor-2",
            LastCommandId = "cmd-last",
            LastCorrelationId = "corr-last",
            LastError = "last error",
            FireCount = 2,
            FailureCount = 1,
        };
        state.Headers[WorkflowScheduleAdapterHeaderKeys.WorkflowName] = "workflow-a";
        state.Headers[WorkflowScheduleAdapterHeaderKeys.Prompt] = "run it";
        state.Headers[WorkflowScheduleAdapterHeaderKeys.ScopeId] = "scope-1";
        state.Headers[WorkflowScheduleAdapterHeaderKeys.SourceActorId] = "definition-actor-1";
        state.Headers["caller"] = "kept";
        state.FireRecords["older"] = new ScheduledDispatchFireRecordState
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(olderFireAt),
            CompletedAt = Timestamp.FromDateTimeOffset(olderFireAt.AddSeconds(5)),
            IdempotencyKey = "older",
            TargetActorId = "run-actor-1",
            CommandId = "cmd-1",
            CorrelationId = "corr-1",
            Manual = false,
        };
        state.FireRecords["newer"] = new ScheduledDispatchFireRecordState
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(newerFireAt),
            CompletedAt = Timestamp.FromDateTimeOffset(newerFireAt.AddSeconds(5)),
            IdempotencyKey = "newer",
            TargetActorId = "run-actor-2",
            CommandId = "cmd-2",
            CorrelationId = "corr-2",
            Error = "failed",
            Manual = true,
        };

        await projector.ProjectAsync(
            CreateContext("scheduled-dispatch:schedule-1"),
            WrapCommitted(state, version: 7, eventId: "evt-7", observedAt));

        dispatcher.Upserts.Should().ContainSingle();
        var document = dispatcher.Upserts.Single();
        document.Id.Should().Be("scheduled-dispatch:schedule-1");
        document.ActorId.Should().Be("scheduled-dispatch:schedule-1");
        document.ScheduleId.Should().Be("schedule-1");
        document.DisplayName.Should().Be("Daily report");
        document.WorkflowName.Should().Be("workflow-a");
        document.Prompt.Should().Be("run it");
        document.CronExpression.Should().Be("*/15 * * * *");
        document.Timezone.Should().Be("UTC");
        document.Enabled.Should().BeTrue();
        document.CreatedAt.Should().Be(createdAt);
        document.UpdatedAt.Should().Be(observedAt);
        document.NextFireAt.Should().Be(nextFireAt);
        document.LastFireAt.Should().Be(lastFireAt);
        document.LastRunActorId.Should().Be("run-actor-2");
        document.LastCommandId.Should().Be("cmd-last");
        document.LastCorrelationId.Should().Be("corr-last");
        document.LastError.Should().Be("last error");
        document.FireCount.Should().Be(2);
        document.FailureCount.Should().Be(1);
        document.ScopeId.Should().Be("scope-1");
        document.TargetActorId.Should().Be("target-actor-1");
        document.StateVersion.Should().Be(7);
        document.LastEventId.Should().Be("evt-7");
        document.Headers.Should().ContainSingle().Which.Should().Be(
            new KeyValuePair<string, string>("caller", "kept"));
        document.FireRecords.Select(x => x.IdempotencyKey).Should().Equal("newer", "older");
        document.FireRecords[0].RunActorId.Should().Be("run-actor-2");
        document.FireRecords[0].Manual.Should().BeTrue();
        document.FireRecords[0].Error.Should().Be("failed");
        document.FireRecords[1].ScheduledFireAt.Should().Be(olderFireAt);
    }

    [Fact]
    public async Task CurrentStateProjector_ShouldFallbackBlankValuesAndIgnoreOtherStateEnvelopes()
    {
        var fallbackNow = DateTimeOffset.Parse("2026-05-29T11:00:00+00:00");
        var dispatcher = new RecordingScheduleWriteDispatcher();
        var projector = new WorkflowScheduleCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(fallbackNow));

        await projector.ProjectAsync(
            CreateContext("scheduled-dispatch:fallback"),
            new EventEnvelope { Payload = Any.Pack(new StringValue { Value = "ignored" }) });

        await projector.ProjectAsync(
            CreateContext("scheduled-dispatch:fallback"),
            WrapCommitted(
                new ScheduledDispatchState
                {
                },
                version: 3,
                eventId: null,
                observedAt: null));

        dispatcher.Upserts.Should().ContainSingle();
        var document = dispatcher.Upserts.Single();
        document.ScheduleId.Should().Be("scheduled-dispatch:fallback");
        document.DisplayName.Should().BeEmpty();
        document.WorkflowName.Should().BeEmpty();
        document.Prompt.Should().BeEmpty();
        document.CronExpression.Should().BeEmpty();
        document.Timezone.Should().BeEmpty();
        document.CreatedAt.Should().Be(fallbackNow);
        document.UpdatedAt.Should().Be(fallbackNow);
        document.NextFireAt.Should().BeNull();
        document.LastFireAt.Should().BeNull();
        document.LastRunActorId.Should().BeEmpty();
        document.LastCommandId.Should().BeEmpty();
        document.LastCorrelationId.Should().BeEmpty();
        document.LastError.Should().BeEmpty();
        document.ScopeId.Should().BeEmpty();
        document.TargetActorId.Should().BeEmpty();
        document.LastEventId.Should().BeEmpty();
        document.Headers.Should().BeEmpty();
        document.FireRecords.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryPort_ShouldTrimGetIdAndMapDetailWithRecentFireOrderingAndDefaults()
    {
        var completedAt = DateTimeOffset.Parse("2026-05-29T09:00:00+00:00");
        var reader = new StubScheduleDocumentReader();
        reader.Documents["schedule-1"] = new WorkflowScheduleDocument
        {
            Id = "doc-1",
            ScheduleId = "schedule-1",
            Enabled = true,
            CreatedAt = DateTimeOffset.Parse("2026-05-29T08:00:00+00:00"),
            UpdatedAt = DateTimeOffset.Parse("2026-05-29T08:30:00+00:00"),
            NextFireAt = null,
            LastFireAt = completedAt,
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["caller"] = "kept",
            },
            FireRecords =
            {
                new WorkflowScheduleFireRecordDocument
                {
                    ScheduledFireAt = completedAt.AddMinutes(-1),
                    CompletedAt = completedAt,
                    IdempotencyKey = "older",
                },
                new WorkflowScheduleFireRecordDocument
                {
                    ScheduledFireAt = completedAt.AddMinutes(9),
                    CompletedAt = completedAt.AddMinutes(10),
                    IdempotencyKey = "newer",
                    RunActorId = "run-actor",
                    CommandId = "cmd",
                    CorrelationId = "corr",
                    Error = "boom",
                    Manual = true,
                },
            },
        };
        var port = new WorkflowScheduleQueryPort(reader);

        (await port.GetAsync(" ")).Should().BeNull();
        var detail = await port.GetAsync(" schedule-1 ");

        reader.GetKeys.Should().Equal("schedule-1");
        detail.Should().NotBeNull();
        detail!.Schedule.ScheduleId.Should().Be("schedule-1");
        detail.Schedule.DisplayName.Should().BeEmpty();
        detail.Schedule.WorkflowName.Should().BeEmpty();
        detail.Schedule.CronExpression.Should().BeEmpty();
        detail.Schedule.Timezone.Should().BeEmpty();
        detail.Schedule.LastRunActorId.Should().BeEmpty();
        detail.Schedule.LastCommandId.Should().BeEmpty();
        detail.Schedule.LastCorrelationId.Should().BeEmpty();
        detail.Schedule.LastError.Should().BeEmpty();
        detail.Schedule.Headers.Should().Contain("caller", "kept");
        detail.Schedule.ScopeId.Should().BeEmpty();
        detail.Schedule.ActorId.Should().BeEmpty();
        detail.RecentFires.Select(x => x.IdempotencyKey).Should().Equal("newer", "older");
        detail.RecentFires[0].RunActorId.Should().Be("run-actor");
        detail.RecentFires[0].CommandId.Should().Be("cmd");
        detail.RecentFires[0].CorrelationId.Should().Be("corr");
        detail.RecentFires[0].Error.Should().Be("boom");
        detail.RecentFires[0].Manual.Should().BeTrue();
        detail.RecentFires[1].RunActorId.Should().BeEmpty();
        detail.RecentFires[1].CommandId.Should().BeEmpty();
        detail.RecentFires[1].CorrelationId.Should().BeEmpty();
        detail.RecentFires[1].Error.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryPort_ShouldClampPagingAndMapListResult()
    {
        var reader = new StubScheduleDocumentReader
        {
            QueryResult = new ProjectionDocumentQueryResult<WorkflowScheduleDocument>
            {
                Items =
                [
                    new WorkflowScheduleDocument
                    {
                        ScheduleId = "schedule-1",
                        DisplayName = "Daily",
                        WorkflowName = "workflow",
                        CronExpression = "0 9 * * *",
                        Timezone = "UTC",
                        Enabled = true,
                        CreatedAt = DateTimeOffset.Parse("2026-05-29T08:00:00+00:00"),
                        UpdatedAt = DateTimeOffset.Parse("2026-05-29T08:30:00+00:00"),
                        Headers = new Dictionary<string, string>
                        {
                            ["trace"] = "on",
                        },
                        ScopeId = "scope-1",
                        TargetActorId = "target-actor",
                    },
                ],
                NextCursor = "next",
                TotalCount = 12,
            },
        };
        var port = new WorkflowScheduleQueryPort(reader);

        var result = await port.ListAsync(0, "cursor", includeTotalCount: true);
        await port.ListAsync(500);

        reader.Queries.Should().HaveCount(2);
        reader.Queries[0].Take.Should().Be(1);
        reader.Queries[0].Cursor.Should().Be("cursor");
        reader.Queries[0].IncludeTotalCount.Should().BeTrue();
        reader.Queries[1].Take.Should().Be(200);
        result.NextCursor.Should().Be("next");
        result.TotalCount.Should().Be(12);
        result.Items.Should().ContainSingle();
        var summary = result.Items.Single();
        summary.ScheduleId.Should().Be("schedule-1");
        summary.DisplayName.Should().Be("Daily");
        summary.WorkflowName.Should().Be("workflow");
        summary.CronExpression.Should().Be("0 9 * * *");
        summary.Headers.Should().Contain("trace", "on");
        summary.ScopeId.Should().Be("scope-1");
        summary.ActorId.Should().Be("target-actor");
    }

    [Fact]
    public void WorkflowScheduleReadModelsAndMetadata_ShouldNormalizeNullableTimestampsAndMaps()
    {
        var localTime = new DateTimeOffset(2026, 5, 29, 17, 0, 0, TimeSpan.FromHours(8));
        var document = new WorkflowScheduleDocument();

        document.CreatedAt.Should().Be(default);
        document.UpdatedAt.Should().Be(default);
        document.NextFireAt.Should().BeNull();
        document.LastFireAt.Should().BeNull();

        document.CreatedAt = localTime;
        document.UpdatedAt = localTime.AddMinutes(1);
        document.NextFireAt = localTime.AddMinutes(2);
        document.LastFireAt = localTime.AddMinutes(-2);
        document.Headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["a"] = "b",
        };

        document.CreatedAt.Offset.Should().Be(TimeSpan.Zero);
        document.UpdatedAt.Offset.Should().Be(TimeSpan.Zero);
        document.NextFireAt.Should().Be(localTime.AddMinutes(2).ToUniversalTime());
        document.LastFireAt.Should().Be(localTime.AddMinutes(-2).ToUniversalTime());
        document.Headers.Should().Contain("a", "b");
        document.Headers = null!;
        document.Headers.Should().BeEmpty();
        document.NextFireAt = null;
        document.LastFireAt = null;
        document.NextFireAt.Should().BeNull();
        document.LastFireAt.Should().BeNull();

        var fireRecord = new WorkflowScheduleFireRecordDocument();
        fireRecord.ScheduledFireAt.Should().Be(default);
        fireRecord.CompletedAt.Should().Be(default);
        fireRecord.ScheduledFireAt = localTime;
        fireRecord.CompletedAt = localTime.AddSeconds(1);
        fireRecord.ScheduledFireAt.Offset.Should().Be(TimeSpan.Zero);
        fireRecord.CompletedAt.Offset.Should().Be(TimeSpan.Zero);

        var metadata = new WorkflowScheduleDocumentMetadataProvider().Metadata;
        metadata.IndexName.Should().Be("workflow-schedules");
        metadata.Mappings.Should().Contain("dynamic", true);
        metadata.Settings.Should().BeEmpty();
        metadata.Aliases.Should().BeEmpty();
    }

    private static WorkflowExecutionMaterializationContext CreateContext(string rootActorId) =>
        new()
        {
            RootActorId = rootActorId,
            ProjectionKind = "workflow-schedule",
        };

    private static EventEnvelope WrapCommitted(
        ScheduledDispatchState state,
        long version,
        string? eventId,
        DateTimeOffset? observedAt) =>
        new()
        {
            Id = "outer-envelope",
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId ?? string.Empty,
                    Version = version,
                    Timestamp = observedAt.HasValue ? Timestamp.FromDateTimeOffset(observedAt.Value) : null,
                    EventData = Any.Pack(new ScheduledDispatchConfiguredEvent()),
                },
                StateRoot = Any.Pack(state),
            }),
        };

    private sealed class RecordingScheduleWriteDispatcher : IProjectionWriteDispatcher<WorkflowScheduleDocument>
    {
        public List<WorkflowScheduleDocument> Upserts { get; } = [];
        public List<string> Deletes { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(
            WorkflowScheduleDocument readModel,
            CancellationToken ct = default)
        {
            Upserts.Add(readModel.Clone());
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
        {
            Deletes.Add(id);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }
    }

    private sealed class StubScheduleDocumentReader : IProjectionDocumentReader<WorkflowScheduleDocument, string>
    {
        public Dictionary<string, WorkflowScheduleDocument> Documents { get; } = new(StringComparer.Ordinal);
        public List<string> GetKeys { get; } = [];
        public List<ProjectionDocumentQuery> Queries { get; } = [];
        public ProjectionDocumentQueryResult<WorkflowScheduleDocument> QueryResult { get; set; } =
            ProjectionDocumentQueryResult<WorkflowScheduleDocument>.Empty;

        public Task<WorkflowScheduleDocument?> GetAsync(string key, CancellationToken ct = default)
        {
            GetKeys.Add(key);
            return Task.FromResult(Documents.GetValueOrDefault(key));
        }

        public Task<ProjectionDocumentQueryResult<WorkflowScheduleDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            Queries.Add(query);
            return Task.FromResult(QueryResult);
        }
    }

    private sealed class FixedProjectionClock(DateTimeOffset utcNow) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
