namespace Aevatar.Configuration;

public sealed record LocalSecretProtectionOptions(bool AllowPlaintextSecrets)
{
    public const string AllowPlaintextSecretsEnv = "AEVATAR_ALLOW_PLAINTEXT_SECRETS";

    public static LocalSecretProtectionOptions FromEnvironment() =>
        new(IsEnvironmentPlaintextOptInEnabled());

    public static readonly LocalSecretProtectionOptions NoPlaintextNoKeychain = new(false);

    public static readonly LocalSecretProtectionOptions DevelopmentPlaintextNoKeychain = new(true);

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
