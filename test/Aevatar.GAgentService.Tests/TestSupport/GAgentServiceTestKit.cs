using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Hooks;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.GAgentService.Abstractions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Tests.TestSupport;

internal static class GAgentServiceTestKit
{
    public const string TestStaticServiceAgentKind = "tests.static-service-agent";
    public static IActorDispatchPort NoOpDispatchPort { get; } = new NoOpActorDispatchPort();

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
        string? agentKind = null,
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
                AgentKind = agentKind ?? TestStaticServiceAgentKind,
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
                    AgentKind = TestStaticServiceAgentKind,
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
        Func<TAgent> factory,
        Action<IServiceCollection>? configureServices = null)
        where TAgent : GAgentBase<TState>
        where TState : class, IMessage<TState>, new()
    {
        var agent = factory();
        AssignActorId(agent, actorId);
        agent.EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<TState>(eventStore);
        var services = new ServiceCollection()
            .AddSingleton<IStreamProvider, InMemoryStreamProvider>()
            .AddSingleton<InMemoryActorRuntimeCallbackScheduler>()
            .AddSingleton<IActorRuntimeCallbackScheduler>(sp =>
                sp.GetRequiredService<InMemoryActorRuntimeCallbackScheduler>())
            .AddSingleton<IEnumerable<IGAgentExecutionHook>>(Array.Empty<IGAgentExecutionHook>());
        configureServices?.Invoke(services);
        agent.Services = services.BuildServiceProvider();
        return agent;
    }

    public static void AssignActorId(IAgent agent, string actorId)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        SetIdMethod.Invoke(agent, [actorId]);
    }

    private sealed class NoOpActorDispatchPort : IActorDispatchPort
    {
        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default) =>
            Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
    }
}

internal sealed class RecordingActorDispatchPort : IActorDispatchPort
{
    public List<(string ActorId, EventEnvelope Envelope)> Calls { get; } = [];

    public Task<DispatchAdmission> DispatchAsync(
        string actorId,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        Calls.Add((actorId, envelope));
        return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
    }
}

[GAgent(GAgentServiceTestKit.TestStaticServiceAgentKind)]
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
