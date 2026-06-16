using Aevatar.Configuration;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Connectors;
using Microsoft.Extensions.Logging;

namespace Aevatar.Bootstrap.Connectors;

public sealed class HttpConnectorBuilder : IConnectorBuilder
{
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly ICredentialProvider? _credentialProvider;

    public HttpConnectorBuilder()
    {
    }

    public HttpConnectorBuilder(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    }

    public HttpConnectorBuilder(ICredentialProvider credentialProvider)
    {
        _credentialProvider = credentialProvider ?? throw new ArgumentNullException(nameof(credentialProvider));
    }

    public HttpConnectorBuilder(IHttpClientFactory? httpClientFactory, ICredentialProvider? credentialProvider)
    {
        _httpClientFactory = httpClientFactory;
        _credentialProvider = credentialProvider;
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

        var httpClientName = BuildHttpClientName(entry.Name);
        if (!TryCreateAuthorizationProvider(entry, httpClientName, logger, out var authorizationProvider))
            return false;

        connector = new HttpConnector(
            entry.Name,
            entry.Http.BaseUrl,
            entry.Http.AllowedMethods,
            entry.Http.AllowedPaths,
            entry.Http.AllowedInputKeys,
            entry.Http.DefaultHeaders,
            entry.TimeoutMs,
            _httpClientFactory,
            httpClientName,
            authorizationProvider);
        return true;
    }

    private bool TryCreateAuthorizationProvider(
        ConnectorConfigEntry entry,
        string httpClientName,
        ILogger logger,
        out IConnectorRequestAuthorizationProvider? authorizationProvider)
    {
        authorizationProvider = null;
        var auth = entry.Http.Auth;
        if (ClientCredentialsConnectorAuthorizationProvider.IsConfigured(auth))
        {
            authorizationProvider = new ClientCredentialsConnectorAuthorizationProvider(auth, _httpClientFactory, httpClientName);
            return true;
        }

        if (!SecretRefHeaderConnectorAuthorizationProvider.HasAuthType(auth))
            return true;

        if (!SecretRefHeaderConnectorAuthorizationProvider.IsConfigured(auth))
        {
            logger.LogWarning("Skip connector {Name}: http.auth secret_ref_header requires secretRef and headerName", entry.Name);
            return false;
        }

        var headerName = auth.HeaderName.Trim();
        if (!SecretRefHeaderConnectorAuthorizationProvider.IsValidHeaderName(headerName))
        {
            logger.LogWarning("Skip connector {Name}: http.auth.headerName is invalid", entry.Name);
            return false;
        }

        if (entry.Http.DefaultHeaders.ContainsKey(headerName))
        {
            logger.LogWarning("Skip connector {Name}: http.auth.headerName duplicates defaultHeaders", entry.Name);
            return false;
        }

        authorizationProvider = new SecretRefHeaderConnectorAuthorizationProvider(auth, _credentialProvider);
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
