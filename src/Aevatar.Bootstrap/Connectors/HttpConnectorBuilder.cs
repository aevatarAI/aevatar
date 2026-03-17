using Aevatar.Configuration;
using Aevatar.Foundation.Abstractions.Connectors;
using Microsoft.Extensions.Logging;

namespace Aevatar.Bootstrap.Connectors;

public sealed class HttpConnectorBuilder : IConnectorBuilder
{
    private readonly IHttpClientFactory? _httpClientFactory;

    public HttpConnectorBuilder()
    {
    }

    public HttpConnectorBuilder(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    }

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
            entry.TimeoutMs,
            _httpClientFactory,
            BuildHttpClientName(entry.Name));
        return true;
    }

    private static string BuildHttpClientName(string connectorName)
    {
        var normalized = string.IsNullOrWhiteSpace(connectorName)
            ? "default"
            : new string(connectorName.Trim()
                .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_')
                .ToArray());
        return $"aevatar.connector.http.{normalized}";
    }
}
