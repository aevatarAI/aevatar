using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

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
    }

    [Fact]
    public async Task AddProjectionScopeStatusRuntimeCore_ShouldRegisterAttachExistingLeaseLookup()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort();
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
        var dispatchPort = new RecordingActorDispatchPort();
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
        var dispatchPort = new RecordingActorDispatchPort();
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

    private static ServiceCollection CreateServices(
        RecordingActorRuntime runtime,
        RecordingActorDispatchPort dispatchPort)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IActorRuntime>(runtime);
        services.AddSingleton<IActorDispatchPort>(dispatchPort);
        return services;
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        public HashSet<string> ExistingActorIds { get; } = [];
        public List<string> CreatedActorIds { get; } = [];

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
            return Task.FromResult<IActor>(new RecordingActor(actorId));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);

        public Task<bool> ExistsAsync(string id) => Task.FromResult(ExistingActorIds.Contains(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public List<(string actorId, EventEnvelope command)> Dispatched { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Dispatched.Add((actorId, envelope));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
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
