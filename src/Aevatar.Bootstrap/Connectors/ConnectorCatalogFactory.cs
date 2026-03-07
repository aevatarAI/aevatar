using Aevatar.Configuration;
using Aevatar.Foundation.Abstractions.Connectors;
using Microsoft.Extensions.Logging;

namespace Aevatar.Bootstrap.Connectors;

public static class ConnectorCatalogFactory
{
    public static StaticConnectorCatalog Build(
        IEnumerable<IConnectorBuilder> connectorBuilders,
        ILogger logger,
        IEnumerable<string?>? configPaths = null)
    {
        ArgumentNullException.ThrowIfNull(connectorBuilders);
        ArgumentNullException.ThrowIfNull(logger);

        var buildersByType = connectorBuilders
            .GroupBy(x => x.Type, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

        var connectors = new Dictionary<string, IConnector>(StringComparer.OrdinalIgnoreCase);
        foreach (var configPath in ResolveConfigPaths(configPaths))
        {
            var entries = AevatarConnectorConfig.LoadConnectors(configPath);
            foreach (var entry in entries)
            {
                if (!buildersByType.TryGetValue(entry.Type, out var builder))
                {
                    logger.LogWarning("Skip connector {Name}: unsupported type {Type}", entry.Name, entry.Type);
                    continue;
                }

                if (!builder.TryBuild(entry, logger, out var connector) || connector == null)
                    continue;

                connectors[connector.Name] = connector;
            }
        }

        return connectors.Count == 0
            ? StaticConnectorCatalog.Empty
            : new StaticConnectorCatalog(connectors.Values);
    }

    private static IEnumerable<string?> ResolveConfigPaths(IEnumerable<string?>? configPaths)
    {
        if (configPaths == null)
        {
            yield return null;
            yield break;
        }

        var any = false;
        foreach (var path in configPaths)
        {
            any = true;
            yield return string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        }

        if (!any)
            yield return null;
    }
}
