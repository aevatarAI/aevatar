using Aevatar.Bootstrap.Connectors;
using Aevatar.Configuration;
using Aevatar.Foundation.Abstractions.Connectors;
using Microsoft.Extensions.Logging;

namespace Aevatar.Bootstrap;

public static class ConnectorRegistration
{
    public static int RegisterConnectors(
        IConnectorRegistry registry,
        IEnumerable<IConnectorBuilder> connectorBuilders,
        ILogger logger,
        string? connectorsJsonPath = null)
    {
        var entries = AevatarConnectorConfig.LoadConnectors(connectorsJsonPath);
        if (entries.Count == 0)
            return 0;

        var buildersByType = connectorBuilders
            .GroupBy(x => x.Type, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var entry in entries)
        {
            if (!buildersByType.TryGetValue(entry.Type, out var builder))
            {
                logger.LogWarning("Skip connector {Name}: unsupported type {Type}", entry.Name, entry.Type);
                continue;
            }

            if (!builder.TryBuild(entry, logger, out var connector) || connector == null)
                continue;

            registry.Register(connector);
            added++;
        }

        return added;
    }
}
