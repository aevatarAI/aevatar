using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.Projectors;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowExternalApprovalContinuationProjectionTests
{
    [Fact]
    public async Task ProjectAsync_WhenRegisterCommitted_ShouldUpsertActiveContinuation()
    {
        var store = new ContinuationStore();
        var projector = new WorkflowExternalApprovalContinuationProjector(
            store,
            store,
            new FixedClock(DateTimeOffset.Parse("2026-04-01T10:00:00Z")));

        await projector.ProjectAsync(
            Context(),
            CommittedEnvelope(7, Register("run-1", "wait-approval", "approval", "nyxid", "instance_code", "app-42")),
            CancellationToken.None);

        var document = store.Stored.Values.Should().ContainSingle().Subject;
        document.Id.Should().Be("external-approval:nyxid:instance_code:app-42");
        document.ActorId.Should().Be("actor-run-1");
        document.RunId.Should().Be("run-1");
        document.StepId.Should().Be("wait-approval");
        document.SignalName.Should().Be("approval");
        document.Active.Should().BeTrue();
        document.StateVersion.Should().Be(7);
        document.LastEventId.Should().Be("evt-7");
    }

    [Fact]
    public async Task ProjectAsync_WhenStaleClearArrivesAfterNewRegister_ShouldNotDeactivate()
    {
        var store = new ContinuationStore();
        var projector = new WorkflowExternalApprovalContinuationProjector(
            store,
            store,
            new FixedClock(DateTimeOffset.Parse("2026-04-01T10:00:00Z")));

        await projector.ProjectAsync(
            Context(),
            CommittedEnvelope(10, Register("run-1", "wait-approval", "approval", "nyxid", "instance_code", "app-42")),
            CancellationToken.None);
        await projector.ProjectAsync(
            Context(),
            CommittedEnvelope(9, Clear("run-1", "wait-approval", "approval", "nyxid", "instance_code", "app-42")),
            CancellationToken.None);

        var document = store.Stored.Values.Single();
        document.Active.Should().BeTrue();
        document.StateVersion.Should().Be(10);
        store.UpsertCount.Should().Be(1);
    }

    [Fact]
    public async Task ProjectAsync_WhenClearMatchesActiveBinding_ShouldDeactivate()
    {
        var store = new ContinuationStore();
        var projector = new WorkflowExternalApprovalContinuationProjector(
            store,
            store,
            new FixedClock(DateTimeOffset.Parse("2026-04-01T10:00:00Z")));

        await projector.ProjectAsync(
            Context(),
            CommittedEnvelope(10, Register("run-1", "wait-approval", "approval", "nyxid", "instance_code", "app-42")),
            CancellationToken.None);
        await projector.ProjectAsync(
            Context(),
            CommittedEnvelope(11, Clear("run-1", "wait-approval", "approval", "nyxid", "instance_code", "app-42")),
            CancellationToken.None);

        var document = store.Stored.Values.Single();
        document.Active.Should().BeFalse();
        document.StateVersion.Should().Be(11);
        document.LastEventId.Should().Be("evt-11");
    }

    [Fact]
    public async Task LookupPort_ShouldReturnOnlyActiveExactIdentity()
    {
        var store = new ContinuationStore();
        store.Stored["external-approval:nyxid:instance_code:app-42"] = new WorkflowExternalApprovalContinuationDocument
        {
            Id = "external-approval:nyxid:instance_code:app-42",
            ActorId = "actor-run-1",
            RunId = "run-1",
            StepId = "wait-approval",
            SignalName = "approval",
            SourceId = "nyxid",
            ExternalIdKind = "instance_code",
            ExternalId = "app-42",
            CallbackIdempotencyKey = "idem-42",
            RequestId = "request-42",
            Active = true,
            StateVersion = 12,
            LastEventId = "evt-12",
            UpdatedAt = DateTimeOffset.Parse("2026-04-01T10:00:00Z"),
        };
        store.Stored["external-approval:nyxid:instance_code:app-43"] = new WorkflowExternalApprovalContinuationDocument
        {
            Id = "external-approval:nyxid:instance_code:app-43",
            SourceId = "nyxid",
            ExternalIdKind = "instance_code",
            ExternalId = "app-43",
            Active = false,
        };
        var port = new WorkflowExternalApprovalContinuationLookupPort(store);

        var found = await port.FindActiveAsync("NyxID", "Instance_Code", "APP-42", CancellationToken.None);
        var missing = await port.FindActiveAsync("nyxid", "instance_code", "app-43", CancellationToken.None);

        found.Should().NotBeNull();
        found!.ActorId.Should().Be("actor-run-1");
        found.SourceVersion.Should().Be(12);
        found.CallbackIdempotencyKey.Should().Be("idem-42");
        missing.Should().BeNull();
    }

    private static WorkflowExecutionMaterializationContext Context() =>
        new()
        {
            RootActorId = "actor-run-1",
            ProjectionKind = "workflow-execution-materialization",
        };

    private static WorkflowExternalApprovalContinuationRegisteredEvent Register(
        string runId,
        string stepId,
        string signalName,
        string sourceId,
        string externalIdKind,
        string externalId) =>
        new()
        {
            RunId = runId,
            StepId = stepId,
            SignalName = signalName,
            SourceId = sourceId,
            ExternalIdKind = externalIdKind,
            ExternalId = externalId,
            CallbackIdempotencyKey = "idem-42",
            RequestId = "request-42",
        };

    private static WorkflowExternalApprovalContinuationClearedEvent Clear(
        string runId,
        string stepId,
        string signalName,
        string sourceId,
        string externalIdKind,
        string externalId) =>
        new()
        {
            RunId = runId,
            StepId = stepId,
            SignalName = signalName,
            SourceId = sourceId,
            ExternalIdKind = externalIdKind,
            ExternalId = externalId,
            CallbackIdempotencyKey = "idem-42",
            RequestId = "request-42",
        };

    private static EventEnvelope CommittedEnvelope(long version, IMessage payload)
    {
        var timestamp = DateTimeOffset.Parse($"2026-04-01T10:{version:00}:00Z");
        return new EventEnvelope
        {
            Id = $"outer-{version}",
            Timestamp = Timestamp.FromDateTimeOffset(timestamp),
            Route = EnvelopeRouteSemantics.CreateObserverPublication("actor-run-1"),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = $"evt-{version}",
                    Version = version,
                    Timestamp = Timestamp.FromDateTimeOffset(timestamp),
                    EventData = Any.Pack(payload),
                },
                StateRoot = Any.Pack(new WorkflowRunState
                {
                    RunId = "run-1",
                    Status = "running",
                }),
            }),
        };
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class ContinuationStore
        : IProjectionDocumentReader<WorkflowExternalApprovalContinuationDocument, string>,
          IProjectionWriteDispatcher<WorkflowExternalApprovalContinuationDocument>
    {
        public Dictionary<string, WorkflowExternalApprovalContinuationDocument> Stored { get; } = new(StringComparer.Ordinal);

        public int UpsertCount { get; private set; }

        public Task<ProjectionWriteResult> UpsertAsync(
            WorkflowExternalApprovalContinuationDocument readModel,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Stored[readModel.Id] = readModel.Clone();
            UpsertCount++;
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var removed = Stored.Remove(id);
            return Task.FromResult(removed
                ? ProjectionWriteResult.Applied()
                : ProjectionWriteResult.Duplicate());
        }

        public Task<WorkflowExternalApprovalContinuationDocument?> GetAsync(
            string key,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Stored.TryGetValue(key, out var document) ? document.Clone() : null);
        }

        public Task<ProjectionDocumentQueryResult<WorkflowExternalApprovalContinuationDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var items = Stored.Values
                .Where(document => Matches(document, query.Filters))
                .Take(query.Take <= 0 ? 50 : query.Take)
                .Select(document => document.Clone())
                .ToList();
            return Task.FromResult(new ProjectionDocumentQueryResult<WorkflowExternalApprovalContinuationDocument>
            {
                Items = items,
            });
        }

        private static bool Matches(
            WorkflowExternalApprovalContinuationDocument document,
            IReadOnlyList<ProjectionDocumentFilter> filters) =>
            filters.All(filter => filter.Operator switch
            {
                ProjectionDocumentFilterOperator.Eq => MatchesEq(document, filter),
                _ => throw new NotSupportedException(filter.Operator.ToString()),
            });

        private static bool MatchesEq(
            WorkflowExternalApprovalContinuationDocument document,
            ProjectionDocumentFilter filter)
        {
            var expected = filter.Value.RawValue;
            return filter.FieldPath switch
            {
                nameof(WorkflowExternalApprovalContinuationDocument.Id) => string.Equals(document.Id, expected as string, StringComparison.Ordinal),
                nameof(WorkflowExternalApprovalContinuationDocument.SourceId) => string.Equals(document.SourceId, expected as string, StringComparison.Ordinal),
                nameof(WorkflowExternalApprovalContinuationDocument.ExternalIdKind) => string.Equals(document.ExternalIdKind, expected as string, StringComparison.Ordinal),
                nameof(WorkflowExternalApprovalContinuationDocument.ExternalId) => string.Equals(document.ExternalId, expected as string, StringComparison.Ordinal),
                nameof(WorkflowExternalApprovalContinuationDocument.Active) => expected is bool active && document.Active == active,
                _ => false,
            };
        }
    }
}
