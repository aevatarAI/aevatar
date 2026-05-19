using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.ChatRouting;

/// <summary>
/// DI registration entry point for the ChatRouting agent package
/// (ingress layer v1 — issue #692, Phase 1).
/// </summary>
public static class ChatRoutingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="ChatRoutePolicyCurrentStateDocument"/> projection
    /// document store (reader + writer). Without this registration the projector's
    /// <c>UpsertAsync</c> has no backing store and the readmodel silently never
    /// materializes.
    ///
    /// Phase 1 wires the InMemory store unconditionally; the Elasticsearch-vs-InMemory
    /// selection (mirroring <c>AddScheduledAgents</c>) lands when an ingress entry
    /// actually consumes the readmodel.
    /// </summary>
    public static IServiceCollection AddChatRoutingAgents(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddInMemoryDocumentProjectionStore<ChatRoutePolicyCurrentStateDocument, string>(
            static document => document.ActorId,
            static key => key);

        return services;
    }
}
