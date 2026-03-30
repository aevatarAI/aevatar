using System.Net.Http;

namespace Aevatar.Bootstrap.Connectors;

public sealed class ConnectorRequestAuthorizationHandler : DelegatingHandler
{
    private readonly IConnectorRequestAuthorizationProvider _authorizationProvider;

    public ConnectorRequestAuthorizationHandler(
        IConnectorRequestAuthorizationProvider authorizationProvider,
        HttpMessageHandler innerHandler)
    {
        _authorizationProvider = authorizationProvider ?? throw new ArgumentNullException(nameof(authorizationProvider));
        InnerHandler = innerHandler ?? throw new ArgumentNullException(nameof(innerHandler));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await _authorizationProvider.ApplyAsync(request, cancellationToken);
        return await base.SendAsync(request, cancellationToken);
    }
}
