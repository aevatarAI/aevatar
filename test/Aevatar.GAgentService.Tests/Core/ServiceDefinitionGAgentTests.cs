using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Core.GAgents;
using Aevatar.GAgentService.Tests.TestSupport;
using Aevatar.Foundation.Runtime.Persistence;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Core;

public sealed class ServiceDefinitionGAgentTests
{
    [Fact]
    public async Task HandleCreateAsync_ShouldPersistAndReplayDefinitionState()
    {
        var eventStore = new InMemoryEventStore();
        var identity = GAgentServiceTestKit.CreateIdentity();
        var actorId = ServiceActorIds.Definition(identity);
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            eventStore,
            actorId,
            static () => new ServiceDefinitionGAgent());
        await agent.ActivateAsync();

        await agent.HandleCreateAsync(new CreateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });

        agent.State.Spec.Identity.ServiceId.Should().Be("svc");
        agent.State.Spec.DisplayName.Should().Be("Service");

        await agent.DeactivateAsync();

        var replayed = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            eventStore,
            actorId,
            static () => new ServiceDefinitionGAgent());
        await replayed.ActivateAsync();

        replayed.State.Spec.Identity.ServiceId.Should().Be("svc");
        replayed.State.Spec.DisplayName.Should().Be("Service");
        replayed.State.LastAppliedEventVersion.Should().Be(1);
    }

    [Fact]
    public async Task HandleCreateAsync_ShouldRejectDuplicateCreate_AndKeepOriginalState()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            new InMemoryEventStore(),
            ServiceActorIds.Definition(identity),
            static () => new ServiceDefinitionGAgent());

        await agent.HandleCreateAsync(new CreateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });

        var act = () => agent.HandleCreateAsync(new CreateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*");
        agent.State.Spec.DisplayName.Should().Be("Service");
        agent.State.LastAppliedEventVersion.Should().Be(1);
    }

    [Fact]
    public async Task HandleUpdateAndSetDefaultServingRevisionAsync_ShouldMutateExistingDefinition()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            new InMemoryEventStore(),
            ServiceActorIds.Definition(identity),
            static () => new ServiceDefinitionGAgent());

        await agent.HandleCreateAsync(new CreateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });

        var updatedSpec = GAgentServiceTestKit.CreateDefinitionSpec(
            identity,
            GAgentServiceTestKit.CreateEndpointSpec(endpointId: "chat", kind: ServiceEndpointKind.Chat, requestTypeUrl: "type.googleapis.com/test.chat"));
        updatedSpec.DisplayName = "Updated";

        await agent.HandleUpdateAsync(new UpdateServiceDefinitionCommand
        {
            Spec = updatedSpec,
        });
        await agent.HandleSetDefaultServingRevisionAsync(new SetDefaultServingRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r2",
        });

        agent.State.Spec.DisplayName.Should().Be("Updated");
        agent.State.Spec.Endpoints.Should().ContainSingle(x => x.EndpointId == "chat");
        agent.State.DefaultServingRevisionId.Should().Be("r2");
        agent.State.LastAppliedEventVersion.Should().Be(3);
    }

    [Fact]
    public async Task HandleUpdateAsync_ShouldRejectMissingDefinition()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            new InMemoryEventStore(),
            ServiceActorIds.Definition(identity),
            static () => new ServiceDefinitionGAgent());

        var act = () => agent.HandleUpdateAsync(new UpdateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not exist*");
    }

    [Fact]
    public async Task HandleUpdateAsync_ShouldRejectMismatchedIdentity()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var otherIdentity = GAgentServiceTestKit.CreateIdentity(serviceId: "svc-other");
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            new InMemoryEventStore(),
            ServiceActorIds.Definition(identity),
            static () => new ServiceDefinitionGAgent());
        await agent.HandleCreateAsync(new CreateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });

        var act = () => agent.HandleUpdateAsync(new UpdateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(otherIdentity),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*is bound to*");
    }

    [Fact]
    public async Task HandleSetDefaultServingRevisionAsync_ShouldRejectBlankRevisionId()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            new InMemoryEventStore(),
            ServiceActorIds.Definition(identity),
            static () => new ServiceDefinitionGAgent());
        await agent.HandleCreateAsync(new CreateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });

        var act = () => agent.HandleSetDefaultServingRevisionAsync(new SetDefaultServingRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = " ",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("revision_id is required.");
    }

    [Fact]
    public async Task HandleCreateAsync_ShouldRejectSpecWithoutIdentity()
    {
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            new InMemoryEventStore(),
            "service-definition:missing-identity",
            static () => new ServiceDefinitionGAgent());

        var act = () => agent.HandleCreateAsync(new CreateServiceDefinitionCommand
        {
            Spec = new ServiceDefinitionSpec
            {
                DisplayName = "Service",
                Endpoints =
                {
                    GAgentServiceTestKit.CreateEndpointSpec(),
                },
            },
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("service identity is required.");
    }

    [Fact]
    public async Task HandleCreateAsync_ShouldRejectSpecWithoutEndpoints()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceDefinitionGAgent, ServiceDefinitionState>(
            new InMemoryEventStore(),
            ServiceActorIds.Definition(identity),
            static () => new ServiceDefinitionGAgent());

        var act = () => agent.HandleCreateAsync(new CreateServiceDefinitionCommand
        {
            Spec = new ServiceDefinitionSpec
            {
                Identity = identity.Clone(),
                DisplayName = "Service",
            },
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("service endpoints are required.");
    }
}
