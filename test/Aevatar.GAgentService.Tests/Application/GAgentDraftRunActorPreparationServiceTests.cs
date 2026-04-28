using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Application.ScopeGAgents;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class GAgentDraftRunActorPreparationServiceTests
{
    [Fact]
    public async Task PrepareAsync_ShouldReturnUnknownActorType_WhenTypeCannotBeResolved()
    {
        var service = new GAgentDraftRunActorPreparationService(
            new StubActorRuntime(_ => null),
            new RecordingGAgentActorRegistryCommandPort(),
            new RecordingScopeResourceAdmissionPort());

        var result = await service.PrepareAsync(
            new GAgentDraftRunPreparationRequest("scope-a", "Aevatar.IamNotReal, Aevatar.IamNotReal"),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(GAgentDraftRunStartError.UnknownActorType);
    }

    [Fact]
    public async Task PrepareAsync_ShouldReuseExistingActor_WithoutRegisteringAgain()
    {
        var runtime = new StubActorRuntime(id => id == "existing-actor" ? new StubActor(id) : null);
        var commandPort = new RecordingGAgentActorRegistryCommandPort();
        var admissionPort = new RecordingScopeResourceAdmissionPort
        {
            Result = ScopeResourceAdmissionResult.Allowed()
        };
        var service = new GAgentDraftRunActorPreparationService(runtime, commandPort, admissionPort);

        var result = await service.PrepareAsync(
            new GAgentDraftRunPreparationRequest(
                "scope-a",
                typeof(FakeAgent).AssemblyQualifiedName!,
                "existing-actor"),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.PreparedActor.Should().BeEquivalentTo(new GAgentDraftRunPreparedActor(
            "scope-a",
            typeof(FakeAgent).AssemblyQualifiedName!,
            "existing-actor",
            false));
        commandPort.RegisteredActors.Should().BeEmpty();
        admissionPort.Targets.Should().ContainSingle().Which.Should().Be(new ScopeResourceTarget(
            "scope-a",
            ScopeResourceKind.GAgentActor,
            typeof(FakeAgent).AssemblyQualifiedName!,
            "existing-actor",
            ScopeResourceOperation.DraftRunReuse));
    }

    [Fact]
    public async Task PrepareAsync_ShouldRejectExistingActor_WhenItIsNotRegisteredInRequestedScope()
    {
        var runtime = new StubActorRuntime(id => id == "existing-actor" ? new StubActor(id) : null);
        var commandPort = new RecordingGAgentActorRegistryCommandPort();
        var admissionPort = new RecordingScopeResourceAdmissionPort
        {
            Result = ScopeResourceAdmissionResult.ScopeMismatch()
        };
        var service = new GAgentDraftRunActorPreparationService(runtime, commandPort, admissionPort);

        var result = await service.PrepareAsync(
            new GAgentDraftRunPreparationRequest(
                "scope-a",
                typeof(FakeAgent).AssemblyQualifiedName!,
                "existing-actor"),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(GAgentDraftRunStartError.ActorTypeMismatch);
        commandPort.RegisteredActors.Should().BeEmpty();
        admissionPort.Targets.Should().ContainSingle();
    }

    [Fact]
    public async Task PrepareAsync_ShouldRegisterGeneratedActorId_WhenActorDoesNotExist()
    {
        var operations = new List<string>();
        var commandPort = new RecordingGAgentActorRegistryCommandPort(operations);
        var service = new GAgentDraftRunActorPreparationService(
            new StubActorRuntime(_ => null, operations),
            commandPort,
            new RecordingScopeResourceAdmissionPort());

        var result = await service.PrepareAsync(
            new GAgentDraftRunPreparationRequest(
                "scope-a",
                typeof(FakeAgent).AssemblyQualifiedName!),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.PreparedActor.Should().NotBeNull();
        result.PreparedActor!.ScopeId.Should().Be("scope-a");
        result.PreparedActor.ActorTypeName.Should().Be(typeof(FakeAgent).AssemblyQualifiedName!);
        result.PreparedActor.ActorId.Should().NotBeNullOrWhiteSpace();
        result.PreparedActor.RequiresRollbackOnFailure.Should().BeTrue();
        operations.Should().ContainInOrder(
            $"runtime:create:{result.PreparedActor.ActorId}",
            $"registry:add:{result.PreparedActor.ActorId}");
        commandPort.RegisteredActors.Should().ContainSingle();
        commandPort.RegisteredActors[0].ScopeId.Should().Be("scope-a");
        commandPort.RegisteredActors[0].GAgentType.Should().Be(typeof(FakeAgent).AssemblyQualifiedName!);
        commandPort.RegisteredActors[0].ActorId.Should().Be(result.PreparedActor.ActorId);
    }

    [Fact]
    public async Task PrepareAsync_ShouldDestroyCreatedActor_WhenRegistrationIsCanceled()
    {
        var operations = new List<string>();
        var runtime = new StubActorRuntime(_ => null, operations);
        var commandPort = new RecordingGAgentActorRegistryCommandPort(operations)
        {
            ThrowOnRegister = new OperationCanceledException("cancelled before registry ack")
        };
        var service = new GAgentDraftRunActorPreparationService(
            runtime,
            commandPort,
            new RecordingScopeResourceAdmissionPort());

        var act = async () => await service.PrepareAsync(
            new GAgentDraftRunPreparationRequest(
                "scope-a",
                typeof(FakeAgent).AssemblyQualifiedName!,
                "draft-actor"),
            CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        runtime.DestroyedActorIds.Should().ContainSingle("draft-actor");
        commandPort.UnregisteredActors.Should().BeEmpty();
        operations.Should().ContainInOrder(
            "runtime:create:draft-actor",
            "registry:add:draft-actor",
            "runtime:destroy:draft-actor");
    }

    [Fact]
    public async Task PrepareAsync_ShouldDestroyCreatedActor_WhenRegistrationFailsBeforeAck()
    {
        var operations = new List<string>();
        var runtime = new StubActorRuntime(_ => null, operations);
        var commandPort = new RecordingGAgentActorRegistryCommandPort(operations)
        {
            ThrowOnRegister = new InvalidOperationException("registry unavailable"),
            ThrowOnUnregister = new InvalidOperationException("registry unregister unavailable")
        };
        var service = new GAgentDraftRunActorPreparationService(
            runtime,
            commandPort,
            new RecordingScopeResourceAdmissionPort());

        var act = async () => await service.PrepareAsync(
            new GAgentDraftRunPreparationRequest(
                "scope-a",
                typeof(FakeAgent).AssemblyQualifiedName!,
                "draft-actor"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("registry unavailable");
        runtime.DestroyedActorIds.Should().ContainSingle("draft-actor");
        commandPort.UnregisteredActors.Should().BeEmpty();
        operations.Should().ContainInOrder(
            "runtime:create:draft-actor",
            "registry:add:draft-actor",
            "runtime:destroy:draft-actor");
    }

    [Fact]
    public async Task PrepareAsync_ShouldNotDestroyCreatedActor_WhenRollbackCannotRemoveRegistration()
    {
        var operations = new List<string>();
        var runtime = new StubActorRuntime(_ => null, operations);
        var commandPort = new RecordingGAgentActorRegistryCommandPort(operations)
        {
            RegisterStage = GAgentActorRegistryCommandStage.AcceptedForDispatch,
            ThrowOnUnregister = new InvalidOperationException("registry unavailable")
        };
        var service = new GAgentDraftRunActorPreparationService(
            runtime,
            commandPort,
            new RecordingScopeResourceAdmissionPort());

        var result = await service.PrepareAsync(
            new GAgentDraftRunPreparationRequest(
                "scope-a",
                typeof(FakeAgent).AssemblyQualifiedName!,
                "draft-actor"),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(GAgentDraftRunStartError.ActorTypeMismatch);
        runtime.DestroyedActorIds.Should().BeEmpty();
        commandPort.UnregisteredActors.Should().ContainSingle().Which.ActorId.Should().Be("draft-actor");
        operations.Should().ContainInOrder(
            "runtime:create:draft-actor",
            "registry:add:draft-actor",
            "registry:remove:draft-actor");
    }

    [Fact]
    public async Task RollbackAsync_ShouldRemoveRegistrationBeforeDestroyingActor_WhenRollbackIsRequired()
    {
        var operations = new List<string>();
        var runtime = new StubActorRuntime(_ => null, operations);
        var commandPort = new RecordingGAgentActorRegistryCommandPort(operations);
        var service = new GAgentDraftRunActorPreparationService(
            runtime,
            commandPort,
            new RecordingScopeResourceAdmissionPort());
        var preparedActor = new GAgentDraftRunPreparedActor(
            "scope-a",
            typeof(FakeAgent).AssemblyQualifiedName!,
            "generated-actor",
            true);

        await service.RollbackAsync(preparedActor, CancellationToken.None);

        runtime.DestroyedActorIds.Should().ContainSingle("generated-actor");
        commandPort.UnregisteredActors.Should().ContainSingle();
        commandPort.UnregisteredActors[0].Should().Be(new GAgentActorRegistration(
            "scope-a",
            typeof(FakeAgent).AssemblyQualifiedName!,
            "generated-actor"));
        operations.Should().ContainInOrder(
            "registry:remove:generated-actor",
            "runtime:destroy:generated-actor");
    }

    [Fact]
    public async Task RollbackAsync_ShouldNotDestroyActor_WhenRegistrationRemovalFails()
    {
        var operations = new List<string>();
        var runtime = new StubActorRuntime(_ => null, operations);
        var commandPort = new RecordingGAgentActorRegistryCommandPort(operations)
        {
            ThrowOnUnregister = new InvalidOperationException("registry unavailable")
        };
        var service = new GAgentDraftRunActorPreparationService(
            runtime,
            commandPort,
            new RecordingScopeResourceAdmissionPort());
        var preparedActor = new GAgentDraftRunPreparedActor(
            "scope-a",
            typeof(FakeAgent).AssemblyQualifiedName!,
            "generated-actor",
            true);

        await service.RollbackAsync(preparedActor, CancellationToken.None);

        commandPort.UnregisteredActors.Should().ContainSingle();
        runtime.DestroyedActorIds.Should().BeEmpty();
        operations.Should().ContainSingle("registry:remove:generated-actor");
    }

    [Fact]
    public async Task RollbackAsync_ShouldSkipWork_WhenRollbackIsNotRequired()
    {
        var runtime = new StubActorRuntime(_ => null);
        var commandPort = new RecordingGAgentActorRegistryCommandPort();
        var service = new GAgentDraftRunActorPreparationService(
            runtime,
            commandPort,
            new RecordingScopeResourceAdmissionPort());

        await service.RollbackAsync(
            new GAgentDraftRunPreparedActor(
                "scope-a",
                typeof(FakeAgent).AssemblyQualifiedName!,
                "existing-actor",
                false),
            CancellationToken.None);

        runtime.DestroyedActorIds.Should().BeEmpty();
        commandPort.UnregisteredActors.Should().BeEmpty();
    }

    private sealed class RecordingGAgentActorRegistryCommandPort(List<string>? operations = null) : IGAgentActorRegistryCommandPort
    {
        public List<GAgentActorRegistration> RegisteredActors { get; } = [];
        public List<GAgentActorRegistration> UnregisteredActors { get; } = [];
        public Exception? ThrowOnRegister { get; init; }
        public Exception? ThrowOnUnregister { get; init; }
        public GAgentActorRegistryCommandStage RegisterStage { get; init; } =
            GAgentActorRegistryCommandStage.AdmissionVisible;

        public Task<GAgentActorRegistryCommandReceipt> RegisterActorAsync(
            GAgentActorRegistration registration,
            CancellationToken cancellationToken = default)
        {
            operations?.Add($"registry:add:{registration.ActorId}");
            RegisteredActors.Add(registration);
            if (ThrowOnRegister is not null)
                throw ThrowOnRegister;

            return Task.FromResult(new GAgentActorRegistryCommandReceipt(
                registration,
                RegisterStage));
        }

        public Task<GAgentActorRegistryCommandReceipt> UnregisterActorAsync(
            GAgentActorRegistration registration,
            CancellationToken cancellationToken = default)
        {
            operations?.Add($"registry:remove:{registration.ActorId}");
            UnregisteredActors.Add(registration);
            if (ThrowOnUnregister is not null)
                throw ThrowOnUnregister;

            return Task.FromResult(new GAgentActorRegistryCommandReceipt(
                registration,
                GAgentActorRegistryCommandStage.AdmissionRemoved));
        }
    }

    private sealed class RecordingScopeResourceAdmissionPort : IScopeResourceAdmissionPort
    {
        public ScopeResourceAdmissionResult Result { get; init; } = ScopeResourceAdmissionResult.NotFound();
        public List<ScopeResourceTarget> Targets { get; } = [];

        public Task<ScopeResourceAdmissionResult> AuthorizeTargetAsync(
            ScopeResourceTarget target,
            CancellationToken cancellationToken = default)
        {
            Targets.Add(target);
            return Task.FromResult(Result);
        }
    }

    private sealed class StubActorRuntime(Func<string, IActor?> getAsync, List<string>? operations = null) : IActorRuntime
    {
        public List<string> DestroyedActorIds { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            Task.FromResult<IActor>(new StubActor(id ?? "created"));

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        {
            _ = agentType;
            var actorId = id ?? "created";
            operations?.Add($"runtime:create:{actorId}");
            return Task.FromResult<IActor>(new StubActor(actorId));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            operations?.Add($"runtime:destroy:{id}");
            DestroyedActorIds.Add(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id) => Task.FromResult(getAsync(id));

        public Task<bool> ExistsAsync(string id) => Task.FromResult(getAsync(id) is not null);

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubActor(string id) : IActor
    {
        public string Id { get; } = id;

        public IAgent Agent { get; } = new FakeAgent();

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class FakeAgent : IAgent
    {
        public string Id { get; } = "fake-agent";

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult(string.Empty);

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
    }
}
