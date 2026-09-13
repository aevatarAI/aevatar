using System.Net;
using System.Text;

namespace Aevatar.AI.Tests;

internal sealed class NyxRelayTestHttpClientFactory(HttpClient client) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => client;
}

internal sealed class NyxRelayOidcDocumentHandler : HttpMessageHandler
{
    private readonly string _discoveryJson;
    private readonly Func<string> _jwksJsonFactory;

    public NyxRelayOidcDocumentHandler(string discoveryJson, string jwksJson)
        : this(discoveryJson, () => jwksJson)
    {
    }

    public NyxRelayOidcDocumentHandler(string discoveryJson, Func<string> jwksJsonFactory)
    {
        _discoveryJson = discoveryJson;
        _jwksJsonFactory = jwksJsonFactory;
    }

    public int JwksRequests { get; private set; }
    public int DiscoveryFailuresRemaining { get; set; }
    public Queue<HttpStatusCode> DiscoveryStatusCodes { get; } = [];
    public List<string> RequestUris { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
        RequestUris.Add(uri);
        if (uri.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
        {
            if (DiscoveryFailuresRemaining > 0)
            {
                DiscoveryFailuresRemaining--;
                throw new HttpRequestException("transient_discovery_failure");
            }
            if (DiscoveryStatusCodes.Count > 0)
            {
                var statusCode = DiscoveryStatusCodes.Dequeue();
                if (statusCode != HttpStatusCode.OK)
                {
                    return Task.FromResult(new HttpResponseMessage(statusCode)
                    {
                        Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                    });
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_discoveryJson, Encoding.UTF8, "application/json"),
            });
        }

        if (uri.EndsWith("/jwks", StringComparison.Ordinal))
        {
            JwksRequests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_jwksJsonFactory(), Encoding.UTF8, "application/json"),
            });
        }

        throw new InvalidOperationException($"Unexpected URL: {uri}");
    }
}
