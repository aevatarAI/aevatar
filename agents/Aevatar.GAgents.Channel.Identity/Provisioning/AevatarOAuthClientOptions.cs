using Aevatar.Configuration.BackendConsole;

namespace Aevatar.GAgents.Channel.Identity;

/// <summary>
/// Deployment-owned OAuth client identity used by browser PKCE and broker
/// token operations.
/// </summary>
public sealed class AevatarOAuthClientOptions
{
    public const string ClientIdConfigurationKey = BackendConsoleOidcClientIdResolver.ConfigurationKey;

    public string ClientId { get; set; } = string.Empty;
}
