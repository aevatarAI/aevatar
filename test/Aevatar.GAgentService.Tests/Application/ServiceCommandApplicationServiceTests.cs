using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Helpers;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Application.Services;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ServiceCommandApplicationServiceTests
{
    [Fact]
    public async Task DefinitionCommands_ShouldUseDefinitionTargetProjectionAndDispatch()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var provisioner = new RecordingCommandTargetProvisioner();
        var dispatchPort = new RecordingActorDispatchPort();
        var catalogProjectionPort = new RecordingCatalogProjectionPort();
        var service = CreateService(
            provisioner,
            dispatchPort,
            new RecordingCatalogQueryReader(),
            catalogProjectionPort,
            new RecordingRevisionProjectionPort());

        var createReceipt = await service.CreateServiceAsync(new CreateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });
        var updateReceipt = await service.UpdateServiceAsync(new UpdateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });
        var defaultReceipt = await service.SetDefaultServingRevisionAsync(new SetDefaultServingRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "rev-1",
        });

        provisioner.DefinitionRequests.Should().HaveCount(3);
        provisioner.DefinitionRequests.Should().OnlyContain(x => ServiceIdentityComparer.Instance.Equals(x, identity));
        catalogProjectionPort.ActorIds.Should().Equal(
            ServiceActorIds.Definition(identity),
            ServiceActorIds.Definition(identity),
            ServiceActorIds.Definition(identity));
        dispatchPort.Calls.Should().HaveCount(3);
        dispatchPort.Calls.Select(x => x.actorId).Should().OnlyContain(x => x == ServiceActorIds.Definition(identity));
        createReceipt.TargetActorId.Should().Be(ServiceActorIds.Definition(identity));
        updateReceipt.TargetActorId.Should().Be(ServiceActorIds.Definition(identity));
        defaultReceipt.CorrelationId.Should().Be($"{ServiceKeys.Build(identity)}:rev-1");
    }

    [Fact]
    public async Task RevisionCommands_ShouldRequireDefinitionAndUseRevisionCatalogProjection()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var missingDefinitionService = CreateService(
            new RecordingCommandTargetProvisioner(),
            new RecordingActorDispatchPort(),
            new RecordingCatalogQueryReader(),
            new RecordingCatalogProjectionPort(),
            new RecordingRevisionProjectionPort());

        var missingDefinition = () => missingDefinitionService.CreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1"),
        });

        await missingDefinition.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Service definition*was not found*");

        var provisioner = new RecordingCommandTargetProvisioner();
        var dispatchPort = new RecordingActorDispatchPort();
        var revisionProjectionPort = new RecordingRevisionProjectionPort();
        var service = CreateService(
            provisioner,
            dispatchPort,
            new RecordingCatalogQueryReader
            {
                GetResult = CreateCatalogSnapshot(identity),
            },
            new RecordingCatalogProjectionPort(),
            revisionProjectionPort);

        var createReceipt = await service.CreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1"),
        });
        var prepareReceipt = await service.PrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r2",
        });
        var publishReceipt = await service.PublishRevisionAsync(new PublishServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r3",
        });

        provisioner.RevisionCatalogRequests.Should().HaveCount(3);
        provisioner.RevisionCatalogRequests.Should().OnlyContain(x => ServiceIdentityComparer.Instance.Equals(x, identity));
        revisionProjectionPort.ActorIds.Should().Equal(
            ServiceActorIds.RevisionCatalog(identity),
            ServiceActorIds.RevisionCatalog(identity),
            ServiceActorIds.RevisionCatalog(identity));
        dispatchPort.Calls.Should().HaveCount(3);
        createReceipt.CorrelationId.Should().Be($"{ServiceKeys.Build(identity)}:r1");
        prepareReceipt.CorrelationId.Should().Be($"{ServiceKeys.Build(identity)}:r2");
        publishReceipt.CorrelationId.Should().Be($"{ServiceKeys.Build(identity)}:r3");
    }

    [Fact]
    public async Task ActivateServingRevisionAsync_ShouldUseDeploymentTarget_AndProvisionerFailuresShouldBubble()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var provisioner = new RecordingCommandTargetProvisioner();
        var dispatchPort = new RecordingActorDispatchPort();
        var catalogProjectionPort = new RecordingCatalogProjectionPort();
        var service = CreateService(
            provisioner,
            dispatchPort,
            new RecordingCatalogQueryReader
            {
                GetResult = CreateCatalogSnapshot(identity),
            },
            catalogProjectionPort,
            new RecordingRevisionProjectionPort());

        var receipt = await service.ActivateServingRevisionAsync(new ActivateServingRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "rev-2",
        });

        receipt.TargetActorId.Should().Be(ServiceActorIds.Deployment(identity));
        provisioner.DeploymentRequests.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(identity);
        catalogProjectionPort.ActorIds.Should().ContainSingle(ServiceActorIds.Deployment(identity));
        dispatchPort.Calls.Should().ContainSingle(x => x.actorId == ServiceActorIds.Deployment(identity));

        provisioner.DeploymentException = new InvalidOperationException("deployment target failed");

        var failingActivation = () => service.ActivateServingRevisionAsync(new ActivateServingRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "rev-3",
        });

        await failingActivation.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("deployment target failed");
    }

    private static ServiceCommandApplicationService CreateService(
        RecordingCommandTargetProvisioner provisioner,
        RecordingActorDispatchPort dispatchPort,
        RecordingCatalogQueryReader catalogQueryReader,
        RecordingCatalogProjectionPort catalogProjectionPort,
        RecordingRevisionProjectionPort revisionProjectionPort) =>
        new(
            dispatchPort,
            provisioner,
            catalogQueryReader,
            catalogProjectionPort,
            revisionProjectionPort);

    private static ServiceCatalogSnapshot CreateCatalogSnapshot(ServiceIdentity identity) =>
        new(
            ServiceKeys.Build(identity),
            identity.TenantId,
            identity.AppId,
            identity.Namespace,
            identity.ServiceId,
            "Service",
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            ServiceDeploymentStatus.Unspecified.ToString(),
            [],
            [],
            DateTimeOffset.UtcNow);

    private sealed class RecordingCommandTargetProvisioner : IServiceCommandTargetProvisioner
    {
        public List<ServiceIdentity> DefinitionRequests { get; } = [];

        public List<ServiceIdentity> RevisionCatalogRequests { get; } = [];

        public List<ServiceIdentity> DeploymentRequests { get; } = [];

        public Exception? DeploymentException { get; set; }

        public Task<string> EnsureDefinitionTargetAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            DefinitionRequests.Add(identity.Clone());
            return Task.FromResult(ServiceActorIds.Definition(identity));
        }

        public Task<string> EnsureRevisionCatalogTargetAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            RevisionCatalogRequests.Add(identity.Clone());
            return Task.FromResult(ServiceActorIds.RevisionCatalog(identity));
        }

        public Task<string> EnsureDeploymentTargetAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            DeploymentRequests.Add(identity.Clone());
            if (DeploymentException != null)
                throw DeploymentException;

            return Task.FromResult(ServiceActorIds.Deployment(identity));
        }
    }

    private sealed class RecordingCatalogQueryReader : IServiceCatalogQueryReader
    {
        public ServiceCatalogSnapshot? GetResult { get; init; }

        public Task<ServiceCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(GetResult);

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> ListAsync(
            string tenantId,
            string appId,
            string @namespace,
            int take = 200,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>([]);
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public List<(string actorId, EventEnvelope envelope)> Calls { get; } = [];

        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Calls.Add((actorId, envelope));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCatalogProjectionPort : IServiceCatalogProjectionPort
    {
        public List<string> ActorIds { get; } = [];

        public Task EnsureProjectionAsync(string actorId, CancellationToken ct = default)
        {
            ActorIds.Add(actorId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRevisionProjectionPort : IServiceRevisionCatalogProjectionPort
    {
        public List<string> ActorIds { get; } = [];

        public Task EnsureProjectionAsync(string actorId, CancellationToken ct = default)
        {
            ActorIds.Add(actorId);
            return Task.CompletedTask;
        }
    }

    private sealed class ServiceIdentityComparer : IEqualityComparer<ServiceIdentity>
    {
        public static ServiceIdentityComparer Instance { get; } = new();

        public bool Equals(ServiceIdentity? x, ServiceIdentity? y)
        {
            if (ReferenceEquals(x, y))
                return true;
            if (x is null || y is null)
                return false;

            return string.Equals(x.TenantId, y.TenantId, StringComparison.Ordinal) &&
                   string.Equals(x.AppId, y.AppId, StringComparison.Ordinal) &&
                   string.Equals(x.Namespace, y.Namespace, StringComparison.Ordinal) &&
                   string.Equals(x.ServiceId, y.ServiceId, StringComparison.Ordinal);
        }

        public int GetHashCode(ServiceIdentity obj) =>
            HashCode.Combine(obj.TenantId, obj.AppId, obj.Namespace, obj.ServiceId);
    }
}
