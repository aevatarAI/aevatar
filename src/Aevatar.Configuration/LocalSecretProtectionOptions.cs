namespace Aevatar.Configuration;

<<<<<<< HEAD
public sealed record LocalSecretProtectionOptions(
    bool AllowPlaintextSecrets,
    bool AllowMasterKeyResolution = true)
=======
public enum LocalSecretMasterKeySource
{
    Auto,
    Disabled,
}

public sealed record LocalSecretProtectionOptions(
    bool AllowPlaintextSecrets,
    LocalSecretMasterKeySource MasterKeySource = LocalSecretMasterKeySource.Auto)
>>>>>>> origin/feat/2026-07-10_scheduled-agent-key-credential
{
    public const string AllowPlaintextSecretsEnv = "AEVATAR_ALLOW_PLAINTEXT_SECRETS";

    public static LocalSecretProtectionOptions FromEnvironment() =>
        new(IsEnvironmentPlaintextOptInEnabled());

<<<<<<< HEAD
    public static readonly LocalSecretProtectionOptions NoPlaintextNoKeychain = new(false, false);

    public static readonly LocalSecretProtectionOptions DevelopmentPlaintextNoKeychain = new(true, false);
=======
    public bool UseLocalMasterKeySources => MasterKeySource == LocalSecretMasterKeySource.Auto;
>>>>>>> origin/feat/2026-07-10_scheduled-agent-key-credential

    public static bool IsEnvironmentPlaintextOptInEnabled() =>
        string.Equals(
            Environment.GetEnvironmentVariable(AllowPlaintextSecretsEnv),
            "true",
            StringComparison.OrdinalIgnoreCase);

    public void ThrowIfPlaintextUnavailable()
    {
        if (AllowPlaintextSecrets)
        {
            return;
        }

        throw new InvalidOperationException(
            "Local plaintext secrets are disabled. Configure encrypted local secrets or set " +
            $"{AllowPlaintextSecretsEnv}=true only for local development.");
    }
}
