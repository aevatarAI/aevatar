using Microsoft.Extensions.Logging;

namespace Aevatar.AI.ToolProviders.NyxId;

public interface INyxIdApiClientFactory
{
    NyxIdApiClient CreateClient();
}

internal sealed class HttpClientFactoryNyxIdApiClientFactory : INyxIdApiClientFactory
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly NyxIdToolOptions _options;
    private readonly NyxIdApiClientTransportPolicy _transportPolicy;
    private readonly ILogger<NyxIdApiClient>? _logger;

    public HttpClientFactoryNyxIdApiClientFactory(
        IHttpClientFactory httpClientFactory,
        NyxIdToolOptions options,
        NyxIdApiClientTransportPolicy transportPolicy,
        ILogger<NyxIdApiClient>? logger = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _transportPolicy = transportPolicy ?? throw new ArgumentNullException(nameof(transportPolicy));
        _logger = logger;
    }

    public NyxIdApiClient CreateClient() =>
        new(
            _options,
            _httpClientFactory.CreateClient(nameof(NyxIdApiClient)),
            _transportPolicy,
            _logger);
}
