using Aevatar.Configuration;
using Aevatar.Foundation.Abstractions.Credentials;

namespace Aevatar.Bootstrap.Connectors;

public sealed class SecretRefHeaderConnectorAuthorizationProvider : IConnectorRequestAuthorizationProvider
{
    public const string AuthType = "secret_ref_header";

    private readonly ConnectorAuthConfig _authConfig;
    private readonly ICredentialProvider? _credentialProvider;

    public SecretRefHeaderConnectorAuthorizationProvider(
        ConnectorAuthConfig authConfig,
        ICredentialProvider? credentialProvider)
    {
        _authConfig = authConfig ?? throw new ArgumentNullException(nameof(authConfig));
        _credentialProvider = credentialProvider;
    }

    public async Task ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured(_authConfig))
            throw new InvalidOperationException("secret_ref_header auth requires secretRef and headerName.");

        if (_credentialProvider is null)
            throw new InvalidOperationException("secret_ref_header credential provider is unavailable.");

        var headerName = _authConfig.HeaderName.Trim();
        if (!IsValidHeaderName(headerName))
            throw new InvalidOperationException("secret_ref_header headerName is invalid.");

        if (request.Headers.Contains(headerName) ||
            request.Content?.Headers.Contains(headerName) == true)
        {
            throw new InvalidOperationException("secret_ref_header header duplicates an existing configured header.");
        }

        var secret = await _credentialProvider.ResolveAsync(_authConfig.SecretRef.Trim(), cancellationToken);
        if (string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException("secret_ref_header secret could not be resolved.");

        if (!request.Headers.TryAddWithoutValidation(headerName, string.Concat(_authConfig.HeaderValuePrefix, secret.Trim())))
            throw new InvalidOperationException("secret_ref_header header could not be applied.");
    }

    public static bool HasAuthType(ConnectorAuthConfig? authConfig) =>
        authConfig != null &&
        string.Equals(authConfig.Type, AuthType, StringComparison.OrdinalIgnoreCase);

    public static bool IsConfigured(ConnectorAuthConfig authConfig) =>
        HasAuthType(authConfig) &&
        !string.IsNullOrWhiteSpace(authConfig.SecretRef) &&
        !string.IsNullOrWhiteSpace(authConfig.HeaderName);

    public static bool IsValidHeaderName(string headerName) =>
        !string.IsNullOrWhiteSpace(headerName) &&
        headerName.All(IsTokenChar);

    private static bool IsTokenChar(char ch) =>
        ch is >= '0' and <= '9' ||
        ch is >= 'A' and <= 'Z' ||
        ch is >= 'a' and <= 'z' ||
        ch is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';
}
