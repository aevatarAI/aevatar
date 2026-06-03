using Aevatar.Configuration;
using Aevatar.Foundation.Abstractions.Connectors;
using Microsoft.Extensions.Logging;

namespace Aevatar.Bootstrap.Connectors;

public sealed class HostCallbackConnectorBuilder : IConnectorBuilder
{
    private readonly IReadOnlyDictionary<string, IHostCallbackConnectorHandler> _handlersByName;

    public HostCallbackConnectorBuilder(IEnumerable<IHostCallbackConnectorHandler>? handlers = null)
    {
        _handlersByName = (handlers ?? [])
            .Where(handler => !string.IsNullOrWhiteSpace(handler.Name))
            .GroupBy(handler => handler.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    public string Type => "host_callback";

    public bool TryBuild(ConnectorConfigEntry entry, ILogger logger, out IConnector? connector)
    {
        connector = null;
        var handlerName = string.IsNullOrWhiteSpace(entry.HostCallback.Handler)
            ? entry.Name
            : entry.HostCallback.Handler.Trim();

        if (string.IsNullOrWhiteSpace(handlerName))
        {
            logger.LogWarning("Skip connector {Name}: hostCallback.handler is required", entry.Name);
            return false;
        }

        if (!_handlersByName.TryGetValue(handlerName, out var handler))
        {
            logger.LogWarning(
                "Skip connector {Name}: host callback handler {HandlerName} is not registered",
                entry.Name,
                handlerName);
            return false;
        }

        connector = new HostCallbackConnector(
            entry.Name,
            handlerName,
            handler,
            entry.HostCallback.AllowedOperations,
            entry.HostCallback.AllowedInputKeys);
        return true;
    }
}
