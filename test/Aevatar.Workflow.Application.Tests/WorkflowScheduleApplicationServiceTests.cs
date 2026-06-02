using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Schedules;
using Aevatar.Workflow.Application.Runs;
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
    public async Task RunNowAsync_WhenDispatchAdmissionIsRejected_ShouldReturnRejectedReceipt()
    {
        var actorPort = new FakeWorkflowScheduleActorPort
        {
            AdmissionFactory = (actorId, envelope) => new DispatchAdmission(
                Accepted: false,
                CommandId: envelope.Id,
                AckedAt: DateTimeOffset.UtcNow,
                ActorId: actorId,
                CorrelationId: envelope.Propagation?.CorrelationId ?? envelope.Id),
        };
        var queryPort = new FakeWorkflowScheduleQueryPort();
        queryPort.Details["schedule-1"] = CreateDetail("schedule-1");
        var service = CreateService(actorPort, queryPort);

        var receipt = await service.RunNowAsync("schedule-1");

        receipt.Accepted.Should().BeFalse();
        actorPort.RunNowRequests.Should().ContainSingle()
            .Which.ActorId.Should().Be("actor:schedule-1");
    }

    [Fact]
    public async Task EnableAsync_WhenDispatchAdmissionIsRejected_ShouldReturnRejectedReceipt()
    {
        var actorPort = new FakeWorkflowScheduleActorPort
        {
            AdmissionFactory = (actorId, envelope) => new DispatchAdmission(
                Accepted: false,
                CommandId: envelope.Id,
                AckedAt: DateTimeOffset.UtcNow,
                ActorId: actorId,
                CorrelationId: envelope.Propagation?.CorrelationId ?? envelope.Id),
        };
        var queryPort = new FakeWorkflowScheduleQueryPort();
        queryPort.Details["schedule-1"] = CreateDetail("schedule-1");
        var service = CreateService(actorPort, queryPort);

        var receipt = await service.EnableAsync("schedule-1", "resume");

        receipt.Should().Be(new WorkflowScheduleMutationReceipt("schedule-1", "actor:schedule-1", false));
        actorPort.Enabled.Should().ContainSingle()
            .Which.Should().Be(("actor:schedule-1", "resume"));
    }

    [Fact]
    public async Task CreateAsync_WhenDispatchAdmissionIsRejected_ShouldReturnRejectedReceipt()
    {
        var actorPort = new FakeWorkflowScheduleActorPort
        {
            AdmissionFactory = (actorId, envelope) => new DispatchAdmission(
                Accepted: false,
                CommandId: envelope.Id,
                AckedAt: DateTimeOffset.UtcNow,
                ActorId: actorId,
                CorrelationId: envelope.Propagation?.CorrelationId ?? envelope.Id),
        };
        var service = CreateService(actorPort);

        var receipt = await service.CreateAsync(CreateConfiguration("schedule-1"));

        receipt.Should().Be(new WorkflowScheduleMutationReceipt("schedule-1", "actor:schedule-1", false));
        actorPort.Configured.Should().ContainSingle();
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

    [Fact]
    public async Task EnableDisableAndRunNow_ShouldRejectMissingActorAfterConfiguredReadModel()
    {
        var actorPort = new FakeWorkflowScheduleActorPort
        {
            ResolveActorId = string.Empty,
        };
        var queryPort = new FakeWorkflowScheduleQueryPort();
        queryPort.Details["schedule-1"] = CreateDetail("schedule-1");
        var service = CreateService(actorPort, queryPort);

        var enable = () => service.EnableAsync(" schedule-1 ", " resume ");
        var runNow = () => service.RunNowAsync("schedule-1");

        await enable.Should().ThrowAsync<WorkflowScheduleNotFoundException>();
        await runNow.Should().ThrowAsync<WorkflowScheduleNotFoundException>();
        actorPort.ResolveScheduleIds.Should().Equal("schedule-1", "schedule-1");
        actorPort.Enabled.Should().BeEmpty();
        actorPort.RunNowRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task EnableDisable_ShouldNormalizeReasonsAndDispatchToResolvedActor()
    {
        var actorPort = new FakeWorkflowScheduleActorPort();
        var queryPort = new FakeWorkflowScheduleQueryPort();
        queryPort.Details["schedule-1"] = CreateDetail("schedule-1");
        var service = CreateService(actorPort, queryPort);

        var enabled = await service.EnableAsync(" schedule-1 ", " resume ");
        var disabled = await service.DisableAsync("schedule-1", " ");

        enabled.Should().Be(new WorkflowScheduleMutationReceipt("schedule-1", "actor:schedule-1", true));
        disabled.Should().Be(new WorkflowScheduleMutationReceipt("schedule-1", "actor:schedule-1", true));
        actorPort.Enabled.Should().ContainSingle()
            .Which.Should().Be(("actor:schedule-1", "resume"));
        actorPort.Disabled.Should().ContainSingle()
            .Which.Should().Be(("actor:schedule-1", string.Empty));
    }

    [Fact]
    public async Task UpdateAsync_ShouldUseRouteScheduleIdAndScrubOptionalAdapterHeaders()
    {
        var actorPort = new FakeWorkflowScheduleActorPort();
        var service = CreateService(actorPort);

        var receipt = await service.UpdateAsync(
            " route-schedule ",
            new WorkflowScheduleConfiguration(
                ScheduleId: "body-schedule",
                DisplayName: " ",
                WorkflowName: " direct ",
                Prompt: " hello ",
                CronExpression: "*/20 * * * *",
                Timezone: " ",
                Enabled: false,
                Headers: new Dictionary<string, string>
                {
                    [" x "] = " y ",
                    ["empty"] = " ",
                    [WorkflowScheduleAdapterHeaderKeys.ScopeId] = "spoofed",
                    [WorkflowScheduleAdapterHeaderKeys.SourceActorId] = "spoofed",
                },
                ScopeId: " ",
                ActorId: " "));

        receipt.Should().Be(new WorkflowScheduleMutationReceipt("route-schedule", "actor:route-schedule", true));
        actorPort.Configured.Should().ContainSingle();
        var configuration = actorPort.Configured.Single().Configuration;
        configuration.ScheduleId.Should().Be("route-schedule");
        configuration.DisplayName.Should().BeEmpty();
        configuration.Timezone.Should().Be("UTC");
        configuration.ScopeId.Should().BeNull();
        configuration.ActorId.Should().BeNull();
        configuration.Headers.Should().Contain("x", "y");
        configuration.Headers.Should().NotContainKey("empty");
        configuration.Headers[WorkflowScheduleAdapterHeaderKeys.WorkflowName].Should().Be("direct");
        configuration.Headers[WorkflowScheduleAdapterHeaderKeys.Prompt].Should().Be("hello");
        configuration.Headers.Should().NotContainKey(WorkflowScheduleAdapterHeaderKeys.ScopeId);
        configuration.Headers.Should().NotContainKey(WorkflowScheduleAdapterHeaderKeys.SourceActorId);
    }

    [Fact]
    public async Task PrepareAsync_ShouldCreateStoredWorkflowStartRequestWithoutResolvingRunActor()
    {
        var service = new WorkflowScheduledDispatchPreparationService();
        var configuration = new WorkflowScheduleConfiguration(
            ScheduleId: "schedule-1",
            DisplayName: "Daily",
            WorkflowName: "daily-workflow",
            Prompt: "run the daily workflow",
            CronExpression: "0 9 * * *",
            Timezone: "UTC",
            Enabled: true,
            Headers: new Dictionary<string, string>
            {
                ["x-trace"] = "trace-1",
            },
            ScopeId: "scope-1",
            ActorId: "definition-actor-1");

        var preparation = await service.PrepareAsync(
            configuration,
            "command-1",
            "correlation-1");

        preparation.TargetActorId.Should().Be("schedule-1");
        preparation.PayloadTypeUrl.Should().Be(Any.Pack(new WorkflowScheduledDispatchStartRequest()).TypeUrl);
        preparation.TriggerEnvelope.Id.Should().Be("command-1");
        preparation.TriggerEnvelope.Route.GetTargetActorId().Should().Be("schedule-1");
        preparation.TriggerEnvelope.Propagation!.CorrelationId.Should().Be("correlation-1");
        preparation.TriggerEnvelope.Timestamp.Should().NotBeNull();

        var request = preparation.TriggerEnvelope.Payload.Unpack<WorkflowScheduledDispatchStartRequest>();
        request.Prompt.Should().Be("run the daily workflow");
        request.ScopeId.Should().Be("scope-1");
        request.ActorId.Should().Be("definition-actor-1");
        request.WorkflowName.Should().Be("daily-workflow");
        request.Headers.Should().Contain(
            new KeyValuePair<string, string>("x-trace", "trace-1"));
        request.Headers.Should().Contain(
            new KeyValuePair<string, string>("workflow.schedule_id", "schedule-1"));
    }

    [Fact]
    public async Task PrepareAsync_ShouldUseCatalogSourceAndOmitBlankScopeAndActor()
    {
        var service = new WorkflowScheduledDispatchPreparationService();
        var configuration = new WorkflowScheduleConfiguration(
            ScheduleId: "schedule-1",
            DisplayName: string.Empty,
            WorkflowName: "catalog-workflow",
            Prompt: "hello",
            CronExpression: "*/15 * * * *",
            Timezone: "UTC",
            Enabled: true,
            Headers: new Dictionary<string, string>(),
            ScopeId: " ",
            ActorId: " ");

        var preparation = await service.PrepareAsync(configuration, "command-1", "correlation-1");

        var request = preparation.TriggerEnvelope.Payload.Unpack<WorkflowScheduledDispatchStartRequest>();
        request.ScopeId.Should().BeEmpty();
        request.WorkflowName.Should().Be("catalog-workflow");
        request.ActorId.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not cron")]
    public void ScheduledDispatchCalculator_Validate_ShouldReturnError_ForInvalidCron(string cronExpression)
    {
        var result = ScheduledDispatchCalculator.Validate(
            cronExpression,
            "UTC",
            new DateTimeOffset(2026, 5, 29, 8, 30, 0, TimeSpan.Zero));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ScheduledDispatchCalculator_TryResolveTimeZone_ShouldRejectInvalidTimezone()
    {
        var resolved = ScheduledDispatchCalculator.TryResolveTimeZone(
            "Invalid/Zone",
            out var timeZone,
            out var error);

        resolved.Should().BeFalse();
        timeZone.Should().Be(TimeZoneInfo.Utc);
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ScheduledDispatchCalculator_GetNextOccurrences_ShouldClampCountAndUseDefaultTimezone()
    {
        var occurrences = ScheduledDispatchCalculator.GetNextOccurrences(
            "*/1 * * * *",
            " ",
            new DateTimeOffset(2026, 5, 29, 8, 30, 0, TimeSpan.Zero),
            150);

        occurrences.Should().HaveCount(100);
        occurrences.First().Should().Be(
            new DateTimeOffset(2026, 5, 29, 8, 31, 0, TimeSpan.Zero));
        occurrences.Should().BeInAscendingOrder();
    }

    [Fact]
    public void ScheduledDispatchCalculator_ComputeDueTime_ShouldFloorPastOrCurrentValues()
    {
        var now = new DateTimeOffset(2026, 5, 29, 8, 30, 0, TimeSpan.Zero);

        ScheduledDispatchCalculator.ComputeDueTime(now, now)
            .Should().Be(TimeSpan.FromSeconds(1));
        ScheduledDispatchCalculator.ComputeDueTime(now.AddSeconds(-5), now)
            .Should().Be(TimeSpan.FromSeconds(1));
        ScheduledDispatchCalculator.ComputeDueTime(now.AddMinutes(3), now)
            .Should().Be(TimeSpan.FromMinutes(3));
    }

    [Fact]
    public void ScheduledDispatchCalculator_BuildIdempotencyKey_ShouldUseUtcRoundTripInstant()
    {
        var scheduledFireAt = new DateTimeOffset(
            2026,
            5,
            29,
            17,
            30,
            0,
            TimeSpan.FromHours(8));

        var key = ScheduledDispatchCalculator.BuildIdempotencyKey("schedule-1", scheduledFireAt);

        key.Should().Be("schedule:schedule-1:fire:2026-05-29T09:30:00.0000000+00:00");
    }

    [Fact]
    public void ScheduledDispatchCalculator_ShouldAdvanceCursorExclusivelyAndThrowForInvalidInputs()
    {
        var boundary = new DateTimeOffset(2026, 5, 29, 8, 30, 0, TimeSpan.Zero);

        var occurrences = ScheduledDispatchCalculator.GetNextOccurrences(
            "*/30 * * * *",
            "UTC",
            boundary,
            -3);

        occurrences.Should().ContainSingle()
            .Which.Should().Be(new DateTimeOffset(2026, 5, 29, 9, 0, 0, TimeSpan.Zero));
        occurrences.Should().NotContain(boundary);

        var invalidTimezone = () => ScheduledDispatchCalculator.GetNextOccurrences(
            "* * * * *",
            "Invalid/Zone",
            boundary,
            1);
        invalidTimezone.Should().Throw<ArgumentException>()
            .WithParameterName("timeZoneId");

        var invalidCron = () => ScheduledDispatchCalculator.GetNextOccurrences(
            "not cron",
            "UTC",
            boundary,
            1);
        invalidCron.Should().Throw<ArgumentException>()
            .WithParameterName("cronExpression");

        ScheduledDispatchValidationResult.Failed(" ")
            .Error.Should().Be("Schedule is invalid.");
        WorkflowScheduleValidationResult.Failed(" ")
            .Error.Should().Be("Schedule is invalid.");
    }

    [Fact]
    public async Task PreviewAsync_ShouldNormalizeBlankTimezoneAndClampLowCount()
    {
        var service = CreateService();

        var preview = await service.PreviewAsync(
            " */30 * * * * ",
            " ",
            0,
            new DateTimeOffset(2026, 5, 29, 8, 31, 0, TimeSpan.Zero));

        preview.CronExpression.Should().Be("*/30 * * * *");
        preview.Timezone.Should().Be("UTC");
        preview.NextFireTimes.Should().ContainSingle()
            .Which.Should().Be(new DateTimeOffset(2026, 5, 29, 9, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task GetAsync_ShouldNormalizeScheduleIdBeforeQuerying()
    {
        var queryPort = new FakeWorkflowScheduleQueryPort();
        queryPort.Details["schedule-1"] = CreateDetail("schedule-1");
        var service = CreateService(new FakeWorkflowScheduleActorPort(), queryPort);

        var detail = await service.GetAsync(" schedule-1 ");

        detail.Should().NotBeNull();
        queryPort.GetScheduleIds.Should().Equal("schedule-1");
    }

    [Fact]
    public async Task CreateAsync_ShouldRemoveCallerSuppliedAdapterHeadersBeforeAddingNormalizedValues()
    {
        var actorPort = new FakeWorkflowScheduleActorPort();
        var preparation = new FakeWorkflowScheduledDispatchPreparationService();
        var service = CreateService(actorPort, new FakeWorkflowScheduleQueryPort(), preparation);

        await service.CreateAsync(new WorkflowScheduleConfiguration(
            ScheduleId: "schedule-1",
            DisplayName: string.Empty,
            WorkflowName: "direct",
            Prompt: "hello",
            CronExpression: "*/15 * * * *",
            Timezone: "UTC",
            Enabled: true,
            Headers: new Dictionary<string, string>
            {
                [WorkflowScheduleAdapterHeaderKeys.WorkflowName] = "spoofed",
                [WorkflowScheduleAdapterHeaderKeys.Prompt] = "spoofed",
                [WorkflowScheduleAdapterHeaderKeys.ScopeId] = "spoofed",
                [WorkflowScheduleAdapterHeaderKeys.SourceActorId] = "spoofed",
                ["caller"] = "kept",
            }));

        var headers = actorPort.Configured.Single().Configuration.Headers;
        headers.Should().Contain(new KeyValuePair<string, string>("caller", "kept"));
        headers[WorkflowScheduleAdapterHeaderKeys.WorkflowName].Should().Be("direct");
        headers[WorkflowScheduleAdapterHeaderKeys.Prompt].Should().Be("hello");
        headers.Should().NotContainKey(WorkflowScheduleAdapterHeaderKeys.ScopeId);
        headers.Should().NotContainKey(WorkflowScheduleAdapterHeaderKeys.SourceActorId);
    }

    private sealed class FakeWorkflowScheduleActorPort : IWorkflowScheduleActorPort
    {
        public List<string> EnsureScheduleIds { get; } = [];
        public List<string> ResolveScheduleIds { get; } = [];
        public List<(string ActorId, WorkflowScheduleConfiguration Configuration, ScheduledDispatchPreparation Dispatch)> Configured { get; } = [];
        public List<(string ActorId, string Reason)> Enabled { get; } = [];
        public List<(string ActorId, string Reason)> Disabled { get; } = [];
        public List<(string ActorId, DateTimeOffset ScheduledFireAt)> RunNowRequests { get; } = [];
        public string? ResolveActorId { get; set; }
        public Func<string, EventEnvelope, DispatchAdmission> AdmissionFactory { get; set; } =
            DispatchAdmissionFactory.Create;

        public Task<string> EnsureScheduleActorAsync(string scheduleId, CancellationToken ct = default)
        {
            EnsureScheduleIds.Add(scheduleId);
            return Task.FromResult($"actor:{scheduleId}");
        }

        public Task<string?> ResolveScheduleActorAsync(string scheduleId, CancellationToken ct = default)
        {
            ResolveScheduleIds.Add(scheduleId);
            return Task.FromResult<string?>(ResolveActorId ?? $"actor:{scheduleId}");
        }

        public Task<DispatchAdmission> DispatchConfigureAsync(
            string actorId,
            WorkflowScheduleConfiguration configuration,
            ScheduledDispatchPreparation dispatch,
            CancellationToken ct = default)
        {
            Configured.Add((actorId, configuration, dispatch));
            return Task.FromResult(AdmissionFactory(actorId, dispatch.TriggerEnvelope));
        }

        public Task<DispatchAdmission> DispatchEnableAsync(
            string actorId,
            string reason,
            CancellationToken ct = default)
        {
            Enabled.Add((actorId, reason));
            return Task.FromResult(AdmissionFactory(actorId, CreateAdmissionEnvelope()));
        }

        public Task<DispatchAdmission> DispatchDisableAsync(
            string actorId,
            string reason,
            CancellationToken ct = default)
        {
            Disabled.Add((actorId, reason));
            return Task.FromResult(AdmissionFactory(actorId, CreateAdmissionEnvelope()));
        }

        public Task<DispatchAdmission> DispatchRunNowAsync(
            string actorId,
            DateTimeOffset scheduledFireAt,
            CancellationToken ct = default)
        {
            RunNowRequests.Add((actorId, scheduledFireAt));
            return Task.FromResult(AdmissionFactory(actorId, CreateAdmissionEnvelope()));
        }

        private static EventEnvelope CreateAdmissionEnvelope() =>
            new()
            {
                Id = Guid.NewGuid().ToString("N"),
                Propagation = new EnvelopePropagation
                {
                    CorrelationId = Guid.NewGuid().ToString("N"),
                },
            };
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
        public List<string> GetScheduleIds { get; } = [];
        public int? LastTake { get; private set; }
        public string? LastCursor { get; private set; }
        public bool? LastIncludeTotalCount { get; private set; }

        public Task<WorkflowScheduleDetail?> GetAsync(string scheduleId, CancellationToken ct = default)
        {
            GetScheduleIds.Add(scheduleId);
            return Task.FromResult(Details.GetValueOrDefault(scheduleId));
        }

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

    private sealed class FakeWorkflowRunActorResolver : IWorkflowRunActorResolver
    {
        public List<WorkflowChatRunRequest> Requests { get; } = [];

        public WorkflowActorResolutionResult Result { get; set; } =
            new(
                new WorkflowRunCreationReceipt("run-actor-1", "definition-actor-1", []),
                "daily-workflow",
                WorkflowChatRunStartError.None);

        public Task<WorkflowActorResolutionResult> ResolveOrCreateAsync(
            WorkflowChatRunRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingWorkflowChatEnvelopeFactory : ICommandEnvelopeFactory<WorkflowChatRunRequest>
    {
        public List<WorkflowChatRunRequest> Commands { get; } = [];
        public List<CommandContext> Contexts { get; } = [];

        public EventEnvelope CreateEnvelope(WorkflowChatRunRequest command, CommandContext context)
        {
            Commands.Add(command);
            Contexts.Add(context);

            var chatRequest = new ChatRequestEvent
            {
                Prompt = command.Prompt,
                SessionId = command.SessionId ?? context.CorrelationId,
                ScopeId = command.ScopeId ?? string.Empty,
            };
            foreach (var (key, value) in command.Metadata ?? new Dictionary<string, string>())
                chatRequest.Metadata[key] = value;

            return new EventEnvelope
            {
                Id = context.CommandId,
                Payload = Any.Pack(chatRequest),
                Route = EnvelopeRouteSemantics.CreateDirect("test", context.TargetId),
                Propagation = new EnvelopePropagation
                {
                    CorrelationId = context.CorrelationId,
                },
            };
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
