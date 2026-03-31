using Aevatar.Foundation.Abstractions.ExternalLinks;
using Microsoft.Extensions.Logging;

namespace Aevatar.Foundation.ExternalLinks.WebSocket;

public sealed class WebSocketTransportFactory : IExternalLinkTransportFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public WebSocketTransportFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public bool CanCreate(string transportType) =>
        string.Equals(transportType, "websocket", StringComparison.OrdinalIgnoreCase);

    public IExternalLinkTransport Create() =>
        new WebSocketTransport(_loggerFactory.CreateLogger<WebSocketTransport>());
}
