using Aevatar.Bootstrap.Connectors;
using Aevatar.Configuration;
using Aevatar.Foundation.Abstractions.Connectors;
using Microsoft.Extensions.Logging;

namespace Aevatar.Bootstrap;

public static class ConnectorRegistration
{
    public static async Task<int> RegisterConnectorsAsync(
        IConnectorRegistry registry,
        IEnumerable<IConnectorBuilder> connectorBuilders,
        ILogger logger,
        string? connectorsJsonPath = null,
        CancellationToken ct = default)
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

            // Refactor (iter87/cluster-087):
            //   Old pattern: startup-built disposable MCP connectors were stored in a sync registry with no shutdown owner.
            //   New principle: bootstrap-created connectors enter the registry as owned lifecycle resources.
            await registry.RegisterAsync(
                global::Aevatar.Foundation.Abstractions.Connectors.ConnectorRegistration.Owned(connector),
                ct);
            added++;
        }

        return added;
    }
}
