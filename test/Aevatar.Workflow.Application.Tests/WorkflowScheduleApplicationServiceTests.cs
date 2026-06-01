using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Schedules;
using Aevatar.Workflow.Application.Schedules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowScheduleApplicationServiceTests
{
    [Fact]
    public async Task CreateAsync_ShouldNormalizeConfigurationAndDispatchConfigure()
    {
        var actorPort = new FakeWorkflowScheduleActorPort();
        var preparation = new FakeWorkflowScheduledDispatchPreparationService();
        var service = CreateService(actorPort, new FakeWorkflowScheduleQueryPort(), preparation);

        var receipt = await service.CreateAsync(new WorkflowScheduleConfiguration(
            ScheduleId: " daily-report ",
            DisplayName: " Daily report ",
            WorkflowName: " direct ",
            Prompt: " summarize status ",
            CronExpression: "*/15 * * * *",
            Timezone: " UTC ",
            Enabled: true,
            Headers: new Dictionary<string, string>
            {
                [" trace "] = " enabled ",
                [" "] = "ignored",
            },
            ScopeId: " scope-1 ",
            ActorId: " actor-1 "));

        receipt.ScheduleId.Should().Be("daily-report");
        receipt.ActorId.Should().Be("actor:daily-report");
        actorPort.EnsureScheduleIds.Should().Equal("daily-report");
        actorPort.Configured.Should().ContainSingle();
        var configured = actorPort.Configured.Single();
        configured.ActorId.Should().Be("actor:daily-report");
        configured.Configuration.ScheduleId.Should().Be("daily-report");
        configured.Configuration.DisplayName.Should().Be("Daily report");
        configured.Configuration.WorkflowName.Should().Be("direct");
        configured.Configuration.Prompt.Should().Be("summarize status");
        configured.Configuration.Timezone.Should().Be("UTC");
        configured.Configuration.ScopeId.Should().Be("scope-1");
        configured.Configuration.ActorId.Should().Be("actor-1");
        configured.Configuration.Headers.Should().Contain(
            new KeyValuePair<string, string>("trace", "enabled"));
        configured.Configuration.Headers[WorkflowScheduleAdapterHeaderKeys.WorkflowName].Should().Be("direct");
        configured.Configuration.Headers[WorkflowScheduleAdapterHeaderKeys.Prompt].Should().Be("summarize status");
        configured.Configuration.Headers[WorkflowScheduleAdapterHeaderKeys.ScopeId].Should().Be("scope-1");
        configured.Configuration.Headers[WorkflowScheduleAdapterHeaderKeys.SourceActorId].Should().Be("actor-1");
        configured.Dispatch.TargetActorId.Should().Be("target:daily-report");
        preparation.Configurations.Should().ContainSingle()
            .Which.ScheduleId.Should().Be("daily-report");
    }

    [Fact]
    public async Task CreateAsync_ShouldRejectInvalidCron()
    {
        var service = CreateService();

        var act = () => service.CreateAsync(new WorkflowScheduleConfiguration(
            ScheduleId: "invalid",
            DisplayName: string.Empty,
            WorkflowName: "direct",
            Prompt: "hello",
            CronExpression: "not cron",
            Timezone: "UTC",
            Enabled: true,
            Headers: new Dictionary<string, string>()));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData("tenant/report")]
    [InlineData("tenant?report")]
    [InlineData("tenant%report")]
    public async Task CreateAsync_ShouldRejectRouteUnsafeScheduleId(string scheduleId)
    {
        var service = CreateService();

        var act = () => service.CreateAsync(CreateConfiguration(scheduleId));

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*letters, digits, '.', '_', ':', and '-'*");
    }

    [Fact]
    public async Task PreviewAsync_ShouldReturnBoundedUtcOccurrences()
    {
        var service = CreateService();

        var preview = await service.PreviewAsync(
            "0 9 * * *",
            "UTC",
            3,
            new DateTimeOffset(2026, 5, 29, 8, 30, 0, TimeSpan.Zero));

        preview.CronExpression.Should().Be("0 9 * * *");
        preview.Timezone.Should().Be("UTC");
        preview.NextFireTimes.Should().Equal(
            new DateTimeOffset(2026, 5, 29, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 30, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 31, 9, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task RunNowAsync_ShouldDispatchManualFireWithStableIdempotencyKey()
    {
        var actorPort = new FakeWorkflowScheduleActorPort();
        var queryPort = new FakeWorkflowScheduleQueryPort();
        queryPort.Details["schedule-1"] = CreateDetail("schedule-1");
        var service = CreateService(actorPort, queryPort);

        var receipt = await service.RunNowAsync("schedule-1");

        receipt.ScheduleId.Should().Be("schedule-1");
        receipt.ActorId.Should().Be("actor:schedule-1");
        receipt.Accepted.Should().BeTrue();
        receipt.IdempotencyKey.Should().Be(
            WorkflowScheduleCalculator.BuildIdempotencyKey("schedule-1", receipt.ScheduledFireAt));
        receipt.IdempotencyKey.Should().StartWith("schedule:schedule-1:fire:");
        actorPort.RunNowRequests.Should().ContainSingle()
            .Which.ActorId.Should().Be("actor:schedule-1");
        actorPort.EnsureScheduleIds.Should().BeEmpty();
        actorPort.ResolveScheduleIds.Should().Equal("schedule-1");
    }

    [Fact]
    public async Task DisableAsync_ShouldReturnNotFound_WhenScheduleReadModelDoesNotExist()
    {
        var actorPort = new FakeWorkflowScheduleActorPort();
        var service = CreateService(actorPort, new FakeWorkflowScheduleQueryPort());

        var act = () => service.DisableAsync("missing", string.Empty);

        await act.Should().ThrowAsync<WorkflowScheduleNotFoundException>();
        actorPort.EnsureScheduleIds.Should().BeEmpty();
        actorPort.Disabled.Should().BeEmpty();
    }

    [Fact]
    public async Task RunNowAsync_ShouldReturnConflict_WhenScheduleIsUnconfigured()
    {
        var actorPort = new FakeWorkflowScheduleActorPort();
        var queryPort = new FakeWorkflowScheduleQueryPort();
        queryPort.Details["schedule-1"] = CreateDetail("schedule-1", workflowName: string.Empty);
        var service = CreateService(actorPort, queryPort);

        var act = () => service.RunNowAsync("schedule-1");

        await act.Should().ThrowAsync<WorkflowScheduleConflictException>();
        actorPort.EnsureScheduleIds.Should().BeEmpty();
        actorPort.RunNowRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ListAsync_ShouldClampTakeBeforeQueryingReadModel()
    {
        var queryPort = new FakeWorkflowScheduleQueryPort();
        var service = CreateService(new FakeWorkflowScheduleActorPort(), queryPort);

        await service.ListAsync(500, "cursor", includeTotalCount: true);

        queryPort.LastTake.Should().Be(200);
        queryPort.LastCursor.Should().Be("cursor");
        queryPort.LastIncludeTotalCount.Should().BeTrue();
    }

    private sealed class FakeWorkflowScheduleActorPort : IWorkflowScheduleActorPort
    {
        public List<string> EnsureScheduleIds { get; } = [];
        public List<string> ResolveScheduleIds { get; } = [];
        public List<(string ActorId, WorkflowScheduleConfiguration Configuration, ScheduledDispatchPreparation Dispatch)> Configured { get; } = [];
        public List<(string ActorId, string Reason)> Enabled { get; } = [];
        public List<(string ActorId, string Reason)> Disabled { get; } = [];
        public List<(string ActorId, DateTimeOffset ScheduledFireAt)> RunNowRequests { get; } = [];

        public Task<string> EnsureScheduleActorAsync(string scheduleId, CancellationToken ct = default)
        {
            EnsureScheduleIds.Add(scheduleId);
            return Task.FromResult($"actor:{scheduleId}");
        }

        public Task<string?> ResolveScheduleActorAsync(string scheduleId, CancellationToken ct = default)
        {
            ResolveScheduleIds.Add(scheduleId);
            return Task.FromResult<string?>($"actor:{scheduleId}");
        }

        public Task DispatchConfigureAsync(
            string actorId,
            WorkflowScheduleConfiguration configuration,
            ScheduledDispatchPreparation dispatch,
            CancellationToken ct = default)
        {
            Configured.Add((actorId, configuration, dispatch));
            return Task.CompletedTask;
        }

        public Task DispatchEnableAsync(
            string actorId,
            string reason,
            CancellationToken ct = default)
        {
            Enabled.Add((actorId, reason));
            return Task.CompletedTask;
        }

        public Task DispatchDisableAsync(
            string actorId,
            string reason,
            CancellationToken ct = default)
        {
            Disabled.Add((actorId, reason));
            return Task.CompletedTask;
        }

        public Task DispatchRunNowAsync(
            string actorId,
            DateTimeOffset scheduledFireAt,
            CancellationToken ct = default)
        {
            RunNowRequests.Add((actorId, scheduledFireAt));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWorkflowScheduledDispatchPreparationService : IWorkflowScheduledDispatchPreparationService
    {
        public List<WorkflowScheduleConfiguration> Configurations { get; } = [];

        public Task<ScheduledDispatchPreparation> PrepareAsync(
            WorkflowScheduleConfiguration configuration,
            string commandId,
            string correlationId,
            CancellationToken ct = default)
        {
            Configurations.Add(configuration);
            var targetActorId = $"target:{configuration.ScheduleId}";
            return Task.FromResult(new ScheduledDispatchPreparation(
                targetActorId,
                new EventEnvelope
                {
                    Id = commandId,
                    Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                    Payload = Any.Pack(new Empty()),
                    Route = EnvelopeRouteSemantics.CreateDirect("test", targetActorId),
                    Propagation = new EnvelopePropagation
                    {
                        CorrelationId = correlationId,
                    },
                },
                Any.Pack(new Empty()).TypeUrl));
        }
    }

    private static WorkflowScheduleApplicationService CreateService(
        FakeWorkflowScheduleActorPort? actorPort = null,
        FakeWorkflowScheduleQueryPort? queryPort = null,
        FakeWorkflowScheduledDispatchPreparationService? preparation = null) =>
        new(
            actorPort ?? new FakeWorkflowScheduleActorPort(),
            queryPort ?? new FakeWorkflowScheduleQueryPort(),
            preparation ?? new FakeWorkflowScheduledDispatchPreparationService());

    private sealed class FakeWorkflowScheduleQueryPort : IWorkflowScheduleQueryPort
    {
        public Dictionary<string, WorkflowScheduleDetail> Details { get; } = new(StringComparer.Ordinal);
        public int? LastTake { get; private set; }
        public string? LastCursor { get; private set; }
        public bool? LastIncludeTotalCount { get; private set; }

        public Task<WorkflowScheduleDetail?> GetAsync(string scheduleId, CancellationToken ct = default) =>
            Task.FromResult(Details.GetValueOrDefault(scheduleId));

        public Task<WorkflowScheduleListResult> ListAsync(
            int take = 50,
            string? cursor = null,
            bool includeTotalCount = false,
            CancellationToken ct = default)
        {
            LastTake = take;
            LastCursor = cursor;
            LastIncludeTotalCount = includeTotalCount;
            return Task.FromResult(new WorkflowScheduleListResult([], null, null));
        }
    }

    private static WorkflowScheduleConfiguration CreateConfiguration(string scheduleId) =>
        new(
            ScheduleId: scheduleId,
            DisplayName: string.Empty,
            WorkflowName: "direct",
            Prompt: "hello",
            CronExpression: "*/15 * * * *",
            Timezone: "UTC",
            Enabled: true,
            Headers: new Dictionary<string, string>());

    private static WorkflowScheduleDetail CreateDetail(
        string scheduleId,
        string workflowName = "direct",
        string cronExpression = "*/15 * * * *") =>
        new(
            new WorkflowScheduleSummary(
                ScheduleId: scheduleId,
                DisplayName: string.Empty,
                WorkflowName: workflowName,
                CronExpression: cronExpression,
                Timezone: "UTC",
                Enabled: true,
                CreatedAt: DateTimeOffset.UnixEpoch,
                UpdatedAt: DateTimeOffset.UnixEpoch,
                NextFireAt: null,
                LastFireAt: null,
                LastRunActorId: string.Empty,
                LastCommandId: string.Empty,
                LastCorrelationId: string.Empty,
                LastError: string.Empty,
                FireCount: 0,
                FailureCount: 0,
                Headers: new Dictionary<string, string>(),
                ScopeId: string.Empty,
                ActorId: string.Empty),
            []);
}
