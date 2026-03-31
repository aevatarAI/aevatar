using Aevatar.Foundation.Abstractions.ExternalLinks;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Foundation.ExternalLinks.WebSocket;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWebSocketExternalLinkTransport(this IServiceCollection services)
    {
        services.AddSingleton<IExternalLinkTransportFactory, WebSocketTransportFactory>();
        return services;
    }
}
