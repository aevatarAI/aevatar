using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Core.AgentProfiles;
using Aevatar.GAgentService.Infrastructure.AgentProfiles;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Infrastructure;

public sealed class AgentProfileActorPortTests
{
    [Fact]
    public async Task DispatchCreateAsync_ShouldEnsureTypedOwnerNamespaceAndOpaqueProfileTargets()
    {
        var runtime = new RecordingActorRuntime();
        var dispatch = new RecordingDispatchPort();
        var port = new AgentProfileActorPort(runtime, dispatch);
        var command = CreateCreateCommand(AgentProfileOwners.ForScope("scope-alpha"), "prof-alpha", "create");
        var expectedNamespaceActorId = AgentProfileActorIds.Namespace(command.Owner);
        var expectedProfileActorId = AgentProfileActorIds.Profile(command.ProfileId);

        var admission = await port.DispatchCreateAsync(command);

        admission.Accepted.Should().BeTrue();
        runtime.CreateCalls.Should().Contain((typeof(AgentProfileNamespaceGAgent), expectedNamespaceActorId));
        runtime.CreateCalls.Should().Contain((typeof(AgentProfileGAgent), expectedProfileActorId));
        runtime.GetActivationCount(expectedNamespaceActorId).Should().Be(1);
        runtime.GetActivationCount(expectedProfileActorId).Should().Be(1);
        dispatch.Calls.Should().ContainSingle();
        dispatch.Calls[0].ActorId.Should().Be(expectedNamespaceActorId);
        AssertEnvelope(dispatch.Calls[0].Envelope, expectedNamespaceActorId, command);
        admission.CommandId.Should().Be(dispatch.Calls[0].Envelope.Id);
        admission.CorrelationId.Should().Be(dispatch.Calls[0].Envelope.Propagation.CorrelationId);
    }

    [Fact]
    public async Task DispatchProfileCommandsAsync_ShouldReactivateKnownProfileActorAndTargetOnlyThatActor()
    {
        var runtime = new RecordingActorRuntime();
        var dispatch = new RecordingDispatchPort();
        var port = new AgentProfileActorPort(runtime, dispatch);
        var profileActorId = AgentProfileActorIds.Profile("prof-alpha");
        runtime.MarkExisting(profileActorId);
        var identity = Identity("prof-alpha");
        var initialize = new InitializeAgentProfileCommand
        {
            Identity = identity.Clone(),
            Operation = Operation("initialize"),
        };
        var update = new UpdateAgentProfileDraftCommand
        {
            Identity = identity.Clone(),
            Draft = new AgentProfileDraft { DisplayName = "Profile" },
            ExpectedAuthorityStateVersion = 3,
            Operation = Operation("update"),
        };
        var publish = new PublishAgentProfileCommand
        {
            Identity = identity.Clone(),
            Snapshot = new AgentProfilePublishedSnapshot { Identity = identity.Clone() },
            ExpectedAuthorityStateVersion = 4,
            Operation = Operation("publish"),
        };

        await port.DispatchInitializeAsync(profileActorId, initialize);
        await port.DispatchUpdateDraftAsync(profileActorId, update);
        await port.DispatchPublishAsync(profileActorId, publish);

        runtime.CreateCalls.Should().BeEmpty();
        runtime.GetActivationCount(profileActorId).Should().Be(3);
        dispatch.Calls.Should().HaveCount(3);
        AssertEnvelope(dispatch.Calls[0].Envelope, profileActorId, initialize);
        AssertEnvelope(dispatch.Calls[1].Envelope, profileActorId, update);
        AssertEnvelope(dispatch.Calls[2].Envelope, profileActorId, publish);
    }

    [Fact]
    public async Task DispatchInitializeAsync_ShouldRejectForgedProfileAddressBeforeLifecycle()
    {
        var identity = Identity("prof-alpha");
        var forgedProfileRuntime = new RecordingActorRuntime();
        var forgedProfileDispatch = new RecordingDispatchPort();
        var forgedProfilePort = new AgentProfileActorPort(forgedProfileRuntime, forgedProfileDispatch);
        var command = new InitializeAgentProfileCommand
        {
            Identity = identity.Clone(),
            Operation = Operation("initialize-forged-profile"),
        };

        var forgedProfile = () => forgedProfilePort.DispatchInitializeAsync("forged-profile-actor", command);

        await forgedProfile.Should().ThrowAsync<ArgumentException>();
        AssertNoRuntimeSideEffects(forgedProfileRuntime, forgedProfileDispatch);
    }

    [Fact]
    public async Task DispatchBindingCommandsAsync_ShouldTargetOnlyTypedOwnerNamespace()
    {
        var runtime = new RecordingActorRuntime();
        var dispatch = new RecordingDispatchPort();
        var port = new AgentProfileActorPort(runtime, dispatch);
        var owner = AgentProfileOwners.ForScope("scope-alpha");
        var expectedNamespaceActorId = AgentProfileActorIds.Namespace(owner);
        var set = new SetAgentProfileDefaultBindingCommand
        {
            Owner = owner.Clone(),
            AgentKind = AgentProfilePolicies.NyxIdChatAgentKind,
            Target = new AgentProfileBindingTarget
            {
                Owner = owner.Clone(),
                ProfileId = "prof-alpha",
                PublishedRevision = 1,
                SnapshotSha256 = ByteString.CopyFrom(new byte[32]),
            },
            Scope = new AgentProfileScopeBindingAdmission(),
            ExpectedAuthorityStateVersion = 7,
            Operation = Operation("set-binding"),
        };
        var clear = new ClearAgentProfileDefaultBindingCommand
        {
            Owner = owner.Clone(),
            AgentKind = AgentProfilePolicies.NyxIdChatAgentKind,
            ExpectedAuthorityStateVersion = 8,
            Operation = Operation("clear-binding"),
        };

        await port.DispatchSetDefaultBindingAsync(set);
        await port.DispatchClearDefaultBindingAsync(clear);

        runtime.CreateCalls.Should().ContainSingle()
            .Which.Should().Be((typeof(AgentProfileNamespaceGAgent), expectedNamespaceActorId));
        runtime.GetActivationCount(expectedNamespaceActorId).Should().Be(2);
        dispatch.Calls.Should().HaveCount(2);
        AssertEnvelope(dispatch.Calls[0].Envelope, expectedNamespaceActorId, set);
        AssertEnvelope(dispatch.Calls[1].Envelope, expectedNamespaceActorId, clear);
    }

    [Fact]
    public async Task DispatchUpdateDraftAsync_ShouldReturnRejectedAdmissionUnchanged()
    {
        var profileActorId = AgentProfileActorIds.Profile("prof-alpha");
        var expectedAdmission = new DispatchAdmission(
            false,
            "cmd-rejected",
            DateTimeOffset.Parse("2026-07-30T00:00:00Z"),
            profileActorId,
            "corr-rejected");
        var dispatch = new RecordingDispatchPort(expectedAdmission);
        var port = new AgentProfileActorPort(new RecordingActorRuntime(), dispatch);
        var command = new UpdateAgentProfileDraftCommand
        {
            Identity = Identity("prof-alpha"),
            Draft = new AgentProfileDraft { DisplayName = "Profile" },
            Operation = Operation("rejected"),
        };

        var admission = await port.DispatchUpdateDraftAsync(profileActorId, command);

        admission.Should().BeSameAs(expectedAdmission);
        admission.Accepted.Should().BeFalse();
    }

    [Fact]
    public async Task DispatchCommandsAsync_ShouldRejectIncompleteOperationBeforeLifecycleOrAdmission()
    {
        var runtime = new RecordingActorRuntime();
        var dispatch = new RecordingDispatchPort();
        var port = new AgentProfileActorPort(runtime, dispatch);
        var identity = Identity("prof-alpha");
        var profileActorId = AgentProfileActorIds.Profile(identity.ProfileId);
        var invalid = new AgentProfileOperationFact
        {
            OperationId = "op-invalid",
            CommandId = string.Empty,
            CorrelationId = "corr-invalid",
        };
        var create = CreateCreateCommand(identity.Owner, identity.ProfileId, "create-invalid");
        create.Operation = invalid.Clone();
        var initialize = new InitializeAgentProfileCommand
        {
            Identity = identity.Clone(),
            Operation = invalid.Clone(),
        };
        var update = new UpdateAgentProfileDraftCommand
        {
            Identity = identity.Clone(),
            Operation = invalid.Clone(),
        };
        var publish = new PublishAgentProfileCommand
        {
            Identity = identity.Clone(),
            Operation = invalid.Clone(),
        };
        var set = new SetAgentProfileDefaultBindingCommand
        {
            Owner = identity.Owner.Clone(),
            Operation = invalid.Clone(),
        };
        var clear = new ClearAgentProfileDefaultBindingCommand
        {
            Owner = identity.Owner.Clone(),
            Operation = invalid.Clone(),
        };

        var dispatches = new Func<Task>[]
        {
            () => port.DispatchCreateAsync(create),
            () => port.DispatchInitializeAsync(profileActorId, initialize),
            () => port.DispatchUpdateDraftAsync(profileActorId, update),
            () => port.DispatchPublishAsync(profileActorId, publish),
            () => port.DispatchSetDefaultBindingAsync(set),
            () => port.DispatchClearDefaultBindingAsync(clear),
        };

        foreach (var dispatchCommand in dispatches)
            await dispatchCommand.Should().ThrowAsync<ArgumentException>();

        AssertNoRuntimeSideEffects(runtime, dispatch);
    }

    [Theory]
    [InlineData(" op-invalid", "cmd-invalid", "corr-invalid")]
    [InlineData("op-invalid", " cmd-invalid", "corr-invalid")]
    [InlineData("op-invalid", "cmd-invalid", " corr-invalid")]
    public async Task DispatchUpdateDraftAsync_ShouldRejectOperationIdentifiersThatWouldDriftFromAdmission(
        string operationId,
        string commandId,
        string correlationId)
    {
        var runtime = new RecordingActorRuntime();
        var dispatch = new RecordingDispatchPort();
        var port = new AgentProfileActorPort(runtime, dispatch);
        var identity = Identity("prof-alpha");
        var command = new UpdateAgentProfileDraftCommand
        {
            Identity = identity.Clone(),
            Operation = new AgentProfileOperationFact
            {
                OperationId = operationId,
                CommandId = commandId,
                CorrelationId = correlationId,
            },
        };

        var act = () => port.DispatchUpdateDraftAsync(AgentProfileActorIds.Profile(identity.ProfileId), command);

        await act.Should().ThrowAsync<ArgumentException>();
        AssertNoRuntimeSideEffects(runtime, dispatch);
    }

    private static void AssertNoRuntimeSideEffects(RecordingActorRuntime runtime, RecordingDispatchPort dispatch)
    {
        runtime.GetCalls.Should().BeEmpty();
        runtime.CreateCalls.Should().BeEmpty();
        runtime.ActivationCalls.Should().BeEmpty();
        dispatch.Calls.Should().BeEmpty();
    }

    private static void AssertEnvelope(EventEnvelope envelope, string expectedActorId, IMessage command)
    {
        var operation = command switch
        {
            CreateAgentProfileCommand create => create.Operation,
            InitializeAgentProfileCommand initialize => initialize.Operation,
            UpdateAgentProfileDraftCommand update => update.Operation,
            PublishAgentProfileCommand publish => publish.Operation,
            SetAgentProfileDefaultBindingCommand set => set.Operation,
            ClearAgentProfileDefaultBindingCommand clear => clear.Operation,
            _ => throw new InvalidOperationException($"Unsupported command type '{command.GetType().Name}'."),
        };

        envelope.Id.Should().Be(operation.CommandId);
        envelope.Propagation.CorrelationId.Should().Be(operation.CorrelationId);
        envelope.Route.GetTargetActorId().Should().Be(expectedActorId);
        envelope.Payload.Is(command.Descriptor).Should().BeTrue();
    }

    private static CreateAgentProfileCommand CreateCreateCommand(
        AgentProfileOwner owner,
        string profileId,
        string operationId) => new()
    {
        Owner = owner.Clone(),
        ProfileId = profileId,
        ProfileSlug = "research-assistant",
        Operation = Operation(operationId),
    };

    private static AgentProfileIdentity Identity(string profileId) => new()
    {
        ProfileId = profileId,
        Owner = AgentProfileOwners.ForScope("scope-alpha"),
        ProfileSlug = "research-assistant",
    };

    private static AgentProfileOperationFact Operation(string operationId) => new()
    {
        OperationId = operationId,
        CommandId = $"cmd-{operationId}",
        CorrelationId = $"corr-{operationId}",
        InputSha256 = ByteString.CopyFrom(Enumerable.Repeat((byte)0x42, 32).ToArray()),
        RequestedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-30T00:00:00Z")),
    };

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        private readonly Dictionary<string, RecordingActor> _actors = new(StringComparer.Ordinal);

        public List<(System.Type ActorType, string ActorId)> CreateCalls { get; } = [];

        public List<string> GetCalls { get; } = [];

        public List<string> ActivationCalls { get; } = [];

        public void MarkExisting(string actorId) => _actors[actorId] = new RecordingActor(actorId, ActivationCalls);

        public int GetActivationCount(string actorId) => _actors[actorId].ActivationCount;

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent => CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default)
        {
            var actorId = id ?? throw new InvalidOperationException("A deterministic actor id is required.");
            CreateCalls.Add((agentType, actorId));
            var actor = new RecordingActor(actorId, ActivationCalls);
            _actors[actorId] = actor;
            return Task.FromResult<IActor>(actor);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IActor?> GetAsync(string id)
        {
            GetCalls.Add(id);
            return Task.FromResult<IActor?>(_actors.GetValueOrDefault(id));
        }

        public Task<bool> ExistsAsync(string id) => Task.FromResult(_actors.ContainsKey(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingDispatchPort(DispatchAdmission? admission = null) : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Calls { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Calls.Add((actorId, envelope));
            return Task.FromResult(admission ?? DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class RecordingActor(string id, List<string> activationCalls) : IActor
    {
        public string Id { get; } = id;

        public IAgent Agent { get; } = new RecordingAgent(id);

        public int ActivationCount { get; private set; }

        public Task ActivateAsync(CancellationToken ct = default)
        {
            ActivationCount++;
            activationCalls.Add(Id);
            return Task.CompletedTask;
        }

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class RecordingAgent(string id) : IAgent
    {
        public string Id { get; } = id;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("recording");

        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
