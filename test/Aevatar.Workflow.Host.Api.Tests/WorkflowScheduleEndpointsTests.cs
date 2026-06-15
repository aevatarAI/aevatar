using Aevatar.Foundation.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Core.Schedules;
using Aevatar.GAgentService.Infrastructure.Schedules;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Schedules;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowScheduleEndpointsTests
{
    [Fact]
    public async Task Preview_ShouldReturnBadRequest_WhenCronIsInvalid()
    {
        var result = await WorkflowScheduleEndpoints.Preview(
            new WorkflowSchedulePreviewHttpRequest
            {
                CronExpression = "invalid",
                Timezone = "UTC",
            },
            new ThrowingPreviewScheduleService());

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Preview_ShouldReturnOccurrences_WhenRequestIsValid()
    {
        var service = new StaticPreviewScheduleService();

        var result = await WorkflowScheduleEndpoints.Preview(
            new WorkflowSchedulePreviewHttpRequest
            {
                CronExpression = "0 9 * * *",
                Timezone = "UTC",
                Count = 2,
                FromUtc = new DateTimeOffset(2026, 5, 29, 0, 0, 0, TimeSpan.Zero),
            },
            service);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        service.LastCount.Should().Be(2);
        service.LastFromUtc.Should().Be(new DateTimeOffset(2026, 5, 29, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task RunNow_ShouldReturnNotFound_WhenScheduleDoesNotExist()
    {
        var result = await WorkflowScheduleEndpoints.RunNow(
            "missing",
            new NotFoundRunNowScheduleService());

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task Enable_ShouldReturnBadRequest_WhenScheduleIdIsInvalid()
    {
        var result = await WorkflowScheduleEndpoints.Enable(
            "tenant/report",
            null,
            new InvalidEnableScheduleService());

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Get_ShouldReturnBadRequest_WhenScheduleIdIsInvalid()
    {
        var result = await WorkflowScheduleEndpoints.Get(
            "tenant/report",
            new InvalidGetScheduleService());

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Create_ShouldReturnAcceptedAndForwardConfiguration()
    {
        var service = new RecordingScheduleService();

        var result = await WorkflowScheduleEndpoints.Create(
            new WorkflowScheduleConfigurationHttpRequest
            {
                ScheduleId = "schedule-1",
                DisplayName = "Daily",
                WorkflowName = "daily",
                Prompt = "hello",
                CronExpression = "0 9 * * *",
                Timezone = "UTC",
                Enabled = true,
                Headers = new Dictionary<string, string> { ["trace"] = "1" },
                ScopeId = "scope-1",
            },
            service);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        service.Created.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new WorkflowScheduleConfiguration(
                "schedule-1",
                "Daily",
                "daily",
                "hello",
                "0 9 * * *",
                "UTC",
                true,
                new Dictionary<string, string> { ["trace"] = "1" },
                "scope-1"));
    }

    [Fact]
    public async Task Create_ShouldAcceptTenantlessWorkflowScheduleAuth()
    {
        var service = new RecordingScheduleService();

        var result = await WorkflowScheduleEndpoints.Create(
            new WorkflowScheduleConfigurationHttpRequest
            {
                ScheduleId = "schedule-1",
                WorkflowName = "daily",
                Prompt = "hello",
                CronExpression = "0 9 * * *",
                Auth = new WorkflowScheduleAuthHttpRequest
                {
                    SenderNyxId = new WorkflowScheduleNyxIdCredentialSourceHttpRequest
                    {
                        Subject = new WorkflowScheduleNyxIdSubjectRefHttpRequest
                        {
                            Platform = "lark",
                            Tenant = " ",
                            ExternalUserId = "ou-user-1",
                        },
                        Scope = "proxy",
                    },
                },
            },
            service);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        service.Created.Should().ContainSingle()
            .Which.Auth!.SenderNyxId!.Subject.Tenant.Should().BeEmpty();
    }

    [Fact]
    public void WorkflowScheduleModels_ShouldNotExposeDeadSourceActorIdField()
    {
        typeof(WorkflowScheduleConfigurationHttpRequest)
            .GetProperty("SourceActorId")
            .Should()
            .BeNull();
        typeof(WorkflowScheduleConfiguration)
            .GetProperty("SourceActorId")
            .Should()
            .BeNull();
        typeof(WorkflowScheduleSummary)
            .GetProperty("SourceActorId")
            .Should()
            .BeNull();
    }

    [Fact]
    public async Task Create_ShouldReturnBadRequest_WhenAuthIsEmpty()
    {
        var result = await WorkflowScheduleEndpoints.Create(
            new WorkflowScheduleConfigurationHttpRequest
            {
                ScheduleId = "schedule-1",
                WorkflowName = "daily",
                Prompt = "hello",
                CronExpression = "0 9 * * *",
                Auth = new WorkflowScheduleAuthHttpRequest(),
            },
            new RecordingScheduleService());

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Create_ShouldReturnBadRequest_WhenAuthSubjectIsNull()
    {
        var result = await WorkflowScheduleEndpoints.Create(
            new WorkflowScheduleConfigurationHttpRequest
            {
                ScheduleId = "schedule-1",
                WorkflowName = "daily",
                Prompt = "hello",
                CronExpression = "0 9 * * *",
                Auth = new WorkflowScheduleAuthHttpRequest
                {
                    SenderNyxId = new WorkflowScheduleNyxIdCredentialSourceHttpRequest
                    {
                        Subject = null!,
                        Scope = "proxy",
                    },
                },
            },
            new RecordingScheduleService());

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Create_ShouldReturnBadRequest_WhenAuthFieldsAreBlank()
    {
        var result = await WorkflowScheduleEndpoints.Create(
            new WorkflowScheduleConfigurationHttpRequest
            {
                ScheduleId = "schedule-1",
                WorkflowName = "daily",
                Prompt = "hello",
                CronExpression = "0 9 * * *",
                Auth = new WorkflowScheduleAuthHttpRequest
                {
                    SenderNyxId = new WorkflowScheduleNyxIdCredentialSourceHttpRequest
                    {
                        Subject = new WorkflowScheduleNyxIdSubjectRefHttpRequest
                        {
                            Platform = "lark",
                            Tenant = " ",
                            ExternalUserId = "ou-user-1",
                        },
                        Scope = "",
                    },
                },
            },
            new RecordingScheduleService());

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Update_ShouldUseRouteScheduleIdAsFallbackAndMapBadRequest()
    {
        var service = new RecordingScheduleService
        {
            UpdateException = new ArgumentException("invalid update"),
        };

        var result = await WorkflowScheduleEndpoints.Update(
            "route-schedule",
            new WorkflowScheduleConfigurationHttpRequest
            {
                WorkflowName = "daily",
                Prompt = "hello",
                CronExpression = "0 9 * * *",
            },
            service);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        service.Updated.Should().ContainSingle()
            .Which.Configuration.ScheduleId.Should().Be("route-schedule");
    }

    [Fact]
    public async Task Create_ShouldReturnConflict_WhenScheduleCannotBePrepared()
    {
        var service = new RecordingScheduleService
        {
            CreateException = new ScheduledDispatchConflictException("schedule-1", "Schedule target cannot be prepared."),
        };

        var result = await WorkflowScheduleEndpoints.Create(
            new WorkflowScheduleConfigurationHttpRequest
            {
                ScheduleId = "schedule-1",
                WorkflowName = "daily",
                Prompt = "hello",
                CronExpression = "0 9 * * *",
            },
            service);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task Update_ShouldReturnConflict_WhenScheduleCannotBePrepared()
    {
        var service = new RecordingScheduleService
        {
            UpdateException = new ScheduledDispatchConflictException("schedule-1", "Schedule target cannot be prepared."),
        };

        var result = await WorkflowScheduleEndpoints.Update(
            "schedule-1",
            new WorkflowScheduleConfigurationHttpRequest
            {
                WorkflowName = "daily",
                Prompt = "hello",
                CronExpression = "0 9 * * *",
            },
            service);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task Disable_ShouldReturnConflict_WhenScheduleCannotMutate()
    {
        var result = await WorkflowScheduleEndpoints.Disable(
            "schedule-1",
            new WorkflowScheduleStateChangeHttpRequest { Reason = "pause" },
            new ConflictDisableScheduleService());

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task List_ShouldForwardQueryParameters()
    {
        var service = new RecordingScheduleService();

        var result = await WorkflowScheduleEndpoints.List(
            service,
            take: 25,
            cursor: "cursor-1",
            includeTotalCount: true);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        service.LastListTake.Should().Be(25);
        service.LastListCursor.Should().Be("cursor-1");
        service.LastListIncludeTotalCount.Should().BeTrue();
    }

    [Theory]
    [InlineData(" schedule-1 ", "scheduled-dispatch:schedule-1")]
    [InlineData("tenant:daily.report_1", "scheduled-dispatch:tenant:daily.report_1")]
    public void ScheduledDispatchActorId_Format_ShouldNormalizeValidIds(
        string scheduleId,
        string expectedActorId)
    {
        ScheduledDispatchActorId.Format(scheduleId).Should().Be(expectedActorId);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("tenant/report")]
    public void ScheduledDispatchActorId_Format_ShouldRejectInvalidIds(string scheduleId)
    {
        var act = () => ScheduledDispatchActorId.Format(scheduleId);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task ScheduledDispatchActorPort_ShouldCreateMissingActorAndPackCreateCommand()
    {
        var runtime = new RecordingActorRuntime();
        var dispatch = new RecordingActorDispatchPort();
        var port = new ScheduledDispatchActorPort(runtime, dispatch);
        var triggerEnvelope = new EventEnvelope
        {
            Id = "template",
            Payload = Any.Pack(new Empty()),
        };

        var actorId = await port.EnsureScheduleActorAsync("schedule-1");
        var configuration = CreateScheduledDispatchConfiguration("schedule-1");
        var prepared = CreatePreparedScheduledDispatchTarget("target-actor", triggerEnvelope);
        await port.DispatchCreateAsync(actorId, configuration, prepared);
        actorId.Should().Be("scheduled-dispatch:schedule-1");
        runtime.Created.Should().ContainSingle()
            .Which.Should().Be((actorId, typeof(ScheduledDispatchGAgent)));
        dispatch.Envelopes.Should().ContainSingle();
        dispatch.Envelopes.Select(x => x.ActorId).Should().OnlyContain(x => x == actorId);
        dispatch.Envelopes.Select(x => x.Envelope.Id).Should().OnlyContain(x => !string.IsNullOrWhiteSpace(x));
        dispatch.Envelopes.Select(x => x.Envelope.Propagation?.CorrelationId)
            .Should().OnlyContain(x => !string.IsNullOrWhiteSpace(x));

        var configure = dispatch.Envelopes[0].Envelope.Payload.Unpack<ScheduledDispatchCreateCommand>();
        configure.ScheduleId.Should().Be("schedule-1");
        configure.DisplayName.Should().Be("Daily");
        configure.TargetActorId.Should().Be("target-actor");
        configure.TriggerEnvelope.Payload.TypeUrl.Should().Be(triggerEnvelope.Payload.TypeUrl);
        configure.CronExpression.Should().Be("0 9 * * *");
        configure.Timezone.Should().Be("UTC");
        configure.Enabled.Should().BeTrue();
        configure.Headers.Should().Contain("trace", "1");
        configure.PayloadTypeUrl.Should().Be(Any.Pack(new Empty()).TypeUrl);
        configure.Target.Kind.Should().Be(ScheduledDispatchTargetKindState.Envelope);
        configure.Target.ActorId.Should().Be("target-actor");
    }

    [Fact]
    public async Task ScheduledDispatchActorPort_ShouldPackMutationsOnlyWhenActorStateIsConfigured()
    {
        var runtime = new RecordingActorRuntime();
        var dispatch = new RecordingActorDispatchPort();
        var port = new ScheduledDispatchActorPort(runtime, dispatch);
        var actorId = "scheduled-dispatch:schedule-1";
        runtime.Existing[actorId] = new RecordingStatefulActor(actorId, CreateConfiguredState());

        await port.DispatchEnableAsync(actorId, "resume");
        await port.DispatchDisableAsync(actorId, null!);
        await port.DispatchRunNowAsync(
            actorId,
            new DateTimeOffset(2026, 5, 29, 17, 30, 0, TimeSpan.FromHours(8)));

        dispatch.Envelopes.Should().HaveCount(3);
        dispatch.Envelopes.Select(x => x.ActorId).Should().OnlyContain(x => x == actorId);

        dispatch.Envelopes[0].Envelope.Payload.Unpack<ScheduledDispatchEnableCommand>()
            .Reason.Should().Be("resume");
        dispatch.Envelopes[1].Envelope.Payload.Unpack<ScheduledDispatchDisableCommand>()
            .Reason.Should().BeEmpty();
        var fire = dispatch.Envelopes[2].Envelope.Payload.Unpack<ScheduledDispatchFireCommand>();
        fire.Manual.Should().BeTrue();
        fire.ScheduledFireAt.ToDateTimeOffset().Should().Be(
            new DateTimeOffset(2026, 5, 29, 9, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task ScheduledDispatchActorPort_ShouldDispatchMutationsWithoutReadingActorState()
    {
        var runtime = new RecordingActorRuntime();
        var dispatch = new RecordingActorDispatchPort();
        var port = new ScheduledDispatchActorPort(runtime, dispatch);
        var actorId = "scheduled-dispatch:schedule-1";
        runtime.Existing[actorId] = new RecordingStatefulActor(actorId, CreateConfiguredState());

        await port.DispatchCreateAsync(
            actorId,
            CreateScheduledDispatchConfiguration("schedule-1"),
            CreatePreparedScheduledDispatchTarget());

        runtime.Existing[actorId] = new RecordingStatefulActor(actorId, new ScheduledDispatchState());
        await port.DispatchUpdateAsync(
            actorId,
            CreateScheduledDispatchConfiguration("schedule-1"),
            CreatePreparedScheduledDispatchTarget());
        await port.DispatchEnableAsync(actorId, "resume");

        dispatch.Envelopes.Should().HaveCount(3);
        dispatch.Envelopes[0].Envelope.Payload.Unpack<ScheduledDispatchCreateCommand>()
            .ScheduleId.Should().Be("schedule-1");
        dispatch.Envelopes[1].Envelope.Payload.Unpack<ScheduledDispatchUpdateCommand>()
            .ScheduleId.Should().Be("schedule-1");
        dispatch.Envelopes[2].Envelope.Payload.Unpack<ScheduledDispatchEnableCommand>()
            .Reason.Should().Be("resume");
    }

    [Fact]
    public async Task ScheduledDispatchActorPort_EnsureScheduleActorAsync_ShouldRespectAlreadyCanceledToken()
    {
        var runtime = new RecordingActorRuntime();
        runtime.Existing["scheduled-dispatch:schedule-1"] = new RecordingActor("scheduled-dispatch:schedule-1");
        var port = new ScheduledDispatchActorPort(runtime, new RecordingActorDispatchPort());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => port.EnsureScheduleActorAsync("schedule-1", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        runtime.GetRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ScheduledDispatchActorPort_EnsureScheduleActorAsync_ShouldReuseExistingActor()
    {
        var runtime = new RecordingActorRuntime();
        runtime.Existing["scheduled-dispatch:schedule-1"] = new RecordingActor("existing-actor");
        var port = new ScheduledDispatchActorPort(runtime, new RecordingActorDispatchPort());

        var actorId = await port.EnsureScheduleActorAsync("schedule-1");
        var resolved = await port.ResolveScheduleActorAsync("schedule-1");

        actorId.Should().Be("existing-actor");
        resolved.Should().Be("existing-actor");
        runtime.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task ScheduledServiceInvocationDispatchPort_ShouldInvokeExplicitServiceInvocationPort()
    {
        var invocationPort = new RecordingServiceInvocationPort();
        var port = new ScheduledServiceInvocationDispatchPort(
            invocationPort,
            new RecordingScheduledServiceInvocationCredentialExchangePort());

        var receipt = await port.DispatchAsync(
            new ScheduledServiceInvocationDispatchRequest(
                new ServiceInvocationRequest
                {
                    CommandId = "cmd-invoke",
                    CorrelationId = "corr-invoke",
                    Payload = Any.Pack(new Empty()),
                }));

        invocationPort.Requests.Should().ContainSingle();
        receipt.CommandId.Should().Be("cmd-invoke");
        receipt.TargetActorId.Should().Be("service-actor");
        receipt.CorrelationId.Should().Be("corr-invoke");
    }

    [Fact]
    public void ScheduledServiceInvocationDispatchPort_ShouldNotImplementActorDispatchPort()
    {
        typeof(ScheduledServiceInvocationDispatchPort)
            .Should()
            .NotBeAssignableTo<IActorDispatchPort>();
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddLogging()
                .AddOptions()
                .BuildServiceProvider(),
        };
        http.Response.Body = new MemoryStream();
        return http;
    }

    private sealed class ThrowingPreviewScheduleService : EmptyWorkflowScheduleApplicationService
    {
        public override Task<WorkflowSchedulePreview> PreviewAsync(
            string cronExpression,
            string? timezone,
            int count,
            DateTimeOffset? fromUtc = null,
            CancellationToken ct = default) =>
            throw new ArgumentException("Cron expression is invalid.");
    }

    private sealed class StaticPreviewScheduleService : EmptyWorkflowScheduleApplicationService
    {
        public int LastCount { get; private set; }
        public DateTimeOffset? LastFromUtc { get; private set; }

        public override Task<WorkflowSchedulePreview> PreviewAsync(
            string cronExpression,
            string? timezone,
            int count,
            DateTimeOffset? fromUtc = null,
            CancellationToken ct = default)
        {
            LastCount = count;
            LastFromUtc = fromUtc;
            return Task.FromResult(new WorkflowSchedulePreview(
                cronExpression,
                timezone ?? "UTC",
                [
                    new DateTimeOffset(2026, 5, 29, 9, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 5, 30, 9, 0, 0, TimeSpan.Zero),
                ]));
        }
    }

    private sealed class NotFoundRunNowScheduleService : EmptyWorkflowScheduleApplicationService
    {
        public override Task<WorkflowScheduleRunNowReceipt> RunNowAsync(
            string scheduleId,
            CancellationToken ct = default) =>
            throw new ScheduledDispatchNotFoundException(scheduleId);
    }

    private sealed class InvalidEnableScheduleService : EmptyWorkflowScheduleApplicationService
    {
        public override Task<WorkflowScheduleMutationReceipt> EnableAsync(
            string scheduleId,
            string reason,
            CancellationToken ct = default) =>
            throw new ArgumentException("Schedule id may only contain letters, digits, '.', '_', ':', and '-'.");
    }

    private sealed class InvalidGetScheduleService : EmptyWorkflowScheduleApplicationService
    {
        public override Task<WorkflowScheduleDetail?> GetAsync(
            string scheduleId,
            CancellationToken ct = default) =>
            throw new ArgumentException("Schedule id may only contain letters, digits, '.', '_', ':', and '-'.");
    }

    private sealed class ConflictDisableScheduleService : EmptyWorkflowScheduleApplicationService
    {
        public override Task<WorkflowScheduleMutationReceipt> DisableAsync(
            string scheduleId,
            string reason,
            CancellationToken ct = default) =>
            throw new ScheduledDispatchConflictException(scheduleId, "Schedule cannot be disabled.");
    }

    private sealed class RecordingScheduleService : EmptyWorkflowScheduleApplicationService
    {
        public List<WorkflowScheduleConfiguration> Created { get; } = [];
        public List<(string ScheduleId, WorkflowScheduleConfiguration Configuration)> Updated { get; } = [];
        public int? LastListTake { get; private set; }
        public string? LastListCursor { get; private set; }
        public bool? LastListIncludeTotalCount { get; private set; }
        public Exception? CreateException { get; set; }
        public Exception? UpdateException { get; set; }

        public override Task<WorkflowScheduleMutationReceipt> CreateAsync(
            WorkflowScheduleConfiguration configuration,
            CancellationToken ct = default)
        {
            Created.Add(configuration);
            if (CreateException != null)
                throw CreateException;

            return Task.FromResult(new WorkflowScheduleMutationReceipt(
                configuration.ScheduleId,
                $"actor:{configuration.ScheduleId}",
                Accepted: true,
                CommandId: "cmd",
                CorrelationId: "corr",
                AckedAt: DateTimeOffset.UtcNow,
                AckStage: "accepted"));
        }

        public override Task<WorkflowScheduleMutationReceipt> UpdateAsync(
            string scheduleId,
            WorkflowScheduleConfiguration configuration,
            CancellationToken ct = default)
        {
            Updated.Add((scheduleId, configuration));
            if (UpdateException != null)
                throw UpdateException;

            return Task.FromResult(new WorkflowScheduleMutationReceipt(
                scheduleId,
                $"actor:{scheduleId}",
                Accepted: true,
                CommandId: "cmd",
                CorrelationId: "corr",
                AckedAt: DateTimeOffset.UtcNow,
                AckStage: "accepted"));
        }

        public override Task<WorkflowScheduleListResult> ListAsync(
            int take = 50,
            string? cursor = null,
            bool includeTotalCount = false,
            CancellationToken ct = default)
        {
            LastListTake = take;
            LastListCursor = cursor;
            LastListIncludeTotalCount = includeTotalCount;
            return Task.FromResult(new WorkflowScheduleListResult([], null, includeTotalCount ? 0 : null));
        }
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        public Dictionary<string, IActor> Existing { get; } = new(StringComparer.Ordinal);
        public List<(string ActorId, System.Type AgentType)> Created { get; } = [];
        public List<string> GetRequests { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default)
        {
            var actorId = id ?? Guid.NewGuid().ToString("N");
            Created.Add((actorId, agentType));
            return Task.FromResult<IActor>(new RecordingActor(actorId));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IActor?> GetAsync(string id)
        {
            GetRequests.Add(id);
            return Task.FromResult(Existing.GetValueOrDefault(id));
        }

        public Task<bool> ExistsAsync(string id) =>
            Task.FromResult(Existing.ContainsKey(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Envelopes { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            Envelopes.Add((actorId, envelope.Clone()));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class RecordingServiceInvocationPort : IServiceInvocationPort
    {
        public List<ServiceInvocationRequest> Requests { get; } = [];

        public Task<ServiceInvocationAcceptedReceipt> InvokeAsync(
            ServiceInvocationRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(new ServiceInvocationAcceptedReceipt
            {
                CommandId = request.CommandId,
                CorrelationId = request.CorrelationId,
                TargetActorId = "service-actor",
            });
        }
    }

    private sealed class RecordingScheduledServiceInvocationCredentialExchangePort
        : IScheduledServiceInvocationCredentialExchangePort
    {
        public Task<ScheduledServiceInvocationCredentialExchangeResult> IssueSenderNyxIdAsync(
            ScheduledServiceInvocationNyxIdCredentialSource source,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(ScheduledServiceInvocationCredentialExchangeResult.Success("sender-token"));
        }
    }

    private sealed class ThrowingWorkflowRunActorResolver : IWorkflowRunActorResolver
    {
        public Task<WorkflowActorResolutionResult> ResolveOrCreateAsync(
            WorkflowChatRunRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException("Workflow target resolution is not used by this test.");
    }

    private sealed class ThrowingWorkflowChatEnvelopeFactory : ICommandEnvelopeFactory<WorkflowChatRunRequest>
    {
        public EventEnvelope CreateEnvelope(WorkflowChatRunRequest command, CommandContext context) =>
            throw new NotSupportedException("Workflow envelope creation is not used by this test.");
    }

    private sealed class RecordingActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent => throw new NotSupportedException();
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class RecordingStatefulActor(string id, ScheduledDispatchState state) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = new RecordingStatefulAgent(id, state);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class RecordingStatefulAgent(string id, ScheduledDispatchState state) : IAgent<ScheduledDispatchState>
    {
        public string Id { get; } = id;
        public ScheduledDispatchState State { get; } = state;
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult(Id);
        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static ScheduledDispatchConfiguration CreateScheduledDispatchConfiguration(string scheduleId) =>
        new(
            scheduleId,
            "Daily",
            new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.Envelope,
                ActorId: "target-actor",
                Envelope: new EventEnvelope
                {
                    Id = "template",
                    Payload = Any.Pack(new Empty()),
                }),
            "0 9 * * *",
            "UTC",
            true,
            new Dictionary<string, string> { ["trace"] = "1" });

    private static PreparedScheduledDispatchTarget CreatePreparedScheduledDispatchTarget(
        string targetActorId = "target-actor",
        EventEnvelope? triggerEnvelope = null)
    {
        var envelope = triggerEnvelope ?? new EventEnvelope
        {
            Id = "template",
            Payload = Any.Pack(new Empty()),
        };
        return new PreparedScheduledDispatchTarget(
            targetActorId,
            envelope,
            envelope.Payload?.TypeUrl ?? Any.Pack(new Empty()).TypeUrl,
            new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.Envelope,
                ActorId: targetActorId,
                Envelope: envelope));
    }

    private static ScheduledDispatchState CreateConfiguredState() =>
        new()
        {
            ScheduleId = "schedule-1",
            CronExpression = "0 9 * * *",
            TriggerEnvelope = new EventEnvelope
            {
                Id = "template",
                Payload = Any.Pack(new Empty()),
            },
        };

    private abstract class EmptyWorkflowScheduleApplicationService : IWorkflowScheduleApplicationService
    {
        public virtual Task<WorkflowScheduleMutationReceipt> CreateAsync(
            WorkflowScheduleConfiguration configuration,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public virtual Task<WorkflowScheduleMutationReceipt> UpdateAsync(
            string scheduleId,
            WorkflowScheduleConfiguration configuration,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public virtual Task<WorkflowScheduleMutationReceipt> EnableAsync(
            string scheduleId,
            string reason,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public virtual Task<WorkflowScheduleMutationReceipt> DisableAsync(
            string scheduleId,
            string reason,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public virtual Task<WorkflowScheduleDetail?> GetAsync(
            string scheduleId,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public virtual Task<WorkflowScheduleListResult> ListAsync(
            int take = 50,
            string? cursor = null,
            bool includeTotalCount = false,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public virtual Task<WorkflowSchedulePreview> PreviewAsync(
            string cronExpression,
            string? timezone,
            int count,
            DateTimeOffset? fromUtc = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public virtual Task<WorkflowScheduleRunNowReceipt> RunNowAsync(
            string scheduleId,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
