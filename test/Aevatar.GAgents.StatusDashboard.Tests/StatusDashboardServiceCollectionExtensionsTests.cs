using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.GAgents.StatusDashboard.Configuration;
using Aevatar.GAgents.StatusDashboard.DependencyInjection;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.GAgents.StatusDashboard.Tests;

public sealed class StatusDashboardServiceCollectionExtensionsTests
{
    [Fact]
    public void AddStatusDashboard_RegistersCommittedStateProjectionActivationHook()
    {
        using var provider = new ServiceCollection()
            .AddStatusDashboard(new ConfigurationBuilder().Build())
            .BuildServiceProvider();

        provider.GetService<ProjectionActivationPlanDispatcher>()
            .Should().NotBeNull("the committed-state hook dispatches activation plans through the shared dispatcher");
        provider.GetServices<ICommittedStatePublicationHook>()
            .Should().ContainSingle(hook => hook is CommittedStateProjectionActivationHook);
        provider.GetServices<IProjectionActivationPlanProvider>()
            .Should().ContainSingle(planProvider =>
                planProvider is HealthProbeCommittedStateProjectionActivationPlanProvider);
    }

    [Fact]
    public void AddStatusDashboard_RegistersProviderNeutralHealthProbeServices()
    {
        var services = new ServiceCollection()
            .AddStatusDashboard(new ConfigurationBuilder().Build());

        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IProjectionDocumentMetadataProvider<HealthProbeTargetDocument>));
        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IHealthStatusQueryPort) &&
            descriptor.ImplementationType == typeof(HealthStatusQueryPort));
    }

    [Fact]
    public async Task AddStatusDashboard_HealthProbeActivation_ShouldNotActivateProjectionScopeStatus()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort();
        var statusActivation = new RecordingStatusActivationService();
        var services = new ServiceCollection();
        services.AddSingleton<IActorRuntime>(runtime);
        services.AddSingleton<IActorDispatchPort>(dispatchPort);
        services.AddSingleton<IProjectionScopeActivationService<ProjectionScopeStatusRuntimeLease>>(statusActivation);
        services.AddStatusDashboard(new ConfigurationBuilder().Build());

        await using var provider = services.BuildServiceProvider();
        var activation = provider.GetRequiredService<
            IProjectionScopeActivationService<HealthProbeMaterializationRuntimeLease>>();

        _ = await activation.EnsureAsync(new ProjectionScopeStartRequest
        {
            RootActorId = "health-probe::self-liveness",
            ProjectionKind = HealthProbeTargetGAgent.ProjectionKind,
            Mode = ProjectionRuntimeMode.DurableMaterialization,
        });

        statusActivation.Requests.Should().BeEmpty(
            "the health document is already the operational status surface, so projecting its scope status recursively only amplifies durable writes");
    }

    [Fact]
    public async Task AddStatusDashboard_Startup_ShouldReleaseLegacyStatusScopesForCurrentAndRetiredProbes()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort();
        var currentSlug = "current-probe";
        var retiredSlug = RetiredStatusProbeTargets.Slugs[0];
        var currentLegacyStatusScopeId = BuildLegacyStatusScopeActorId(currentSlug);
        var retiredLegacyStatusScopeId = BuildLegacyStatusScopeActorId(retiredSlug);
        runtime.SeedActor(currentLegacyStatusScopeId);
        runtime.SeedActor(retiredLegacyStatusScopeId);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{StatusDashboardOptions.SectionName}:UseBuiltInTargets"] = "false",
                [$"{StatusDashboardOptions.SectionName}:Targets:0:Slug"] = currentSlug,
                [$"{StatusDashboardOptions.SectionName}:Targets:0:Name"] = "Current probe",
                [$"{StatusDashboardOptions.SectionName}:Targets:0:Category"] = "test",
                [$"{StatusDashboardOptions.SectionName}:Targets:0:Probe"] = "http_status",
                [$"{StatusDashboardOptions.SectionName}:Targets:0:IntervalSeconds"] = "60",
                [$"{StatusDashboardOptions.SectionName}:Targets:0:TimeoutMs"] = "1000",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IActorRuntime>(runtime);
        services.AddSingleton<IActorDispatchPort>(dispatchPort);
        services.AddProjectionScopeStatusRuntimeCore();
        services.AddStatusDashboard(configuration);

        await using var provider = services.BuildServiceProvider();
        var startup = provider.GetServices<IHostedService>()
            .Single(service => service is HealthProbeStartupService);

        await startup.StartAsync(CancellationToken.None);

        var releasedActorIds = dispatchPort.Dispatches
            .Where(dispatch => dispatch.Envelope.Payload.Is(ReleaseProjectionScopeCommand.Descriptor))
            .Select(dispatch => dispatch.ActorId);
        releasedActorIds.Should().BeEquivalentTo(currentLegacyStatusScopeId, retiredLegacyStatusScopeId);
    }

    private static string BuildLegacyStatusScopeActorId(string slug)
    {
        var healthScopeActorId = ProjectionScopeActorId.Build(new ProjectionRuntimeScopeKey(
            HealthProbeStoreCommands.BuildActorId(slug),
            HealthProbeTargetGAgent.ProjectionKind,
            ProjectionRuntimeMode.DurableMaterialization));
        return ProjectionScopeActorId.Build(new ProjectionRuntimeScopeKey(
            healthScopeActorId,
            ProjectionScopeStatusMaterializationContext.ProjectionKindValue,
            ProjectionRuntimeMode.DurableMaterialization));
    }

    private sealed class RecordingStatusActivationService
        : IProjectionScopeActivationService<ProjectionScopeStatusRuntimeLease>
    {
        public List<ProjectionScopeStartRequest> Requests { get; } = [];

        public Task<ProjectionScopeStatusRuntimeLease> EnsureAsync(
            ProjectionScopeStartRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(new ProjectionScopeStatusRuntimeLease(
                new ProjectionScopeStatusMaterializationContext
                {
                    RootActorId = request.RootActorId,
                }));
        }
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        private readonly HashSet<string> _actorIds = new(StringComparer.Ordinal);

        public void SeedActor(string actorId) => _actorIds.Add(actorId);

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent => CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        {
            _ = agentType;
            return CreateByKindAsync("test", id, ct);
        }

        public Task<IActor> CreateByKindAsync(string agentKind, string? id = null, CancellationToken ct = default)
        {
            _ = agentKind;
            ct.ThrowIfCancellationRequested();
            var actorId = id ?? Guid.NewGuid().ToString("N");
            _actorIds.Add(actorId);
            return Task.FromResult<IActor>(new RecordingActor(actorId));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _actorIds.Remove(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id) =>
            Task.FromResult<IActor?>(_actorIds.Contains(id) ? new RecordingActor(id) : null);

        public Task<bool> ExistsAsync(string id) => Task.FromResult(_actorIds.Contains(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Dispatches { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Dispatches.Add((actorId, envelope.Clone()));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class RecordingActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent => throw new NotSupportedException();
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
