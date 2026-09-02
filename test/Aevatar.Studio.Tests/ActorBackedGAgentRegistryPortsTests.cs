using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgents.Registry;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Infrastructure.ActorBacked;
using Aevatar.Studio.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Studio.Tests;

public sealed class ActorBackedGAgentRegistryPortsTests
{
    private const string CanonicalKind = "tests.registry-agent";

    [Fact]
    public async Task RegisterAndAuthorize_ShouldDispatchAgentKindPayloads()
    {
        var actor = new StubActor("gagent-registry-scope-a");
        var dispatch = new RecordingCommandDispatchService();
        var runtime = new RecordingActorRuntime { ExistingActor = actor };
        var ports = NewPorts(
            dispatch,
            runtime,
            new RecordingBootstrap(actor),
            new RecordingDocumentReader());

        var receipt = await ports.RegisterActorAsync(new GAgentActorRegistration(
            " scope-a ",
            $" {CanonicalKind} ",
            " actor-1 "));
        var admission = await ports.AuthorizeTargetAsync(new ScopeResourceTarget(
            "scope-a",
            ScopeResourceKind.GAgentActor,
            CanonicalKind,
            "actor-1",
            ScopeResourceOperation.Chat));

        receipt.Registration.AgentKind.Should().Be(CanonicalKind);
        receipt.Stage.Should().Be(GAgentActorRegistryCommandStage.AdmissionVisible);
        admission.Status.Should().Be(ScopeResourceAdmissionStatus.Allowed);
        dispatch.Payloads.Should().HaveCount(3);
        dispatch.Payloads[0].Should().BeOfType<ActorRegisteredEvent>()
            .Which.AgentKind.Should().Be(CanonicalKind);
        dispatch.Payloads[1].Should().BeOfType<ScopeResourceAdmissionRequested>()
            .Which.AgentKind.Should().Be(CanonicalKind);
        dispatch.Payloads[2].Should().BeOfType<ScopeResourceAdmissionRequested>()
            .Which.Operation.Should().Be(GAgentRegistryOperation.Chat);
    }

    [Fact]
    public async Task ListActorsAsync_ShouldReturnOnlyCanonicalAgentKindGroups()
    {
        var state = new GAgentRegistryState
        {
            Groups =
            {
                new GAgentRegistryEntry
                {
                    AgentKind = CanonicalKind,
                    ActorIds = { "actor-1" },
                },
                new GAgentRegistryEntry
                {
                    AgentKind = "Legacy.Registry.Agent, Tests",
                    ActorIds = { "legacy-actor" },
                },
            },
        };
        var documentReader = new RecordingDocumentReader
        {
            Document = new GAgentRegistryCurrentStateDocument
            {
                Id = "gagent-registry-scope-a",
                ActorId = "gagent-registry-scope-a",
                StateVersion = 9,
                LastEventId = "evt-9",
                UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-06-04T08:00:00Z")),
                StateRoot = Any.Pack(state),
            },
        };
        var ports = NewPorts(
            new RecordingCommandDispatchService(),
            new RecordingActorRuntime(),
            new RecordingBootstrap(new StubActor("gagent-registry-scope-a")),
            documentReader);

        var snapshot = await ports.ListActorsAsync(" scope-a ");

        var group = snapshot.Groups.Should().ContainSingle().Subject;
        group.AgentKind.Should().Be(CanonicalKind);
        group.ActorIds.Should().ContainSingle("actor-1");
        snapshot.StateVersion.Should().Be(9);
        snapshot.UpdatedAt.Should().Be(DateTimeOffset.Parse("2026-06-04T08:00:00Z"));
        documentReader.RequestedKeys.Should().ContainSingle("gagent-registry-scope-a");
    }

    [Fact]
    public async Task AuthorizeTargetAsync_ShouldReturnNotFoundForUnregisteredAgentKind()
    {
        var ports = NewPorts(
            new RecordingCommandDispatchService(),
            new RecordingActorRuntime(),
            new RecordingBootstrap(new StubActor("gagent-registry-scope-a")),
            new RecordingDocumentReader());

        var result = await ports.AuthorizeTargetAsync(new ScopeResourceTarget(
            "scope-a",
            ScopeResourceKind.GAgentActor,
            "tests.missing-agent",
            "actor-1",
            ScopeResourceOperation.Use));

        result.Status.Should().Be(ScopeResourceAdmissionStatus.NotFound);
    }

    [Fact]
    public async Task AuthorizeTargetAsync_ShouldMapControlAsTypedRegistryOperation()
    {
        var actor = new StubActor("gagent-registry-scope-a");
        var dispatch = new RecordingCommandDispatchService();
        var ports = NewPorts(
            dispatch,
            new RecordingActorRuntime { ExistingActor = actor },
            new RecordingBootstrap(actor),
            new RecordingDocumentReader());
        var control = System.Enum.Parse<ScopeResourceOperation>("Control");

        var admission = await ports.AuthorizeTargetAsync(new ScopeResourceTarget(
            "scope-a",
            ScopeResourceKind.GAgentActor,
            CanonicalKind,
            "actor-1",
            control));

        admission.Status.Should().Be(ScopeResourceAdmissionStatus.Allowed);
        var requested = dispatch.Payloads.Should()
            .ContainSingle()
            .Which.Should()
            .BeOfType<ScopeResourceAdmissionRequested>()
            .Which;
        requested.Operation.ToString().Should().Be("Control");
    }

    private static ActorBackedGAgentRegistryPorts NewPorts(
        RecordingCommandDispatchService dispatch,
        RecordingActorRuntime runtime,
        RecordingBootstrap bootstrap,
        RecordingDocumentReader documentReader) =>
        new(
            bootstrap,
            runtime,
            new StudioActorCommandDispatch(dispatch),
            new StaticScopeResolver(),
            documentReader,
            BuildRegistry(),
            NullLogger<ActorBackedGAgentRegistryPorts>.Instance);

    private static IAgentKindRegistry BuildRegistry() =>
        new AgentKindRegistry(
            [
                new AgentRegistration(CanonicalKind, typeof(TestRegistryAgent), typeof(object)),
            ]);

    private sealed class RecordingCommandDispatchService
        : ICommandDispatchService<StudioActorCommand, StudioActorCommandReceipt, StudioActorCommandStartError>
    {
        public List<IMessage> Payloads { get; } = [];

        public Task<CommandDispatchResult<StudioActorCommandReceipt, StudioActorCommandStartError>> DispatchAsync(
            StudioActorCommand command,
            CancellationToken ct = default)
        {
            Payloads.Add(command.Payload);
            return Task.FromResult(CommandDispatchResult<StudioActorCommandReceipt, StudioActorCommandStartError>.Success(
                new StudioActorCommandReceipt(command.Actor.Id, "cmd-1", "corr-1")));
        }
    }

    private sealed class RecordingDocumentReader
        : IProjectionDocumentReader<GAgentRegistryCurrentStateDocument, string>
    {
        public GAgentRegistryCurrentStateDocument? Document { get; init; }
        public List<string> RequestedKeys { get; } = [];

        public Task<GAgentRegistryCurrentStateDocument?> GetAsync(string key, CancellationToken ct = default)
        {
            RequestedKeys.Add(key);
            return Task.FromResult(Document);
        }

        public Task<ProjectionDocumentQueryResult<GAgentRegistryCurrentStateDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            Task.FromResult(ProjectionDocumentQueryResult<GAgentRegistryCurrentStateDocument>.Empty);
    }

    private sealed class RecordingBootstrap(IActor actor) : IStudioActorBootstrap
    {
        public Task<IActor> EnsureAsync<TAgent>(string actorId, CancellationToken ct = default)
            where TAgent : IAgent, IProjectedActor =>
            Task.FromResult(actor);
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        public IActor? ExistingActor { get; init; }

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            Task.FromResult<IActor>(new StubActor(id ?? "created"));

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default) =>
            Task.FromResult<IActor>(new StubActor(id ?? "created"));

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IActor?> GetAsync(string id) =>
            Task.FromResult(ExistingActor is not null && ExistingActor.Id == id ? ExistingActor : null);

        public Task<bool> ExistsAsync(string id) => Task.FromResult(ExistingActor?.Id == id);

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StaticScopeResolver : IAppScopeResolver
    {
        public AppScopeContext? Resolve(HttpContext? httpContext = null) => new("scope-a", "test");

        public bool HasAuthenticatedRequestWithoutScope(HttpContext? httpContext = null) => false;

        public bool HasHttpRequestContext(HttpContext? httpContext = null) => false;
    }

    private sealed class StubActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = new TestRegistryAgent();
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    [GAgent(CanonicalKind)]
    private sealed class TestRegistryAgent : IAgent
    {
        public string Id { get; } = "test-registry-agent";
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult(string.Empty);
        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
    }
}
