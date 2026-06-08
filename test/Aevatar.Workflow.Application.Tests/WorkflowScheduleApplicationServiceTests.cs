using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Schedules;
using Aevatar.Workflow.Application.DependencyInjection;
using Aevatar.Workflow.Application.Schedules;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowScheduleApplicationServiceTests
{
    [Fact]
    public async Task CreateAsync_ShouldValidateCronAndComputeNextFire()
    {
        var service = CreateService();

        var result = await service.CreateAsync(new WorkflowScheduleCreateCommand(
            "morning",
            "Morning run",
            "0 9 * * *",
            "UTC",
            Target()));

        result.Succeeded.Should().BeTrue();
        result.Value!.ScheduleId.Should().Be("morning");
        result.Value.Status.Should().Be(WorkflowScheduleStatus.Enabled);
        result.Value.NextFireAtUtc.Should().Be(DateTimeOffset.Parse("2026-01-01T09:00:00+00:00"));
    }

    [Fact]
    public async Task CreateAsync_ShouldRejectInvalidCron()
    {
        var service = CreateService();

        var result = await service.CreateAsync(new WorkflowScheduleCreateCommand(
            "bad",
            "Bad run",
            "bad cron",
            "UTC",
            Target()));

        result.Succeeded.Should().BeFalse();
        result.Error.Code.Should().Be(WorkflowScheduleErrorCode.InvalidCron);
    }

    [Fact]
    public async Task PreviewAsync_ShouldReturnBoundedUtcOccurrences()
    {
        var service = CreateService();

        var result = await service.PreviewAsync(
            "*/15 * * * *",
            "UTC",
            DateTimeOffset.Parse("2026-01-01T00:00:00+00:00"),
            3);

        result.Succeeded.Should().BeTrue();
        result.Value!.FireTimesUtc.Should().Equal(
            DateTimeOffset.Parse("2026-01-01T00:15:00+00:00"),
            DateTimeOffset.Parse("2026-01-01T00:30:00+00:00"),
            DateTimeOffset.Parse("2026-01-01T00:45:00+00:00"));
    }

    [Fact]
    public async Task RunNowAsync_ShouldDispatchWithScheduleIdempotencyKey()
    {
        var dispatch = new RecordingDispatchService();
        var service = CreateService(dispatch: dispatch);
        await service.CreateAsync(new WorkflowScheduleCreateCommand(
            "morning",
            "Morning run",
            "0 9 * * *",
            "UTC",
            Target()));

        var scheduledFireAt = DateTimeOffset.Parse("2026-01-01T09:00:00+00:00");
        var result = await service.RunNowAsync(new WorkflowScheduleFireRequest("morning", scheduledFireAt));

        result.Succeeded.Should().BeTrue();
        result.Value!.Status.Should().Be(WorkflowScheduleFireStatus.Accepted);
        var expectedKey = "schedule:morning:fire:2026-01-01T09:00:00.0000000+00:00";
        result.Value.Run.IdempotencyKey.Should().Be(expectedKey);
        result.Value.Run.AcceptedCommandId.Should().Be(expectedKey);
        dispatch.Requests.Should().ContainSingle();
        dispatch.Requests[0].CommandIdSeed.Should().Be(expectedKey);
        dispatch.Requests[0].CorrelationIdSeed.Should().Be(expectedKey);
        dispatch.Requests[0].Prompt.Should().Be("run the morning workflow");
        dispatch.Requests[0].Source!.Kind.Should().Be(WorkflowChatSourceKind.CatalogWorkflow);
        dispatch.Requests[0].LlmControl.Should().BeNull();
    }

    [Fact]
    public async Task RunNowAsync_ShouldReturnDuplicateForSameScheduledFire()
    {
        var dispatch = new RecordingDispatchService();
        var service = CreateService(dispatch: dispatch);
        await service.CreateAsync(new WorkflowScheduleCreateCommand(
            "morning",
            "Morning run",
            "0 9 * * *",
            "UTC",
            Target()));

        var scheduledFireAt = DateTimeOffset.Parse("2026-01-01T09:00:00+00:00");
        await service.RunNowAsync(new WorkflowScheduleFireRequest("morning", scheduledFireAt));
        var duplicate = await service.RunNowAsync(new WorkflowScheduleFireRequest("morning", scheduledFireAt));

        duplicate.Succeeded.Should().BeTrue();
        duplicate.Value!.Status.Should().Be(WorkflowScheduleFireStatus.Duplicate);
        dispatch.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task RunNowAsync_ShouldExchangeSenderNyxIdTokenForScheduleAuth()
    {
        var dispatch = new RecordingDispatchService();
        var exchange = new RecordingCredentialExchangePort("sender-token");
        var service = CreateService(credentialExchange: exchange, dispatch: dispatch);
        await service.CreateAsync(new WorkflowScheduleCreateCommand(
            "morning",
            "Morning run",
            "0 9 * * *",
            "UTC",
            Target(auth: Auth())));

        var scheduledFireAt = DateTimeOffset.Parse("2026-01-01T09:00:00+00:00");
        var result = await service.RunNowAsync(new WorkflowScheduleFireRequest("morning", scheduledFireAt));

        result.Succeeded.Should().BeTrue();
        exchange.Sources.Should().ContainSingle().Which.Should().BeEquivalentTo(Auth().SenderNyxId);
        dispatch.Requests.Should().ContainSingle();
        dispatch.Requests[0].LlmControl.Should().NotBeNull();
        dispatch.Requests[0].LlmControl!.SenderNyxIdAccessToken.Should().Be("sender-token");
    }

    [Fact]
    public async Task RunNowAsync_ShouldRejectWhenScheduleAuthExchangeFails()
    {
        var store = new MemoryStore();
        var dispatch = new RecordingDispatchService();
        var exchange = new RecordingCredentialExchangePort(error: "exchange failed");
        var service = CreateService(store: store, credentialExchange: exchange, dispatch: dispatch);
        await service.CreateAsync(new WorkflowScheduleCreateCommand(
            "morning",
            "Morning run",
            "0 9 * * *",
            "UTC",
            Target(auth: Auth())));

        var scheduledFireAt = DateTimeOffset.Parse("2026-01-01T09:00:00+00:00");
        var result = await service.RunNowAsync(new WorkflowScheduleFireRequest("morning", scheduledFireAt));

        result.Succeeded.Should().BeFalse();
        result.Error.Code.Should().Be(WorkflowScheduleErrorCode.CredentialExchangeFailed);
        dispatch.Requests.Should().BeEmpty();
        var run = await store.GetRunAsync("schedule:morning:fire:2026-01-01T09:00:00.0000000+00:00");
        run.Should().NotBeNull();
        run!.Status.Should().Be(WorkflowScheduleFireStatus.Rejected);
        run.Error.Should().Be("exchange failed");
    }

    [Fact]
    public async Task CreateAsync_ShouldRejectInvalidScheduleAuth()
    {
        var service = CreateService();

        var result = await service.CreateAsync(new WorkflowScheduleCreateCommand(
            "morning",
            "Morning run",
            "0 9 * * *",
            "UTC",
            Target(auth: new WorkflowScheduleAuth(new WorkflowScheduleNyxIdCredentialSource(
                new WorkflowScheduleNyxIdSubjectRef("", "tenant", "user"),
                "proxy")))));

        result.Succeeded.Should().BeFalse();
        result.Error.Code.Should().Be(WorkflowScheduleErrorCode.InvalidTarget);
    }

    [Fact]
    public async Task RunNowAsync_ShouldNotExchangeTokenForDuplicateScheduledFire()
    {
        var dispatch = new RecordingDispatchService();
        var exchange = new RecordingCredentialExchangePort("sender-token");
        var service = CreateService(credentialExchange: exchange, dispatch: dispatch);
        await service.CreateAsync(new WorkflowScheduleCreateCommand(
            "morning",
            "Morning run",
            "0 9 * * *",
            "UTC",
            Target(auth: Auth())));

        var scheduledFireAt = DateTimeOffset.Parse("2026-01-01T09:00:00+00:00");
        await service.RunNowAsync(new WorkflowScheduleFireRequest("morning", scheduledFireAt));
        await service.RunNowAsync(new WorkflowScheduleFireRequest("morning", scheduledFireAt));

        exchange.Sources.Should().ContainSingle();
        dispatch.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task RunNowAsync_ShouldNotAdvanceScheduleByDefault()
    {
        var store = new MemoryStore();
        var service = CreateService(store: store);
        await service.CreateAsync(new WorkflowScheduleCreateCommand(
            "morning",
            "Morning run",
            "0 9 * * *",
            "UTC",
            Target()));

        var scheduledFireAt = DateTimeOffset.Parse("2026-01-01T09:00:00+00:00");
        await service.RunNowAsync(new WorkflowScheduleFireRequest("morning", scheduledFireAt));

        var definition = await store.GetAsync("morning");
        definition!.NextFireAtUtc.Should().Be(DateTimeOffset.Parse("2026-01-01T09:00:00+00:00"));
    }

    [Fact]
    public async Task RunNowAsync_ShouldAdvanceScheduleWhenRequested()
    {
        var store = new MemoryStore();
        var service = CreateService(store: store);
        await service.CreateAsync(new WorkflowScheduleCreateCommand(
            "morning",
            "Morning run",
            "0 9 * * *",
            "UTC",
            Target()));

        var scheduledFireAt = DateTimeOffset.Parse("2026-01-01T09:00:00+00:00");
        await service.RunNowAsync(new WorkflowScheduleFireRequest(
            "morning",
            scheduledFireAt,
            AdvanceSchedule: true));

        var definition = await store.GetAsync("morning");
        definition!.NextFireAtUtc.Should().Be(DateTimeOffset.Parse("2026-01-02T09:00:00+00:00"));
    }

    [Fact]
    public async Task CreateAsync_ShouldScheduleWakeupForEnabledSchedule()
    {
        var wakeup = new RecordingWakeupScheduler();
        var service = CreateService(wakeupScheduler: wakeup);

        await service.CreateAsync(new WorkflowScheduleCreateCommand(
            "morning",
            "Morning run",
            "0 9 * * *",
            "UTC",
            Target()));

        wakeup.Scheduled.Should().ContainSingle(x => x.ScheduleId == "morning");
    }

    [Fact]
    public void AddWorkflowApplication_ShouldRegisterScheduleApplicationService()
    {
        var services = new ServiceCollection();

        services.AddWorkflowApplication();

        services.Should().Contain(x =>
            x.ServiceType == typeof(IWorkflowScheduleApplicationService) &&
            x.ImplementationType == typeof(WorkflowScheduleApplicationService));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IWorkflowScheduleWakeupScheduler) &&
            x.ImplementationType == typeof(NoopWorkflowScheduleWakeupScheduler));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IWorkflowScheduleCredentialExchangePort) &&
            x.ImplementationType == typeof(NoopWorkflowScheduleCredentialExchangePort));
    }

    private static WorkflowScheduleApplicationService CreateService(
        IWorkflowScheduleStore? store = null,
        IWorkflowScheduleWakeupScheduler? wakeupScheduler = null,
        IWorkflowScheduleCredentialExchangePort? credentialExchange = null,
        RecordingDispatchService? dispatch = null) =>
        new(
            store ?? new MemoryStore(),
            wakeupScheduler ?? new NoopWorkflowScheduleWakeupScheduler(),
            credentialExchange ?? new NoopWorkflowScheduleCredentialExchangePort(),
            dispatch ?? new RecordingDispatchService(),
            new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00+00:00")));

    private static WorkflowScheduleTarget Target(WorkflowScheduleAuth? auth = null) =>
        new(
            "run the morning workflow",
            WorkflowChatSource.CatalogWorkflow("direct"),
            Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["source"] = "schedule",
            },
            Auth: auth);

    private static WorkflowScheduleAuth Auth() =>
        new(new WorkflowScheduleNyxIdCredentialSource(
            new WorkflowScheduleNyxIdSubjectRef("lark", "tenant-1", "user-1"),
            "urn:nyxid:scope:proxy"));

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingCredentialExchangePort(string? accessToken = null, string? error = null)
        : IWorkflowScheduleCredentialExchangePort
    {
        public List<WorkflowScheduleNyxIdCredentialSource> Sources { get; } = [];

        public Task<WorkflowScheduleCredentialExchangeResult> IssueSenderNyxIdAsync(
            WorkflowScheduleNyxIdCredentialSource source,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Sources.Add(source);
            return Task.FromResult(error == null
                ? WorkflowScheduleCredentialExchangeResult.Success(accessToken ?? "token")
                : WorkflowScheduleCredentialExchangeResult.Failure(error));
        }
    }

    private sealed class RecordingDispatchService
        : ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
    {
        public List<WorkflowChatRunRequest> Requests { get; } = [];

        public Task<CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>> DispatchAsync(
            WorkflowChatRunRequest command,
            CancellationToken ct = default)
        {
            Requests.Add(command);
            return Task.FromResult(CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Success(
                new WorkflowChatRunAcceptedReceipt(
                    "actor-1",
                    command.WorkflowName ?? "direct",
                    command.CommandIdSeed ?? "cmd",
                    command.CorrelationIdSeed ?? "corr")));
        }
    }

    private sealed class RecordingWakeupScheduler : IWorkflowScheduleWakeupScheduler
    {
        public List<WorkflowScheduleDefinition> Scheduled { get; } = [];

        public List<string> Canceled { get; } = [];

        public Task ScheduleAsync(
            WorkflowScheduleDefinition definition,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Scheduled.Add(definition);
            return Task.CompletedTask;
        }

        public Task CancelAsync(
            string scheduleId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Canceled.Add(scheduleId);
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryStore : IWorkflowScheduleStore
    {
        private readonly InMemoryStoreInner _inner = new();

        public Task<WorkflowScheduleDefinition?> GetAsync(string scheduleId, CancellationToken ct = default) =>
            _inner.GetAsync(scheduleId, ct);

        public Task<IReadOnlyList<WorkflowScheduleDefinition>> ListAsync(CancellationToken ct = default) =>
            _inner.ListAsync(ct);

        public Task AddAsync(WorkflowScheduleDefinition definition, CancellationToken ct = default) =>
            _inner.AddAsync(definition, ct);

        public Task UpdateAsync(WorkflowScheduleDefinition definition, CancellationToken ct = default) =>
            _inner.UpdateAsync(definition, ct);

        public Task<WorkflowScheduleRunRecord?> GetRunAsync(string idempotencyKey, CancellationToken ct = default) =>
            _inner.GetRunAsync(idempotencyKey, ct);

        public Task AddRunAsync(WorkflowScheduleRunRecord run, CancellationToken ct = default) =>
            _inner.AddRunAsync(run, ct);

        public Task UpdateRunAsync(WorkflowScheduleRunRecord run, CancellationToken ct = default) =>
            _inner.UpdateRunAsync(run, ct);
    }

    private sealed class InMemoryStoreInner
    {
        private readonly Dictionary<string, WorkflowScheduleDefinition> _schedules = new(StringComparer.Ordinal);
        private readonly Dictionary<string, WorkflowScheduleRunRecord> _runs = new(StringComparer.Ordinal);

        public Task<WorkflowScheduleDefinition?> GetAsync(string scheduleId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_schedules.GetValueOrDefault(scheduleId));
        }

        public Task<IReadOnlyList<WorkflowScheduleDefinition>> ListAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<WorkflowScheduleDefinition>>(_schedules.Values.ToList());
        }

        public Task AddAsync(WorkflowScheduleDefinition definition, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _schedules.Add(definition.ScheduleId, definition);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(WorkflowScheduleDefinition definition, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _schedules[definition.ScheduleId] = definition;
            return Task.CompletedTask;
        }

        public Task<WorkflowScheduleRunRecord?> GetRunAsync(string idempotencyKey, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_runs.GetValueOrDefault(idempotencyKey));
        }

        public Task AddRunAsync(WorkflowScheduleRunRecord run, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _runs.Add(run.IdempotencyKey, run);
            return Task.CompletedTask;
        }

        public Task UpdateRunAsync(WorkflowScheduleRunRecord run, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _runs[run.IdempotencyKey] = run;
            return Task.CompletedTask;
        }
    }
}
