using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Application.Schedules;
using Aevatar.GAgentService.Core.Schedules;
using Aevatar.GAgentService.Infrastructure.Schedules;
using Aevatar.GAgentService.Projection.Queries;
using Aevatar.GAgentService.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ScheduledDispatchApplicationServiceTests
{
    [Fact]
    public async Task CreateAsync_ShouldNormalizeGeneratedEnvelopeScheduleAndDispatchCreate()
    {
        var actorPort = new RecordingScheduledDispatchActorPort();
        var queryPort = new RecordingScheduledDispatchQueryPort();
        var service = new ScheduledDispatchApplicationService(
            actorPort,
            queryPort,
            new ScheduledDispatchTargetPreparationService());
        var envelope = new EventEnvelope
        {
            Payload = Any.Pack(new StringValue { Value = "run" }),
            Route = EnvelopeRouteSemantics.CreateDirect("publisher-1", "target-1"),
        };

        var receipt = await service.CreateAsync(new ScheduledDispatchConfiguration(
            string.Empty,
            " Daily ",
            new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.Envelope,
                Envelope: envelope),
            " 0 9 * * * ",
            " UTC ",
            true,
            new Dictionary<string, string>
            {
                [" trace "] = " enabled ",
                [" "] = "ignored",
                ["empty"] = " ",
            }));

        receipt.Accepted.Should().BeTrue();
        receipt.ScheduleId.Should().NotBeNullOrWhiteSpace();
        actorPort.EnsuredScheduleIds.Should().ContainSingle().Which.Should().Be(receipt.ScheduleId);
        var created = actorPort.Created.Should().ContainSingle().Which;
        created.Configuration.ScheduleId.Should().Be(receipt.ScheduleId);
        created.Configuration.DisplayName.Should().Be("Daily");
        created.Configuration.Target.ActorId.Should().Be("target-1");
        created.Configuration.Headers.Should().Contain("trace", "enabled");
        created.Configuration.Headers.Should().NotContainKey("empty");
        created.Dispatch.TargetActorId.Should().Be("target-1");
        created.Dispatch.TriggerEnvelope.Id.Should().Be($"schedule-{receipt.ScheduleId}-trigger");
        created.Dispatch.TriggerEnvelope.Propagation!.CorrelationId.Should().Be($"schedule-{receipt.ScheduleId}");
    }

    [Fact]
    public async Task CreateAsync_ShouldPreserveServiceInvocationAuthInActorCommand()
    {
        var actorPort = new RecordingScheduledDispatchActorPort();
        var service = new ScheduledDispatchApplicationService(
            actorPort,
            new RecordingScheduledDispatchQueryPort(),
            new ScheduledDispatchTargetPreparationService());
        var auth = new ScheduledServiceInvocationAuth(new ScheduledServiceInvocationNyxIdCredentialSource(
            new ScheduledServiceInvocationNyxIdSubjectRef("lark", "tenant-1", "ou-user-1"),
            "proxy"));

        await service.CreateAsync(new ScheduledDispatchConfiguration(
            "schedule-auth",
            "Invoke",
            new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.ServiceInvocation,
                ServiceInvocation: new ScheduledServiceInvocationTargetDescriptor(
                    new ServiceIdentity { TenantId = "tenant", AppId = "app", Namespace = "default", ServiceId = "svc" },
                    "run",
                    Any.Pack(new StringValue { Value = "invoke" }),
                    Auth: auth)),
            "0 9 * * *",
            "UTC",
            true,
            new Dictionary<string, string>()));

        var created = actorPort.Created.Should().ContainSingle().Which;
        created.Configuration.Target.ServiceInvocation!.Auth.Should().BeEquivalentTo(auth);
        created.Dispatch.Descriptor.ServiceInvocation!.Auth!.SenderNyxId!.Subject.ExternalUserId.Should().Be("ou-user-1");
    }

    [Fact]
    public async Task UpdateAsync_ShouldNormalizeServiceInvocationAndDispatchUpdate()
    {
        var actorPort = new RecordingScheduledDispatchActorPort();
        var service = new ScheduledDispatchApplicationService(
            actorPort,
            new RecordingScheduledDispatchQueryPort(),
            new ScheduledDispatchTargetPreparationService());
        var payload = Any.Pack(new StringValue { Value = "invoke" });

        var receipt = await service.UpdateAsync(
            " schedule-1 ",
            new ScheduledDispatchConfiguration(
                "ignored",
                "Invoke",
                new ScheduledDispatchTargetDescriptor(
                    ScheduledDispatchTargetKind.ServiceInvocation,
                    ActorId: "must-clear",
                    Envelope: new EventEnvelope { Payload = Any.Pack(new Empty()) },
                    ServiceInvocation: new ScheduledServiceInvocationTargetDescriptor(
                        new ServiceIdentity
                        {
                            TenantId = "tenant",
                            AppId = "app",
                            Namespace = "default",
                            ServiceId = "svc",
                        },
                        " run ",
                        payload,
                        " rev-1 ")),
                "0 10 * * *",
                null,
                false,
                new Dictionary<string, string>()));

        receipt.ScheduleId.Should().Be("schedule-1");
        actorPort.EnsuredScheduleIds.Should().BeEmpty();
        var updated = actorPort.Updated.Should().ContainSingle().Which;
        updated.ActorId.Should().Be("actor:schedule-1");
        updated.Configuration.Target.ActorId.Should().BeNull();
        updated.Configuration.Target.Envelope.Should().BeNull();
        updated.Configuration.Target.ServiceInvocation.Should().NotBeNull();
        updated.Configuration.Target.ServiceInvocation!.EndpointId.Should().Be("run");
        updated.Configuration.Target.ServiceInvocation.RevisionId.Should().Be("rev-1");
        updated.Configuration.Timezone.Should().Be("UTC");
        updated.Dispatch.TargetActorId.Should().Be(ScheduledDispatchAdapterConventions.ServiceInvocationTargetActorId);
    }

    [Fact]
    public async Task UpdateAsync_WhenScheduleMissing_ShouldThrowNotFoundWithoutEnsuringActor()
    {
        var actorPort = new RecordingScheduledDispatchActorPort();
        actorPort.MissingScheduleIds.Add("missing");
        var service = new ScheduledDispatchApplicationService(
            actorPort,
            new RecordingScheduledDispatchQueryPort(),
            new ScheduledDispatchTargetPreparationService());

        var act = () => service.UpdateAsync(" missing ", CreateEnvelopeConfiguration("ignored"));

        await act.Should().ThrowAsync<ScheduledDispatchNotFoundException>();
        actorPort.EnsuredScheduleIds.Should().BeEmpty();
        actorPort.Updated.Should().BeEmpty();
    }

    [Theory]
    [InlineData("tenant/report")]
    [InlineData("tenant?report")]
    [InlineData("tenant report")]
    public async Task CreateAsync_ShouldRejectRouteUnsafeScheduleId(string scheduleId)
    {
        var service = CreateService();

        var act = () => service.CreateAsync(CreateEnvelopeConfiguration(scheduleId));

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*letters, digits, '.', '_', ':', and '-'*");
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("ABC")]
    [InlineData("abc-123_DEF:ghi.jkl")]
    public void ScheduledDispatchActorIdFormat_ShouldAcceptAsciiScheduleIds(string scheduleId)
    {
        ScheduledDispatchActorId.Format($" {scheduleId} ")
            .Should().Be($"scheduled-dispatch:{scheduleId}");
    }

    [Theory]
    [InlineData("计划")]
    [InlineData("éclair")]
    [InlineData("emoji-🙂")]
    public void ScheduledDispatchActorIdFormat_ShouldRejectNonAsciiScheduleIds(string scheduleId)
    {
        var act = () => ScheduledDispatchActorId.Format(scheduleId);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*letters, digits, '.', '_', ':', and '-'*");
    }

    [Theory]
    [InlineData("", "")]
    [InlineData(" scheduled-dispatch:schedule-1 ", "schedule-1")]
    [InlineData("external-actor", "external-actor")]
    public void ScheduledDispatchActorIdUnformat_ShouldReturnUserScheduleId(string actorId, string expected)
    {
        ScheduledDispatchActorId.Unformat(actorId).Should().Be(expected);
    }

    [Fact]
    public async Task CreateAsync_ShouldRejectInvalidTargetShapes()
    {
        var service = CreateService();

        var missingEnvelopePayload = () => service.CreateAsync(new ScheduledDispatchConfiguration(
            "schedule-1",
            string.Empty,
            new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.Envelope,
                ActorId: "actor-1",
                Envelope: new EventEnvelope()),
            "0 9 * * *",
            "UTC",
            true,
            new Dictionary<string, string>()));
        var missingServiceId = () => service.CreateAsync(new ScheduledDispatchConfiguration(
            "schedule-2",
            string.Empty,
            new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.ServiceInvocation,
                ServiceInvocation: new ScheduledServiceInvocationTargetDescriptor(
                    new ServiceIdentity { TenantId = "tenant" },
                    "run",
                    Any.Pack(new Empty()))),
            "0 9 * * *",
            "UTC",
            true,
            new Dictionary<string, string>()));
        var missingEndpoint = () => service.CreateAsync(new ScheduledDispatchConfiguration(
            "schedule-3",
            string.Empty,
            new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.ServiceInvocation,
                ServiceInvocation: new ScheduledServiceInvocationTargetDescriptor(
                    new ServiceIdentity { ServiceId = "svc" },
                    " ",
                    Any.Pack(new Empty()))),
            "0 9 * * *",
            "UTC",
            true,
            new Dictionary<string, string>()));

        await missingEnvelopePayload.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*envelope payload*");
        await missingServiceId.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*service id*");
        await missingEndpoint.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*endpoint id*");
    }

    [Fact]
    public async Task CreateAsync_ShouldRejectEmptyServiceInvocationAuth()
    {
        var service = CreateService();

        var act = () => service.CreateAsync(new ScheduledDispatchConfiguration(
            "schedule-auth",
            string.Empty,
            new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.ServiceInvocation,
                ServiceInvocation: new ScheduledServiceInvocationTargetDescriptor(
                    new ServiceIdentity { ServiceId = "svc" },
                    "run",
                    Any.Pack(new Empty()),
                    Auth: new ScheduledServiceInvocationAuth())),
            "0 9 * * *",
            "UTC",
            true,
            new Dictionary<string, string>()));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ScheduledDispatchActorPort_ShouldCreateActorWhenMissingAndDispatchServiceInvocationCommands()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort();
        var port = new ScheduledDispatchActorPort(runtime, dispatchPort);
        var configuration = new ScheduledDispatchConfiguration(
            "schedule-1",
            "Invoke",
            new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.ServiceInvocation,
                ServiceInvocation: new ScheduledServiceInvocationTargetDescriptor(
                    new ServiceIdentity { TenantId = "tenant", AppId = "app", Namespace = "default", ServiceId = "svc" },
                    "run",
                    Any.Pack(new StringValue { Value = "invoke" }),
                    "rev-1",
                    new ServiceInvocationCaller { ServiceKey = "tenant:app:default:caller", TenantId = "tenant", AppId = "app" },
                    new ScheduledServiceInvocationAuth(new ScheduledServiceInvocationNyxIdCredentialSource(
                        new ScheduledServiceInvocationNyxIdSubjectRef("lark", "tenant-1", "ou-user-1"),
                        "proxy")))),
            "0 9 * * *",
            "UTC",
            true,
            new Dictionary<string, string> { ["trace"] = "scheduled" },
            ScheduledDispatchScheduleKind.Workflow);
        var prepared = await new ScheduledDispatchTargetPreparationService()
            .PrepareAsync(configuration, "cmd-1", "corr-1");

        var actorId = await port.EnsureScheduleActorAsync("schedule-1");
        var receipt = await port.DispatchCreateAsync(actorId, configuration, prepared);

        actorId.Should().Be("scheduled-dispatch:schedule-1");
        runtime.CreatedIds.Should().ContainSingle().Which.Should().Be("scheduled-dispatch:schedule-1");
        receipt.Accepted.Should().BeTrue();
        var command = dispatchPort.Envelopes.Should().ContainSingle().Which.Payload.Unpack<ScheduledDispatchCreateCommand>();
        command.ScheduleId.Should().Be("schedule-1");
        command.Headers.Should().Contain("trace", "scheduled");
        command.Target.Kind.Should().Be(ScheduledDispatchTargetKindState.ServiceInvocation);
        command.Target.ServiceInvocation.EndpointId.Should().Be("run");
        command.Target.ServiceInvocation.Auth.SenderNyxId.Subject.ExternalUserId.Should().Be("ou-user-1");
        command.ScheduleKind.Should().Be(ScheduledDispatchScheduleKindState.Workflow);
    }

    [Fact]
    public async Task ScheduledDispatchActorPort_ShouldResolveExistingActorAndDispatchLifecycleCommands()
    {
        var runtime = new RecordingActorRuntime();
        runtime.ExistingActors["scheduled-dispatch:schedule-1"] = new RecordingActor("scheduled-dispatch:schedule-1");
        var dispatchPort = new RecordingActorDispatchPort();
        var port = new ScheduledDispatchActorPort(runtime, dispatchPort);

        var ensured = await port.EnsureScheduleActorAsync("schedule-1");
        var resolved = await port.ResolveScheduleActorAsync("schedule-1");
        var missing = await port.ResolveScheduleActorAsync("missing");
        await port.DispatchEnableAsync(ensured, null!);
        await port.DispatchDisableAsync(ensured, "pause");
        await port.DispatchRunNowAsync(ensured, new DateTimeOffset(2026, 6, 9, 8, 0, 0, TimeSpan.Zero));

        ensured.Should().Be("scheduled-dispatch:schedule-1");
        resolved.Should().Be("scheduled-dispatch:schedule-1");
        missing.Should().BeNull();
        runtime.CreatedIds.Should().BeEmpty();
        dispatchPort.Envelopes[0].Payload.Unpack<ScheduledDispatchEnableCommand>().Reason.Should().BeEmpty();
        dispatchPort.Envelopes[1].Payload.Unpack<ScheduledDispatchDisableCommand>().Reason.Should().Be("pause");
        var fire = dispatchPort.Envelopes[2].Payload.Unpack<ScheduledDispatchFireCommand>();
        fire.Manual.Should().BeTrue();
        fire.ScheduledFireAt.ToDateTimeOffset().Should().Be(new DateTimeOffset(2026, 6, 9, 8, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task ScheduledDispatchActorPort_ShouldMapEnvelopeUpdateAndRejectUnsupportedTarget()
    {
        var dispatchPort = new RecordingActorDispatchPort();
        var port = new ScheduledDispatchActorPort(new RecordingActorRuntime(), dispatchPort);
        var configuration = CreateEnvelopeConfiguration("schedule-1");
        var prepared = await new ScheduledDispatchTargetPreparationService()
            .PrepareAsync(configuration, "cmd-1", "corr-1");

        await port.DispatchUpdateAsync("scheduled-dispatch:schedule-1", configuration, prepared);
        var unsupported = () => port.DispatchUpdateAsync(
            "scheduled-dispatch:schedule-1",
            configuration,
            new PreparedScheduledDispatchTarget(
                null,
                new EventEnvelope { Payload = Any.Pack(new Empty()) },
                Any.Pack(new Empty()).TypeUrl,
                new ScheduledDispatchTargetDescriptor((ScheduledDispatchTargetKind)99)));

        var command = dispatchPort.Envelopes.Should().ContainSingle().Which.Payload.Unpack<ScheduledDispatchUpdateCommand>();
        command.Target.Kind.Should().Be(ScheduledDispatchTargetKindState.Envelope);
        command.Target.ActorId.Should().Be("actor-1");
        command.Target.Envelope.Payload.Unpack<Empty>().Should().NotBeNull();
        command.ScheduleKind.Should().Be(ScheduledDispatchScheduleKindState.Generic);
        await unsupported.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Unsupported scheduled dispatch target kind*");
    }

    [Fact]
    public async Task EnableDisableRunNow_ShouldResolveExistingActorAndReturnNotFoundWhenMissing()
    {
        var actorPort = new RecordingScheduledDispatchActorPort();
        var service = new ScheduledDispatchApplicationService(
            actorPort,
            new RecordingScheduledDispatchQueryPort(),
            new ScheduledDispatchTargetPreparationService());

        var enabled = await service.EnableAsync(" schedule-1 ", " resume ");
        var disabled = await service.DisableAsync("schedule-1", null!);
        var runNow = await service.RunNowAsync("schedule-1");
        actorPort.MissingScheduleIds.Add("missing");
        var missing = () => service.RunNowAsync("missing");

        enabled.Should().BeEquivalentTo(new
        {
            ScheduleId = "schedule-1",
            ScheduleActorId = "actor:schedule-1",
            Accepted = true,
            CommandId = "cmd-1",
            CorrelationId = "corr-1",
            AckStage = "accepted",
        });
        enabled.AckedAt.Should().NotBe(default);
        disabled.Should().BeEquivalentTo(new
        {
            ScheduleId = "schedule-1",
            ScheduleActorId = "actor:schedule-1",
            Accepted = true,
            CommandId = "cmd-1",
            CorrelationId = "corr-1",
            AckStage = "accepted",
        });
        disabled.AckedAt.Should().NotBe(default);
        runNow.ScheduleId.Should().Be("schedule-1");
        runNow.ScheduleActorId.Should().Be("actor:schedule-1");
        runNow.CommandId.Should().Be("cmd-1");
        runNow.CorrelationId.Should().Be("corr-1");
        runNow.AckedAt.Should().NotBe(default);
        runNow.AckStage.Should().Be("accepted");
        runNow.IdempotencyKey.Should().Be(
            ScheduledDispatchCalculator.BuildIdempotencyKey("schedule-1", runNow.ScheduledFireAt));
        actorPort.Enabled.Should().ContainSingle().Which.Should().Be(("actor:schedule-1", "resume"));
        actorPort.Disabled.Should().ContainSingle().Which.Should().Be(("actor:schedule-1", string.Empty));
        actorPort.RunNow.Should().ContainSingle().Which.ActorId.Should().Be("actor:schedule-1");
        await missing.Should().ThrowAsync<ScheduledDispatchNotFoundException>();
    }

    [Fact]
    public async Task GetListAndPreview_ShouldNormalizeInputs()
    {
        var queryPort = new RecordingScheduledDispatchQueryPort();
        var service = new ScheduledDispatchApplicationService(
            new RecordingScheduledDispatchActorPort(),
            queryPort,
            new ScheduledDispatchTargetPreparationService());

        await service.GetAsync(" schedule-1 ");
        await service.ListAsync(0, "cursor-1", includeTotalCount: true);
        await service.ListAsync(500);
        var preview = await service.PreviewAsync(
            "0 9 * * *",
            null,
            500,
            new DateTimeOffset(2026, 5, 29, 8, 30, 0, TimeSpan.Zero));
        var invalidPreview = () => service.PreviewAsync("invalid", "UTC", 5);

        queryPort.GetScheduleIds.Should().ContainSingle().Which.Should().Be("schedule-1");
        await service.ListAsync(new ScheduledDispatchListQuery(
            25,
            "cursor-2",
            true,
            ScheduledDispatchTargetKind.ServiceInvocation,
            "chat",
            ScheduledDispatchScheduleKind.Workflow));
        queryPort.FilteredListRequests.Should().HaveCount(3);
        queryPort.FilteredListRequests[0].Should().Be(new ScheduledDispatchListQuery(1, "cursor-1", true));
        queryPort.FilteredListRequests[1].Should().Be(new ScheduledDispatchListQuery(200));
        queryPort.FilteredListRequests[2].Should().Be(new ScheduledDispatchListQuery(
            25,
            "cursor-2",
            true,
            ScheduledDispatchTargetKind.ServiceInvocation,
            "chat",
            ScheduledDispatchScheduleKind.Workflow));
        preview.Timezone.Should().Be("UTC");
        preview.NextFireTimes.Should().HaveCount(100);
        await invalidPreview.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ScheduledDispatchQueryPort_ShouldApplyTypedFiltersBeforePaging()
    {
        var reader = new RecordingScheduledDispatchDocumentReader
        {
            Result = new ProjectionDocumentQueryResult<ScheduledDispatchDocument>
            {
                Items =
                [
                    new ScheduledDispatchDocument
                    {
                        ScheduleId = "workflow-1",
                        TargetKind = ScheduledDispatchTargetKind.ServiceInvocation.ToString(),
                        ServiceEndpointId = "chat",
                        ServiceId = "daily",
                        ScheduleKind = ScheduledDispatchScheduleKind.Workflow.ToString(),
                    },
                ],
                NextCursor = "workflow-cursor",
                TotalCount = 1,
            },
        };
        var port = new ScheduledDispatchQueryPort(reader);

        var result = await port.ListAsync(new ScheduledDispatchListQuery(
            25,
            "cursor",
            true,
            ScheduledDispatchTargetKind.ServiceInvocation,
            "chat",
            ScheduledDispatchScheduleKind.Workflow));

        result.Items.Should().ContainSingle()
            .Which.ScheduleId.Should().Be("workflow-1");
        result.NextCursor.Should().Be("workflow-cursor");
        result.TotalCount.Should().Be(1);
        reader.LastQuery.Should().NotBeNull();
        reader.LastQuery!.Take.Should().Be(25);
        reader.LastQuery.Cursor.Should().Be("cursor");
        reader.LastQuery.IncludeTotalCount.Should().BeTrue();
        reader.LastQuery.Filters.Should().BeEquivalentTo(
            new[]
            {
                new ProjectionDocumentFilter
                {
                    FieldPath = nameof(ScheduledDispatchDocument.TargetKind),
                    Operator = ProjectionDocumentFilterOperator.Eq,
                    Value = ProjectionDocumentValue.FromString(ScheduledDispatchTargetKind.ServiceInvocation.ToString()),
                },
                new ProjectionDocumentFilter
                {
                    FieldPath = nameof(ScheduledDispatchDocument.ServiceEndpointId),
                    Operator = ProjectionDocumentFilterOperator.Eq,
                    Value = ProjectionDocumentValue.FromString("chat"),
                },
                new ProjectionDocumentFilter
                {
                    FieldPath = nameof(ScheduledDispatchDocument.ScheduleKind),
                    Operator = ProjectionDocumentFilterOperator.Eq,
                    Value = ProjectionDocumentValue.FromString(ScheduledDispatchScheduleKind.Workflow.ToString()),
                },
            },
            options => options.ComparingByMembers<ProjectionDocumentValue>());
    }

    [Fact]
    public async Task TargetPreparation_ShouldPreserveExistingEnvelopeIdentityAndCorrelation()
    {
        var service = new ScheduledDispatchTargetPreparationService();
        var envelope = new EventEnvelope
        {
            Id = "existing-command",
            Payload = Any.Pack(new StringValue { Value = "run" }),
            Route = EnvelopeRouteSemantics.CreateDirect("publisher-1", "route-target"),
            Propagation = new EnvelopePropagation { CorrelationId = "existing-correlation" },
        };

        var prepared = await service.PrepareAsync(
            new ScheduledDispatchConfiguration(
                "schedule-1",
                string.Empty,
                new ScheduledDispatchTargetDescriptor(
                    ScheduledDispatchTargetKind.Envelope,
                    Envelope: envelope),
                "0 9 * * *",
                "UTC",
                true,
                new Dictionary<string, string>()),
            "cmd-1",
            "corr-1");

        prepared.TargetActorId.Should().Be("route-target");
        prepared.TriggerEnvelope.Id.Should().Be("existing-command");
        prepared.TriggerEnvelope.Route.PublisherActorId.Should().Be("publisher-1");
        prepared.TriggerEnvelope.Propagation!.CorrelationId.Should().Be("existing-correlation");
        envelope.Id.Should().Be("existing-command");
    }

    [Fact]
    public async Task TargetPreparation_ShouldRejectMissingEnvelopeTargetActorAndUnsupportedKind()
    {
        var service = new ScheduledDispatchTargetPreparationService();
        var missingActor = () => service.PrepareAsync(
            new ScheduledDispatchConfiguration(
                "schedule-1",
                string.Empty,
                new ScheduledDispatchTargetDescriptor(
                    ScheduledDispatchTargetKind.Envelope,
                    Envelope: new EventEnvelope { Payload = Any.Pack(new Empty()) }),
                "0 9 * * *",
                "UTC",
                true,
                new Dictionary<string, string>()),
            "cmd-1",
            "corr-1");
        var unsupported = () => service.PrepareAsync(
            new ScheduledDispatchConfiguration(
                "schedule-1",
                string.Empty,
                new ScheduledDispatchTargetDescriptor((ScheduledDispatchTargetKind)99),
                "0 9 * * *",
                "UTC",
                true,
                new Dictionary<string, string>()),
            "cmd-1",
            "corr-1");

        await missingActor.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*actor id*");
        await unsupported.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Unsupported scheduled dispatch target kind*");
    }

    [Fact]
    public void Calculator_ShouldReportInvalidInputsAndComputeDueTime()
    {
        ScheduledDispatchCalculator.TryGetNextOccurrence(
                string.Empty,
                "UTC",
                DateTimeOffset.UtcNow,
                out _,
                out var missingCronError)
            .Should().BeFalse();
        missingCronError.Should().Be("Cron expression is required.");

        ScheduledDispatchCalculator.TryResolveTimeZone(
                "invalid-zone",
                out _,
                out var timezoneError)
            .Should().BeFalse();
        timezoneError.Should().NotBeNullOrWhiteSpace();

        ScheduledDispatchCalculator.ComputeDueTime(
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow)
            .Should().Be(TimeSpan.FromSeconds(1));
        ScheduledDispatchCalculator.NormalizeTimezone(" Asia/Shanghai ").Should().Be("Asia/Shanghai");
    }

    private static ScheduledDispatchApplicationService CreateService() =>
        new(
            new RecordingScheduledDispatchActorPort(),
            new RecordingScheduledDispatchQueryPort(),
            new ScheduledDispatchTargetPreparationService());

    private static ScheduledDispatchConfiguration CreateEnvelopeConfiguration(string scheduleId) =>
        new(
            scheduleId,
            string.Empty,
            new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.Envelope,
                ActorId: "actor-1",
                Envelope: new EventEnvelope { Payload = Any.Pack(new Empty()) }),
            "0 9 * * *",
            "UTC",
            true,
            new Dictionary<string, string>());

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        public Dictionary<string, IActor> ExistingActors { get; } = new(StringComparer.Ordinal);
        public List<string> CreatedIds { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent
        {
            ct.ThrowIfCancellationRequested();
            var actor = new RecordingActor(id ?? Guid.NewGuid().ToString("N"));
            CreatedIds.Add(actor.Id);
            ExistingActors[actor.Id] = actor;
            return Task.FromResult<IActor>(actor);
        }

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IActor?> GetAsync(string id) =>
            Task.FromResult(ExistingActors.GetValueOrDefault(id));

        public Task<bool> ExistsAsync(string id) =>
            Task.FromResult(ExistingActors.ContainsKey(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public List<EventEnvelope> Envelopes { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Envelopes.Add(envelope.Clone());
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class RecordingActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = new RecordingAgent(id);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class RecordingAgent(string id) : IAgent
    {
        public string Id { get; } = id;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult(string.Empty);

        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<System.Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingScheduledDispatchActorPort : IScheduledDispatchActorPort
    {
        public List<string> EnsuredScheduleIds { get; } = [];
        public HashSet<string> MissingScheduleIds { get; } = new(StringComparer.Ordinal);
        public List<(string ActorId, ScheduledDispatchConfiguration Configuration, PreparedScheduledDispatchTarget Dispatch)> Created { get; } = [];
        public List<(string ActorId, ScheduledDispatchConfiguration Configuration, PreparedScheduledDispatchTarget Dispatch)> Updated { get; } = [];
        public List<(string ActorId, string Reason)> Enabled { get; } = [];
        public List<(string ActorId, string Reason)> Disabled { get; } = [];
        public List<(string ActorId, DateTimeOffset ScheduledFireAt)> RunNow { get; } = [];

        public Task<string> EnsureScheduleActorAsync(string scheduleId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            EnsuredScheduleIds.Add(scheduleId);
            return Task.FromResult($"actor:{scheduleId}");
        }

        public Task<string?> ResolveScheduleActorAsync(string scheduleId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(MissingScheduleIds.Contains(scheduleId) ? null : $"actor:{scheduleId}");
        }

        public Task<DispatchAdmission> DispatchCreateAsync(
            string actorId,
            ScheduledDispatchConfiguration configuration,
            PreparedScheduledDispatchTarget dispatch,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Created.Add((actorId, configuration, dispatch));
            return Task.FromResult(CreateAdmission(actorId));
        }

        public Task<DispatchAdmission> DispatchUpdateAsync(
            string actorId,
            ScheduledDispatchConfiguration configuration,
            PreparedScheduledDispatchTarget dispatch,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Updated.Add((actorId, configuration, dispatch));
            return Task.FromResult(CreateAdmission(actorId));
        }

        public Task<DispatchAdmission> DispatchEnableAsync(
            string actorId,
            string reason,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Enabled.Add((actorId, reason));
            return Task.FromResult(CreateAdmission(actorId));
        }

        public Task<DispatchAdmission> DispatchDisableAsync(
            string actorId,
            string reason,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Disabled.Add((actorId, reason));
            return Task.FromResult(CreateAdmission(actorId));
        }

        public Task<DispatchAdmission> DispatchRunNowAsync(
            string actorId,
            DateTimeOffset scheduledFireAt,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            RunNow.Add((actorId, scheduledFireAt));
            return Task.FromResult(CreateAdmission(actorId));
        }

        private static DispatchAdmission CreateAdmission(string actorId) =>
            new(true, "cmd-1", DateTimeOffset.UtcNow, actorId, "corr-1");
    }

    private sealed class RecordingScheduledDispatchDocumentReader : IProjectionDocumentReader<ScheduledDispatchDocument, string>
    {
        public ProjectionDocumentQuery? LastQuery { get; private set; }
        public ProjectionDocumentQueryResult<ScheduledDispatchDocument> Result { get; set; } = new();

        public Task<ScheduledDispatchDocument?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult<ScheduledDispatchDocument?>(null);

        public Task<ProjectionDocumentQueryResult<ScheduledDispatchDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            LastQuery = query;
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingScheduledDispatchQueryPort : IScheduledDispatchQueryPort
    {
        public List<string> GetScheduleIds { get; } = [];
        public List<(int Take, string? Cursor, bool IncludeTotalCount)> ListRequests { get; } = [];
        public List<ScheduledDispatchListQuery> FilteredListRequests { get; } = [];

        public Task<ScheduledDispatchDetail?> GetAsync(string scheduleId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            GetScheduleIds.Add(scheduleId);
            return Task.FromResult<ScheduledDispatchDetail?>(null);
        }

        public Task<ScheduledDispatchListResult> ListAsync(
            int take = 50,
            string? cursor = null,
            bool includeTotalCount = false,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ListRequests.Add((take, cursor, includeTotalCount));
            return Task.FromResult(new ScheduledDispatchListResult([], null, includeTotalCount ? 0 : null));
        }

        public Task<ScheduledDispatchListResult> ListAsync(
            ScheduledDispatchListQuery query,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            FilteredListRequests.Add(query);
            return Task.FromResult(new ScheduledDispatchListResult([], null, query.IncludeTotalCount ? 0 : null));
        }
    }
}
