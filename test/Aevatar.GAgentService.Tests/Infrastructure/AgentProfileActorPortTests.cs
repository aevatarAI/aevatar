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
    }

    [Fact]
    public async Task EnsureCreateTargetsAsync_ShouldKeepDifferentOwnerNamespacesSeparate()
    {
        var runtime = new RecordingActorRuntime();
        var port = new AgentProfileActorPort(runtime, new RecordingDispatchPort());
        var scopeOwner = AgentProfileOwners.ForScope("scope-alpha");
        var systemOwner = AgentProfileOwners.ForSystem();

        await port.EnsureCreateTargetsAsync(scopeOwner, "prof-alpha");
        await port.EnsureCreateTargetsAsync(systemOwner, "prof-beta");

        var scopeNamespaceActorId = AgentProfileActorIds.Namespace(scopeOwner);
        var systemNamespaceActorId = AgentProfileActorIds.Namespace(systemOwner);
        scopeNamespaceActorId.Should().NotBe(systemNamespaceActorId);
        runtime.CreateCalls.Should().Contain((typeof(AgentProfileNamespaceGAgent), scopeNamespaceActorId));
        runtime.CreateCalls.Should().Contain((typeof(AgentProfileNamespaceGAgent), systemNamespaceActorId));
        runtime.CreateCalls.Should().Contain((typeof(AgentProfileGAgent), AgentProfileActorIds.Profile("prof-alpha")));
        runtime.CreateCalls.Should().Contain((typeof(AgentProfileGAgent), AgentProfileActorIds.Profile("prof-beta")));
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
            NamespaceActorId = AgentProfileActorIds.Namespace(identity.Owner),
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
            ProfileId = "prof-alpha",
            Enabled = true,
            CohortBasisPoints = 10_000,
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
        var expectedAdmission = new DispatchAdmission(
            false,
            "cmd-rejected",
            DateTimeOffset.Parse("2026-07-30T00:00:00Z"),
            "profile-actor",
            "corr-rejected");
        var dispatch = new RecordingDispatchPort(expectedAdmission);
        var port = new AgentProfileActorPort(new RecordingActorRuntime(), dispatch);
        var command = new UpdateAgentProfileDraftCommand
        {
            Identity = Identity("prof-alpha"),
            Draft = new AgentProfileDraft { DisplayName = "Profile" },
            Operation = Operation("rejected"),
        };

        var admission = await port.DispatchUpdateDraftAsync("profile-actor", command);

        admission.Should().BeSameAs(expectedAdmission);
        admission.Accepted.Should().BeFalse();
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
        ProfileActorId = AgentProfileActorIds.Profile(profileId),
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

        public void MarkExisting(string actorId) => _actors[actorId] = new RecordingActor(actorId);

        public int GetActivationCount(string actorId) => _actors[actorId].ActivationCount;

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent => CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default)
        {
            var actorId = id ?? throw new InvalidOperationException("A deterministic actor id is required.");
            CreateCalls.Add((agentType, actorId));
            var actor = new RecordingActor(actorId);
            _actors[actorId] = actor;
            return Task.FromResult<IActor>(actor);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IActor?> GetAsync(string id) =>
            Task.FromResult<IActor?>(_actors.GetValueOrDefault(id));

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

    private sealed class RecordingActor(string id) : IActor
    {
        public string Id { get; } = id;

        public IAgent Agent { get; } = new RecordingAgent(id);

        public int ActivationCount { get; private set; }

        public Task ActivateAsync(CancellationToken ct = default)
        {
            ActivationCount++;
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
