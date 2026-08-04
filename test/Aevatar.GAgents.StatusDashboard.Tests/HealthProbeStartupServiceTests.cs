using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.StatusDashboard.Configuration;
using Aevatar.GAgents.StatusDashboard.Executors;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.GAgents.StatusDashboard.Tests;

public sealed class HealthProbeStartupServiceTests
{
    [Fact]
    public async Task StartAsync_ShouldDispatchProbeConfigureCommandWithoutProjectionPriming()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort();
        using var service = CreateService(runtime, dispatchPort);

        await service.StartAsync(CancellationToken.None);

        var actorId = HealthProbeStoreCommands.BuildActorId("self-liveness");
        runtime.GetCalls.Should().Equal(ExpectedGetCalls(actorId));
        runtime.CreateCalls.Should().ContainSingle().Which.Should().Be(actorId);
        dispatchPort.Dispatches.Should().ContainSingle();
        dispatchPort.Dispatches[0].ActorId.Should().Be(actorId);
        dispatchPort.Dispatches[0].Envelope.Payload.Is(HealthProbeConfigureCommand.Descriptor)
            .Should().BeTrue("startup still dispatches actor configuration");
    }

    [Fact]
    public async Task StartAsync_ShouldRedispatchProbeConfigurationAfterOneMinute()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-31T08:00:00Z"));
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort();
        using var service = CreateService(runtime, dispatchPort, timeProvider: timeProvider);

        await service.StartAsync(CancellationToken.None);
        dispatchPort.Dispatches.Should().ContainSingle();

        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await dispatchPort.SecondDispatch.Task.WaitAsync(TimeSpan.FromSeconds(5));

        dispatchPort.Dispatches.Should().HaveCount(2);
        dispatchPort.Dispatches.Should().OnlyContain(dispatch =>
            dispatch.Envelope.Payload.Is(HealthProbeConfigureCommand.Descriptor));
        await service.StopAsync(CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        dispatchPort.Dispatches.Should().HaveCount(2);
    }

    [Fact]
    public async Task StartAsync_ShouldReturnWhenManifestIsEmpty()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort();
        using var service = CreateService(
            runtime,
            dispatchPort,
            new StatusDashboardOptions { UseBuiltInTargets = false });

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        runtime.GetCalls.Should().Equal(RetiredActorIds());
        runtime.CreateCalls.Should().BeEmpty();
        dispatchPort.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task StartAsync_ShouldSkipTargetWhenExecutorKindIsUnknown()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort();
        using var service = CreateService(
            runtime,
            dispatchPort,
            registry: new HealthProbeExecutorRegistry([]));

        await service.StartAsync(CancellationToken.None);

        runtime.GetCalls.Should().Equal(RetiredActorIds());
        runtime.CreateCalls.Should().BeEmpty();
        dispatchPort.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task StartAsync_ShouldDisableExistingRetiredProbeActor()
    {
        var retiredSlug = RetiredStatusProbeTargets.Slugs[0];
        var retiredActorId = HealthProbeStoreCommands.BuildActorId(retiredSlug);
        var runtime = new RecordingActorRuntime();
        runtime.SeedActor(retiredActorId);
        var dispatchPort = new RecordingActorDispatchPort();
        using var service = CreateService(
            runtime,
            dispatchPort,
            new StatusDashboardOptions { UseBuiltInTargets = false });

        await service.StartAsync(CancellationToken.None);

        runtime.CreateCalls.Should().BeEmpty();
        dispatchPort.Dispatches.Should().ContainSingle();
        dispatchPort.Dispatches[0].ActorId.Should().Be(retiredActorId);
        var command = dispatchPort.Dispatches[0].Envelope.Payload.Unpack<HealthProbeConfigureCommand>();
        command.Spec.Slug.Should().Be(retiredSlug);
        command.Spec.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task StartAsync_ShouldContinueWhenDispatchFails()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new ThrowingActorDispatchPort(new InvalidOperationException("dispatch failed"));
        using var service = CreateService(runtime, dispatchPort);

        await service.StartAsync(CancellationToken.None);

        runtime.GetCalls.Should().Equal(ExpectedGetCalls(HealthProbeStoreCommands.BuildActorId("self-liveness")));
        runtime.CreateCalls.Should().ContainSingle();
        dispatchPort.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task StartAsync_ShouldSwallowCancellationWhenTokenIsCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new ThrowingActorDispatchPort(new OperationCanceledException(cancellation.Token));
        using var service = CreateService(runtime, dispatchPort);

        await service.StartAsync(cancellation.Token);

        runtime.GetCalls.Should().Equal(ExpectedGetCalls(HealthProbeStoreCommands.BuildActorId("self-liveness")));
        runtime.CreateCalls.Should().ContainSingle();
        dispatchPort.Attempts.Should().Be(1);
    }

    [Fact]
    public void Source_ShouldNotOwnProjectionActivationOrSleepRetry()
    {
        var source = StripLineComments(File.ReadAllText(GetProductionSourcePath()));

        source.Should().NotContain("EnsureProjectionForActorAsync");
        source.Should().NotContain("HealthProbeProjectionPort");
        source.Should().NotContain(string.Concat("Task", ".Delay"));
    }

    private static string GetProductionSourcePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "agents")))
            directory = directory.Parent;

        directory.Should().NotBeNull("the test runs from a repository checkout");
        return Path.Combine(
            directory!.FullName,
            "agents",
            "Aevatar.GAgents.StatusDashboard",
            "HealthProbeStartupService.cs");
    }

    private static string StripLineComments(string source) =>
        string.Join(
            Environment.NewLine,
            source.Split(Environment.NewLine)
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static HealthProbeStartupService CreateService(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        StatusDashboardOptions? options = null,
        IHealthProbeExecutorRegistry? registry = null,
        TimeProvider? timeProvider = null) =>
        new(
            Options.Create(options ?? BuildOptions()),
            runtime,
            dispatchPort,
            registry ?? new HealthProbeExecutorRegistry([new TestHealthProbeExecutor()]),
            NullLogger<HealthProbeStartupService>.Instance,
            timeProvider: timeProvider);

    private static StatusDashboardOptions BuildOptions() =>
        new()
        {
            UseBuiltInTargets = false,
            Targets =
            [
                new StatusProbeTargetConfig
                {
                    Slug = "self-liveness",
                    Name = "Self liveness",
                    Category = "self",
                    Probe = "test",
                    IntervalSeconds = 60,
                    TimeoutMs = 1_000,
                },
            ],
        };

    private static IReadOnlyList<string> RetiredActorIds() =>
        RetiredStatusProbeTargets.Slugs
            .Select(HealthProbeStoreCommands.BuildActorId)
            .ToArray();

    private static IReadOnlyList<string> ExpectedGetCalls(string currentActorId) =>
        [currentActorId, .. RetiredActorIds()];

    private sealed class TestHealthProbeExecutor : IHealthProbeExecutor
    {
        public string Kind => "test";

        public Task<HealthProbeOutcome> ProbeAsync(
            HealthProbeTargetDescriptor descriptor,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        private readonly Dictionary<string, IActor> _actors = new(StringComparer.Ordinal);

        public List<string> GetCalls { get; } = [];

        public List<string> CreateCalls { get; } = [];

        public void SeedActor(string actorId) =>
            _actors[actorId] = new RecordingActor(actorId);

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent
        {
            var actorId = id ?? Guid.NewGuid().ToString("N");
            CreateCalls.Add(actorId);
            var actor = new RecordingActor(actorId);
            _actors[actorId] = actor;
            return Task.FromResult<IActor>(actor);
        }

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        {
            var actorId = id ?? Guid.NewGuid().ToString("N");
            CreateCalls.Add(actorId);
            var actor = new RecordingActor(actorId);
            _actors[actorId] = actor;
            return Task.FromResult<IActor>(actor);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            _actors.Remove(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id)
        {
            GetCalls.Add(id);
            return Task.FromResult<IActor?>(_actors.GetValueOrDefault(id));
        }

        public Task<bool> ExistsAsync(string id) =>
            Task.FromResult(_actors.ContainsKey(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Dispatches { get; } = [];
        public TaskCompletionSource SecondDispatch { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Dispatches.Add((actorId, envelope.Clone()));
            if (Dispatches.Count == 2)
                SecondDispatch.TrySetResult();
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class ThrowingActorDispatchPort(Exception exception) : IActorDispatchPort
    {
        public int Attempts { get; private set; }

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Attempts++;
            throw exception;
        }
    }

    private sealed class RecordingActor(string id) : IActor
    {
        public string Id { get; } = id;

        public IAgent Agent => throw new NotSupportedException("test stub");

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
