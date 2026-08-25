using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.Streaming;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.CQRS.Projection.Core.Tests;

/// <summary>
/// The status runtime registration after the source scope took ownership of its status writers:
/// it registers the legacy shadow kind and the terminal materializer kind (both ensured by the
/// source scope actor itself), the status query ports and the fleet advertisement — and no
/// longer any status activation/lease/release service; the materialization activation service
/// is the plain one and ensures only the scope it is asked for.
/// </summary>
public sealed class ProjectionScopeStatusRuntimeRegistrationTests
{
    private const string LegacyStatusShadowKind =
        "projection.materialization-scope.projection-scope-status-materialization-context";

    [Fact]
    public void AddProjectionScopeStatusRuntimeCore_RegistersStatusServices()
    {
        var services = new ServiceCollection();

        services.AddProjectionScopeStatusRuntimeCore();

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IProjectionScopeWatermarkQueryPort) &&
            descriptor.ImplementationType == typeof(ProjectionScopeStatusQueryPort));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IProjectionScopeIntrospectionQueryPort) &&
            descriptor.ImplementationType == typeof(ProjectionScopeIntrospectionQueryPort));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IProjectionDocumentMetadataProvider<ProjectionScopeStatusDocument>) &&
            descriptor.ImplementationType == typeof(ProjectionScopeStatusDocumentMetadataProvider));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IProjectionMaterializer<ProjectionScopeStatusMaterializationContext>));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(Func<ProjectionRuntimeScopeKey, ProjectionScopeStatusMaterializationContext>));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IProjectionFailureReplayService) &&
            descriptor.ImplementationType == typeof(ProjectionFailureReplayService));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(ProjectionFailureRecoveryReconciler) &&
            descriptor.ImplementationType == typeof(ProjectionFailureRecoveryReconciler));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType == typeof(ProjectionFailureRecoveryHostedService));
    }

    [Fact]
    public void AddProjectionScopeStatusRuntimeCore_RegistersNoStatusLeaseActivationOrReleaseService()
    {
        // The source scope ensures its own status writers; no activation service decides from
        // relay evidence any more, so the status runtime has no lease, activation, attach
        // lookup or release service of its own (ProjectionScopeStatusRuntimeLease is gone).
        var services = new ServiceCollection();

        services.AddProjectionScopeStatusRuntimeCore();

        services.Should().NotContain(descriptor =>
            descriptor.ServiceType.FullName!.Contains("ProjectionScopeStatusRuntimeLease", StringComparison.Ordinal));
        services.Should().NotContain(descriptor =>
            descriptor.ServiceType.IsGenericType &&
            (descriptor.ServiceType.GetGenericTypeDefinition() == typeof(IProjectionScopeActivationService<>) ||
             descriptor.ServiceType.GetGenericTypeDefinition() == typeof(IProjectionScopeReleaseService<>) ||
             descriptor.ServiceType.GetGenericTypeDefinition() == typeof(IProjectionScopeAttachExistingLeaseLookup<>)));
    }

    [Fact]
    public async Task AddProjectionScopeStatusRuntimeCore_ShouldRegisterLegacyStatusShadowKind()
    {
        // The source scope resolves this kind through the registry to create its legacy shadow
        // by kind (EnsureLegacyStatusShadowAsync); the kind is the relay evidence of the shadow.
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort(runtime);
        var services = CreateServices(runtime, dispatchPort);
        services.AddProjectionScopeStatusRuntimeCore();

        await using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<Aevatar.Foundation.Abstractions.TypeSystem.IAgentKindRegistry>();

        registry.TryGetKindForAgentType(
                typeof(ProjectionMaterializationScopeGAgent<ProjectionScopeStatusMaterializationContext>),
                out var kind)
            .Should().BeTrue();
        kind.Should().Be(LegacyStatusShadowKind);
        registry.TryResolve(kind, out var implementation).Should().BeTrue();
        implementation.StateContractType.Should().Be(typeof(ProjectionScopeState));
    }

    [Fact]
    public async Task AddProjectionScopeStatusRuntimeCore_ShouldRegisterTerminalStatusMaterializerKind()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort(runtime);
        var services = CreateServices(runtime, dispatchPort);
        services.AddProjectionScopeStatusRuntimeCore();

        await using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<Aevatar.Foundation.Abstractions.TypeSystem.IAgentKindRegistry>();

        registry.TryResolve(ProjectionScopeStatusGAgent.AgentKind, out var implementation).Should().BeTrue();
        implementation.StateContractType.Should().Be(typeof(ProjectionScopeStatusTerminalState));
        registry.TryGetKindForAgentType(typeof(ProjectionScopeStatusGAgent), out var kind).Should().BeTrue();
        kind.Should().Be(ProjectionScopeStatusGAgent.AgentKind);
    }

    [Fact]
    public async Task AddProjectionScopeStatusRuntimeCore_ShouldAdvertiseTerminalStatusCapabilityToTheFleet()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort(runtime);
        var services = CreateServices(runtime, dispatchPort);
        services.AddProjectionScopeStatusRuntimeCore();
        services.AddProjectionScopeStatusRuntimeCore();

        await using var provider = services.BuildServiceProvider();
        var advertisements = provider
            .GetServices<Aevatar.Foundation.Abstractions.Runtime.IRuntimeFleetCapabilityAdvertisement>()
            .ToArray();
        var advertisement = advertisements
            .Should().ContainSingle(candidate =>
                candidate.GetCapability().Capability ==
                Aevatar.Foundation.Abstractions.Runtime.RuntimeFleetCapability.ProjectionScopeStatusTerminalV2)
            .Subject;

        // Phase A uses the known V2 capability with a distinct bridge contract. It therefore
        // closes old V2 admission in mixed fleets and quiesces only after bridge unanimity.
        var capability = advertisement.GetCapability();
        ProjectionScopeStatusGAgent.ContractVersion.Should().Be(2);
        capability.ContractId.Should().Be(
            Aevatar.Foundation.Abstractions.Runtime.RuntimeFleetCapabilityContracts
                .ProjectionScopeStatusTerminalQuiescenceV1);
        capability.ReaderContractVersion.Should().Be(
            Aevatar.Foundation.Abstractions.Runtime.RuntimeFleetCapabilityContracts
                .ProjectionScopeStatusTerminalQuiescenceReaderVersion);
        capability.ReaderContractVersion.Should().Be(
            3,
            "the Phase-A binary understands the terminal quiescence receipt");
        advertisement.GetReaderImplementationType().Should().Be(typeof(ProjectionScopeStatusGAgent));
    }

    [Fact]
    public async Task AddProjectionScopeStatusRuntimeCore_WithoutTurnoverSupport_ShouldSuppressV3Advertisement()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort(runtime);
        var services = CreateServices(runtime, dispatchPort);
        services.AddProjectionScopeStatusRuntimeCore();

        await using var provider = services.BuildServiceProvider();
        var advertisement = provider
            .GetServices<IRuntimeFleetCapabilityAdvertisement>()
            .OfType<ProjectionScopeStatusTerminalActivationSealCapabilityAdvertisement>()
            .Should().ContainSingle().Subject;

        advertisement.IsAvailable.Should().BeFalse();
        advertisement.GetCapability().Capability.Should()
            .Be(RuntimeFleetCapability.ProjectionScopeStatusTerminalV3);
    }

    [Fact]
    public async Task AddProjectionScopeStatusRuntimeCore_WithTurnoverSupport_ShouldAdvertiseExactV3Contract()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort(runtime);
        var services = CreateServices(runtime, dispatchPort);
        services.AddSingleton<IRuntimeActorStateSchemaActivationSealSupport, TurnoverSupport>();
        services.AddProjectionScopeStatusRuntimeCore();

        await using var provider = services.BuildServiceProvider();
        var advertisement = provider
            .GetServices<IRuntimeFleetCapabilityAdvertisement>()
            .OfType<ProjectionScopeStatusTerminalActivationSealCapabilityAdvertisement>()
            .Should().ContainSingle().Subject;

        advertisement.IsAvailable.Should().BeTrue();
        var capability = advertisement.GetCapability();
        capability.Capability.Should().Be(RuntimeFleetCapability.ProjectionScopeStatusTerminalV3);
        capability.ContractId.Should().Be(
            RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealV1);
        capability.ReaderContractVersion.Should().Be(
            RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealReaderVersion);
        advertisement.GetReaderImplementationType().Should().Be(typeof(ProjectionScopeStatusGAgent));
    }

    [Fact]
    public async Task AddProjectionScopeStatusRuntimeCore_ShouldNotAdvertiseThePhaseUnawareTerminalStatusCapability()
    {
        // The absence of the v1 advertisement is what makes a mixed fleet fail closed: this binary
        // never lets the (unmanaged) v1 gate look unanimous, and a phase-unaware binary that does
        // not advertise v2 keeps the v2 gate shut for the whole fleet.
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort(runtime);
        var services = CreateServices(runtime, dispatchPort);
        services.AddProjectionScopeStatusRuntimeCore();

        await using var provider = services.BuildServiceProvider();
        var capabilities = provider
            .GetServices<Aevatar.Foundation.Abstractions.Runtime.IRuntimeFleetCapabilityAdvertisement>()
            .Select(static candidate => candidate.GetCapability())
            .ToArray();

        capabilities.Should().NotContain(static candidate =>
            candidate.Capability ==
            Aevatar.Foundation.Abstractions.Runtime.RuntimeFleetCapability.ProjectionScopeStatusTerminalV1);
        capabilities.Should().NotContain(static candidate =>
            candidate.ContractId ==
            Aevatar.Foundation.Abstractions.Runtime.RuntimeFleetCapabilityContracts
                .ProjectionScopeStatusTerminalV1);
        capabilities.Should().NotContain(static candidate =>
            candidate.Capability ==
                Aevatar.Foundation.Abstractions.Runtime.RuntimeFleetCapability.ProjectionScopeStatusTerminalV2 &&
            candidate.ReaderContractVersion <
                Aevatar.Foundation.Abstractions.Runtime.RuntimeFleetCapabilityContracts
                    .ProjectionScopeStatusTerminalReaderVersion);
    }

    [Theory]
    [InlineData(ProjectionScopeStatusMaterializationContext.ProjectionKindValue, true)]
    [InlineData(ProjectionScopeStatusTerminalMaterializationContext.ProjectionKindValue, true)]
    [InlineData("channel-bot-registration", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsProjectionScopeStatusKind_ShouldCoverBothStatusWriterKinds(string? projectionKind, bool expected)
    {
        ProjectionScopeStatusRuntimeRegistration.IsProjectionScopeStatusKind(projectionKind).Should().Be(expected);
    }

    [Fact]
    public async Task AddProjectionMaterializationRuntimeCore_ResolvesThePlainActivationService()
    {
        // No status-activation wrapper any more: the registered activation service is the plain
        // scope activation service for the registered scope agent.
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort(runtime);
        var services = CreateServices(runtime, dispatchPort);
        services.AddProjectionScopeStatusRuntimeCore();
        AddTestMaterializationRuntime(services);

        await using var provider = services.BuildServiceProvider();
        var activation = provider.GetRequiredService<IProjectionScopeActivationService<TestMaterializationLease>>();

        activation.Should().BeOfType<ProjectionScopeActivationService<
            TestMaterializationLease,
            TestMaterializationContext,
            ProjectionMaterializationScopeGAgent<TestMaterializationContext>>>();
    }

    [Fact]
    public void AddProjectionMaterializationRuntimeCore_WithStatusRuntime_RegistersNoStatusLeaseService()
    {
        var services = new ServiceCollection();
        services.AddProjectionScopeStatusRuntimeCore();
        AddTestMaterializationRuntime(services);

        services.Should().NotContain(descriptor =>
            descriptor.ServiceType.FullName!.Contains("ProjectionScopeStatusRuntimeLease", StringComparison.Ordinal));
        services.Should().NotContain(descriptor =>
            descriptor.ImplementationType != null &&
            descriptor.ImplementationType.FullName!.Contains("ProjectionScopeStatusRuntimeLease", StringComparison.Ordinal));
        services.Where(descriptor =>
                descriptor.ServiceType.IsGenericType &&
                descriptor.ServiceType.GetGenericTypeDefinition() == typeof(IProjectionScopeActivationService<>))
            .Should().ContainSingle()
            .Which.ServiceType.Should().Be(typeof(IProjectionScopeActivationService<TestMaterializationLease>));
    }

    [Fact]
    public async Task MaterializationActivation_EnsuresOnlyTheRequestedScope()
    {
        // The legacy status shadow is ensured by the source scope actor on its own turn, never
        // by the activation service alongside the scope.
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort(runtime);
        var services = CreateServices(runtime, dispatchPort);
        services.AddProjectionScopeStatusRuntimeCore();
        AddTestMaterializationRuntime(services);

        await using var provider = services.BuildServiceProvider();
        var activation = provider.GetRequiredService<IProjectionScopeActivationService<TestMaterializationLease>>();

        await activation.EnsureAsync(new ProjectionScopeStartRequest
        {
            RootActorId = "root-actor",
            ProjectionKind = "channel-bot-registration",
            Mode = ProjectionRuntimeMode.DurableMaterialization,
        });

        var mainScopeKey = new ProjectionRuntimeScopeKey(
            "root-actor",
            "channel-bot-registration",
            ProjectionRuntimeMode.DurableMaterialization);
        var sourceScopeActorId = ProjectionScopeActorId.Build(mainScopeKey);
        var legacyActorId = ProjectionScopeStatusRoutes.BuildLegacyActorId(sourceScopeActorId);
        var terminalActorId = ProjectionScopeStatusRoutes.BuildTerminalActorId(sourceScopeActorId);
        runtime.CreatedActorIds.Should().Equal(sourceScopeActorId);
        var ensure = dispatchPort.Dispatched.Should().ContainSingle().Subject;
        ensure.actorId.Should().Be(sourceScopeActorId);
        ensure.command.Payload!.Unpack<EnsureProjectionScopeCommand>().Should().Be(
            new EnsureProjectionScopeCommand
            {
                RootActorId = "root-actor",
                ProjectionKind = "channel-bot-registration",
                Mode = ProjectionScopeMode.DurableMaterialization,
            });
        (await dispatchPort.GetAsync(sourceScopeActorId, legacyActorId)).Should().BeNull();
        (await dispatchPort.GetAsync(sourceScopeActorId, terminalActorId)).Should().BeNull();
    }

    [Fact]
    public async Task MaterializationActivation_WithExactEvidence_UsesWarmPathWithoutTouchingStatusWriters()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort(runtime);
        var services = CreateServices(runtime, dispatchPort);
        services.AddProjectionScopeStatusRuntimeCore();
        AddTestMaterializationRuntime(services);
        var mainScopeKey = new ProjectionRuntimeScopeKey(
            "root-warm",
            "projection-warm",
            ProjectionRuntimeMode.DurableMaterialization);
        var mainScopeActorId = ProjectionScopeActorId.Build(mainScopeKey);
        await dispatchPort.UpsertAsync(ProjectionScopeObservationRelayBinding.Create(
            mainScopeKey.RootActorId,
            mainScopeActorId,
            "projection.materialization-scope.test-materialization-context",
            1));

        await using var provider = services.BuildServiceProvider();
        var activation = provider.GetRequiredService<IProjectionScopeActivationService<TestMaterializationLease>>();
        await activation.EnsureAsync(new ProjectionScopeStartRequest
        {
            RootActorId = mainScopeKey.RootActorId,
            ProjectionKind = mainScopeKey.ProjectionKind,
            Mode = mainScopeKey.Mode,
        });

        runtime.ExistsCallCount.Should().Be(0);
        runtime.CreatedActorIds.Should().BeEmpty();
        dispatchPort.Dispatched.Should().BeEmpty();
    }

    private static void AddTestMaterializationRuntime(IServiceCollection services) =>
        services.AddProjectionMaterializationRuntimeCore<
            TestMaterializationContext,
            TestMaterializationLease,
            ProjectionMaterializationScopeGAgent<TestMaterializationContext>>(
            scopeKey => new TestMaterializationContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            context => new TestMaterializationLease(context));

    private static ServiceCollection CreateServices(
        RecordingActorRuntime runtime,
        RecordingActorDispatchPort dispatchPort)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IActorRuntime>(runtime);
        services.AddSingleton<IActorDispatchPort>(dispatchPort);
        services.AddSingleton<IStreamForwardingRegistry>(dispatchPort);
        services.AddSingleton<IStreamForwardingBindingAuthority>(dispatchPort);
        return services;
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        public HashSet<string> ExistingActorIds { get; } = [];
        public List<string> CreatedActorIds { get; } = [];
        public List<(string agentKind, string actorId)> CreatedByKind { get; } = [];
        public int ExistsCallCount { get; private set; }

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent
        {
            ct.ThrowIfCancellationRequested();
            var actorId = id ?? Guid.NewGuid().ToString("N");
            ExistingActorIds.Add(actorId);
            CreatedActorIds.Add(actorId);
            return Task.FromResult<IActor>(new RecordingActor(actorId));
        }

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IActor> CreateByKindAsync(string agentKind, string? id = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrWhiteSpace(agentKind);
            var actorId = id ?? Guid.NewGuid().ToString("N");
            ExistingActorIds.Add(actorId);
            CreatedActorIds.Add(actorId);
            CreatedByKind.Add((agentKind, actorId));
            return Task.FromResult<IActor>(new RecordingActor(actorId));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);

        public Task<bool> ExistsAsync(string id)
        {
            ExistsCallCount++;
            return Task.FromResult(ExistingActorIds.Contains(id));
        }

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class TurnoverSupport : IRuntimeActorStateSchemaActivationSealSupport;

    private sealed class RecordingActorDispatchPort
        : IActorDispatchPort,
          IStreamForwardingRegistry,
          IStreamForwardingBindingAuthority
    {
        private readonly RecordingActorRuntime _runtime;
        private readonly Dictionary<(string Source, string Target), StreamForwardingBinding> _bindings = [];

        public RecordingActorDispatchPort(RecordingActorRuntime runtime)
        {
            _runtime = runtime;
        }

        public List<(string actorId, EventEnvelope command)> Dispatched { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Dispatched.Add((actorId, envelope));
            if (envelope.Payload?.Is(EnsureProjectionScopeCommand.Descriptor) == true)
            {
                var command = envelope.Payload.Unpack<EnsureProjectionScopeCommand>();
                var targetKind = _runtime.CreatedByKind
                    .Last(item => string.Equals(item.actorId, actorId, StringComparison.Ordinal))
                    .agentKind;
                _bindings[(command.RootActorId, actorId)] = ProjectionScopeObservationRelayBinding.Create(
                    command.RootActorId,
                    actorId,
                    targetKind,
                    1);
            }
            else if (envelope.Payload?.Is(ReleaseProjectionScopeCommand.Descriptor) == true)
            {
                var command = envelope.Payload.Unpack<ReleaseProjectionScopeCommand>();
                _bindings.Remove((command.RootActorId, actorId));
            }

            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }

        public Task UpsertAsync(StreamForwardingBinding binding, CancellationToken ct = default)
        {
            _bindings[(binding.SourceStreamId, binding.TargetStreamId)] = binding;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string sourceStreamId, string targetStreamId, CancellationToken ct = default)
        {
            _bindings.Remove((sourceStreamId, targetStreamId));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StreamForwardingBinding>> ListBySourceAsync(
            string sourceStreamId,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StreamForwardingBinding>>(
                _bindings.Values.Where(binding => binding.SourceStreamId == sourceStreamId).ToList());

        public Task<StreamForwardingBinding?> GetAsync(
            string sourceStreamId,
            string targetStreamId,
            CancellationToken ct = default) =>
            Task.FromResult(_bindings.GetValueOrDefault((sourceStreamId, targetStreamId)));
    }

    private sealed class RecordingActor : IActor
    {
        public RecordingActor(string id)
        {
            Id = id;
        }

        public string Id { get; }

        public IAgent Agent => throw new NotSupportedException();

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class TestMaterializationContext : IProjectionMaterializationContext
    {
        public string RootActorId { get; init; } = string.Empty;

        public string ProjectionKind { get; init; } = string.Empty;
    }

    private sealed class TestMaterializationLease
        : ProjectionRuntimeLeaseBase,
          IProjectionContextRuntimeLease<TestMaterializationContext>
    {
        public TestMaterializationLease(TestMaterializationContext context)
            : base(context.RootActorId)
        {
            Context = context;
        }

        public TestMaterializationContext Context { get; }
    }
}
