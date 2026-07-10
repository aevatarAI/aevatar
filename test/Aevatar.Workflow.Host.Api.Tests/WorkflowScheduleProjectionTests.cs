using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Core.Schedules;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.Metadata;
using Aevatar.GAgentService.Projection.Projectors;
using Aevatar.GAgentService.Projection.Queries;
using Aevatar.GAgentService.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowScheduleProjectionTests
{
    [Fact]
    public async Task CurrentStateProjector_ShouldMapScheduledDispatchStateAndServiceInvocationTarget()
    {
        var observedAt = DateTimeOffset.Parse("2026-05-29T09:15:00+00:00");
        var createdAt = observedAt.AddHours(-2);
        var nextFireAt = observedAt.AddMinutes(45);
        var lastFireAt = observedAt.AddMinutes(-15);
        var olderFireAt = observedAt.AddHours(-1);
        var newerFireAt = observedAt.AddMinutes(-5);
        var dispatcher = new RecordingScheduleWriteDispatcher();
        var projector = new ScheduledDispatchCurrentStateProjector(
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
            Deleted = true,
            DeletedAt = observedAt.AddMinutes(-1),
            CreatedAt = createdAt,
            NextFireAt = nextFireAt,
            LastFireAt = lastFireAt,
            LastTargetActorId = "run-actor-2",
            LastCommandId = "cmd-last",
            LastCorrelationId = "corr-last",
            LastError = "last error",
            FireCount = 2,
            FailureCount = 1,
            Target = new ScheduledDispatchTargetState
            {
                Kind = ScheduledDispatchTargetKindState.ServiceInvocation,
                ServiceInvocation = new ScheduledServiceInvocationTargetState
                {
                    Identity = new ServiceIdentity
                    {
                        TenantId = "scope-1",
                        AppId = ScopeServiceIdentityDefaults.ServiceAppId,
                        Namespace = ScopeServiceIdentityDefaults.ServiceNamespace,
                        ServiceId = "workflow-a",
                    },
                    EndpointId = "chat",
                    Payload = Any.Pack(new ChatRequestEvent { Prompt = "run it" }),
                },
            },
        };
        state.Headers["caller"] = "kept";
        state.Headers["workflow.schedule.workflow_name"] = "workflow-a";
        state.Headers["workflow.schedule.scope_id"] = "scope-1";
        state.Headers["workflow.schedule.source_actor_id"] = "definition-actor-1";
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
        document.Id.Should().Be("schedule-1");
        document.ActorId.Should().Be("scheduled-dispatch:schedule-1");
        document.ScheduleActorId.Should().Be("scheduled-dispatch:schedule-1");
        document.ScheduleId.Should().Be("schedule-1");
        document.DisplayName.Should().Be("Daily report");
        document.TargetKind.Should().Be(ScheduledDispatchTargetKind.ServiceInvocation.ToString());
        document.CronExpression.Should().Be("*/15 * * * *");
        document.Timezone.Should().Be("UTC");
        document.Enabled.Should().BeTrue();
        document.Deleted.Should().BeTrue();
        document.DeletedAt.Should().Be(observedAt.AddMinutes(-1));
        document.CreatedAt.Should().Be(createdAt);
        document.UpdatedAt.Should().Be(observedAt);
        document.NextFireAt.Should().Be(nextFireAt);
        document.LastFireAt.Should().Be(lastFireAt);
        document.LastTargetActorId.Should().Be("run-actor-2");
        document.LastCommandId.Should().Be("cmd-last");
        document.LastCorrelationId.Should().Be("corr-last");
        document.LastError.Should().Be("last error");
        document.FireCount.Should().Be(2);
        document.FailureCount.Should().Be(1);
        document.ServiceKey.Should().Be("scope-1:default:default:workflow-a");
        document.ServiceId.Should().Be("workflow-a");
        document.ServiceEndpointId.Should().Be("chat");
        document.Prompt.Should().Be("run it");
        document.TargetActorId.Should().Be("target-actor-1");
        document.StateVersion.Should().Be(7);
        document.LastEventId.Should().Be("evt-7");
        document.Headers.Should().Contain("caller", "kept");
        document.Headers.Should().Contain("workflow.schedule.workflow_name", "workflow-a");
        document.Headers.Should().Contain("workflow.schedule.scope_id", "scope-1");
        document.Headers.Should().Contain("workflow.schedule.source_actor_id", "definition-actor-1");
        document.FireRecords.Select(x => x.IdempotencyKey).Should().Equal("newer", "older");
        document.FireRecords[0].TargetActorId.Should().Be("run-actor-2");
        document.FireRecords[0].Manual.Should().BeTrue();
        document.FireRecords[0].Error.Should().Be("failed");
        document.FireRecords[1].ScheduledFireAt.Should().Be(olderFireAt);
    }

    [Fact]
    public async Task CurrentStateProjector_ShouldFallbackBlankValuesAndIgnoreOtherStateEnvelopes()
    {
        var fallbackNow = DateTimeOffset.Parse("2026-05-29T11:00:00+00:00");
        var dispatcher = new RecordingScheduleWriteDispatcher();
        var projector = new ScheduledDispatchCurrentStateProjector(
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
        document.Id.Should().Be("scheduled-dispatch:fallback");
        document.ScheduleId.Should().Be("scheduled-dispatch:fallback");
        document.DisplayName.Should().BeEmpty();
        document.CronExpression.Should().BeEmpty();
        document.Timezone.Should().BeEmpty();
        document.CreatedAt.Should().Be(fallbackNow);
        document.UpdatedAt.Should().Be(fallbackNow);
        document.NextFireAt.Should().BeNull();
        document.LastFireAt.Should().BeNull();
        document.LastTargetActorId.Should().BeEmpty();
        document.LastCommandId.Should().BeEmpty();
        document.LastCorrelationId.Should().BeEmpty();
        document.LastError.Should().BeEmpty();
        document.ServiceKey.Should().BeEmpty();
        document.ServiceId.Should().BeEmpty();
        document.ServiceEndpointId.Should().BeEmpty();
        document.ScheduleActorId.Should().Be("scheduled-dispatch:fallback");
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
        reader.Documents["schedule-1"] = new ScheduledDispatchDocument
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
            Prompt = "saved prompt",
            FireRecords =
            {
                new ScheduledDispatchFireRecordDocument
                {
                    ScheduledFireAt = completedAt.AddMinutes(-1),
                    CompletedAt = completedAt,
                    IdempotencyKey = "older",
                },
                new ScheduledDispatchFireRecordDocument
                {
                    ScheduledFireAt = completedAt.AddMinutes(9),
                    CompletedAt = completedAt.AddMinutes(10),
                    IdempotencyKey = "newer",
                    TargetActorId = "run-actor",
                    CommandId = "cmd",
                    CorrelationId = "corr",
                    Error = "boom",
                    Manual = true,
                },
            },
        };
        var port = new ScheduledDispatchQueryPort(reader);

        (await port.GetAsync(" ")).Should().BeNull();
        var detail = await port.GetAsync(" schedule-1 ");

        reader.GetKeys.Should().Equal("schedule-1");
        detail.Should().NotBeNull();
        detail!.Schedule.ScheduleId.Should().Be("schedule-1");
        detail.Schedule.Deleted.Should().BeFalse();
        detail.Schedule.DisplayName.Should().BeEmpty();
        detail.Schedule.CronExpression.Should().BeEmpty();
        detail.Schedule.Timezone.Should().BeEmpty();
        detail.Schedule.LastTargetActorId.Should().BeEmpty();
        detail.Schedule.LastCommandId.Should().BeEmpty();
        detail.Schedule.LastCorrelationId.Should().BeEmpty();
        detail.Schedule.LastError.Should().BeEmpty();
        detail.Schedule.Headers.Should().Contain("caller", "kept");
        detail.Schedule.ScheduleActorId.Should().BeEmpty();
        detail.Schedule.TargetActorId.Should().BeEmpty();
        detail.Schedule.Prompt.Should().Be("saved prompt");
        detail.RecentFires.Select(x => x.IdempotencyKey).Should().Equal("newer", "older");
        detail.RecentFires[0].TargetActorId.Should().Be("run-actor");
        detail.RecentFires[0].CommandId.Should().Be("cmd");
        detail.RecentFires[0].CorrelationId.Should().Be("corr");
        detail.RecentFires[0].Error.Should().Be("boom");
        detail.RecentFires[0].Manual.Should().BeTrue();
        detail.RecentFires[1].TargetActorId.Should().BeEmpty();
        detail.RecentFires[1].CommandId.Should().BeEmpty();
        detail.RecentFires[1].CorrelationId.Should().BeEmpty();
        detail.RecentFires[1].Error.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryPort_ShouldMapDeletedDetailForApplicationLayerVisibilityBoundary()
    {
        var deletedAt = DateTimeOffset.Parse("2026-05-29T09:30:00+00:00");
        var reader = new StubScheduleDocumentReader();
        reader.Documents["deleted-schedule"] = new ScheduledDispatchDocument
        {
            ScheduleId = "deleted-schedule",
            Deleted = true,
            DeletedAt = deletedAt,
            CreatedAt = DateTimeOffset.Parse("2026-05-29T08:00:00+00:00"),
            UpdatedAt = deletedAt,
        };
        var port = new ScheduledDispatchQueryPort(reader);

        var detail = await port.GetAsync(" deleted-schedule ");

        reader.GetKeys.Should().Equal("deleted-schedule");
        detail.Should().NotBeNull();
        detail!.Schedule.ScheduleId.Should().Be("deleted-schedule");
        detail.Schedule.Deleted.Should().BeTrue();
    }

    [Fact]
    public async Task QueryPort_ShouldClampPagingAndMapListResult()
    {
        var reader = new StubScheduleDocumentReader
        {
            QueryResult = new ProjectionDocumentQueryResult<ScheduledDispatchDocument>
            {
                Items =
                [
                    new ScheduledDispatchDocument
                    {
                        ScheduleId = "schedule-1",
                        DisplayName = "Daily",
                        CronExpression = "0 9 * * *",
                        Timezone = "UTC",
                        Enabled = true,
                        CreatedAt = DateTimeOffset.Parse("2026-05-29T08:00:00+00:00"),
                        UpdatedAt = DateTimeOffset.Parse("2026-05-29T08:30:00+00:00"),
                        Headers = new Dictionary<string, string>
                        {
                            ["trace"] = "on",
                        },
                        Prompt = "list prompt",
                        ScheduleActorId = "schedule-actor",
                        TargetActorId = "target-actor",
                    },
                ],
                NextCursor = "next",
                TotalCount = 12,
            },
        };
        var port = new ScheduledDispatchQueryPort(reader);

        var result = await InvokeScheduleListQueryAsync(port, 0, "cursor", includeTotalCount: true);
        await InvokeScheduleListQueryAsync(port, 500);

        reader.Queries.Should().HaveCount(2);
        reader.Queries[0].Take.Should().Be(1);
        reader.Queries[0].Cursor.Should().Be("cursor");
        reader.Queries[0].IncludeTotalCount.Should().BeTrue();
        reader.Queries[1].Take.Should().Be(200);
        result.NextCursor.Should().Be("next");
        result.TotalCount.Should().Be(12);
        result.Items.Should().ContainSingle();
        var deletedFilter = reader.Queries[0].Filters.Should().ContainSingle(filter =>
            filter.FieldPath == nameof(ScheduledDispatchDocument.Deleted)).Subject;
        deletedFilter.Operator.Should().Be(ProjectionDocumentFilterOperator.EqOrMissing);
        deletedFilter.Value.Kind.Should().Be(ProjectionDocumentValueKind.Bool);
        deletedFilter.Value.RawValue.Should().Be(false);
        var summary = result.Items.Single();
        summary.ScheduleId.Should().Be("schedule-1");
        summary.DisplayName.Should().Be("Daily");
        summary.CronExpression.Should().Be("0 9 * * *");
        summary.Headers.Should().Contain("trace", "on");
        summary.Prompt.Should().Be("list prompt");
        summary.ScheduleActorId.Should().Be("schedule-actor");
        summary.TargetActorId.Should().Be("target-actor");
    }

    [Fact]
    public void WorkflowScheduleReadModelsAndMetadata_ShouldNormalizeNullableTimestampsAndMaps()
    {
        var localTime = new DateTimeOffset(2026, 5, 29, 17, 0, 0, TimeSpan.FromHours(8));
        var document = new ScheduledDispatchDocument();

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

        var fireRecord = new ScheduledDispatchFireRecordDocument();
        fireRecord.ScheduledFireAt.Should().Be(default);
        fireRecord.CompletedAt.Should().Be(default);
        fireRecord.ScheduledFireAt = localTime;
        fireRecord.CompletedAt = localTime.AddSeconds(1);
        fireRecord.ScheduledFireAt.Offset.Should().Be(TimeSpan.Zero);
        fireRecord.CompletedAt.Offset.Should().Be(TimeSpan.Zero);

        var metadata = new ScheduledDispatchDocumentMetadataProvider().Metadata;
        metadata.IndexName.Should().Be("scheduled-dispatches");
        metadata.Mappings.Should().Contain("dynamic", true);
        metadata.Settings.Should().BeEmpty();
        metadata.Aliases.Should().BeEmpty();
    }

    private static ScheduledDispatchProjectionContext CreateContext(string rootActorId) =>
        new()
        {
            RootActorId = rootActorId,
            ProjectionKind = "scheduled-dispatch",
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

    private sealed class RecordingScheduleWriteDispatcher : IProjectionWriteDispatcher<ScheduledDispatchDocument>
    {
        public List<ScheduledDispatchDocument> Upserts { get; } = [];
        public List<string> Deletes { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(
            ScheduledDispatchDocument readModel,
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

    private sealed class StubScheduleDocumentReader : IProjectionDocumentReader<ScheduledDispatchDocument, string>
    {
        public Dictionary<string, ScheduledDispatchDocument> Documents { get; } = new(StringComparer.Ordinal);
        public List<string> GetKeys { get; } = [];
        public List<ProjectionDocumentQuery> Queries { get; } = [];
        public ProjectionDocumentQueryResult<ScheduledDispatchDocument> QueryResult { get; set; } =
            ProjectionDocumentQueryResult<ScheduledDispatchDocument>.Empty;

        public Task<ScheduledDispatchDocument?> GetAsync(string key, CancellationToken ct = default)
        {
            GetKeys.Add(key);
            return Task.FromResult(Documents.GetValueOrDefault(key));
        }

        public Task<ProjectionDocumentQueryResult<ScheduledDispatchDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            Queries.Add(query);
            return Task.FromResult(QueryResult);
        }
    }

    private static async Task<ScheduledDispatchListResult> InvokeScheduleListQueryAsync(
        ScheduledDispatchQueryPort port,
        int take = 50,
        string? cursor = null,
        bool includeTotalCount = false,
        CancellationToken ct = default)
    {
        ScheduleListQuery query = port.ListAsync;
        return await query(take, cursor, includeTotalCount, ct);
    }

    private delegate Task<ScheduledDispatchListResult> ScheduleListQuery(
        int take,
        string? cursor,
        bool includeTotalCount,
        CancellationToken ct);

    private sealed class FixedProjectionClock(DateTimeOffset utcNow) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
