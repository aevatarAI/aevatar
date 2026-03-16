using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Hooks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgentService.Abstractions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Tests.TestSupport;

internal static class GAgentServiceTestKit
{
    private static readonly MethodInfo SetIdMethod = typeof(GAgentBase)
        .GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("GAgentBase.SetId was not found.");

    public static ServiceIdentity CreateIdentity(string serviceId = "svc") =>
        new()
        {
            TenantId = "tenant",
            AppId = "app",
            Namespace = "default",
            ServiceId = serviceId,
        };

    public static ServiceEndpointSpec CreateEndpointSpec(
        string endpointId = "run",
        ServiceEndpointKind kind = ServiceEndpointKind.Command,
        string requestTypeUrl = "type.googleapis.com/test.command") =>
        new()
        {
            EndpointId = endpointId,
            DisplayName = endpointId,
            Kind = kind,
            RequestTypeUrl = requestTypeUrl,
        };

    public static ServiceEndpointDescriptor CreateEndpointDescriptor(
        string endpointId = "run",
        ServiceEndpointKind kind = ServiceEndpointKind.Command,
        string requestTypeUrl = "type.googleapis.com/test.command") =>
        new()
        {
            EndpointId = endpointId,
            DisplayName = endpointId,
            Kind = kind,
            RequestTypeUrl = requestTypeUrl,
        };

    public static ServiceDefinitionSpec CreateDefinitionSpec(
        ServiceIdentity? identity = null,
        params ServiceEndpointSpec[] endpoints)
    {
        var spec = new ServiceDefinitionSpec
        {
            Identity = (identity ?? CreateIdentity()).Clone(),
            DisplayName = "Service",
        };
        spec.Endpoints.Add((endpoints.Length == 0
            ? [CreateEndpointSpec()]
            : endpoints).Select(x => x.Clone()));
        return spec;
    }

    public static ServiceRevisionSpec CreateStaticRevisionSpec(
        ServiceIdentity? identity = null,
        string revisionId = "r1",
        string? actorTypeName = null,
        params ServiceEndpointDescriptor[] endpoints)
    {
        var spec = new ServiceRevisionSpec
        {
            Identity = (identity ?? CreateIdentity()).Clone(),
            RevisionId = revisionId,
            ImplementationKind = ServiceImplementationKind.Static,
            StaticSpec = new StaticServiceRevisionSpec
            {
                ActorTypeName = actorTypeName ?? typeof(TestStaticServiceAgent).AssemblyQualifiedName!,
                PreferredActorId = $"static:{revisionId}",
            },
        };
        spec.StaticSpec.Endpoints.Add((endpoints.Length == 0
            ? [CreateEndpointDescriptor()]
            : endpoints).Select(x => x.Clone()));
        return spec;
    }

    public static PreparedServiceRevisionArtifact CreatePreparedStaticArtifact(
        ServiceIdentity? identity = null,
        string revisionId = "r1",
        params ServiceEndpointDescriptor[] endpoints)
    {
        var artifact = new PreparedServiceRevisionArtifact
        {
            Identity = (identity ?? CreateIdentity()).Clone(),
            RevisionId = revisionId,
            ImplementationKind = ServiceImplementationKind.Static,
            DeploymentPlan = new ServiceDeploymentPlan
            {
                StaticPlan = new StaticServiceDeploymentPlan
                {
                    ActorTypeName = typeof(TestStaticServiceAgent).AssemblyQualifiedName!,
                    PreferredActorId = $"static:{revisionId}",
                },
            },
        };
        artifact.Endpoints.Add((endpoints.Length == 0
            ? [CreateEndpointDescriptor()]
            : endpoints).Select(x => x.Clone()));
        return artifact;
    }

    public static TAgent CreateStatefulAgent<TAgent, TState>(
        InMemoryEventStore eventStore,
        string actorId,
        Func<TAgent> factory)
        where TAgent : GAgentBase<TState>
        where TState : class, IMessage<TState>, new()
    {
        var agent = factory();
        AssignActorId(agent, actorId);
        agent.EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<TState>(eventStore);
        agent.Services = new ServiceCollection()
            .AddSingleton<IEnumerable<IGAgentExecutionHook>>(Array.Empty<IGAgentExecutionHook>())
            .BuildServiceProvider();
        return agent;
    }

    public static void AssignActorId(IAgent agent, string actorId)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        SetIdMethod.Invoke(agent, [actorId]);
    }
}

internal sealed class TestStaticServiceAgent : IAgent
{
    public string Id { get; private set; } = Guid.NewGuid().ToString("N");

    public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

    public Task<string> GetDescriptionAsync() => Task.FromResult("test-static-service-agent");

    public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() =>
        Task.FromResult<IReadOnlyList<Type>>([]);

    public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
}
