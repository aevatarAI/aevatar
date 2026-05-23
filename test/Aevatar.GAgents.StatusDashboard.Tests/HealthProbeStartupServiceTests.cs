using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.StatusDashboard.Configuration;
using Aevatar.GAgents.StatusDashboard.Executors;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgents.StatusDashboard.Tests;

public sealed class HealthProbeStartupServiceTests
{
    [Fact]
    public async Task StartAsync_ShouldDispatchProbeConfigureCommandWithoutProjectionPriming()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort();
        var service = new HealthProbeStartupService(
            Options.Create(new StatusDashboardOptions
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
            }),
            runtime,
            dispatchPort,
            new HealthProbeExecutorRegistry([new TestHealthProbeExecutor()]),
            NullLogger<HealthProbeStartupService>.Instance);

        await service.StartAsync(CancellationToken.None);

        var actorId = HealthProbeStoreCommands.BuildActorId("self-liveness");
        runtime.GetCalls.Should().ContainSingle().Which.Should().Be(actorId);
        runtime.CreateCalls.Should().ContainSingle().Which.Should().Be(actorId);
        dispatchPort.Dispatches.Should().ContainSingle();
        dispatchPort.Dispatches[0].ActorId.Should().Be(actorId);
        dispatchPort.Dispatches[0].Envelope.Payload.Is(HealthProbeConfigureCommand.Descriptor)
            .Should().BeTrue("startup still dispatches actor configuration");
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

        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Dispatches.Add((actorId, envelope.Clone()));
            return Task.CompletedTask;
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
