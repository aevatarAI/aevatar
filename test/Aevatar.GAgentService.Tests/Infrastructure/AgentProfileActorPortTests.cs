using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Core.AgentProfiles;
using Aevatar.GAgentService.Infrastructure.AgentProfiles;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.GAgentService.Tests.Infrastructure;

public sealed class AgentProfileActorPortTests
{
    private const string ProfileId = "prof-semantic-alpha";

    [Fact]
    public async Task EnsureCreateTargetsAsync_ShouldCreateNamespaceAndOpaqueProfileActors()
    {
        var runtime = new RecordingActorRuntime();
        var port = new AgentProfileActorPort(runtime, new RecordingActorDispatchPort());
        var expectedProfileActorId = AgentProfileActorIds.Profile(ProfileId);

        var targets = await port.EnsureCreateTargetsAsync(ProfileId);

        targets.Should().Be(new AgentProfileActorTargets(
            AgentProfileActorIds.Namespace,
            expectedProfileActorId));
        expectedProfileActorId.Should().NotContain(ProfileId);
        runtime.GetCalls.Should().Equal(
            AgentProfileActorIds.Namespace,
            expectedProfileActorId);
        runtime.CreateCalls.Should().Equal(
            (typeof(AgentProfileNamespaceGAgent), AgentProfileActorIds.Namespace),
            (typeof(AgentProfileGAgent), expectedProfileActorId));
    }

    [Fact]
    public async Task EnsureCreateTargetsAsync_ShouldReactivateKnownActorsThroughRuntimeLookup()
    {
        var expectedProfileActorId = AgentProfileActorIds.Profile(ProfileId);
        var runtime = new RecordingActorRuntime();
        runtime.AddDeactivated<AgentProfileNamespaceGAgent>(AgentProfileActorIds.Namespace);
        runtime.AddDeactivated<AgentProfileGAgent>(expectedProfileActorId);
        var port = new AgentProfileActorPort(runtime, new RecordingActorDispatchPort());

        var targets = await port.EnsureCreateTargetsAsync(ProfileId);

        targets.Should().Be(new AgentProfileActorTargets(
            AgentProfileActorIds.Namespace,
            expectedProfileActorId));
        runtime.MaterializedCalls.Should().Equal(
            (typeof(AgentProfileNamespaceGAgent), AgentProfileActorIds.Namespace),
            (typeof(AgentProfileGAgent), expectedProfileActorId));
        runtime.CreateCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchCreateAsync_ShouldEnsureBothTargetsAndDispatchTypedEnvelopeToNamespace()
    {
        var command = CreateCommand();

        await AssertDispatchAsync(
            command,
            AgentProfileActorIds.Namespace,
            (port, ct) => port.DispatchCreateAsync(command, ct));
    }

    [Fact]
    public async Task DispatchCreateAsync_ShouldRejectMismatchedProfileActorTargetBeforeLifecycle()
    {
        var command = CreateCommand();
        command.ProfileActorId = "caller-supplied-profile-actor";
        var runtime = new RecordingActorRuntime();
        var dispatch = new RecordingActorDispatchPort();
        var port = new AgentProfileActorPort(runtime, dispatch);

        var act = () => port.DispatchCreateAsync(command);

        await act.Should().ThrowAsync<InvalidOperationException>();
        AssertNoLifecycleOrDispatch(runtime, dispatch);
    }

    [Theory]
    [InlineData(InvalidCommandPart.Identity)]
    [InlineData(InvalidCommandPart.Operation)]
    public async Task DispatchCreateAsync_ShouldRejectMissingRequiredDataBeforeLifecycle(
        InvalidCommandPart invalidPart)
    {
        var command = CreateCommand();
        if (invalidPart == InvalidCommandPart.Identity)
            command.Identity = null!;
        else
            command.Operation = null!;
        var runtime = new RecordingActorRuntime();
        var dispatch = new RecordingActorDispatchPort();
        var port = new AgentProfileActorPort(runtime, dispatch);

        var act = () => port.DispatchCreateAsync(command);

        await act.Should().ThrowAsync<ArgumentNullException>();
        AssertNoLifecycleOrDispatch(runtime, dispatch);
    }

    [Fact]
    public async Task DispatchUpdateDraftAsync_ShouldEnsureOnlyProfileAndDispatchTypedEnvelope()
    {
        var command = new UpdateAgentProfileDraftCommand
        {
            Identity = Identity(),
            Operation = Operation("update"),
        };

        await AssertDispatchAsync(
            command,
            AgentProfileActorIds.Profile(ProfileId),
            (port, ct) => port.DispatchUpdateDraftAsync(command, ct));
    }

    [Fact]
    public async Task DispatchUpsertSkillBindingAsync_ShouldEnsureOnlyProfileAndDispatchTypedEnvelope()
    {
        var command = new UpsertAgentProfileSkillBindingCommand
        {
            Identity = Identity(),
            Operation = Operation("upsert"),
        };

        await AssertDispatchAsync(
            command,
            AgentProfileActorIds.Profile(ProfileId),
            (port, ct) => port.DispatchUpsertSkillBindingAsync(command, ct));
    }

    [Fact]
    public async Task DispatchRemoveSkillBindingAsync_ShouldEnsureOnlyProfileAndDispatchTypedEnvelope()
    {
        var command = new RemoveAgentProfileSkillBindingCommand
        {
            Identity = Identity(),
            Operation = Operation("remove"),
        };

        await AssertDispatchAsync(
            command,
            AgentProfileActorIds.Profile(ProfileId),
            (port, ct) => port.DispatchRemoveSkillBindingAsync(command, ct));
    }

    [Fact]
    public async Task DispatchPublishAsync_ShouldEnsureOnlyProfileAndDispatchTypedEnvelope()
    {
        var command = new PublishAgentProfileCommand
        {
            Identity = Identity(),
            Operation = Operation("publish"),
        };

        await AssertDispatchAsync(
            command,
            AgentProfileActorIds.Profile(ProfileId),
            (port, ct) => port.DispatchPublishAsync(command, ct));
    }

    public static TheoryData<MutationDispatchKind, InvalidCommandPart> InvalidMutationCommands =>
        new()
        {
            { MutationDispatchKind.UpdateDraft, InvalidCommandPart.Command },
            { MutationDispatchKind.UpdateDraft, InvalidCommandPart.Identity },
            { MutationDispatchKind.UpdateDraft, InvalidCommandPart.Operation },
            { MutationDispatchKind.UpsertSkillBinding, InvalidCommandPart.Command },
            { MutationDispatchKind.UpsertSkillBinding, InvalidCommandPart.Identity },
            { MutationDispatchKind.UpsertSkillBinding, InvalidCommandPart.Operation },
            { MutationDispatchKind.RemoveSkillBinding, InvalidCommandPart.Command },
            { MutationDispatchKind.RemoveSkillBinding, InvalidCommandPart.Identity },
            { MutationDispatchKind.RemoveSkillBinding, InvalidCommandPart.Operation },
            { MutationDispatchKind.Publish, InvalidCommandPart.Command },
            { MutationDispatchKind.Publish, InvalidCommandPart.Identity },
            { MutationDispatchKind.Publish, InvalidCommandPart.Operation },
        };

    [Theory]
    [MemberData(nameof(InvalidMutationCommands))]
    public async Task DispatchMutationAsync_ShouldRejectInvalidCommandBeforeLifecycle(
        MutationDispatchKind dispatchKind,
        InvalidCommandPart invalidPart)
    {
        IMessage? command = CreateMutationCommand(dispatchKind);
        if (invalidPart == InvalidCommandPart.Command)
            command = null;
        else
            ClearMutationPart(command, invalidPart);
        var runtime = new RecordingActorRuntime();
        var dispatch = new RecordingActorDispatchPort();
        var port = new AgentProfileActorPort(runtime, dispatch);

        var act = () => DispatchMutationAsync(port, dispatchKind, command);

        await act.Should().ThrowAsync<ArgumentNullException>();
        AssertNoLifecycleOrDispatch(runtime, dispatch);
    }

    [Fact]
    public async Task DispatchAsync_ShouldReturnRejectedAdmissionUnchanged()
    {
        var command = new UpdateAgentProfileDraftCommand
        {
            Identity = Identity(),
            Operation = Operation("rejected"),
        };
        var rejection = new DispatchAdmission(
            false,
            "rejected-command",
            DateTimeOffset.Parse("2026-07-23T01:02:03Z"),
            "rejected-actor",
            "rejected-correlation");
        var dispatch = new RecordingActorDispatchPort(rejection);
        var port = new AgentProfileActorPort(new RecordingActorRuntime(), dispatch);

        var result = await port.DispatchUpdateDraftAsync(command);

        result.Should().BeSameAs(rejection);
        dispatch.Calls.Should().ContainSingle();
    }

    private static async Task AssertDispatchAsync<TCommand>(
        TCommand command,
        string expectedActorId,
        Func<AgentProfileActorPort, CancellationToken, Task<DispatchAdmission>> dispatchCommand)
        where TCommand : IMessage
    {
        var admission = new DispatchAdmission(
            true,
            "admission-command",
            DateTimeOffset.Parse("2026-07-23T01:02:03Z"),
            "admission-actor",
            "admission-correlation");
        var runtime = new RecordingActorRuntime();
        var dispatch = new RecordingActorDispatchPort(admission);
        var port = new AgentProfileActorPort(runtime, dispatch);
        using var cancellation = new CancellationTokenSource();

        var result = await dispatchCommand(port, cancellation.Token);

        result.Should().BeSameAs(admission);
        var call = dispatch.Calls.Should().ContainSingle().Subject;
        call.ActorId.Should().Be(expectedActorId);
        call.CancellationToken.Should().Be(cancellation.Token);
        call.Envelope.Id.Should().Be(OperationOf(command).CommandId);
        call.Envelope.Propagation.CorrelationId.Should()
            .Be(OperationOf(command).CorrelationId);
        call.Envelope.Route.GetTargetActorId().Should().Be(expectedActorId);
        call.Envelope.Payload.Is(command.Descriptor).Should().BeTrue();
        command.Descriptor.Parser.ParseFrom(call.Envelope.Payload.Value)
            .Should().Be(command);

        var expectedProfileActorId = AgentProfileActorIds.Profile(ProfileId);
        if (command is CreateAgentProfileCommand)
        {
            runtime.GetCalls.Should().Equal(
                AgentProfileActorIds.Namespace,
                expectedProfileActorId);
            runtime.CreateCalls.Should().Equal(
                (typeof(AgentProfileNamespaceGAgent), AgentProfileActorIds.Namespace),
                (typeof(AgentProfileGAgent), expectedProfileActorId));
        }
        else
        {
            runtime.GetCalls.Should().Equal(expectedProfileActorId);
            runtime.CreateCalls.Should().Equal(
                (typeof(AgentProfileGAgent), expectedProfileActorId));
        }
    }

    private static CreateAgentProfileCommand CreateCommand() =>
        new()
        {
            Identity = Identity(),
            InitialContent = GAgentServiceTestKit.CreateAgentProfileContent(),
            Operation = Operation("create"),
            ProfileActorId = AgentProfileActorIds.Profile(ProfileId),
        };

    private static IMessage CreateMutationCommand(MutationDispatchKind dispatchKind) =>
        dispatchKind switch
        {
            MutationDispatchKind.UpdateDraft => new UpdateAgentProfileDraftCommand
            {
                Identity = Identity(),
                Operation = Operation("invalid-update"),
            },
            MutationDispatchKind.UpsertSkillBinding => new UpsertAgentProfileSkillBindingCommand
            {
                Identity = Identity(),
                Operation = Operation("invalid-upsert"),
            },
            MutationDispatchKind.RemoveSkillBinding => new RemoveAgentProfileSkillBindingCommand
            {
                Identity = Identity(),
                Operation = Operation("invalid-remove"),
            },
            MutationDispatchKind.Publish => new PublishAgentProfileCommand
            {
                Identity = Identity(),
                Operation = Operation("invalid-publish"),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(dispatchKind)),
        };

    private static void ClearMutationPart(IMessage command, InvalidCommandPart invalidPart)
    {
        switch (command)
        {
            case UpdateAgentProfileDraftCommand value:
                if (invalidPart == InvalidCommandPart.Identity)
                    value.Identity = null!;
                else
                    value.Operation = null!;
                break;
            case UpsertAgentProfileSkillBindingCommand value:
                if (invalidPart == InvalidCommandPart.Identity)
                    value.Identity = null!;
                else
                    value.Operation = null!;
                break;
            case RemoveAgentProfileSkillBindingCommand value:
                if (invalidPart == InvalidCommandPart.Identity)
                    value.Identity = null!;
                else
                    value.Operation = null!;
                break;
            case PublishAgentProfileCommand value:
                if (invalidPart == InvalidCommandPart.Identity)
                    value.Identity = null!;
                else
                    value.Operation = null!;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }
    }

    private static Task<DispatchAdmission> DispatchMutationAsync(
        AgentProfileActorPort port,
        MutationDispatchKind dispatchKind,
        IMessage? command) =>
        dispatchKind switch
        {
            MutationDispatchKind.UpdateDraft =>
                port.DispatchUpdateDraftAsync((UpdateAgentProfileDraftCommand)command!),
            MutationDispatchKind.UpsertSkillBinding =>
                port.DispatchUpsertSkillBindingAsync((UpsertAgentProfileSkillBindingCommand)command!),
            MutationDispatchKind.RemoveSkillBinding =>
                port.DispatchRemoveSkillBindingAsync((RemoveAgentProfileSkillBindingCommand)command!),
            MutationDispatchKind.Publish =>
                port.DispatchPublishAsync((PublishAgentProfileCommand)command!),
            _ => throw new ArgumentOutOfRangeException(nameof(dispatchKind)),
        };

    private static void AssertNoLifecycleOrDispatch(
        RecordingActorRuntime runtime,
        RecordingActorDispatchPort dispatch)
    {
        runtime.GetCalls.Should().BeEmpty();
        runtime.CreateCalls.Should().BeEmpty();
        runtime.MaterializedCalls.Should().BeEmpty();
        dispatch.Calls.Should().BeEmpty();
    }

    private static AgentProfileIdentity Identity() =>
        GAgentServiceTestKit.CreateAgentProfileIdentity(ProfileId);

    private static AgentProfileOperationFact Operation(string suffix) =>
        new()
        {
            OperationId = $"operation-semantic-{suffix}",
            CommandId = $"command-attempt-{suffix}",
            CorrelationId = $"correlation-trace-{suffix}",
            InputSha256 = ByteString.CopyFrom(0x11, 0x22, 0x33),
        };

    private static AgentProfileOperationFact OperationOf(IMessage command) =>
        command switch
        {
            CreateAgentProfileCommand value => value.Operation,
            UpdateAgentProfileDraftCommand value => value.Operation,
            UpsertAgentProfileSkillBindingCommand value => value.Operation,
            RemoveAgentProfileSkillBindingCommand value => value.Operation,
            PublishAgentProfileCommand value => value.Operation,
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        private readonly Dictionary<string, IActor> _activeActors = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Type> _knownActorTypes = new(StringComparer.Ordinal);

        public List<string> GetCalls { get; } = [];
        public List<(Type ActorType, string ActorId)> CreateCalls { get; } = [];
        public List<(Type ActorType, string ActorId)> MaterializedCalls { get; } = [];

        public void AddDeactivated<TAgent>(string actorId)
            where TAgent : IAgent =>
            _knownActorTypes.Add(actorId, typeof(TAgent));

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
            var actorId = id ?? $"created:{agentType.Name}";
            CreateCalls.Add((agentType, actorId));
            _knownActorTypes[actorId] = agentType;
            return Task.FromResult(CreateActive(actorId));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            _activeActors.Remove(id);
            _knownActorTypes.Remove(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id)
        {
            GetCalls.Add(id);
            if (_activeActors.TryGetValue(id, out var active))
                return Task.FromResult<IActor?>(active);
            if (!_knownActorTypes.TryGetValue(id, out var actorType))
                return Task.FromResult<IActor?>(null);

            MaterializedCalls.Add((actorType, id));
            return Task.FromResult<IActor?>(CreateActive(id));
        }

        public Task<bool> ExistsAsync(string id) =>
            Task.FromResult(_knownActorTypes.ContainsKey(id));

        public Task LinkAsync(
            string parentId,
            string childId,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
            Task.CompletedTask;

        private IActor CreateActive(string actorId)
        {
            var actor = new RecordingActor(actorId);
            _activeActors[actorId] = actor;
            return actor;
        }
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        private readonly DispatchAdmission? _admission;

        public RecordingActorDispatchPort(DispatchAdmission? admission = null)
        {
            _admission = admission;
        }

        public List<(string ActorId, EventEnvelope Envelope, CancellationToken CancellationToken)> Calls { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            Calls.Add((actorId, envelope, ct));
            return Task.FromResult(
                _admission ?? DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class RecordingActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = new TestStaticServiceAgent();
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    public enum MutationDispatchKind
    {
        UpdateDraft,
        UpsertSkillBinding,
        RemoveSkillBinding,
        Publish,
    }

    public enum InvalidCommandPart
    {
        Command,
        Identity,
        Operation,
    }
}
