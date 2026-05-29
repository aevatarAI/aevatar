using Aevatar.CQRS.Core.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.StudioMember;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Domain.Studio.Models;
using Aevatar.Studio.Projection.CommandServices;
using Aevatar.Studio.Projection.Projectors;
using Aevatar.Studio.Projection.Repair;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

public sealed class StudioWorkflowDraftMemberRepairServiceTests
{
    [Fact]
    public async Task RepairScopeAsync_ShouldDispatchEnsureMember_ForEachExplicitScopeDraft()
    {
        var workspace = new StubWorkspaceQueryPort([
            NewDraft("workflow-1", "Workflow One"),
            NewDraft("workflow-2", "Workflow Two"),
        ]);
        var bootstrap = new StudioWorkflowDraftMemberCommandDispatchTestHarness.RecordingBootstrap();
        var dispatch = new StudioWorkflowDraftMemberCommandDispatchTestHarness.RecordingDispatchPort();
        var service = NewService(workspace, bootstrap, dispatch);

        var result = await service.RepairScopeAsync("scope-1");

        result.ScopeId.Should().Be("scope-1");
        result.DraftCount.Should().Be(2);
        result.AcceptedCount.Should().Be(2);
        result.SkippedCount.Should().Be(0);
        result.FailedCount.Should().Be(0);
        bootstrap.EnsuredActorIds.Should().Equal(
            "studio-member:scope-1:workflow-1",
            "studio-member:scope-1:workflow-2");
        dispatch.Dispatches.Should().HaveCount(2);
        var first = dispatch.Dispatches[0];
        first.ActorId.Should().Be("studio-member:scope-1:workflow-1");
        var command = first.Envelope.Payload.Unpack<EnsureStudioMember>();
        command.ScopeId.Should().Be("scope-1");
        command.MemberId.Should().Be("workflow-1");
        command.DisplayName.Should().Be("Workflow One");
        first.Envelope.Id.Should().Be(
            "aevatar.studio.projection.workflow-draft-member-ensure:scope-1:workflow-1");
        first.Envelope.Runtime?.Deduplication?.OperationId.Should().Be(first.Envelope.Id);
    }

    [Fact]
    public async Task RepairScopeAsync_ShouldReturnSkipped_ForDraftWithoutWorkflowId()
    {
        var workspace = new StubWorkspaceQueryPort([
            NewDraft("   ", "Missing Id"),
        ]);
        var dispatch = new StudioWorkflowDraftMemberCommandDispatchTestHarness.RecordingDispatchPort();
        var service = NewService(
            workspace,
            new StudioWorkflowDraftMemberCommandDispatchTestHarness.RecordingBootstrap(),
            dispatch);

        var result = await service.RepairScopeAsync("scope-1");

        result.DraftCount.Should().Be(1);
        result.AcceptedCount.Should().Be(0);
        result.SkippedCount.Should().Be(1);
        result.FailedCount.Should().Be(0);
        result.Items.Should().ContainSingle()
            .Which.Status.Should().Be(StudioWorkflowDraftMemberRepairItem.Skipped);
        dispatch.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task RepairScopeAsync_ShouldSurfaceFailedItem_WithoutRetainedRetryState()
    {
        var workspace = new StubWorkspaceQueryPort([
            NewDraft("workflow-1", "Workflow One"),
        ]);
        var service = NewService(
            workspace,
            new StudioWorkflowDraftMemberCommandDispatchTestHarness.RecordingBootstrap(),
            new StudioWorkflowDraftMemberCommandDispatchTestHarness.ThrowingDispatchPort());

        var result = await service.RepairScopeAsync("scope-1");

        result.AcceptedCount.Should().Be(0);
        result.SkippedCount.Should().Be(0);
        result.FailedCount.Should().Be(1);
        var failed = result.Items.Should().ContainSingle().Subject;
        failed.Status.Should().Be(StudioWorkflowDraftMemberRepairItem.Failed);
        failed.MemberId.Should().Be("workflow-1");
        failed.CommandId.Should().Be(
            "aevatar.studio.projection.workflow-draft-member-ensure:scope-1:workflow-1");
        failed.Error.Should().Be("dispatch failed");
    }

    [Fact]
    public async Task RepairScopeAsync_ShouldRethrowCancellation_WithoutFailedItem()
    {
        var workspace = new StubWorkspaceQueryPort([
            NewDraft("workflow-1", "Workflow One"),
        ]);
        var service = NewService(
            workspace,
            new StudioWorkflowDraftMemberCommandDispatchTestHarness.RecordingBootstrap(),
            new CancelingDispatchPort());

        var act = () => service.RepairScopeAsync("scope-1");

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void EnsureCommandFactory_ShouldShareStableIdentity_ForLiveProjectionAndRepair()
    {
        var factory = new StudioWorkflowDraftMemberEnsureCommandFactory();

        var live = factory.TryCreate(
            "scope-1",
            "workflow-1",
            "Workflow One",
            Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-05-25T00:00:00Z")));
        var repair = factory.TryCreate(
            " scope-1 ",
            " workflow-1 ",
            "Workflow One",
            Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-05-30T00:00:00Z")));

        live.Should().NotBeNull();
        repair.Should().NotBeNull();
        repair!.ActorId.Should().Be(live!.ActorId);
        repair.CommandId.Should().Be(live.CommandId);
        repair.DeduplicationOperationId.Should().Be(live.DeduplicationOperationId);
        repair.Command.ScopeId.Should().Be(live.Command.ScopeId);
        repair.Command.MemberId.Should().Be(live.Command.MemberId);
        repair.Command.DisplayName.Should().Be(live.Command.DisplayName);
    }

    private static StudioWorkflowDraftMemberRepairService NewService(
        IStudioWorkspaceQueryPort workspaceQueryPort,
        IStudioActorBootstrap bootstrap,
        IActorDispatchPort dispatchPort) =>
        new(
            workspaceQueryPort,
            bootstrap,
            StudioWorkflowDraftMemberCommandDispatchTestHarness.CreateCommandDispatch(dispatchPort),
            new StudioWorkflowDraftMemberEnsureCommandFactory());

    private static StudioWorkflowDraftRecord NewDraft(string workflowId, string name) =>
        new(
            workflowId,
            name,
            $"{workflowId}.yaml",
            $"scope://scope-1/{workflowId}.yaml",
            "scope:scope-1",
            "scope-1",
            "name: workflow\nsteps: []\n",
            Layout: null,
            DateTimeOffset.Parse("2026-05-25T00:00:00Z"),
            DateTimeOffset.Parse("2026-05-25T00:00:00Z"),
            1);

    private sealed class StubWorkspaceQueryPort(IReadOnlyList<StudioWorkflowDraftRecord> drafts)
        : IStudioWorkspaceQueryPort
    {
        public Task<StudioWorkspaceSnapshot> GetAsync(CancellationToken ct = default) =>
            throw new NotSupportedException("repair must use explicit scope.");

        public Task<StudioWorkspaceSnapshot> GetAsync(string scopeId, CancellationToken ct = default) =>
            Task.FromResult(new StudioWorkspaceSnapshot(
                $"studio-workspace:{scopeId}",
                scopeId,
                new StudioWorkspaceSettings(
                    RuntimeBaseUrl: string.Empty,
                    Directories: [],
                    AppearanceTheme: "blue",
                    ColorMode: "light"),
                Directories: [],
                Drafts: drafts,
                StateVersion: 11,
                UpdatedAtUtc: DateTimeOffset.Parse("2026-05-25T00:00:00Z")));
    }

    private sealed class CancelingDispatchPort : IActorDispatchPort
    {
        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default) =>
            throw new OperationCanceledException();
    }

}

internal static class StudioWorkflowDraftMemberCommandDispatchTestHarness
{
    public class RecordingBootstrap : IStudioActorBootstrap
    {
        public List<string> EnsuredActorIds { get; } = [];

        public Task<IActor> EnsureAsync<TAgent>(string actorId, CancellationToken ct = default)
            where TAgent : IAgent, IProjectedActor
        {
            EnsuredActorIds.Add(actorId);
            return Task.FromResult<IActor>(new StubActor(actorId));
        }
    }

    public class RecordingDispatchPort : IActorDispatchPort
    {
        public List<DispatchedCommand> Dispatches { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            Dispatches.Add(new DispatchedCommand(actorId, envelope));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }

        public sealed record DispatchedCommand(string ActorId, EventEnvelope Envelope);
    }

    public class ThrowingDispatchPort : IActorDispatchPort
    {
        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("dispatch failed");
    }

    public static StudioProjectionActorCommandDispatch CreateCommandDispatch(IActorDispatchPort dispatchPort)
    {
        var service = new DefaultCommandDispatchService<
            StudioProjectionActorCommand,
            StudioProjectionActorCommandTarget,
            StudioProjectionActorCommandReceipt,
            StudioProjectionActorCommandStartError>(
            new DefaultCommandDispatchPipeline<
                StudioProjectionActorCommand,
                StudioProjectionActorCommandTarget,
                StudioProjectionActorCommandReceipt,
                StudioProjectionActorCommandStartError>(
                new StudioProjectionActorCommandTargetResolver(),
                new DefaultCommandContextPolicy(),
                new StudioProjectionActorCommandEnvelopeFactory(),
                new ActorCommandTargetDispatcher<StudioProjectionActorCommandTarget>(dispatchPort),
                new StudioProjectionActorCommandReceiptFactory()));
        return new StudioProjectionActorCommandDispatch(service);
    }

    private sealed class StubActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent => throw new NotSupportedException();
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
