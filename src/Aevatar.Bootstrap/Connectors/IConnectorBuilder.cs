using Aevatar.Configuration;
using Aevatar.Foundation.Abstractions.Connectors;
using Microsoft.Extensions.Logging;

namespace Aevatar.Bootstrap.Connectors;

public interface IConnectorBuilder
{
    string Type { get; }

    bool TryBuild(ConnectorConfigEntry entry, ILogger logger, out IConnector? connector);
}
