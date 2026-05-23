using Aevatar.Foundation.Abstractions;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Domain.Studio.Models;
using Aevatar.Studio.Infrastructure.ActorBacked;
using Aevatar.Studio.Workspace;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using ApplicationWorkspaceDirectory = Aevatar.Studio.Application.Studio.Abstractions.StudioWorkspaceDirectory;
using ApplicationWorkspaceSettings = Aevatar.Studio.Application.Studio.Abstractions.StudioWorkspaceSettings;
using SystemType = System.Type;

namespace Aevatar.Tools.Cli.Tests;

public sealed class ActorDispatchStudioWorkspaceCommandPortTests
{
    [Fact]
    public async Task UpdateSettingsAsync_ShouldDispatchSettingsUpdatedEvent()
    {
        var harness = new CommandPortHarness();

        var receipt = await harness.Port.UpdateSettingsAsync(
            new ApplicationWorkspaceSettings("http://127.0.0.1:5100", [], "teal", "dark"),
            expectedVersion: 3);

        var evt = harness.SinglePayload<StudioWorkspaceSettingsUpdated>();
        evt.WorkspaceId.Should().Be("studio-workspace:scope-1");
        evt.ScopeId.Should().Be("scope-1");
        evt.ExpectedVersion.Should().Be(3);
        evt.Settings.RuntimeBaseUrl.Should().Be("http://127.0.0.1:5100");
        receipt.WorkspaceId.Should().Be("studio-workspace:scope-1");
        receipt.ActorId.Should().Be("studio-workspace:scope-1");
        receipt.CommandId.Should().Be(harness.SingleEnvelopeId);
        receipt.ExpectedVersion.Should().Be(3);
    }

    [Fact]
    public async Task AddDirectoryAsync_ShouldDispatchDirectoryAddedEvent()
    {
        var harness = new CommandPortHarness();

        await harness.Port.AddDirectoryAsync(
            new ApplicationWorkspaceDirectory("dir-1", "Drafts", "/tmp/drafts", true),
            expectedVersion: 4);

        var evt = harness.SinglePayload<StudioWorkspaceDirectoryAdded>();
        evt.WorkspaceId.Should().Be("studio-workspace:scope-1");
        evt.ScopeId.Should().Be("scope-1");
        evt.ExpectedVersion.Should().Be(4);
        evt.Directory.DirectoryId.Should().Be("dir-1");
        evt.Directory.Label.Should().Be("Drafts");
        evt.Directory.Path.Should().Be("/tmp/drafts");
        evt.Directory.IsBuiltIn.Should().BeTrue();
    }

    [Fact]
    public async Task RemoveDirectoryAsync_ShouldDispatchDirectoryRemovedEvent()
    {
        var harness = new CommandPortHarness();

        await harness.Port.RemoveDirectoryAsync(" dir-1 ", expectedVersion: 5);

        var evt = harness.SinglePayload<StudioWorkspaceDirectoryRemoved>();
        evt.WorkspaceId.Should().Be("studio-workspace:scope-1");
        evt.ScopeId.Should().Be("scope-1");
        evt.ExpectedVersion.Should().Be(5);
        evt.DirectoryId.Should().Be("dir-1");
    }

    [Fact]
    public async Task SaveDraftAsync_ShouldDispatchDraftSavedEventWithoutServerLayoutFact()
    {
        var harness = new CommandPortHarness();
        var updatedAt = DateTimeOffset.Parse("2026-05-19T10:00:00Z");
        var createdAt = updatedAt.AddHours(-1);

        await harness.Port.SaveDraftAsync(
            new StudioWorkflowDraftRecord(
                "workflow-1",
                "workflow-one",
                "workflow-one.yaml",
                "/tmp/drafts/workflow-one.yaml",
                "dir-1",
                "Drafts",
                "name: workflow-one\nsteps: []\n",
                NewLayout(),
                updatedAt,
                createdAt,
                Version: 2),
            expectedVersion: 6);

        var evt = harness.SinglePayload<StudioWorkflowDraftSaved>();
        evt.WorkspaceId.Should().Be("studio-workspace:scope-1");
        evt.ScopeId.Should().Be("scope-1");
        evt.ExpectedVersion.Should().Be(6);
        evt.Draft.WorkflowId.Should().Be("workflow-1");
        evt.Draft.Name.Should().Be("workflow-one");
        evt.Draft.FileName.Should().Be("workflow-one.yaml");
        evt.Draft.DirectoryId.Should().Be("dir-1");
        evt.Draft.DirectoryLabel.Should().Be("Drafts");
        evt.Draft.Yaml.Should().Contain("workflow-one");
        evt.Draft.Version.Should().Be(2);
        evt.Draft.CreatedAtUtc.ToDateTimeOffset().Should().Be(createdAt);
        evt.Draft.UpdatedAtUtc.ToDateTimeOffset().Should().Be(updatedAt);
    }

    [Fact]
    public async Task SaveDraftAsync_WithExplicitScope_ShouldDispatchRequestedScopeInsteadOfAmbientScope()
    {
        var harness = new CommandPortHarness("ambient-scope");
        var updatedAt = DateTimeOffset.Parse("2026-05-19T10:00:00Z");

        var receipt = await harness.Port.SaveDraftAsync(
            " requested-scope ",
            new StudioWorkflowDraftRecord(
                "workflow-1",
                "workflow-one",
                "workflow-one.yaml",
                "/tmp/drafts/workflow-one.yaml",
                "dir-1",
                "Drafts",
                "name: workflow-one\nsteps: []\n",
                Layout: null,
                updatedAt,
                updatedAt,
                Version: 2),
            expectedVersion: 8);

        var evt = harness.SinglePayload<StudioWorkflowDraftSaved>("studio-workspace:requested-scope");
        evt.WorkspaceId.Should().Be("studio-workspace:requested-scope");
        evt.ScopeId.Should().Be("requested-scope");
        evt.ExpectedVersion.Should().Be(8);
        evt.Draft.WorkflowId.Should().Be("workflow-1");
        receipt.WorkspaceId.Should().Be("studio-workspace:requested-scope");
        receipt.ActorId.Should().Be("studio-workspace:requested-scope");
        receipt.ExpectedVersion.Should().Be(8);
    }

    [Fact]
    public async Task DeleteDraftAsync_ShouldDispatchDraftDeletedEvent()
    {
        var harness = new CommandPortHarness();

        await harness.Port.DeleteDraftAsync(" workflow-1 ", expectedVersion: 7);

        var evt = harness.SinglePayload<StudioWorkflowDraftDeleted>();
        evt.WorkspaceId.Should().Be("studio-workspace:scope-1");
        evt.ScopeId.Should().Be("scope-1");
        evt.ExpectedVersion.Should().Be(7);
        evt.WorkflowId.Should().Be("workflow-1");
    }

    [Fact]
    public async Task DeleteDraftAsync_WithExplicitScope_ShouldDispatchRequestedScopeInsteadOfAmbientScope()
    {
        var harness = new CommandPortHarness("ambient-scope");

        var receipt = await harness.Port.DeleteDraftAsync(" requested-scope ", " workflow-1 ", expectedVersion: 9);

        var evt = harness.SinglePayload<StudioWorkflowDraftDeleted>("studio-workspace:requested-scope");
        evt.WorkspaceId.Should().Be("studio-workspace:requested-scope");
        evt.ScopeId.Should().Be("requested-scope");
        evt.ExpectedVersion.Should().Be(9);
        evt.WorkflowId.Should().Be("workflow-1");
        receipt.WorkspaceId.Should().Be("studio-workspace:requested-scope");
        receipt.ActorId.Should().Be("studio-workspace:requested-scope");
        receipt.ExpectedVersion.Should().Be(9);
    }

    [Fact]
    public async Task DeleteDraftAsync_WhenExpectedVersionMissing_ShouldDispatchZeroButReturnWeakReceipt()
    {
        var harness = new CommandPortHarness();

        var receipt = await harness.Port.DeleteDraftAsync("workflow-1");

        var evt = harness.SinglePayload<StudioWorkflowDraftDeleted>();
        evt.ExpectedVersion.Should().Be(0);
        evt.WorkflowId.Should().Be("workflow-1");
        receipt.ExpectedVersion.Should().BeNull();
        receipt.CommandId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task RemoveDirectoryAsync_WhenDirectoryIdIsBlank_ShouldRejectBeforeDispatch()
    {
        var harness = new CommandPortHarness();

        var act = () => harness.Port.RemoveDirectoryAsync("   ");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("directoryId is required.");
        harness.DispatchCount.Should().Be(0);
    }

    private static WorkflowLayoutDocument NewLayout() => new()
    {
        EntryWorkflow = "workflow-one",
        Viewport = new WorkflowViewport(1, 2, 0.75),
        NodePositions = new Dictionary<string, WorkflowNodeLayout>(StringComparer.Ordinal)
        {
            ["start"] = new(10, 20),
        },
        Groups = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["group-1"] = ["start"],
        },
        Collapsed = ["group-1"],
    };

    private sealed class CommandPortHarness
    {
        private readonly RecordingDispatchPort _dispatchPort = new();

        public CommandPortHarness(string scopeId = "scope-1")
        {
            Port = new ActorDispatchStudioWorkspaceCommandPort(
                new StubBootstrap(),
                CreateCommandDispatch(_dispatchPort),
                new StubScopeResolver(scopeId));
        }

        public ActorDispatchStudioWorkspaceCommandPort Port { get; }

        public int DispatchCount => _dispatchPort.Dispatches.Count;

        public string SingleEnvelopeId => _dispatchPort.Dispatches.Should().ContainSingle().Which.Envelope.Id;

        public TPayload SinglePayload<TPayload>(string expectedActorId = "studio-workspace:scope-1")
            where TPayload : IMessage, new()
        {
            _dispatchPort.Dispatches.Should().ContainSingle();
            var dispatch = _dispatchPort.Dispatches[0];
            dispatch.ActorId.Should().Be(expectedActorId);
            dispatch.Envelope.Payload.Is(new TPayload().Descriptor).Should().BeTrue();
            return dispatch.Envelope.Payload.Unpack<TPayload>();
        }
    }

    private sealed class StubBootstrap : IStudioActorBootstrap
    {
        public Task<IActor> EnsureAsync<TAgent>(string actorId, CancellationToken ct = default)
            where TAgent : IAgent, IProjectedActor =>
            Task.FromResult<IActor>(new StubActor(actorId));
    }

    private sealed class RecordingDispatchPort : IActorDispatchPort
    {
        public List<DispatchRecord> Dispatches { get; } = [];

        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Dispatches.Add(new DispatchRecord(actorId, envelope));
            return Task.CompletedTask;
        }
    }

    private sealed record DispatchRecord(string ActorId, EventEnvelope Envelope);

    private static StudioActorCommandDispatch CreateCommandDispatch(IActorDispatchPort dispatchPort)
    {
        var service = new Aevatar.CQRS.Core.Commands.DefaultCommandDispatchService<
            StudioActorCommand,
            StudioActorCommandTarget,
            StudioActorCommandReceipt,
            StudioActorCommandStartError>(
            new Aevatar.CQRS.Core.Commands.DefaultCommandDispatchPipeline<
                StudioActorCommand,
                StudioActorCommandTarget,
                StudioActorCommandReceipt,
                StudioActorCommandStartError>(
                new StudioActorCommandTargetResolver(),
                new Aevatar.CQRS.Core.Commands.DefaultCommandContextPolicy(),
                new StudioActorCommandEnvelopeFactory(),
                new Aevatar.CQRS.Core.Commands.ActorCommandTargetDispatcher<StudioActorCommandTarget>(dispatchPort),
                new StudioActorCommandReceiptFactory()));
        return new StudioActorCommandDispatch(service);
    }

    private sealed class StubActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = new StubAgent(id);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class StubAgent(string id) : IAgent
    {
        public string Id { get; } = id;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult(string.Empty);
        public Task<IReadOnlyList<SystemType>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<SystemType>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubScopeResolver(string scopeId) : IAppScopeResolver
    {
        public AppScopeContext? Resolve(HttpContext? httpContext = null) => new(scopeId, "test");
        public bool HasAuthenticatedRequestWithoutScope(HttpContext? httpContext = null) => false;
    }
}
