using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Identity.Broker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgents.Channel.Identity.DependencyInjection;

/// <summary>
/// DI extensions for the Channel.Identity module. Wires the
/// <see cref="ExternalIdentityBindingGAgent"/> projection chain and the
/// production NyxID broker client. Slash-command routing in the turn runner
/// and the OAuth callback / CAE-webhook endpoints layer on top in their own
/// composition roots. See ADR-0018 §Dependencies.
/// </summary>
public static class IdentityServiceCollectionExtensions
{
    /// <summary>
    /// Registers the projection chain for the per-user binding actor:
    /// projector, materialization-context kind, metadata provider, and the
    /// projection-backed <see cref="IExternalIdentityBindingQueryPort"/>.
    /// Caller still needs to register a projection document store (e.g.
    /// <c>AddInMemoryDocumentProjectionStore</c> for tests, or the
    /// Elasticsearch provider for production).
    /// </summary>
    public static IServiceCollection AddChannelIdentityProjection(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddCurrentStateProjectionMaterializer<
            ExternalIdentityBindingMaterializationContext,
            ExternalIdentityBindingProjector>();

        services.TryAddSingleton<
            IProjectionDocumentMetadataProvider<ExternalIdentityBindingDocument>,
            ExternalIdentityBindingDocumentMetadataProvider>();

        services.TryAddSingleton<
            IExternalIdentityBindingQueryPort,
            ExternalIdentityBindingProjectionQueryPort>();
        services.TryAddSingleton<
            IProjectionReadinessPort,
            ExternalIdentityBindingProjectionReadinessPort>();

        return services;
    }

    /// <summary>
    /// Registers the broker revocation webhook validator. Registered separately
    /// from the broker client so test harnesses can swap the validator without
    /// pulling the full HTTP client.
    /// </summary>
    public static IServiceCollection AddChannelIdentityWebhookValidators(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<Endpoints.BrokerRevocationWebhookValidator>();
        return services;
    }

    /// <summary>
    /// Registers the production NyxID broker client
    /// (<see cref="NyxIdRemoteCapabilityBroker"/>) plus its supporting
    /// <see cref="StateTokenCodec"/>. Configuration binds the
    /// <c>Aevatar:NyxIdBroker</c> section into <see cref="NyxIdBrokerOptions"/>.
    /// </summary>
    public static IServiceCollection AddNyxIdRemoteCapabilityBroker(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // AddOptions wires up the IOptionsMonitor<NyxIdBrokerOptions> machinery
        // unconditionally so callers without an IConfiguration (e.g. tests
        // that programmatically push options) still resolve the monitor.
        services.AddOptions<NyxIdBrokerOptions>();
        if (configuration is not null)
        {
            services.Configure<NyxIdBrokerOptions>(configuration.GetSection("Aevatar:NyxIdBroker"));
        }

        // Both NyxIdRemoteCapabilityBroker and StateTokenCodec consume
        // IOptionsMonitor<NyxIdBrokerOptions> directly so config reload is
        // observed without a process restart (glm-5.1 L73). No snapshot
        // registration of NyxIdBrokerOptions is needed — leaving it out
        // also disambiguates ctor selection for StateTokenCodec, which has
        // a snapshot-friendly convenience overload reserved for tests.
        services.TryAddSingleton(sp => TimeProvider.System);
        services.TryAddSingleton<StateTokenCodec>();

        services.AddHttpClient<NyxIdRemoteCapabilityBroker>();
        services.TryAddSingleton<INyxIdCapabilityBroker>(sp => sp.GetRequiredService<NyxIdRemoteCapabilityBroker>());
        services.TryAddSingleton<INyxIdBrokerCallbackClient>(sp => sp.GetRequiredService<NyxIdRemoteCapabilityBroker>());

        return services;
    }
}
