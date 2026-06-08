using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Schedules;
using Aevatar.Workflow.Application.Schedules;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Core.Schedules;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Aevatar.Workflow.Infrastructure.DependencyInjection;
using Aevatar.Workflow.Infrastructure.Schedules;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowScheduleInfrastructureTests
{
    [Fact]
    public void MapWorkflowCapabilityEndpoints_ShouldRegisterScheduleRoutes()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        using var app = builder.Build();

        app.MapGroup("/api").MapWorkflowScheduleEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(x => x.RoutePattern.RawText)
            .Where(x => x != null)
            .ToHashSet(StringComparer.Ordinal);

        routes.Should().Contain("/api/workflow-schedules/");
        routes.Should().Contain("/api/workflow-schedules/{scheduleId}");
        routes.Should().Contain("/api/workflow-schedules/{scheduleId}:enable");
        routes.Should().Contain("/api/workflow-schedules/{scheduleId}:disable");
        routes.Should().Contain("/api/workflow-schedules/{scheduleId}:run-now");
        routes.Should().Contain("/api/workflow-schedules/preview");
    }

    [Fact]
    public void AddWorkflowCapabilityServices_ShouldRegisterScheduleStore()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkflowSchedules:Store:StorePath"] = "/tmp/aevatar-workflow-schedules.pb",
            })
            .Build();

        services.AddWorkflowCapability(configuration);

        services.Should().Contain(x =>
            x.ServiceType == typeof(IWorkflowScheduleStore) &&
            x.ImplementationType == typeof(FileWorkflowScheduleStore));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IWorkflowScheduleApplicationService) &&
            x.ImplementationType == typeof(WorkflowScheduleApplicationService));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IHostedService) &&
            x.ImplementationType == typeof(WorkflowScheduleDispatcherHostedService));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<WorkflowScheduleStoreOptions>>().Value.StorePath
            .Should().Be("/tmp/aevatar-workflow-schedules.pb");
    }

    [Fact]
    public async Task FileWorkflowScheduleStore_ShouldRoundTripDefinitionsAndRuns()
    {
        var path = Path.Combine(Path.GetTempPath(), $"workflow-schedules-{Guid.NewGuid():N}.pb");
        try
        {
            var store = new FileWorkflowScheduleStore(
                Options.Create(new WorkflowScheduleStoreOptions { StorePath = path }));
            var definition = new WorkflowScheduleDefinition(
                "schedule-1",
                "Schedule One",
                "0 9 * * *",
                "UTC",
                WorkflowScheduleStatus.Enabled,
                new WorkflowScheduleTarget(
                    "hello",
                    WorkflowChatSource.CatalogWorkflow("direct"),
                    Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["source"] = "test",
                    }),
                DateTimeOffset.Parse("2026-01-01T00:00:00+00:00"),
                DateTimeOffset.Parse("2026-01-01T00:00:00+00:00"),
                DateTimeOffset.Parse("2026-01-01T09:00:00+00:00"),
                new WorkflowScheduleWakeupLease(
                    "workflow-schedule-wakeup:schedule-1",
                    "workflow-schedule-due:schedule-1",
                    7,
                    WorkflowScheduleWakeupBackend.Dedicated,
                    2));
            var run = new WorkflowScheduleRunRecord(
                "run-1",
                "schedule-1",
                DateTimeOffset.Parse("2026-01-01T09:00:00+00:00"),
                DateTimeOffset.Parse("2026-01-01T09:00:01+00:00"),
                "schedule:schedule-1:fire:2026-01-01T09:00:00.0000000+00:00",
                WorkflowScheduleFireStatus.Accepted,
                "cmd-1",
                "corr-1",
                "actor-1");

            await store.AddAsync(definition);
            await store.AddRunAsync(run);

            var reloaded = new FileWorkflowScheduleStore(
                Options.Create(new WorkflowScheduleStoreOptions { StorePath = path }));
            var storedDefinition = await reloaded.GetAsync("schedule-1");
            var storedRun = await reloaded.GetRunAsync(run.IdempotencyKey);

            storedDefinition.Should().BeEquivalentTo(definition, options => options
                .Excluding(x => x.Path.EndsWith(".Headers", StringComparison.Ordinal)));
            storedDefinition!.Target.Headers.Should().BeEmpty();
            storedRun.Should().BeEquivalentTo(run);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task OrleansReminderWakeupScheduler_ShouldScheduleDueEventAndPersistLease()
    {
        var store = new InMemoryWorkflowScheduleStore();
        var callbacks = new RecordingRuntimeCallbackScheduler();
        var runtime = new RecordingActorRuntime();
        var scheduler = new OrleansReminderWorkflowScheduleWakeupScheduler(
            callbacks,
            runtime,
            store,
            new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T08:00:00+00:00")));
        var definition = Definition("schedule-1") with
        {
            NextFireAtUtc = DateTimeOffset.Parse("2026-01-01T09:00:00+00:00"),
        };
        await store.AddAsync(definition);

        await scheduler.ScheduleAsync(definition);

        callbacks.Timeouts.Should().ContainSingle();
        var request = callbacks.Timeouts[0];
        request.ActorId.Should().Be("workflow-schedule-wakeup:schedule-1");
        runtime.Created.Should().ContainSingle().Which.Should().Be((
            typeof(WorkflowScheduleWakeupGAgent),
            "workflow-schedule-wakeup:schedule-1"));
        request.CallbackId.Should().Be("workflow-schedule-due:schedule-1");
        request.DueTime.Should().Be(TimeSpan.FromHours(1));
        request.DeliveryMode.Should().Be(RuntimeCallbackDeliveryMode.EnvelopeRedelivery);
        request.TriggerEnvelope.Payload.Should().NotBeNull();
        request.TriggerEnvelope.Payload!.Is(WorkflowScheduleDueEvent.Descriptor).Should().BeTrue();
        var due = request.TriggerEnvelope.Payload.Unpack<WorkflowScheduleDueEvent>();
        due.ScheduleId.Should().Be("schedule-1");
        due.ScheduledFireAtUnixTimeMs.Should().Be(DateTimeOffset.Parse("2026-01-01T09:00:00+00:00").ToUnixTimeMilliseconds());

        var stored = await store.GetAsync("schedule-1");
        stored!.WakeupLease.Should().BeEquivalentTo(new WorkflowScheduleWakeupLease(
            "workflow-schedule-wakeup:schedule-1",
            "workflow-schedule-due:schedule-1",
            1,
            WorkflowScheduleWakeupBackend.Dedicated,
            2));
    }

    [Fact]
    public async Task OrleansReminderWakeupScheduler_ShouldCancelPersistedLease()
    {
        var store = new InMemoryWorkflowScheduleStore();
        var callbacks = new RecordingRuntimeCallbackScheduler();
        var runtime = new RecordingActorRuntime();
        var scheduler = new OrleansReminderWorkflowScheduleWakeupScheduler(
            callbacks,
            runtime,
            store,
            new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T08:00:00+00:00")));
        var definition = Definition("schedule-1") with
        {
            WakeupLease = new WorkflowScheduleWakeupLease(
                "workflow-schedule-wakeup:schedule-1",
                "workflow-schedule-due:schedule-1",
                4,
                WorkflowScheduleWakeupBackend.Dedicated,
                2),
        };
        await store.AddAsync(definition);

        await scheduler.CancelAsync("schedule-1");

        callbacks.Cancelled.Should().ContainSingle().Which.Should().BeEquivalentTo(new RuntimeCallbackLease(
            "workflow-schedule-wakeup:schedule-1",
            "workflow-schedule-due:schedule-1",
            4,
            RuntimeCallbackBackend.Dedicated)
        {
            SlotEpoch = 2,
        });
        var stored = await store.GetAsync("schedule-1");
        stored!.WakeupLease.Should().BeNull();
    }

    [Fact]
    public void AddWorkflowScheduleInfrastructure_ShouldUseNoopWakeupByDefault()
    {
        var services = new ServiceCollection();
        services.AddWorkflowScheduleInfrastructure();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IWorkflowScheduleWakeupScheduler>()
            .Should().BeOfType<NoopWorkflowScheduleWakeupScheduler>();
    }

    [Fact]
    public void AddWorkflowScheduleInfrastructure_ShouldUseOrleansWakeupWhenEnabled()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IActorRuntimeCallbackScheduler, RecordingRuntimeCallbackScheduler>();
        services.AddSingleton<IActorRuntime, RecordingActorRuntime>();
        services.AddWorkflowScheduleInfrastructure(
            configureWakeup: options => options.UseOrleansReminders = true);

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IWorkflowScheduleWakeupScheduler>()
            .Should().BeOfType<OrleansReminderWorkflowScheduleWakeupScheduler>();
    }

    [Fact]
    public async Task WorkflowScheduleDueEventHandlerPort_ShouldFireAndAdvanceSchedule()
    {
        var schedules = new RecordingScheduleApplicationService();
        var handler = new WorkflowScheduleDueEventHandlerPort(schedules);

        await handler.HandleDueAsync(new WorkflowScheduleDueEvent
        {
            ScheduleId = "schedule-1",
            ScheduledFireAtUnixTimeMs = DateTimeOffset.Parse("2026-01-01T09:00:00+00:00").ToUnixTimeMilliseconds(),
        });

        schedules.FireRequests.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new WorkflowScheduleFireRequest(
                "schedule-1",
                DateTimeOffset.Parse("2026-01-01T09:00:00+00:00"),
                Force: false,
                AdvanceSchedule: true));
    }

    private static WorkflowScheduleDefinition Definition(string scheduleId) =>
        new(
            scheduleId,
            "Schedule One",
            "0 9 * * *",
            "UTC",
            WorkflowScheduleStatus.Enabled,
            new WorkflowScheduleTarget("hello", WorkflowChatSource.CatalogWorkflow("direct")),
            DateTimeOffset.Parse("2026-01-01T00:00:00+00:00"),
            DateTimeOffset.Parse("2026-01-01T00:00:00+00:00"),
            DateTimeOffset.Parse("2026-01-01T09:00:00+00:00"));

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingRuntimeCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public List<RuntimeCallbackTimeoutRequest> Timeouts { get; } = [];

        public List<RuntimeCallbackLease> Cancelled { get; } = [];

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Timeouts.Add(request);
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                Timeouts.Count,
                RuntimeCallbackBackend.Dedicated)
            {
                SlotEpoch = 2,
            });
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                1,
                RuntimeCallbackBackend.Dedicated)
            {
                SlotEpoch = 2,
            });

        public Task CancelAsync(
            RuntimeCallbackLease lease,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Cancelled.Add(lease);
            return Task.CompletedTask;
        }

        public Task PurgeActorAsync(
            string actorId,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        private readonly HashSet<string> _actors = new(StringComparer.Ordinal);

        public List<(Type AgentType, string ActorId)> Created { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(
            string? id = null,
            CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(
            Type agentType,
            string? id = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var actorId = id ?? Guid.NewGuid().ToString("N");
            Created.Add((agentType, actorId));
            _actors.Add(actorId);
            return Task.FromResult<IActor>(new RecordingActor(actorId));
        }

        public Task<IActor> CreateByKindAsync(
            string agentKind,
            string? id = null,
            CancellationToken ct = default) =>
            CreateAsync(typeof(WorkflowScheduleWakeupGAgent), id, ct);

        public Task DestroyAsync(
            string id,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _actors.Remove(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id) =>
            Task.FromResult<IActor?>(_actors.Contains(id) ? new RecordingActor(id) : null);

        public Task<bool> ExistsAsync(string id) =>
            Task.FromResult(_actors.Contains(id));

        public Task LinkAsync(
            string parentId,
            string childId,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task UnlinkAsync(
            string childId,
            CancellationToken ct = default) =>
            Task.CompletedTask;
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

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult(Id);

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<Type>>([]);
    }

    private sealed class RecordingScheduleApplicationService : IWorkflowScheduleApplicationService
    {
        public List<WorkflowScheduleFireRequest> FireRequests { get; } = [];

        public Task<WorkflowScheduleResult<WorkflowScheduleFireResult>> RunNowAsync(
            WorkflowScheduleFireRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            FireRequests.Add(request);
            return Task.FromResult(WorkflowScheduleResult<WorkflowScheduleFireResult>.Success(
                new WorkflowScheduleFireResult(
                    WorkflowScheduleFireStatus.Accepted,
                    new WorkflowScheduleRunRecord(
                        "run-1",
                        request.ScheduleId,
                        request.ScheduledFireAtUtc ?? DateTimeOffset.UnixEpoch,
                        DateTimeOffset.Parse("2026-01-01T09:00:01+00:00"),
                        "key",
                        WorkflowScheduleFireStatus.Accepted))));
        }

        public Task<WorkflowScheduleResult<WorkflowScheduleDefinition>> CreateAsync(WorkflowScheduleCreateCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkflowScheduleResult<WorkflowScheduleDefinition>> UpdateAsync(string scheduleId, WorkflowScheduleUpdateCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkflowScheduleResult<WorkflowScheduleDefinition>> EnableAsync(string scheduleId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkflowScheduleResult<WorkflowScheduleDefinition>> DisableAsync(string scheduleId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkflowScheduleResult<WorkflowScheduleDefinition>> GetAsync(string scheduleId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkflowScheduleListResult> ListAsync(WorkflowScheduleListQuery query, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkflowScheduleResult<WorkflowSchedulePreview>> PreviewAsync(string cron, string timezone, DateTimeOffset? fromUtc, int count, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
