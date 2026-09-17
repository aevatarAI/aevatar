using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.DependencyInjection;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowTerminalStateReconcilerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReconcileAsync_ShouldPageAndDispatchOnlyStaleRunningActorsByTypedIdentity()
    {
        var reader = new StubCurrentStateReader();
        reader.Results.Enqueue(new ProjectionDocumentQueryResult<WorkflowExecutionCurrentStateDocument>
        {
            Items =
            [
                Document("actor-alpha", "run-alpha", 41, "running", Now.AddMinutes(-16)),
                Document("actor-failed", "run-failed", 42, "failed", Now.AddHours(-1)),
                Document("actor-recent", "run-recent", 43, "running", Now.AddMinutes(-14)),
                Document("", "run-missing-actor", 44, "running", Now.AddHours(-1)),
                Document("actor-missing-run", "", 45, "running", Now.AddHours(-1)),
            ],
            NextCursor = "page-2",
        });
        reader.Results.Enqueue(new ProjectionDocumentQueryResult<WorkflowExecutionCurrentStateDocument>
        {
            Items =
            [
                Document("actor-beta", "run-beta", 99, "running", Now.AddMinutes(-15)),
                Document("actor-gone", "run-gone", 100, "running", Now.AddHours(-2)),
            ],
        });
        var runtime = new StubActorRuntime("actor-alpha", "actor-beta");
        var dispatchPort = new RecordingDispatchPort();
        var reconciler = CreateReconciler(reader, runtime, dispatchPort);

        var dispatchedCount = await reconciler.ReconcileAsync();

        dispatchedCount.Should().Be(2);
        reader.Queries.Should().HaveCount(2);
        reader.Queries[0].Cursor.Should().BeNull();
        reader.Queries[1].Cursor.Should().Be("page-2");
        reader.Queries.Should().OnlyContain(query => query.Take == WorkflowTerminalStateReconciler.PageSize);
        ShouldContainCandidateFilters(reader.Queries[0], Now.AddMinutes(-15));
        reader.Queries[0].Sorts.Select(sort => (sort.FieldPath, sort.Direction)).Should().Equal(
            (nameof(WorkflowExecutionCurrentStateDocument.UpdatedAtUtcValue), ProjectionDocumentSortDirection.Asc),
            (nameof(WorkflowExecutionCurrentStateDocument.RootActorId), ProjectionDocumentSortDirection.Asc));

        runtime.ExistsIds.Should().Equal("actor-alpha", "actor-beta", "actor-gone");
        dispatchPort.Dispatched.Select(dispatch => dispatch.ActorId).Should().Equal("actor-alpha", "actor-beta");

        var alpha = dispatchPort.Dispatched[0];
        alpha.Envelope.Route.PublisherActorId.Should().Be(WorkflowTerminalStateReconciler.PublisherActorId);
        alpha.Envelope.Route.GetTargetActorId().Should().Be("actor-alpha");
        var alphaCommand = alpha.Envelope.Payload.Unpack<ReconcileWorkflowTerminalStateCommand>();
        alphaCommand.RunId.Should().Be("run-alpha");
        alphaCommand.ObservedStateVersion.Should().Be(41);

        var beta = dispatchPort.Dispatched[1];
        beta.Envelope.Route.GetTargetActorId().Should().Be("actor-beta");
        var betaCommand = beta.Envelope.Payload.Unpack<ReconcileWorkflowTerminalStateCommand>();
        betaCommand.RunId.Should().Be("run-beta");
        betaCommand.ObservedStateVersion.Should().Be(99);
    }

    [Fact]
    public async Task ReconcileAsync_WhenCursorDoesNotAdvance_ShouldStopPaging()
    {
        var reader = new StubCurrentStateReader();
        reader.Results.Enqueue(new ProjectionDocumentQueryResult<WorkflowExecutionCurrentStateDocument>
        {
            NextCursor = "stalled",
        });
        reader.Results.Enqueue(new ProjectionDocumentQueryResult<WorkflowExecutionCurrentStateDocument>
        {
            NextCursor = "stalled",
        });
        var reconciler = CreateReconciler(
            reader,
            new StubActorRuntime(),
            new RecordingDispatchPort());

        var dispatchedCount = await reconciler.ReconcileAsync();

        dispatchedCount.Should().Be(0);
        reader.Queries.Select(query => query.Cursor).Should().Equal(null, "stalled");
        reader.Results.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileAsync_ShouldReachTailCandidateAcrossAllCursorPages()
    {
        var reader = new StubCurrentStateReader();
        var nonCandidate = Document(
            "actor-failed",
            "run-failed",
            1,
            "failed",
            Now.AddHours(-1));
        for (var page = 0; page < 125; page++)
        {
            reader.Results.Enqueue(new ProjectionDocumentQueryResult<WorkflowExecutionCurrentStateDocument>
            {
                Items = Enumerable.Repeat(nonCandidate, WorkflowTerminalStateReconciler.PageSize).ToArray(),
                NextCursor = $"page-{page + 1}",
            });
        }

        reader.Results.Enqueue(new ProjectionDocumentQueryResult<WorkflowExecutionCurrentStateDocument>
        {
            Items =
            [
                Document("actor-tail", "run-tail", 25_001, "running", Now.AddHours(-1)),
            ],
        });
        var dispatchPort = new RecordingDispatchPort();
        var reconciler = CreateReconciler(
            reader,
            new StubActorRuntime("actor-tail"),
            dispatchPort);

        var dispatchedCount = await reconciler.ReconcileAsync();

        dispatchedCount.Should().Be(1);
        reader.Queries.Should().HaveCount(126);
        reader.Queries[^1].Cursor.Should().Be("page-125");
        dispatchPort.Dispatched.Should().ContainSingle().Which.ActorId.Should().Be("actor-tail");
    }

    [Fact]
    public async Task ReconcileAsync_WhenDisabled_ShouldNotReadOrDispatch()
    {
        var reader = new StubCurrentStateReader();
        var dispatchPort = new RecordingDispatchPort();
        var reconciler = CreateReconciler(
            reader,
            new StubActorRuntime("actor-alpha"),
            dispatchPort,
            new WorkflowExecutionProjectionOptions
            {
                EnableTerminalStateReconciliation = false,
            });

        var dispatchedCount = await reconciler.ReconcileAsync();

        dispatchedCount.Should().Be(0);
        reader.Queries.Should().BeEmpty();
        dispatchPort.Dispatched.Should().BeEmpty();
    }

    [Fact]
    public void AddWorkflowExecutionProjectionCQRS_ShouldRegisterTerminalReconciliationRuntime()
    {
        var services = new ServiceCollection();

        services.AddWorkflowExecutionProjectionCQRS();

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(WorkflowTerminalStateReconciler) &&
            descriptor.ImplementationType == typeof(WorkflowTerminalStateReconciler) &&
            descriptor.Lifetime == ServiceLifetime.Singleton);
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType == typeof(WorkflowTerminalStateReconciliationHostedService) &&
            descriptor.Lifetime == ServiceLifetime.Singleton);
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(TimeProvider) &&
            descriptor.ImplementationInstance == TimeProvider.System);
    }

    private static WorkflowTerminalStateReconciler CreateReconciler(
        StubCurrentStateReader reader,
        StubActorRuntime runtime,
        RecordingDispatchPort dispatchPort,
        WorkflowExecutionProjectionOptions? options = null) =>
        new(
            reader,
            runtime,
            dispatchPort,
            options ?? new WorkflowExecutionProjectionOptions
            {
                TerminalStateReconciliationStaleAfterSeconds = 900,
            },
            new FixedTimeProvider(Now));

    private static WorkflowExecutionCurrentStateDocument Document(
        string actorId,
        string runId,
        long stateVersion,
        string status,
        DateTimeOffset updatedAt) =>
        new()
        {
            Id = actorId,
            RootActorId = actorId,
            RunId = runId,
            StateVersion = stateVersion,
            Status = status,
            UpdatedAt = updatedAt,
        };

    private static void ShouldContainCandidateFilters(
        ProjectionDocumentQuery query,
        DateTimeOffset expectedCutoff)
    {
        var status = query.Filters.Should().ContainSingle(filter =>
            filter.FieldPath == nameof(WorkflowExecutionCurrentStateDocument.Status)).Subject;
        status.Operator.Should().Be(ProjectionDocumentFilterOperator.Eq);
        status.Value.Kind.Should().Be(ProjectionDocumentValueKind.String);
        status.Value.RawValue.Should().Be("running");

        var updatedAt = query.Filters.Should().ContainSingle(filter =>
            filter.FieldPath == nameof(WorkflowExecutionCurrentStateDocument.UpdatedAtUtcValue)).Subject;
        updatedAt.Operator.Should().Be(ProjectionDocumentFilterOperator.Lte);
        updatedAt.Value.Kind.Should().Be(ProjectionDocumentValueKind.DateTime);
        updatedAt.Value.RawValue.Should().Be(expectedCutoff.UtcDateTime);
    }

    private sealed class StubCurrentStateReader
        : IProjectionDocumentReader<WorkflowExecutionCurrentStateDocument, string>
    {
        public Queue<ProjectionDocumentQueryResult<WorkflowExecutionCurrentStateDocument>> Results { get; } = new();
        public List<ProjectionDocumentQuery> Queries { get; } = [];

        public Task<WorkflowExecutionCurrentStateDocument?> GetAsync(
            string key,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ProjectionDocumentQueryResult<WorkflowExecutionCurrentStateDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Queries.Add(query);
            return Task.FromResult(
                Results.Count == 0
                    ? ProjectionDocumentQueryResult<WorkflowExecutionCurrentStateDocument>.Empty
                    : Results.Dequeue());
        }
    }

    private sealed class StubActorRuntime(params string[] existingActorIds) : IActorRuntime
    {
        private readonly HashSet<string> _existingActorIds = new(existingActorIds, StringComparer.Ordinal);

        public List<string> ExistsIds { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            throw new NotSupportedException();

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IActor?> GetAsync(string id) =>
            throw new NotSupportedException();

        public Task<bool> ExistsAsync(string id)
        {
            ExistsIds.Add(id);
            return Task.FromResult(_existingActorIds.Contains(id));
        }

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Dispatched { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Dispatched.Add((actorId, envelope.Clone()));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
