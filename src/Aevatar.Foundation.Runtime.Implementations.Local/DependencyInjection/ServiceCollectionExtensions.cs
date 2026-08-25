// ─────────────────────────────────────────────────────────────
// ServiceCollectionExtensions - dependency injection extensions.
// AddAevatarRuntime registers a full local actor runtime and related services.
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions.Context;
using Aevatar.Foundation.Abstractions.Propagation;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Core.Configurations;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Core.Propagation;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Runtime.Actors;
using Aevatar.Foundation.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Implementations.Local.ActivationIndex;
using Aevatar.Foundation.Runtime.Implementations.Local.Actors;
using Aevatar.Foundation.Runtime.Implementations.Local.TypeSystem;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Foundation.Runtime.Observability;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;

/// <summary>Service registration extension methods.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers full local actor runtime (stream + actor + persistence).</summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configureStreams">Optional stream buffering configuration.</param>
    /// <returns>Service collection for fluent chaining.</returns>
    public static IServiceCollection AddAevatarRuntime(
        this IServiceCollection services,
        Action<InMemoryStreamOptions>? configureStreams = null,
        Action<EventSourcingRuntimeOptions>? configureEventSourcing = null)
    {
        // Streaming
        var streamOptions = new InMemoryStreamOptions();
        configureStreams?.Invoke(streamOptions);
        services.TryAddSingleton(streamOptions);
        services.TryAddSingleton<InMemoryStreamForwardingRegistry>();
        services.TryAddSingleton<IStreamProvider>(sp =>
            new InMemoryStreamProvider(
                sp.GetRequiredService<InMemoryStreamOptions>(),
                sp.GetService<ILoggerFactory>() ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
                sp.GetRequiredService<InMemoryStreamForwardingRegistry>()));
        services.TryAddSingleton<IStreamLifecycleManager>(sp =>
            (IStreamLifecycleManager)sp.GetRequiredService<IStreamProvider>());
        services.TryAddSingleton<IStreamForwardingRegistry>(sp =>
            sp.GetRequiredService<InMemoryStreamForwardingRegistry>());
        services.TryAddSingleton<IStreamForwardingBindingAuthority>(sp =>
            sp.GetRequiredService<InMemoryStreamForwardingRegistry>());
        services.TryAddSingleton<IActorEventSubscriptionProvider>(sp =>
            new StreamProviderActorEventSubscriptionProvider(sp.GetRequiredService<IStreamProvider>()));
        services.TryAddSingleton<InMemoryActorRuntimeCallbackScheduler>();
        services.TryAddSingleton<IActorRuntimeCallbackScheduler>(sp =>
            sp.GetRequiredService<InMemoryActorRuntimeCallbackScheduler>());
        services.TryAddSingleton<IRuntimeFleetReconcileScheduleOwner>(sp =>
            sp.GetRequiredService<InMemoryActorRuntimeCallbackScheduler>());
        services.TryAddSingleton<IRuntimeFleetReconcileDeliveryVerifier>(sp =>
            sp.GetRequiredService<InMemoryActorRuntimeCallbackScheduler>());

        // Actor Runtime
        services.TryAddSingleton<IActorRuntime>(sp =>
        {
            return new LocalActorRuntime(
                sp.GetRequiredService<IStreamProvider>(),
                sp,
                sp.GetRequiredService<IStreamLifecycleManager>(),
                sp.GetService<ILogger<LocalActorRuntime>>());
        });
        services.TryAddSingleton<IActorDispatchPort, LocalActorDispatchPort>();

        // Persistence
        var eventSourcingOptions = new EventSourcingRuntimeOptions();
        configureEventSourcing?.Invoke(eventSourcingOptions);
        services.Replace(ServiceDescriptor.Singleton(eventSourcingOptions));

        services.TryAddSingleton(typeof(IStateStore<>), typeof(InMemoryStateStore<>));
        services.TryAddSingleton<ILocalActorRuntimeEnvelopeStore,
            InMemoryLocalActorRuntimeEnvelopeStore>();
        services.TryAddSingleton(
            typeof(IEventSourcingSnapshotStore<>),
            typeof(LocalActorRuntimeEnvelopeSnapshotStore<>));
        services.TryAddSingleton<ICommittedStatePublicationStateStore, InMemoryCommittedStatePublicationStateStore>();
        services.TryAddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>));
        services.TryAddSingleton<IEventStore, InMemoryEventStore>();
        services.TryAddSingleton<IEventStoreMaintenance>(sp =>
            (IEventStoreMaintenance)sp.GetRequiredService<IEventStore>());
        services.TryAddSingleton<IActorDeactivationHookDispatcher, ActorDeactivationHookDispatcher>();
        services.TryAddSingleton<AsyncLocalRuntimeActorStateSchemaContextAccessor>();
        services.TryAddSingleton<IRuntimeActorStateSchemaContextReader>(sp =>
            sp.GetRequiredService<AsyncLocalRuntimeActorStateSchemaContextAccessor>());
        services.TryAddSingleton<IRuntimeActorStateSchemaContextAccessor>(sp =>
            sp.GetRequiredService<AsyncLocalRuntimeActorStateSchemaContextAccessor>());
        services.TryAddSingleton<IRuntimeActorStateSchemaContextBinder>(sp =>
            sp.GetRequiredService<AsyncLocalRuntimeActorStateSchemaContextAccessor>());
        services.TryAddSingleton<AsyncLocalRuntimeFleetReconcileDeliveryAttestationAccessor>();
        services.TryAddSingleton<IRuntimeFleetReconcileDeliveryAttestationReader>(sp =>
            sp.GetRequiredService<AsyncLocalRuntimeFleetReconcileDeliveryAttestationAccessor>());
        services.TryAddSingleton<IRuntimeFleetReconcileDeliveryAttestationBinder>(sp =>
            sp.GetRequiredService<AsyncLocalRuntimeFleetReconcileDeliveryAttestationAccessor>());
        services.TryAddSingleton<IRuntimeFleetCapabilityAdmissionReader,
            DenyAllRuntimeFleetCapabilityAdmissionReader>();
        services.TryAddSingleton<IRuntimeFleetCapabilityQuiescenceReader,
            DenyAllRuntimeFleetCapabilityQuiescenceReader>();
        services.TryAddSingleton<IRuntimeLocalMembershipIdentityReader,
            UnavailableRuntimeLocalMembershipIdentityReader>();
        services.TryAddSingleton<IRuntimeFleetMembershipSnapshotSource,
            UnavailableRuntimeFleetMembershipSnapshotSource>();
        services.TryAddSingleton<ILocalActivationIndexStore, InMemoryLocalActivationIndexStore>();

        // Context
        services.TryAddSingleton<IAgentContextAccessor, AsyncLocalAgentContextAccessor>();
        services.TryAddSingleton<ICorrelationLinkPolicy, DefaultCorrelationLinkPolicy>();
        services.TryAddSingleton<IEnvelopePropagationPolicy, DefaultEnvelopePropagationPolicy>();
        services.TryAddSingleton<IActorKindProbe, LocalActorKindProbe>();
        services.TryAddSingleton<IAgentKindVerifier, DefaultAgentKindVerifier>();
        services.TryAddSingleton(typeof(IAgentClassDefaultsProvider<>), typeof(NullAgentClassDefaultsProvider<>));

        // Kind-token identity registry (issue #498). Mirrors the Orleans
        // runtime registration so in-memory + Orleans paths share the same
        // identity model.
        services.AddAevatarAgentKindRegistry(builder =>
            builder.ScanAssemblies(typeof(RuntimeFleetCapabilityAuthorityGAgent).Assembly));

        return services;
    }

    /// <summary>
    /// Replaces <see cref="IEventStore"/> with file-backed persistence.
    /// </summary>
    public static IServiceCollection AddFileEventStore(
        this IServiceCollection services,
        Action<FileEventStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new FileEventStoreOptions();
        configure?.Invoke(options);
        services.Replace(ServiceDescriptor.Singleton(options));
        services.Replace(ServiceDescriptor.Singleton<IEventStore, FileEventStore>());
        services.Replace(ServiceDescriptor.Singleton<IEventStoreMaintenance>(sp =>
            (IEventStoreMaintenance)sp.GetRequiredService<IEventStore>()));
        services.Replace(ServiceDescriptor.Singleton<ILocalActorRuntimeEnvelopeStore,
            FileLocalActorRuntimeEnvelopeStore>());
        services.Replace(ServiceDescriptor.Singleton(
            typeof(IEventSourcingSnapshotStore<>),
            typeof(LocalActorRuntimeEnvelopeSnapshotStore<>)));
        services.Replace(ServiceDescriptor.Singleton<ICommittedStatePublicationStateStore, FileCommittedStatePublicationStateStore>());
        return services;
    }
}
