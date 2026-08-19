using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionScopeStatusRuntimeRegistrationTests
{
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
            descriptor.ServiceType == typeof(IProjectionScopeActivationService<ProjectionScopeStatusRuntimeLease>));
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
    public async Task AddProjectionScopeStatusRuntimeCore_ShouldRegisterAttachExistingLeaseLookup()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort(runtime);
        var services = CreateServices(runtime, dispatchPort);
        services.AddProjectionScopeStatusRuntimeCore();
        var scopeKey = new ProjectionRuntimeScopeKey(
            "root-status-actor",
            ProjectionScopeStatusMaterializationContext.ProjectionKindValue,
            ProjectionRuntimeMode.DurableMaterialization);
        runtime.ExistingActorIds.Add(ProjectionScopeActorId.Build(scopeKey));

        await using var provider = services.BuildServiceProvider();
        var lookup = provider.GetRequiredService<IProjectionScopeAttachExistingLeaseLookup<ProjectionScopeStatusRuntimeLease>>();

        var lease = await lookup.TryGetAsync(new ProjectionScopeStartRequest
        {
            RootActorId = scopeKey.RootActorId,
            ProjectionKind = scopeKey.ProjectionKind,
            Mode = scopeKey.Mode,
        });

        lease.Should().NotBeNull();
        lease!.Context.RootActorId.Should().Be("root-status-actor");
        lease.Context.ProjectionKind.Should().Be(ProjectionScopeStatusMaterializationContext.ProjectionKindValue);
        runtime.CreatedActorIds.Should().BeEmpty();
        dispatchPort.Dispatched.Should().BeEmpty();
    }

    [Fact]
    public async Task MaterializationActivation_EnsuresStatusScopeForNormalProjection()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort(runtime);
        var services = CreateServices(runtime, dispatchPort);
        services.AddProjectionScopeStatusRuntimeCore();
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
        var statusScopeKey = new ProjectionRuntimeScopeKey(
            ProjectionScopeActorId.Build(mainScopeKey),
            ProjectionScopeStatusMaterializationContext.ProjectionKindValue,
            ProjectionRuntimeMode.DurableMaterialization);
        runtime.CreatedActorIds.Should().Contain(ProjectionScopeActorId.Build(mainScopeKey));
        runtime.CreatedActorIds.Should().Contain(ProjectionScopeActorId.Build(statusScopeKey));
        var dispatchedCommands = dispatchPort.Dispatched
            .Select(x => x.command.Payload!.Unpack<EnsureProjectionScopeCommand>())
            .ToList();
        dispatchedCommands.Select(x => x.ProjectionKind)
            .Should()
            .Equal("channel-bot-registration", ProjectionScopeStatusMaterializationContext.ProjectionKindValue);
        dispatchedCommands[1].RootActorId.Should().Be(ProjectionScopeActorId.Build(mainScopeKey));
    }

    [Fact]
    public async Task StatusActivation_DoesNotRecursivelyEnsureStatusForItself()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort(runtime);
        var services = CreateServices(runtime, dispatchPort);
        services.AddProjectionScopeStatusRuntimeCore();

        await using var provider = services.BuildServiceProvider();
        var activation = provider.GetRequiredService<IProjectionScopeActivationService<ProjectionScopeStatusRuntimeLease>>();

        await activation.EnsureAsync(new ProjectionScopeStartRequest
        {
            RootActorId = "root-actor",
            ProjectionKind = ProjectionScopeStatusMaterializationContext.ProjectionKindValue,
            Mode = ProjectionRuntimeMode.DurableMaterialization,
        });

        runtime.CreatedActorIds.Should().ContainSingle();
        dispatchPort.Dispatched.Should().ContainSingle();
        dispatchPort.Dispatched[0].command.Payload!.Unpack<EnsureProjectionScopeCommand>().ProjectionKind
            .Should()
            .Be(ProjectionScopeStatusMaterializationContext.ProjectionKindValue);
    }

    [Fact]
    public async Task MaterializationAndStatusActivation_ExactEvidence_ShouldUseWarmPathForBothScopes()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort(runtime);
        var services = CreateServices(runtime, dispatchPort);
        services.AddProjectionScopeStatusRuntimeCore();
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
        var mainScopeKey = new ProjectionRuntimeScopeKey(
            "root-warm",
            "projection-warm",
            ProjectionRuntimeMode.DurableMaterialization);
        var mainScopeActorId = ProjectionScopeActorId.Build(mainScopeKey);
        var statusScopeKey = new ProjectionRuntimeScopeKey(
            mainScopeActorId,
            ProjectionScopeStatusMaterializationContext.ProjectionKindValue,
            ProjectionRuntimeMode.DurableMaterialization);
        await dispatchPort.UpsertAsync(ProjectionScopeObservationRelayBinding.Create(
            mainScopeKey.RootActorId,
            mainScopeActorId,
            "projection.materialization-scope.test-materialization-context",
            1));
        await dispatchPort.UpsertAsync(ProjectionScopeObservationRelayBinding.Create(
            statusScopeKey.RootActorId,
            ProjectionScopeActorId.Build(statusScopeKey),
            "projection.materialization-scope.projection-scope-status-materialization-context",
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
    public async Task MaterializationActivation_WithTerminalRouteEvidence_ShouldNotEnsureLegacyStatusScope()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort(runtime);
        var services = CreateServices(runtime, dispatchPort);
        services.AddProjectionScopeStatusRuntimeCore();
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
        var mainScopeKey = new ProjectionRuntimeScopeKey(
            "root-terminal",
            "projection-terminal",
            ProjectionRuntimeMode.DurableMaterialization);
        var sourceScopeActorId = ProjectionScopeActorId.Build(mainScopeKey);
        var terminalActorId = ProjectionScopeStatusRoutes.BuildTerminalActorId(sourceScopeActorId);
        var legacyActorId = ProjectionScopeStatusRoutes.BuildLegacyActorId(sourceScopeActorId);
        // The source scope adopted the terminal route: exact terminal evidence lives on its stream.
        await dispatchPort.UpsertAsync(ProjectionScopeObservationRelayBinding.Create(
            sourceScopeActorId,
            terminalActorId,
            ProjectionScopeStatusGAgent.AgentKind,
            1));

        await using var provider = services.BuildServiceProvider();
        var activation = provider.GetRequiredService<IProjectionScopeActivationService<TestMaterializationLease>>();
        await activation.EnsureAsync(new ProjectionScopeStartRequest
        {
            RootActorId = mainScopeKey.RootActorId,
            ProjectionKind = mainScopeKey.ProjectionKind,
            Mode = mainScopeKey.Mode,
        });

        runtime.CreatedActorIds.Should().ContainSingle().Which.Should().Be(sourceScopeActorId);
        runtime.CreatedActorIds.Should().NotContain(legacyActorId);
        var ensure = dispatchPort.Dispatched.Should().ContainSingle().Subject;
        ensure.actorId.Should().Be(sourceScopeActorId);
        ensure.command.Payload!.Unpack<EnsureProjectionScopeCommand>().ProjectionKind
            .Should().Be(mainScopeKey.ProjectionKind);
        (await dispatchPort.GetAsync(sourceScopeActorId, legacyActorId)).Should().BeNull();
    }

    public enum NonTerminalEvidence
    {
        LegacyStatusKind,
        LegacyCompatibleShape,
        ZeroActivationGeneration,
    }

    [Theory]
    [InlineData(NonTerminalEvidence.LegacyStatusKind)]
    [InlineData(NonTerminalEvidence.LegacyCompatibleShape)]
    [InlineData(NonTerminalEvidence.ZeroActivationGeneration)]
    public async Task MaterializationActivation_WithNonTerminalBindingForTerminalId_ShouldStillEnsureLegacyStatusScope(
        NonTerminalEvidence evidence)
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort(runtime);
        var services = CreateServices(runtime, dispatchPort);
        services.AddProjectionScopeStatusRuntimeCore();
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
        var mainScopeKey = new ProjectionRuntimeScopeKey(
            "root-mixed",
            "projection-mixed",
            ProjectionRuntimeMode.DurableMaterialization);
        var sourceScopeActorId = ProjectionScopeActorId.Build(mainScopeKey);
        var terminalActorId = ProjectionScopeStatusRoutes.BuildTerminalActorId(sourceScopeActorId);
        var legacyActorId = ProjectionScopeStatusRoutes.BuildLegacyActorId(sourceScopeActorId);
        var binding = ProjectionScopeObservationRelayBinding.Create(
            sourceScopeActorId,
            terminalActorId,
            ProjectionScopeStatusGAgent.AgentKind,
            1);
        switch (evidence)
        {
            case NonTerminalEvidence.LegacyStatusKind:
                binding.TargetActorKind = "projection.materialization-scope.projection-scope-status-materialization-context";
                break;
            case NonTerminalEvidence.LegacyCompatibleShape:
                binding.TargetActorKind = string.Empty;
                binding.ActivationGeneration = 0;
                break;
            case NonTerminalEvidence.ZeroActivationGeneration:
                binding.ActivationGeneration = 0;
                break;
        }

        await dispatchPort.UpsertAsync(binding);

        await using var provider = services.BuildServiceProvider();
        var activation = provider.GetRequiredService<IProjectionScopeActivationService<TestMaterializationLease>>();
        await activation.EnsureAsync(new ProjectionScopeStartRequest
        {
            RootActorId = mainScopeKey.RootActorId,
            ProjectionKind = mainScopeKey.ProjectionKind,
            Mode = mainScopeKey.Mode,
        });

        runtime.CreatedActorIds.Should().Equal(sourceScopeActorId, legacyActorId);
        dispatchPort.Dispatched
            .Select(x => x.command.Payload!.Unpack<EnsureProjectionScopeCommand>().ProjectionKind)
            .Should()
            .Equal(mainScopeKey.ProjectionKind, ProjectionScopeStatusMaterializationContext.ProjectionKindValue);
    }

    [Fact]
    public async Task MaterializationActivation_ForTerminalStatusKind_ShouldNotEnsureStatusScopeOfItsOwn()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort(runtime);
        var services = CreateServices(runtime, dispatchPort);
        services.AddProjectionScopeStatusRuntimeCore();
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

        await using var provider = services.BuildServiceProvider();
        var activation = provider.GetRequiredService<IProjectionScopeActivationService<TestMaterializationLease>>();
        await activation.EnsureAsync(new ProjectionScopeStartRequest
        {
            RootActorId = "projection.durable.scope:some-kind:root-actor",
            ProjectionKind = ProjectionScopeStatusTerminalMaterializationContext.ProjectionKindValue,
            Mode = ProjectionRuntimeMode.DurableMaterialization,
        });

        runtime.CreatedActorIds.Should().ContainSingle();
        dispatchPort.Dispatched.Should().ContainSingle()
            .Which.command.Payload!.Unpack<EnsureProjectionScopeCommand>().ProjectionKind
            .Should().Be(ProjectionScopeStatusTerminalMaterializationContext.ProjectionKindValue);
    }

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
