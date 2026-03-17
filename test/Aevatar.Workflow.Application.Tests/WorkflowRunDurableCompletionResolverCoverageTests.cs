using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Runs;
using FluentAssertions;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowRunDurableCompletionResolverCoverageTests
{
    [Theory]
    [InlineData(WorkflowRunCompletionStatus.Completed, WorkflowProjectionCompletionStatus.Completed)]
    [InlineData(WorkflowRunCompletionStatus.TimedOut, WorkflowProjectionCompletionStatus.TimedOut)]
    [InlineData(WorkflowRunCompletionStatus.Failed, WorkflowProjectionCompletionStatus.Failed)]
    [InlineData(WorkflowRunCompletionStatus.Stopped, WorkflowProjectionCompletionStatus.Stopped)]
    [InlineData(WorkflowRunCompletionStatus.NotFound, WorkflowProjectionCompletionStatus.NotFound)]
    [InlineData(WorkflowRunCompletionStatus.Disabled, WorkflowProjectionCompletionStatus.Disabled)]
    public async Task ResolveAsync_ShouldMapTerminalCompletionStatuses(
        WorkflowRunCompletionStatus snapshotStatus,
        WorkflowProjectionCompletionStatus expected)
    {
        var port = new FakeCurrentStateQueryPort
        {
            Snapshot = new WorkflowActorSnapshot { CompletionStatus = snapshotStatus },
        };
        var resolver = new WorkflowRunDurableCompletionResolver(port);

        var observation = await resolver.ResolveAsync(
            new WorkflowChatRunAcceptedReceipt("actor-1", "workflow-1", "cmd-1", "corr-1"),
            CancellationToken.None);

        observation.Should().Be(new CommandDurableCompletionObservation<WorkflowProjectionCompletionStatus>(true, expected));
        port.ActorIds.Should().ContainSingle().Which.Should().Be("actor-1");
    }

    [Theory]
    [InlineData(WorkflowRunCompletionStatus.Running)]
    [InlineData(WorkflowRunCompletionStatus.Unknown)]
    public async Task ResolveAsync_ShouldReturnIncomplete_ForNonTerminalStatuses(
        WorkflowRunCompletionStatus snapshotStatus)
    {
        var resolver = new WorkflowRunDurableCompletionResolver(
            new FakeCurrentStateQueryPort
            {
                Snapshot = new WorkflowActorSnapshot { CompletionStatus = snapshotStatus },
            });

        var observation = await resolver.ResolveAsync(
            new WorkflowChatRunAcceptedReceipt("actor-1", "workflow-1", "cmd-1", "corr-1"),
            CancellationToken.None);

        observation.Should().Be(CommandDurableCompletionObservation<WorkflowProjectionCompletionStatus>.Incomplete);
    }

    [Fact]
    public async Task ResolveAsync_ShouldReturnIncomplete_WhenSnapshotMissing()
    {
        var resolver = new WorkflowRunDurableCompletionResolver(new FakeCurrentStateQueryPort());

        var observation = await resolver.ResolveAsync(
            new WorkflowChatRunAcceptedReceipt("actor-1", "workflow-1", "cmd-1", "corr-1"),
            CancellationToken.None);

        observation.Should().Be(CommandDurableCompletionObservation<WorkflowProjectionCompletionStatus>.Incomplete);
    }

    [Fact]
    public async Task ResolveAsync_ShouldReturnIncomplete_WhenProjectionQueryThrows()
    {
        var resolver = new WorkflowRunDurableCompletionResolver(
            new FakeCurrentStateQueryPort
            {
                Exception = new InvalidOperationException("projection failed"),
            });

        var observation = await resolver.ResolveAsync(
            new WorkflowChatRunAcceptedReceipt("actor-1", "workflow-1", "cmd-1", "corr-1"),
            CancellationToken.None);

        observation.Should().Be(CommandDurableCompletionObservation<WorkflowProjectionCompletionStatus>.Incomplete);
    }

    [Fact]
    public async Task ResolveAsync_ShouldRethrowOperationCanceled_WhenCancellationRequested()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var resolver = new WorkflowRunDurableCompletionResolver(
            new FakeCurrentStateQueryPort
            {
                ThrowOnCancellation = true,
            });

        var act = async () => await resolver.ResolveAsync(
            new WorkflowChatRunAcceptedReceipt("actor-1", "workflow-1", "cmd-1", "corr-1"),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ResolveAsync_ShouldThrow_WhenReceiptIsNull()
    {
        var resolver = new WorkflowRunDurableCompletionResolver(new FakeCurrentStateQueryPort());

        var act = async () => await resolver.ResolveAsync(null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ResolveAsync_ShouldThrow_WhenActorIdIsBlank()
    {
        var resolver = new WorkflowRunDurableCompletionResolver(new FakeCurrentStateQueryPort());

        var act = async () => await resolver.ResolveAsync(
            new WorkflowChatRunAcceptedReceipt(" ", "workflow-1", "cmd-1", "corr-1"),
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    private sealed class FakeCurrentStateQueryPort : IWorkflowExecutionCurrentStateQueryPort
    {
        public WorkflowActorSnapshot? Snapshot { get; set; }
        public Exception? Exception { get; set; }
        public bool ThrowOnCancellation { get; set; }
        public List<string> ActorIds { get; } = [];
        public bool EnableActorQueryEndpoints => true;

        public Task<WorkflowActorSnapshot?> GetActorSnapshotAsync(string actorId, CancellationToken ct = default)
        {
            ActorIds.Add(actorId);
            if (ThrowOnCancellation)
                ct.ThrowIfCancellationRequested();
            if (Exception != null)
                throw Exception;
            return Task.FromResult(Snapshot);
        }

        public Task<IReadOnlyList<WorkflowActorSnapshot>> ListActorSnapshotsAsync(int take = 200, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkflowActorProjectionState?> GetActorProjectionStateAsync(string actorId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
