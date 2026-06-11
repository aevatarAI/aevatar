using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Commands;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.Maintenance;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.GAgents.Channel.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgents.Device;

/// <summary>
/// DI registration entry point for the device registration package.
/// </summary>
public static class DeviceServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Device Registration projection pipeline (materialization runtime,
    /// projector, query port, document metadata, startup service, and projection store).
    /// Pass <paramref name="configuration"/> so the document projection store matches the
    /// host environment (Elasticsearch in prod, InMemory for local dev / tests).
    /// </summary>
    public static IServiceCollection AddDeviceRegistration(
        this IServiceCollection services, IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The helper logs a misconfiguration warning (Console.Error during SCE
        // composition; structured log when a real logger is wired in tests) when
        // configuration is present but Endpoints/Enabled are both empty, so
        // operators see the InMemory fallback at startup.
        var useElasticsearch = ElasticsearchProjectionConfiguration.IsEnabled(
            configuration,
            storeName: "DeviceRegistration");

        // ─── Retired-actor cleanup contribution ───
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IRetiredActorSpec, DeviceRetiredActorSpec>());
        services.TryAddSingleton<ProjectionActivationPlanDispatcher>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            ICommittedStatePublicationHook,
            CommittedStateProjectionActivationHook>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionActivationPlanProvider,
            DeviceRegistrationCommittedStateProjectionActivationPlanProvider>());

        // ─── Device Registration projection pipeline ───
        services.AddProjectionMaterializationRuntimeCore<
            DeviceRegistrationMaterializationContext,
            DeviceRegistrationMaterializationRuntimeLease,
            ProjectionMaterializationScopeGAgent<DeviceRegistrationMaterializationContext>>(
            static scopeKey => new DeviceRegistrationMaterializationContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new DeviceRegistrationMaterializationRuntimeLease(context));
        services.AddCurrentStateProjectionMaterializer<
            DeviceRegistrationMaterializationContext,
            DeviceRegistrationProjector>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<DeviceRegistrationDocument>,
            DeviceRegistrationDocumentMetadataProvider>();
        services.TryAddSingleton<IDeviceRegistrationQueryPort, DeviceRegistrationQueryPort>();
        services.TryAddSingleton<DeviceRegistrationProjectionBootstrapActivator>();
        services.AddHostedService<DeviceRegistrationStartupService>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ITombstoneCompactionTarget, DeviceTombstoneCompactionTarget>());

        // Refactor (iter47/issue-873-device-endpoint-direct-runtime-dispatch):
        //   Old pattern: Device HTTP endpoint resolves/creates actors, builds EventEnvelope directly, and dispatches through runtime/dispatch ports.
        //   New principle: Endpoint delegates to typed application command facade; target resolution, envelope construction, dispatch receipt, and resource ownership live behind command skeleton contracts. No callback-time auto-create.
        services.TryAddSingleton<ICommandContextPolicy, DefaultCommandContextPolicy>();
        services.TryAddSingleton<DeviceRegistrationCommandFacade>();
        services.TryAddSingleton<IDeviceCallbackCommandService, DeviceCallbackCommandFacade>();
        services.TryAddSingleton<DeviceRegistrationCommandEnvelopeFactory>();
        services.TryAddSingleton<DeviceRegistrationCommandReceiptFactory>();
        services.TryAddSingleton<DeviceCallbackCommandEnvelopeFactory>();
        services.TryAddSingleton<DeviceCallbackCommandReceiptFactory>();
        services.TryAddSingleton<ICommandTargetDispatcher<DeviceRegistrationCommandTarget>, ActorCommandTargetDispatcher<DeviceRegistrationCommandTarget>>();
        services.TryAddSingleton<ICommandTargetDispatcher<DeviceCallbackCommandTarget>, ActorCommandTargetDispatcher<DeviceCallbackCommandTarget>>();
        services.TryAddSingleton<ICommandTargetResolver<DeviceRegisterCommand, DeviceRegistrationCommandTarget, DeviceRegistrationCommandStartError>, DeviceRegistrationCommandTargetResolver<DeviceRegisterCommand>>();
        services.TryAddSingleton<ICommandTargetResolver<DeviceUnregisterCommand, DeviceRegistrationCommandTarget, DeviceRegistrationCommandStartError>, DeviceRegistrationCommandTargetResolver<DeviceUnregisterCommand>>();
        services.TryAddSingleton<ICommandTargetResolver<DeviceCallbackDispatchCommand, DeviceCallbackCommandTarget, DeviceCallbackCommandStartError>, DeviceCallbackCommandTargetResolver>();
        services.TryAddSingleton<ICommandEnvelopeFactory<DeviceRegisterCommand>>(sp => sp.GetRequiredService<DeviceRegistrationCommandEnvelopeFactory>());
        services.TryAddSingleton<ICommandEnvelopeFactory<DeviceUnregisterCommand>>(sp => sp.GetRequiredService<DeviceRegistrationCommandEnvelopeFactory>());
        services.TryAddSingleton<ICommandEnvelopeFactory<DeviceCallbackDispatchCommand>>(sp => sp.GetRequiredService<DeviceCallbackCommandEnvelopeFactory>());
        services.TryAddSingleton<ICommandReceiptFactory<DeviceRegistrationCommandTarget, DeviceCommandAcceptedReceipt>>(sp => sp.GetRequiredService<DeviceRegistrationCommandReceiptFactory>());
        services.TryAddSingleton<ICommandReceiptFactory<DeviceCallbackCommandTarget, DeviceCommandAcceptedReceipt>>(sp => sp.GetRequiredService<DeviceCallbackCommandReceiptFactory>());
        services.TryAddSingleton<ICommandDispatchPipeline<DeviceRegisterCommand, DeviceRegistrationCommandTarget, DeviceCommandAcceptedReceipt, DeviceRegistrationCommandStartError>, DefaultCommandDispatchPipeline<DeviceRegisterCommand, DeviceRegistrationCommandTarget, DeviceCommandAcceptedReceipt, DeviceRegistrationCommandStartError>>();
        services.TryAddSingleton<ICommandDispatchPipeline<DeviceUnregisterCommand, DeviceRegistrationCommandTarget, DeviceCommandAcceptedReceipt, DeviceRegistrationCommandStartError>, DefaultCommandDispatchPipeline<DeviceUnregisterCommand, DeviceRegistrationCommandTarget, DeviceCommandAcceptedReceipt, DeviceRegistrationCommandStartError>>();
        services.TryAddSingleton<ICommandDispatchPipeline<DeviceCallbackDispatchCommand, DeviceCallbackCommandTarget, DeviceCommandAcceptedReceipt, DeviceCallbackCommandStartError>, DefaultCommandDispatchPipeline<DeviceCallbackDispatchCommand, DeviceCallbackCommandTarget, DeviceCommandAcceptedReceipt, DeviceCallbackCommandStartError>>();
        services.TryAddSingleton<ICommandDispatchService<DeviceRegisterCommand, DeviceCommandAcceptedReceipt, DeviceRegistrationCommandStartError>, DefaultCommandDispatchService<DeviceRegisterCommand, DeviceRegistrationCommandTarget, DeviceCommandAcceptedReceipt, DeviceRegistrationCommandStartError>>();
        services.TryAddSingleton<ICommandDispatchService<DeviceUnregisterCommand, DeviceCommandAcceptedReceipt, DeviceRegistrationCommandStartError>, DefaultCommandDispatchService<DeviceUnregisterCommand, DeviceRegistrationCommandTarget, DeviceCommandAcceptedReceipt, DeviceRegistrationCommandStartError>>();
        services.TryAddSingleton<ICommandDispatchService<DeviceCallbackDispatchCommand, DeviceCommandAcceptedReceipt, DeviceCallbackCommandStartError>, DefaultCommandDispatchService<DeviceCallbackDispatchCommand, DeviceCallbackCommandTarget, DeviceCommandAcceptedReceipt, DeviceCallbackCommandStartError>>();

        if (useElasticsearch)
        {
            services.AddElasticsearchDocumentProjectionStore<DeviceRegistrationDocument, string>(
                optionsFactory: _ => ElasticsearchProjectionConfiguration.BindOptions(configuration!),
                metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<DeviceRegistrationDocument>>().Metadata,
                keySelector: static doc => doc.Id,
                keyFormatter: static key => key);
        }
        else
        {
            services.AddInMemoryDocumentProjectionStore<DeviceRegistrationDocument, string>(
                static doc => doc.Id, static key => key);
        }

        return services;
    }

}
