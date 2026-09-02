namespace Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet;

public sealed class GarnetSecretStoreOptions
{
    /// <summary>
    /// Garnet connection string (Redis protocol), for example: localhost:6379,abortConnect=false.
    /// </summary>
    public string ConnectionString { get; set; } = "localhost:6379,abortConnect=false";

    /// <summary>
    /// Database index. -1 uses the default database from connection options.
    /// </summary>
    public int Database { get; set; } = -1;

    public string SecretVaultPrefix { get; set; } = "aevatar:secret-vault";

    public string RuntimeSecretPrefix { get; set; } = "aevatar:runtime-secrets";

    public string KeyringPath { get; set; } = string.Empty;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
            throw new InvalidOperationException("GarnetSecretStore requires a non-empty ConnectionString.");
        if (string.IsNullOrWhiteSpace(KeyringPath))
            throw new InvalidOperationException("GarnetSecretStore requires a non-empty KeyringPath.");
        if (string.IsNullOrWhiteSpace(SecretVaultPrefix))
            throw new InvalidOperationException("GarnetSecretStore requires a non-empty SecretVaultPrefix.");
        if (string.IsNullOrWhiteSpace(RuntimeSecretPrefix))
            throw new InvalidOperationException("GarnetSecretStore requires a non-empty RuntimeSecretPrefix.");

        var vaultPrefix = NormalizePrefix(SecretVaultPrefix);
        var runtimePrefix = NormalizePrefix(RuntimeSecretPrefix);
        if (string.Equals(vaultPrefix, runtimePrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("GarnetSecretStore requires distinct SecretVaultPrefix and RuntimeSecretPrefix values.");
    }

    internal string NormalizedSecretVaultPrefix => NormalizePrefix(SecretVaultPrefix);

    internal string NormalizedRuntimeSecretPrefix => NormalizePrefix(RuntimeSecretPrefix);

    private static string NormalizePrefix(string value) => value.Trim().TrimEnd(':');
}
