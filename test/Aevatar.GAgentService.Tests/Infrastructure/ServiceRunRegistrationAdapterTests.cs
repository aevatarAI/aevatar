using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Core.GAgents;
using Aevatar.GAgentService.Infrastructure.Adapters;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Infrastructure;

public sealed class ServiceRunRegistrationAdapterTests
{
    [Fact]
    public async Task RegisterAsync_ShouldCreateActorWithCompositeId_AndDispatchRegisterEnvelope()
    {
        var runtime = new RecordingRunRegistryRuntime();
        var dispatchPort = new RecordingDispatchPort();
        var projectionPort = new RecordingServiceRunProjectionPort();
        var adapter = new ServiceRunRegistrationAdapter(runtime, dispatchPort, projectionPort);

        var record = BuildRecord(scopeId: "tenant-1", serviceId: "svc-1", runId: "run-1");
        var result = await adapter.RegisterAsync(record);

        var expectedActorId = ServiceRunIds.BuildActorId("tenant-1", "svc-1", "run-1");
        result.RunActorId.Should().Be(expectedActorId);
        result.RunId.Should().Be("run-1");
        runtime.CreateCalls.Should().ContainSingle();
        runtime.CreateCalls[0].agentType.Should().Be(typeof(ServiceRunGAgent));
        runtime.CreateCalls[0].actorId.Should().Be(expectedActorId);
        projectionPort.EnsureCalls.Should().Equal(expectedActorId);
        dispatchPort.Calls.Should().ContainSingle();
        dispatchPort.Calls[0].actorId.Should().Be(expectedActorId);
        dispatchPort.Calls[0].envelope.Payload.TypeUrl.Should().Contain("RegisterServiceRunRequested");
    }

    [Fact]
    public async Task RegisterAsync_ShouldNotCollide_OnSameRunIdAcrossScopes()
    {
        var runtime = new RecordingRunRegistryRuntime();
        var adapter = new ServiceRunRegistrationAdapter(
            runtime,
            new RecordingDispatchPort(),
            new RecordingServiceRunProjectionPort());

        await adapter.RegisterAsync(BuildRecord("tenant-a", "svc", "run-shared"));
        await adapter.RegisterAsync(BuildRecord("tenant-b", "svc", "run-shared"));

        runtime.CreateCalls.Should().HaveCount(2);
        runtime.CreateCalls[0].actorId.Should().Be(ServiceRunIds.BuildActorId("tenant-a", "svc", "run-shared"));
        runtime.CreateCalls[1].actorId.Should().Be(ServiceRunIds.BuildActorId("tenant-b", "svc", "run-shared"));
        runtime.CreateCalls[0].actorId.Should().NotBe(runtime.CreateCalls[1].actorId);
    }

    [Fact]
    public async Task RegisterAsync_ShouldRejectMissingRequiredFields()
    {
        var adapter = new ServiceRunRegistrationAdapter(
            new RecordingRunRegistryRuntime(),
            new RecordingDispatchPort(),
            new RecordingServiceRunProjectionPort());

        var noRun = BuildRecord("tenant", "svc", string.Empty);
        var act = () => adapter.RegisterAsync(noRun);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("run_id*");

        var noScope = BuildRecord(string.Empty, "svc", "run-1");
        var act2 = () => adapter.RegisterAsync(noScope);
        await act2.Should().ThrowAsync<InvalidOperationException>().WithMessage("scope_id*");

        var noService = BuildRecord("tenant", string.Empty, "run-1");
        var act3 = () => adapter.RegisterAsync(noService);
        await act3.Should().ThrowAsync<InvalidOperationException>().WithMessage("service_id*");
    }

    [Fact]
    public async Task UpdateStatusAsync_ShouldDispatchUpdateEnvelope()
    {
        var dispatchPort = new RecordingDispatchPort();
        var adapter = new ServiceRunRegistrationAdapter(
            new RecordingRunRegistryRuntime(),
            dispatchPort,
            new RecordingServiceRunProjectionPort());

        await adapter.UpdateStatusAsync("service-run:tenant:svc:run-1", "run-1", ServiceRunStatus.Completed);

        dispatchPort.Calls.Should().ContainSingle();
        dispatchPort.Calls[0].actorId.Should().Be("service-run:tenant:svc:run-1");
        dispatchPort.Calls[0].envelope.Payload.TypeUrl.Should().Contain("UpdateServiceRunStatusRequested");
    }

    [Fact]
    public async Task UpdateStatusAsync_ShouldNoOp_WhenStatusUnspecified()
    {
        var dispatchPort = new RecordingDispatchPort();
        var adapter = new ServiceRunRegistrationAdapter(
            new RecordingRunRegistryRuntime(),
            dispatchPort,
            new RecordingServiceRunProjectionPort());

        await adapter.UpdateStatusAsync("service-run:tenant:svc:run-1", "run-1", ServiceRunStatus.Unspecified);

        dispatchPort.Calls.Should().BeEmpty();
    }

    private static ServiceRunRecord BuildRecord(string scopeId, string serviceId, string runId) =>
        new()
        {
            ScopeId = scopeId,
            ServiceId = serviceId,
            ServiceKey = $"{scopeId}:{serviceId}",
            RunId = runId,
            CommandId = $"cmd-{runId}",
            CorrelationId = $"corr-{runId}",
            EndpointId = "run",
            ImplementationKind = ServiceImplementationKind.Static,
            TargetActorId = "primary-actor",
            RevisionId = "r1",
            DeploymentId = "dep-1",
            Status = ServiceRunStatus.Unspecified,
            CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        };

    private sealed class RecordingRunRegistryRuntime : IActorRuntime
    {
        public List<(System.Type agentType, string actorId)> CreateCalls { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default)
        {
            var actorId = id ?? $"created:{agentType.Name}";
            CreateCalls.Add((agentType, actorId));
            return Task.FromResult<IActor>(new RecordingActor(actorId));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);

        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingDispatchPort : IActorDispatchPort
    {
        public List<(string actorId, EventEnvelope envelope)> Calls { get; } = [];

        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Calls.Add((actorId, envelope));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingServiceRunProjectionPort : IServiceRunCurrentStateProjectionPort
    {
        public List<string> EnsureCalls { get; } = [];

        public Task EnsureProjectionAsync(string actorId, CancellationToken ct = default)
        {
            EnsureCalls.Add(actorId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingActor : IActor
    {
        public RecordingActor(string id)
        {
            Id = id;
        }

        public string Id { get; }

        public IAgent Agent { get; } = new TestStaticServiceAgent();

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
