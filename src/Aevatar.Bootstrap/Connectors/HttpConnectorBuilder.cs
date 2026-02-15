using Aevatar.Configuration;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Workflow.Core.Connectors;
using Microsoft.Extensions.Logging;

namespace Aevatar.Bootstrap.Connectors;

public sealed class HttpConnectorBuilder : IConnectorBuilder
{
    public string Type => "http";

    public bool TryBuild(ConnectorConfigEntry entry, ILogger logger, out IConnector? connector)
    {
        connector = null;
        if (string.IsNullOrWhiteSpace(entry.Http.BaseUrl))
        {
            logger.LogWarning("Skip connector {Name}: http.baseUrl is required", entry.Name);
            return false;
        }

        connector = new HttpConnector(
            entry.Name,
            entry.Http.BaseUrl,
            entry.Http.AllowedMethods,
            entry.Http.AllowedPaths,
            entry.Http.AllowedInputKeys,
            entry.Http.DefaultHeaders,
            entry.TimeoutMs);
        return true;
    }
}
