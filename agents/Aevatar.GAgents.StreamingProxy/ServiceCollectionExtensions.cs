using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Core.Commands;
using Aevatar.CQRS.Core.DependencyInjection;
using Aevatar.CQRS.Core.Interactions;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Core.Streaming;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.StreamingProxy.Application.Rooms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgents.StreamingProxy;

// Refactor (iter43/issue-865-streaming-proxy-room-chat-host-orchestration):
//   Old pattern: StreamingProxy chat endpoint and participant coordinator fetch runtime actor objects, run Nyx participant discussion loops, mutate participant side-store state, and dispatch room events from Host/Application-side orchestration.
//   New principle: StreamingProxyGAgent owns participant admission, reply rounds, leave/failure decisions, and terminal-state publication; Host submits one typed command and observes projection/readmodel events only. Coordinator is adapter-only for Nyx external calls.
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddStreamingProxy(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddCqrsCore();
        services.AddMemoryCache();
        services.TryAddSingleton<StreamingProxyNyxParticipantCoordinator>();
        services.TryAddSingleton<IStreamingProxyRoomCredentialHandleStore, StreamingProxyRoomCredentialHandleStore>();
        services.TryAddSingleton<IStreamingProxyRoomCommandService, StreamingProxyRoomCommandService>();
        services.AddProjectionReadModelRuntime();
        services.TryAddSingleton<IProjectionClock, SystemProjectionClock>();

        services.AddEventSinkProjectionRuntimeCore<
            StreamingProxyRoomSessionProjectionContext,
            StreamingProxyRoomSessionRuntimeLease,
            StreamingProxyRoomSessionEnvelope,
            ProjectionSessionScopeGAgent<StreamingProxyRoomSessionProjectionContext>>(
            static scopeKey => new StreamingProxyRoomSessionProjectionContext
            {
                SessionId = scopeKey.SessionId,
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new StreamingProxyRoomSessionRuntimeLease(context));
        services.TryAddSingleton<IProjectionSessionEventCodec<StreamingProxyRoomSessionEnvelope>, StreamingProxyRoomSessionEventCodec>();
        services.TryAddSingleton<IProjectionSessionEventHub<StreamingProxyRoomSessionEnvelope>, ProjectionSessionEventHub<StreamingProxyRoomSessionEnvelope>>();
        services.TryAddSingleton<IStreamingProxyRoomSessionProjectionPort, StreamingProxyRoomSessionProjectionPort>();
        services.TryAddSingleton<IStreamingProxyRoomSubscriptionObservationPort, StreamingProxyRoomSubscriptionObservationPort>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<StreamingProxyRoomSessionProjectionContext>,
            StreamingProxyRoomSessionEventProjector>());

        services.AddProjectionMaterializationRuntimeCore<
            StreamingProxyCurrentStateProjectionContext,
            StreamingProxyCurrentStateRuntimeLease,
            ProjectionMaterializationScopeGAgent<StreamingProxyCurrentStateProjectionContext>>(
            static scopeKey => new StreamingProxyCurrentStateProjectionContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new StreamingProxyCurrentStateRuntimeLease(context));
        services.TryAddSingleton<StreamingProxyCurrentStateProjectionPort>();
        services.AddCurrentStateProjectionMaterializer<
            StreamingProxyCurrentStateProjectionContext,
            StreamingProxyChatSessionTerminalProjector>();
        services.AddCurrentStateProjectionMaterializer<
            StreamingProxyCurrentStateProjectionContext,
            StreamingProxyRoomParticipantsProjector>();
        services.TryAddSingleton<
            IProjectionDocumentMetadataProvider<StreamingProxyChatSessionTerminalSnapshot>,
            StreamingProxyChatSessionTerminalSnapshotMetadataProvider>();
        services.TryAddSingleton<
            IProjectionDocumentMetadataProvider<StreamingProxyRoomParticipantsSnapshot>,
            StreamingProxyRoomParticipantsSnapshotMetadataProvider>();
        services.TryAddSingleton<IStreamingProxyChatSessionTerminalQueryPort, StreamingProxyChatSessionTerminalQueryPort>();
        services.TryAddSingleton<IStreamingProxyRoomParticipantsQueryPort, StreamingProxyRoomParticipantsQueryPort>();
        services.TryAddSingleton<StreamingProxyChatDurableCompletionResolver>();
        AddStreamingProxyRoomInteraction(services);
        AddStreamingProxyReadModelProviders(services, configuration);

        return services;
    }

    private static void AddStreamingProxyRoomInteraction(IServiceCollection services)
    {
        // Refactor (iter43/issue-865-streaming-proxy-room-chat-host-orchestration):
        //   Old pattern: StreamingProxy chat endpoint and participant coordinator fetch runtime actor objects, run Nyx participant discussion loops, mutate participant side-store state, and dispatch room events from Host/Application-side orchestration.
        //   New principle: StreamingProxyGAgent owns participant admission, reply rounds, leave/failure decisions, and terminal-state publication; Host submits one typed command and observes projection/readmodel events only. Coordinator is adapter-only for Nyx external calls.
        services.TryAddSingleton<ICommandTargetResolver<StreamingProxyRoomChatCommand, StreamingProxyRoomChatCommandTarget, StreamingProxyRoomChatStartError>, StreamingProxyRoomChatCommandTargetResolver>();
        services.TryAddSingleton<ICommandObservationLifecycle<StreamingProxyRoomChatCommand, StreamingProxyRoomChatCommandTarget, StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError>, StreamingProxyRoomObservationLifecycle>();
        services.TryAddSingleton<ICommandEnvelopeFactory<StreamingProxyRoomChatCommand>, StreamingProxyRoomChatCommandEnvelopeFactory>();
        services.TryAddSingleton<ICommandTargetDispatcher<StreamingProxyRoomChatCommandTarget>, ActorCommandTargetDispatcher<StreamingProxyRoomChatCommandTarget>>();
        services.TryAddSingleton<ICommandReceiptFactory<StreamingProxyRoomChatCommandTarget, StreamingProxyRoomChatAcceptedReceipt>, StreamingProxyRoomChatAcceptedReceiptFactory>();
        services.TryAddSingleton<ICommandDispatchPipeline<StreamingProxyRoomChatCommand, StreamingProxyRoomChatCommandTarget, StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError>, DefaultCommandDispatchPipeline<StreamingProxyRoomChatCommand, StreamingProxyRoomChatCommandTarget, StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError>>();
        services.TryAddSingleton<ICommandCompletionPolicy<StreamingProxyRoomSessionEnvelope, StreamingProxyProjectionCompletionStatus>, StreamingProxyRoomChatCompletionPolicy>();
        services.TryAddSingleton<ICommandFinalizeEmitter<StreamingProxyRoomChatAcceptedReceipt, StreamingProxyProjectionCompletionStatus, StreamingProxyRoomSessionEnvelope>, StreamingProxyRoomChatFinalizeEmitter>();
        services.TryAddSingleton<ICommandDurableCompletionResolver<StreamingProxyRoomChatAcceptedReceipt, StreamingProxyProjectionCompletionStatus>, StreamingProxyRoomChatDurableCompletionResolverAdapter>();
        services.TryAddSingleton<IEventOutputStream<StreamingProxyRoomSessionEnvelope, StreamingProxyRoomSessionEnvelope>, StreamingProxyRoomChatOutputStream>();
        services.TryAddSingleton<ICommandInteractionService<StreamingProxyRoomChatCommand, StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError, StreamingProxyRoomSessionEnvelope, StreamingProxyProjectionCompletionStatus>>(sp =>
            new DefaultCommandInteractionService<StreamingProxyRoomChatCommand, StreamingProxyRoomChatCommandTarget, StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError, StreamingProxyRoomSessionEnvelope, StreamingProxyRoomSessionEnvelope, StreamingProxyProjectionCompletionStatus>(
                sp.GetRequiredService<ICommandDispatchPipeline<StreamingProxyRoomChatCommand, StreamingProxyRoomChatCommandTarget, StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError>>(),
                sp.GetRequiredService<IEventOutputStream<StreamingProxyRoomSessionEnvelope, StreamingProxyRoomSessionEnvelope>>(),
                sp.GetRequiredService<ICommandCompletionPolicy<StreamingProxyRoomSessionEnvelope, StreamingProxyProjectionCompletionStatus>>(),
                sp.GetRequiredService<ICommandFinalizeEmitter<StreamingProxyRoomChatAcceptedReceipt, StreamingProxyProjectionCompletionStatus, StreamingProxyRoomSessionEnvelope>>(),
                sp.GetRequiredService<ICommandDurableCompletionResolver<StreamingProxyRoomChatAcceptedReceipt, StreamingProxyProjectionCompletionStatus>>(),
                sp.GetService<Microsoft.Extensions.Logging.ILogger<DefaultCommandInteractionService<StreamingProxyRoomChatCommand, StreamingProxyRoomChatCommandTarget, StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError, StreamingProxyRoomSessionEnvelope, StreamingProxyRoomSessionEnvelope, StreamingProxyProjectionCompletionStatus>>>(),
                sp.GetRequiredService<ICommandObservationLifecycle<StreamingProxyRoomChatCommand, StreamingProxyRoomChatCommandTarget, StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError>>(),
                sp.GetRequiredService<ICommandReceiptFactory<StreamingProxyRoomChatCommandTarget, StreamingProxyRoomChatAcceptedReceipt>>()));
    }

    private static void AddStreamingProxyReadModelProviders(
        IServiceCollection services,
        IConfiguration? configuration)
    {
        AddTerminalSnapshotReadModelProvider(services, configuration);
        AddRoomParticipantsReadModelProvider(services, configuration);
    }

    private static void AddTerminalSnapshotReadModelProvider(
        IServiceCollection services,
        IConfiguration? configuration)
    {
        if (services.Any(x => x.ServiceType == typeof(IProjectionDocumentReader<StreamingProxyChatSessionTerminalSnapshot, string>)))
            return;

        var elasticsearchEnabled = ResolveElasticsearchDocumentEnabled(configuration);
        var inMemoryEnabled = ResolveOptionalBool(
            configuration?["Projection:Document:Providers:InMemory:Enabled"],
            fallbackValue: !elasticsearchEnabled);
        var providerCount = (elasticsearchEnabled ? 1 : 0) + (inMemoryEnabled ? 1 : 0);
        if (providerCount != 1)
        {
            throw new InvalidOperationException(
                "Exactly one document projection provider must be enabled for StreamingProxy.");
        }

        if (elasticsearchEnabled)
        {
            services.AddElasticsearchDocumentProjectionStore<StreamingProxyChatSessionTerminalSnapshot, string>(
                optionsFactory: _ => BuildElasticsearchDocumentOptions(configuration!),
                metadataFactory: sp => sp
                    .GetRequiredService<IProjectionDocumentMetadataProvider<StreamingProxyChatSessionTerminalSnapshot>>()
                    .Metadata,
                keySelector: readModel => readModel.Id,
                keyFormatter: key => key);
            return;
        }

        services.AddInMemoryDocumentProjectionStore<StreamingProxyChatSessionTerminalSnapshot, string>(
            keySelector: readModel => readModel.Id,
            keyFormatter: key => key,
            defaultSortSelector: readModel => readModel.UpdatedAt.ToDateTimeOffset());
    }

    private static void AddRoomParticipantsReadModelProvider(
        IServiceCollection services,
        IConfiguration? configuration)
    {
        if (services.Any(x => x.ServiceType == typeof(IProjectionDocumentReader<StreamingProxyRoomParticipantsSnapshot, string>)))
            return;

        var elasticsearchEnabled = ResolveElasticsearchDocumentEnabled(configuration);
        var inMemoryEnabled = ResolveOptionalBool(
            configuration?["Projection:Document:Providers:InMemory:Enabled"],
            fallbackValue: !elasticsearchEnabled);
        var providerCount = (elasticsearchEnabled ? 1 : 0) + (inMemoryEnabled ? 1 : 0);
        if (providerCount != 1)
        {
            throw new InvalidOperationException(
                "Exactly one document projection provider must be enabled for StreamingProxy.");
        }

        if (elasticsearchEnabled)
        {
            services.AddElasticsearchDocumentProjectionStore<StreamingProxyRoomParticipantsSnapshot, string>(
                optionsFactory: _ => BuildElasticsearchDocumentOptions(configuration!),
                metadataFactory: sp => sp
                    .GetRequiredService<IProjectionDocumentMetadataProvider<StreamingProxyRoomParticipantsSnapshot>>()
                    .Metadata,
                keySelector: readModel => readModel.Id,
                keyFormatter: key => key);
            return;
        }

        services.AddInMemoryDocumentProjectionStore<StreamingProxyRoomParticipantsSnapshot, string>(
            keySelector: readModel => readModel.Id,
            keyFormatter: key => key,
            defaultSortSelector: readModel => readModel.UpdatedAt.ToDateTimeOffset());
    }

    private static bool ResolveElasticsearchDocumentEnabled(IConfiguration? configuration)
    {
        if (configuration == null)
            return false;

        var section = configuration.GetSection("Projection:Document:Providers:Elasticsearch");
        var explicitEnabled = section["Enabled"];
        var hasEndpoints = section
            .GetSection("Endpoints")
            .GetChildren()
            .Select(x => x.Value?.Trim() ?? string.Empty)
            .Any(x => x.Length > 0);
        return ResolveOptionalBool(explicitEnabled, hasEndpoints);
    }

    private static ElasticsearchProjectionDocumentStoreOptions BuildElasticsearchDocumentOptions(
        IConfiguration configuration)
    {
        var options = new ElasticsearchProjectionDocumentStoreOptions();
        configuration.GetSection("Projection:Document:Providers:Elasticsearch").Bind(options);
        if (options.Endpoints.Count == 0)
        {
            throw new InvalidOperationException(
                "Projection:Document:Providers:Elasticsearch is enabled but Endpoints is empty.");
        }

        return options;
    }

    private static bool ResolveOptionalBool(string? rawValue, bool fallbackValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return fallbackValue;
        if (!bool.TryParse(rawValue, out var parsed))
            throw new InvalidOperationException($"Invalid boolean value '{rawValue}'.");

        return parsed;
    }
}
