using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.Projectors;
using Aevatar.GAgentService.Projection.Queries;
using Aevatar.GAgentService.Projection.ReadModels;
using Aevatar.GAgentService.Abstractions.Queries;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class ServiceRunCurrentStateProjectorTests
{
    [Fact]
    public async Task ProjectAsync_ShouldMaterializeCurrentState_FromCommittedStateRoot()
    {
        var store = new RecordingDocumentStore<ServiceRunCurrentStateReadModel>(x => x.Id);
        var projector = new ServiceRunCurrentStateProjector(
            store,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-04-27T00:00:00+00:00")));
        var observedAt = DateTimeOffset.Parse("2026-04-27T01:00:00+00:00");
        var record = BuildRecord(
            scopeId: "tenant-1",
            serviceId: "svc-1",
            runId: "run-1",
            commandId: "cmd-1",
            implementation: ServiceImplementationKind.Workflow,
            targetActorId: "workflow-run:abc",
            createdAt: observedAt);
        var envelope = WrapCommittedRunState(
            record,
            stateVersion: 3,
            eventId: "evt-registered",
            observedAt: observedAt);
        var context = new ServiceRunCurrentStateProjectionContext
        {
            RootActorId = "service-run:run-1",
            ProjectionKind = "service-runs",
        };

        await projector.ProjectAsync(context, envelope);

        var doc = await store.GetAsync("run-1");
        doc.Should().NotBeNull();
        doc!.RunId.Should().Be("run-1");
        doc.CommandId.Should().Be("cmd-1");
        doc.ScopeId.Should().Be("tenant-1");
        doc.ServiceId.Should().Be("svc-1");
        doc.ActorId.Should().Be("service-run:run-1");
        doc.ImplementationKind.Should().Be((int)ServiceImplementationKind.Workflow);
        doc.TargetActorId.Should().Be("workflow-run:abc");
        doc.Status.Should().Be((int)ServiceRunStatus.Accepted);
        doc.StateVersion.Should().Be(3);
        doc.LastEventId.Should().Be("evt-registered");
    }

    [Fact]
    public async Task ProjectAsync_ShouldIgnoreEnvelope_WithoutCommittedStateRoot()
    {
        var store = new RecordingDocumentStore<ServiceRunCurrentStateReadModel>(x => x.Id);
        var projector = new ServiceRunCurrentStateProjector(
            store,
            new FixedProjectionClock(DateTimeOffset.UtcNow));
        var context = new ServiceRunCurrentStateProjectionContext
        {
            RootActorId = "service-run:run-x",
            ProjectionKind = "service-runs",
        };

        await projector.ProjectAsync(context, new EventEnvelope
        {
            Id = "raw",
            Payload = Any.Pack(new StringValue { Value = "noop" }),
        });

        (await store.ReadItemsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task QueryReader_ShouldFilterByScopeAndService_AndResolveByRunIdAndCommandId()
    {
        var store = new RecordingDocumentStore<ServiceRunCurrentStateReadModel>(x => x.Id);
        var projector = new ServiceRunCurrentStateProjector(
            store,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-04-27T00:00:00+00:00")));
        var reader = new ServiceRunQueryReader(store);
        await projector.ProjectAsync(
            CreateContext("service-run:run-a"),
            WrapCommittedRunState(
                BuildRecord("tenant-1", "svc-1", "run-a", "cmd-a", ServiceImplementationKind.Static, "actor-a"),
                stateVersion: 1,
                eventId: "evt-a",
                observedAt: DateTimeOffset.Parse("2026-04-27T01:00:00+00:00")));
        await projector.ProjectAsync(
            CreateContext("service-run:run-b"),
            WrapCommittedRunState(
                BuildRecord("tenant-1", "svc-1", "run-b", "cmd-b", ServiceImplementationKind.Workflow, "actor-b"),
                stateVersion: 1,
                eventId: "evt-b",
                observedAt: DateTimeOffset.Parse("2026-04-27T02:00:00+00:00")));
        await projector.ProjectAsync(
            CreateContext("service-run:run-c"),
            WrapCommittedRunState(
                BuildRecord("tenant-2", "svc-1", "run-c", "cmd-c", ServiceImplementationKind.Scripting, "actor-c"),
                stateVersion: 1,
                eventId: "evt-c",
                observedAt: DateTimeOffset.Parse("2026-04-27T03:00:00+00:00")));

        var listForTenant1 = await reader.ListAsync(new ServiceRunQuery("tenant-1", "svc-1"));
        listForTenant1.Should().HaveCount(2);
        listForTenant1.Select(x => x.RunId).Should().BeEquivalentTo(new[] { "run-a", "run-b" });

        var listForTenant2 = await reader.ListAsync(new ServiceRunQuery("tenant-2", "svc-1"));
        listForTenant2.Select(x => x.RunId).Should().Equal("run-c");

        var byRun = await reader.GetByRunIdAsync("tenant-1", "svc-1", "run-a");
        byRun.Should().NotBeNull();
        byRun!.CommandId.Should().Be("cmd-a");

        var byCommand = await reader.GetByCommandIdAsync("tenant-1", "svc-1", "cmd-b");
        byCommand.Should().NotBeNull();
        byCommand!.RunId.Should().Be("run-b");

        var byRunWrongScope = await reader.GetByRunIdAsync("tenant-1", "svc-1", "run-c");
        byRunWrongScope.Should().BeNull();
    }

    private static ServiceRunCurrentStateProjectionContext CreateContext(string rootActorId) =>
        new()
        {
            RootActorId = rootActorId,
            ProjectionKind = "service-runs",
        };

    private static ServiceRunRecord BuildRecord(
        string scopeId,
        string serviceId,
        string runId,
        string commandId,
        ServiceImplementationKind implementation,
        string targetActorId,
        DateTimeOffset? createdAt = null) =>
        new()
        {
            ScopeId = scopeId,
            ServiceId = serviceId,
            ServiceKey = $"{scopeId}:{serviceId}",
            RunId = runId,
            CommandId = commandId,
            CorrelationId = commandId,
            EndpointId = "run",
            ImplementationKind = implementation,
            TargetActorId = targetActorId,
            RevisionId = "r1",
            DeploymentId = "dep-1",
            Status = ServiceRunStatus.Accepted,
            CreatedAt = createdAt.HasValue ? Timestamp.FromDateTimeOffset(createdAt.Value) : null,
            UpdatedAt = createdAt.HasValue ? Timestamp.FromDateTimeOffset(createdAt.Value) : null,
            Identity = new ServiceIdentity
            {
                TenantId = scopeId,
                AppId = "app",
                Namespace = "default",
                ServiceId = serviceId,
            },
        };

    private static EventEnvelope WrapCommittedRunState(
        ServiceRunRecord record,
        long stateVersion,
        string eventId,
        DateTimeOffset observedAt)
    {
        var state = new ServiceRunState
        {
            Record = record.Clone(),
            LastAppliedEventVersion = stateVersion,
            LastEventId = eventId,
        };
        return new EventEnvelope
        {
            Id = $"outer-{eventId}",
            Timestamp = Timestamp.FromDateTimeOffset(observedAt),
            Route = EnvelopeRouteSemantics.CreateObserverPublication("root-actor"),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId,
                    Version = stateVersion,
                    Timestamp = Timestamp.FromDateTimeOffset(observedAt),
                    EventData = Any.Pack(new ServiceRunRegisteredEvent
                    {
                        Record = record.Clone(),
                    }),
                },
                StateRoot = Any.Pack(state),
            }),
        };
    }
}
