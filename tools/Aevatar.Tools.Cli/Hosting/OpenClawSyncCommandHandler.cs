using System.Text.Json;
using Aevatar.Configuration;

namespace Aevatar.Tools.Cli.Hosting;

internal static class OpenClawSyncCommandHandler
{
    private const string DefaultMode = "bidirectional";
    private const string DefaultPrecedence = "aevatar";
    private const string DefaultOpenClawConfig = "~/.openclaw/openclaw.json";

    public static Task<int> PlanAsync(
        string? mode,
        string? precedence,
        bool dryRun,
        string? openClawConfigPath,
        string? aevatarSecretsPath,
        CancellationToken ct)
    {
        _ = ct;
        try
        {
            var resolvedSecretsPath = ResolveSecretsPath(aevatarSecretsPath);
            var resolvedOpenClawPath = ResolveOpenClawPath(openClawConfigPath);

            var aevatarState = OpenClawProviderSyncPersistence.ReadAevatarState(resolvedSecretsPath);
            var openClawDoc = OpenClawProviderSyncPersistence.LoadOpenClawDocument(resolvedOpenClawPath);
            var plan = OpenClawProviderSyncPlanner.BuildPlan(
                aevatarState,
                openClawDoc.State,
                mode ?? DefaultMode,
                precedence ?? DefaultPrecedence,
                resolvedSecretsPath,
                resolvedOpenClawPath);

            var payload = new
            {
                ok = true,
                command = "aevatar openclaw sync plan",
                dryRun,
                mode = plan.Mode,
                precedence = plan.Precedence,
                paths = new
                {
                    aevatarSecrets = plan.AevatarSecretsPath,
                    openClawConfig = plan.OpenClawConfigPath,
                },
                changes = new
                {
                    aevatar = plan.AevatarChanges,
                    openClaw = plan.OpenClawChanges,
                },
                effectiveDefaultProvider = plan.EffectiveDefaultProvider,
                providers = plan.Providers.Select(x => new
                {
                    name = x.ProviderName,
                    winner = x.Winner,
                    aevatar = ToPublicSnapshot(x.Aevatar),
                    openClaw = ToPublicSnapshot(x.OpenClaw),
                    merged = ToPublicSnapshot(x.Merged),
                }).ToList(),
                warnings = plan.Warnings
                    .Concat(openClawDoc.Warnings)
                    .Distinct(StringComparer.Ordinal)
                    .ToList(),
            };

            Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
            }));

            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[openclaw.sync.plan] {ex.Message}");
            return Task.FromResult(1);
        }
    }

    public static Task<int> ApplyAsync(
        string? mode,
        string? precedence,
        bool createBackup,
        string? openClawConfigPath,
        string? aevatarSecretsPath,
        CancellationToken ct)
    {
        _ = ct;
        try
        {
            var resolvedSecretsPath = ResolveSecretsPath(aevatarSecretsPath);
            var resolvedOpenClawPath = ResolveOpenClawPath(openClawConfigPath);

            var aevatarState = OpenClawProviderSyncPersistence.ReadAevatarState(resolvedSecretsPath);
            var openClawDoc = OpenClawProviderSyncPersistence.LoadOpenClawDocument(resolvedOpenClawPath);
            var plan = OpenClawProviderSyncPlanner.BuildPlan(
                aevatarState,
                openClawDoc.State,
                mode ?? DefaultMode,
                precedence ?? DefaultPrecedence,
                resolvedSecretsPath,
                resolvedOpenClawPath);

            var backupPath = default(string);
            if (plan.AevatarChanges)
                OpenClawProviderSyncPersistence.ApplyToAevatar(plan.AevatarTarget, plan.AevatarSecretsPath);
            if (plan.OpenClawChanges)
                backupPath = OpenClawProviderSyncPersistence.ApplyToOpenClaw(openClawDoc, plan.OpenClawTarget, createBackup);

            var result = new OpenClawSyncApplyResult(
                AevatarSecretsPath: plan.AevatarSecretsPath,
                OpenClawConfigPath: plan.OpenClawConfigPath,
                EffectiveDefaultProvider: plan.EffectiveDefaultProvider,
                ProviderCount: plan.Providers.Count,
                AevatarUpdated: plan.AevatarChanges,
                OpenClawUpdated: plan.OpenClawChanges,
                OpenClawBackupPath: backupPath);

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                ok = true,
                command = "aevatar openclaw sync apply",
                mode = plan.Mode,
                precedence = plan.Precedence,
                result = new
                {
                    aevatarUpdated = result.AevatarUpdated,
                    openClawUpdated = result.OpenClawUpdated,
                    providerCount = result.ProviderCount,
                    effectiveDefaultProvider = result.EffectiveDefaultProvider,
                    aevatarSecrets = result.AevatarSecretsPath,
                    openClawConfig = result.OpenClawConfigPath,
                    openClawBackup = result.OpenClawBackupPath ?? string.Empty,
                },
                warnings = plan.Warnings
                    .Concat(openClawDoc.Warnings)
                    .Distinct(StringComparer.Ordinal)
                    .ToList(),
            }, new JsonSerializerOptions
            {
                WriteIndented = true,
            }));

            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[openclaw.sync.apply] {ex.Message}");
            return Task.FromResult(1);
        }
    }

    private static object ToPublicSnapshot(OpenClawProviderSnapshot snapshot) => new
    {
        providerType = snapshot.ProviderType,
        model = snapshot.Model,
        endpoint = snapshot.Endpoint,
        apiKeyConfigured = !string.IsNullOrWhiteSpace(snapshot.ApiKey),
    };

    private static string ResolveSecretsPath(string? value)
    {
        var candidate = OpenClawProviderSyncPersistence.ExpandPath(value);
        return string.IsNullOrWhiteSpace(candidate)
            ? AevatarPaths.SecretsJson
            : candidate;
    }

    private static string ResolveOpenClawPath(string? value)
    {
        var candidate = OpenClawProviderSyncPersistence.ExpandPath(value);
        return string.IsNullOrWhiteSpace(candidate)
            ? OpenClawProviderSyncPersistence.ExpandPath(DefaultOpenClawConfig)
            : candidate;
    }
}
