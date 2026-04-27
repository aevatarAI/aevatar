// ─────────────────────────────────────────────────────────────
// EnvironmentSecretsStore — read-only IAevatarSecretsStore backed by
// IConfiguration (env vars + non-secret config files).
//
// Used by hosts that must not persist secrets to local files
// (e.g. Aevatar.Mainnet.Host.Api). Secrets are expected to come from
// the deploy platform via AEVATAR_-prefixed environment variables.
// Set / Remove deliberately throw so a misconfigured caller fails
// fast at the call site rather than silently falling back to disk.
// ─────────────────────────────────────────────────────────────

using Microsoft.Extensions.Configuration;

namespace Aevatar.Configuration;

/// <summary>
/// Read-only secrets store that resolves keys from <see cref="IConfiguration"/>.
/// Mutation methods throw <see cref="InvalidOperationException"/> by design:
/// hosts opting into this store have explicitly disabled local file persistence.
/// </summary>
public sealed class EnvironmentSecretsStore : IAevatarSecretsStore
{
    private readonly IConfiguration _configuration;

    public EnvironmentSecretsStore(IConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public string? Get(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        var value = _configuration[key];
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public string? GetApiKey(string providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName)) return null;

        var byProvidersSection = _configuration[$"LLMProviders:Providers:{providerName}:ApiKey"];
        if (!string.IsNullOrWhiteSpace(byProvidersSection))
            return byProvidersSection;

        var byProviderSection = _configuration[$"LLMProviders:{providerName}:ApiKey"];
        if (!string.IsNullOrWhiteSpace(byProviderSection))
            return byProviderSection;

        var byEnvConvention = _configuration[$"{providerName}_API_KEY"];
        if (!string.IsNullOrWhiteSpace(byEnvConvention))
            return byEnvConvention;

        return null;
    }

    public string? GetDefaultProvider()
    {
        var value = _configuration["LLMProviders:Default"];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Returns a snapshot of secret-shaped configuration entries. Only keys
    /// that match the conventions <see cref="GetApiKey"/> understands are
    /// included: anything under <c>LLMProviders:</c> (provider definitions,
    /// API keys, default name) and any <c>{NAME}_API_KEY</c>-style keys.
    /// <para>
    /// This is intentionally narrower than dumping the entire
    /// <see cref="IConfiguration"/> view: in env-driven hosts, the config root
    /// also contains binding URLs, feature flags and connection strings, none
    /// of which belong on the secrets API surface.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, string> GetAll()
    {
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _configuration.AsEnumerable())
        {
            if (string.IsNullOrEmpty(kv.Value)) continue;
            if (!IsSecretShapedKey(kv.Key)) continue;
            snapshot[kv.Key] = kv.Value;
        }
        return snapshot;
    }

    private static bool IsSecretShapedKey(string key) =>
        key.StartsWith("LLMProviders:", StringComparison.OrdinalIgnoreCase) ||
        key.EndsWith("_API_KEY", StringComparison.OrdinalIgnoreCase);

    public void Set(string key, string value) =>
        throw new InvalidOperationException(
            "EnvironmentSecretsStore is read-only: this host disables local file secrets persistence. " +
            "Inject secrets via AEVATAR_-prefixed environment variables or platform-managed configuration.");

    public void Remove(string key) =>
        throw new InvalidOperationException(
            "EnvironmentSecretsStore is read-only: this host disables local file secrets persistence. " +
            "Manage secret removal via AEVATAR_-prefixed environment variables or platform-managed configuration.");
}
