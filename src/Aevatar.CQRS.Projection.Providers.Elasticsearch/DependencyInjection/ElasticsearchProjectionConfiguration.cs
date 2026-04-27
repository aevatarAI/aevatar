using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;

/// <summary>
/// Shared configuration helpers for projection-store SCEs that pick between
/// the Elasticsearch and InMemory document stores at startup. Hosts an
/// <c>Enabled</c> auto-detection rule (explicit flag → endpoints presence →
/// false) plus a typed <see cref="ElasticsearchProjectionDocumentStoreOptions"/>
/// binder, so individual agent packages don't each copy the same
/// <c>ResolveElasticsearchEnabled</c> / <c>BuildElasticsearchOptions</c>
/// pair into their service-collection extensions.
/// </summary>
public static class ElasticsearchProjectionConfiguration
{
    public const string SectionPath = "Projection:Document:Providers:Elasticsearch";

    /// <summary>
    /// Returns true when Elasticsearch is the projection store. Honors an
    /// explicit <c>Enabled</c> flag; otherwise auto-detects from endpoints
    /// presence. When <paramref name="configuration"/> is null (unit-test
    /// composition), returns false so the caller falls back to InMemory.
    /// </summary>
    /// <param name="configuration">Host configuration root, or null.</param>
    /// <param name="logger">
    /// Optional logger; receives a single warning when a configuration is
    /// provided but neither the explicit flag nor any endpoint is set
    /// (production misconfiguration). When null the warning falls back to
    /// <see cref="Console.Error"/> so SCE composition (which has no DI-built
    /// logger yet) still surfaces the regression in startup output.
    /// </param>
    /// <param name="storeName">
    /// Caller-supplied identifier for the warning text (e.g. "ChannelRuntime",
    /// "DeviceRegistration"). Helps operators trace which projection slice
    /// degraded to InMemory.
    /// </param>
    public static bool IsEnabled(
        IConfiguration? configuration,
        ILogger? logger = null,
        string? storeName = null)
    {
        if (configuration is null)
            return false;

        var section = configuration.GetSection(SectionPath);
        var explicitEnabled = section["Enabled"];
        if (!string.IsNullOrWhiteSpace(explicitEnabled))
            return string.Equals(explicitEnabled.Trim(), "true", StringComparison.OrdinalIgnoreCase);

        var hasEndpoints = section.GetSection("Endpoints").GetChildren()
            .Any(static x => !string.IsNullOrWhiteSpace(x.Value));

        if (!hasEndpoints)
        {
            // Configuration is wired but ES is silent — production misconfiguration.
            // Warn so operators can spot the regression in startup logs instead of
            // discovering it after the next restart wipes the InMemory replica.
            // SCEs run before the host builds its logger pipeline, so they pass a
            // null logger and we route the warning to Console.Error (matching the
            // pre-helper Console.Error.WriteLine behavior). Tests that wire a mock
            // logger receive the structured-log call instead.
            var resolvedStoreName = storeName ?? "ProjectionStore";
            if (logger is not null)
            {
                logger.LogWarning(
                    "{StoreName}: Elasticsearch is not configured ({Section}:Endpoints empty). " +
                    "Falling back to volatile InMemory projection store. Set {Section}:Enabled=true " +
                    "or populate Endpoints for production.",
                    resolvedStoreName,
                    SectionPath,
                    SectionPath);
            }
            else
            {
                Console.Error.WriteLine(
                    $"{resolvedStoreName}: Elasticsearch is not configured ({SectionPath}:Endpoints empty). " +
                    $"Falling back to volatile InMemory projection store. Set {SectionPath}:Enabled=true " +
                    $"or populate Endpoints for production.");
            }
        }

        return hasEndpoints;
    }

    /// <summary>
    /// Binds the typed Elasticsearch projection-store options from the
    /// host configuration. Caller is responsible for non-null configuration.
    /// </summary>
    public static ElasticsearchProjectionDocumentStoreOptions BindOptions(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new ElasticsearchProjectionDocumentStoreOptions();
        configuration.GetSection(SectionPath).Bind(options);
        return options;
    }
}
