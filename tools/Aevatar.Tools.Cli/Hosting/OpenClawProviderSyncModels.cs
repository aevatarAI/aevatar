namespace Aevatar.Tools.Cli.Hosting;

internal sealed record OpenClawProviderSnapshot(
    string ProviderType,
    string Model,
    string Endpoint,
    string ApiKey)
{
    public static readonly OpenClawProviderSnapshot Empty = new("", "", "", "");
}

internal sealed record OpenClawProviderSet(
    string DefaultProvider,
    IReadOnlyDictionary<string, OpenClawProviderSnapshot> Providers);

internal sealed record OpenClawSyncProviderDecision(
    string ProviderName,
    OpenClawProviderSnapshot Aevatar,
    OpenClawProviderSnapshot OpenClaw,
    OpenClawProviderSnapshot Merged,
    string Winner);

internal sealed record OpenClawSyncPlan(
    string Mode,
    string Precedence,
    string AevatarSecretsPath,
    string OpenClawConfigPath,
    string EffectiveDefaultProvider,
    IReadOnlyList<OpenClawSyncProviderDecision> Providers,
    OpenClawProviderSet AevatarTarget,
    OpenClawProviderSet OpenClawTarget,
    bool AevatarChanges,
    bool OpenClawChanges,
    IReadOnlyList<string> Warnings);

internal sealed record OpenClawSyncApplyResult(
    string AevatarSecretsPath,
    string OpenClawConfigPath,
    string EffectiveDefaultProvider,
    int ProviderCount,
    bool AevatarUpdated,
    bool OpenClawUpdated,
    string? OpenClawBackupPath);
