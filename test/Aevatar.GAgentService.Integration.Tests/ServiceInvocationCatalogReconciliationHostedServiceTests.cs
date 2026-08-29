using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Hosting.Backfill;
using Aevatar.GAgentService.Projection.ReadModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgentService.Integration.Tests;

public sealed class ServiceInvocationCatalogReconciliationHostedServiceTests
{
    [Theory]
    [InlineData("source-version")]
    [InlineData("serving-target")]
    public async Task RunReconciliationOnceAsync_ShouldDispatchAllThreeSourceRefreshes_WhenCatalogIsStale(
        string staleKind)
    {
        var identity = Identity();
        var readModels = ReadModels(identity);
        if (staleKind == "source-version")
            readModels.Invocation.SourceRevisionVersion--;
        else
            readModels.Invocation.Entries[0].SelectedRevisionId = "rev-stale";
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(readModels, dispatchPort);

        var converged = await service.RunReconciliationOnceAsync(CancellationToken.None);

        converged.Should().BeFalse();
        dispatchPort.Calls.Select(static call => call.ActorId).Should().Equal(
            ServiceActorIds.Definition(identity),
            ServiceActorIds.RevisionCatalog(identity),
            ServiceActorIds.ServingSet(identity));
        dispatchPort.Calls.Should().OnlyContain(call =>
            call.Envelope.Payload.Is(RefreshServiceInvocationCatalogObservationCommand.Descriptor));
        dispatchPort.Calls
            .Select(call => call.Envelope.Payload.Unpack<RefreshServiceInvocationCatalogObservationCommand>().Identity)
            .Should().OnlyContain(observed => ServiceKeys.Build(observed) == ServiceKeys.Build(identity));
    }

    [Fact]
    public async Task RunReconciliationOnceAsync_ShouldNotDispatch_WhenCatalogVersionsAndTargetAreCurrent()
    {
        var identity = Identity();
        var readModels = ReadModels(identity);
        var dispatchPort = new RecordingDispatchPort();
        var service = CreateService(readModels, dispatchPort);

        var converged = await service.RunReconciliationOnceAsync(CancellationToken.None);

        converged.Should().BeTrue();
        dispatchPort.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task RunReconciliationOnceAsync_ShouldAttemptRemainingSources_WhenOneRefreshIsRejected()
    {
        var identity = Identity();
        var readModels = ReadModels(identity);
        readModels.Invocation.SourceCatalogVersion--;
        var dispatchPort = new RecordingDispatchPort
        {
            RejectedActorId = ServiceActorIds.Definition(identity),
        };
        var service = CreateService(readModels, dispatchPort);

        var converged = await service.RunReconciliationOnceAsync(CancellationToken.None);

        converged.Should().BeFalse();
        dispatchPort.Calls.Should().HaveCount(3);
        dispatchPort.Calls.Select(static call => call.ActorId).Should().Contain(
            ServiceActorIds.RevisionCatalog(identity),
            ServiceActorIds.ServingSet(identity));
    }

    private static ServiceInvocationCatalogReconciliationHostedService CreateService(
        ReconciliationReadModels readModels,
        IActorDispatchPort dispatchPort) =>
        new(
            new StaticDocumentReader<ServiceCatalogReadModel>([readModels.Service], static model => model.Id),
            new StaticDocumentReader<ServiceServingSetReadModel>([readModels.Serving], static model => model.Id),
            new StaticDocumentReader<ServiceRevisionCatalogReadModel>([readModels.Revisions], static model => model.Id),
            new StaticDocumentReader<ServiceInvocationCatalogReadModel>([readModels.Invocation], static model => model.Id),
            dispatchPort,
            NullLogger<ServiceInvocationCatalogReconciliationHostedService>.Instance);

    private static ReconciliationReadModels ReadModels(ServiceIdentity identity)
    {
        var serviceKey = ServiceKeys.Build(identity);
        var service = new ServiceCatalogReadModel
        {
            Id = serviceKey,
            StateVersion = 7,
            TenantId = identity.TenantId,
            AppId = identity.AppId,
            Namespace = identity.Namespace,
            ServiceId = identity.ServiceId,
            Endpoints =
            {
                new ServiceCatalogEndpointReadModel { EndpointId = "run" },
            },
        };
        var serving = new ServiceServingSetReadModel
        {
            Id = serviceKey,
            StateVersion = 5,
            Targets =
            {
                new ServiceServingTargetReadModel
                {
                    DeploymentId = "dep-current",
                    RevisionId = "rev-current",
                    PrimaryActorId = "actor-current",
                    AllocationWeight = 100,
                    ServingState = ServiceServingState.Active.ToString(),
                    EnabledEndpointIds = { "run" },
                },
            },
        };
        var revisions = new ServiceRevisionCatalogReadModel
        {
            Id = serviceKey,
            StateVersion = 9,
        };
        var invocation = new ServiceInvocationCatalogReadModel
        {
            Id = serviceKey,
            SourceCatalogVersion = service.StateVersion,
            SourceServingVersion = serving.StateVersion,
            SourceRevisionVersion = revisions.StateVersion,
            Entries =
            {
                new ServiceInvocationReadinessEntryReadModel
                {
                    ServiceKey = serviceKey,
                    EndpointId = "run",
                    ReadinessStatus = ServiceInvokeReadinessStatus.Ready,
                    SelectedRevisionId = "rev-current",
                    SelectedDeploymentId = "dep-current",
                    SelectedActorId = "actor-current",
                },
            },
        };
        return new ReconciliationReadModels(service, serving, revisions, invocation);
    }

    private static ServiceIdentity Identity() =>
        new()
        {
            TenantId = "tenant-1",
            AppId = "app-1",
            Namespace = "namespace-1",
            ServiceId = "service-1",
        };

    private sealed record ReconciliationReadModels(
        ServiceCatalogReadModel Service,
        ServiceServingSetReadModel Serving,
        ServiceRevisionCatalogReadModel Revisions,
        ServiceInvocationCatalogReadModel Invocation);

    private sealed class StaticDocumentReader<TReadModel>(
        IReadOnlyList<TReadModel> items,
        Func<TReadModel, string> keySelector)
        : IProjectionDocumentReader<TReadModel, string>
        where TReadModel : class, IProjectionReadModel
    {
        public Task<TReadModel?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(items.FirstOrDefault(item =>
                string.Equals(keySelector(item), key, StringComparison.Ordinal)));

        public Task<ProjectionDocumentQueryResult<TReadModel>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            Task.FromResult(new ProjectionDocumentQueryResult<TReadModel>
            {
                Items = items,
            });
    }

    private sealed class RecordingDispatchPort : IActorDispatchPort
    {
        public string? RejectedActorId { get; init; }

        public List<(string ActorId, EventEnvelope Envelope)> Calls { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            Calls.Add((actorId, envelope.Clone()));
            var admission = DispatchAdmissionFactory.Create(actorId, envelope);
            return Task.FromResult(admission with
            {
                Accepted = !string.Equals(actorId, RejectedActorId, StringComparison.Ordinal),
            });
        }
    }
}
