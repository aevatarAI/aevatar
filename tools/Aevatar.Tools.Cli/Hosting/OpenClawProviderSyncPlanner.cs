namespace Aevatar.Tools.Cli.Hosting;

internal static class OpenClawProviderSyncPlanner
{
    public static OpenClawSyncPlan BuildPlan(
        OpenClawProviderSet aevatar,
        OpenClawProviderSet openClaw,
        string mode,
        string precedence,
        string aevatarSecretsPath,
        string openClawConfigPath)
    {
        var normalizedMode = Normalize(mode);
        var normalizedPrecedence = Normalize(precedence);
        if (!string.Equals(normalizedMode, "bidirectional", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unsupported sync mode: {mode}. Only 'bidirectional' is supported in this PoC.");
        if (!string.Equals(normalizedPrecedence, "aevatar", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unsupported precedence: {precedence}. Only 'aevatar' is supported in this PoC.");

        var warnings = new List<string>();
        var mergedProviders = new SortedDictionary<string, OpenClawProviderSnapshot>(StringComparer.OrdinalIgnoreCase);
        var decisions = new List<OpenClawSyncProviderDecision>();

        var providerNames = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in aevatar.Providers.Keys)
            providerNames.Add(name);
        foreach (var name in openClaw.Providers.Keys)
            providerNames.Add(name);

        foreach (var providerName in providerNames)
        {
            var aevatarProvider = aevatar.Providers.GetValueOrDefault(providerName, OpenClawProviderSnapshot.Empty);
            var openClawProvider = openClaw.Providers.GetValueOrDefault(providerName, OpenClawProviderSnapshot.Empty);

            var merged = MergeWithAevatarPrecedence(aevatarProvider, openClawProvider);
            var winner = ResolveWinner(aevatarProvider, openClawProvider);

            mergedProviders[providerName] = merged;
            decisions.Add(new OpenClawSyncProviderDecision(
                ProviderName: providerName,
                Aevatar: aevatarProvider,
                OpenClaw: openClawProvider,
                Merged: merged,
                Winner: winner));
        }

        var effectiveDefault = FirstNonBlank(aevatar.DefaultProvider, openClaw.DefaultProvider);
        if (string.IsNullOrWhiteSpace(effectiveDefault) && mergedProviders.Count > 0)
        {
            effectiveDefault = mergedProviders.Keys.First();
            warnings.Add($"No default provider found; selected '{effectiveDefault}' from merged providers.");
        }

        var mergedSet = new OpenClawProviderSet(
            DefaultProvider: effectiveDefault,
            Providers: mergedProviders);

        var aevatarChanges = !Equivalent(aevatar, mergedSet);
        var openClawChanges = !Equivalent(openClaw, mergedSet);

        return new OpenClawSyncPlan(
            Mode: normalizedMode,
            Precedence: normalizedPrecedence,
            AevatarSecretsPath: aevatarSecretsPath,
            OpenClawConfigPath: openClawConfigPath,
            EffectiveDefaultProvider: effectiveDefault,
            Providers: decisions,
            AevatarTarget: mergedSet,
            OpenClawTarget: mergedSet,
            AevatarChanges: aevatarChanges,
            OpenClawChanges: openClawChanges,
            Warnings: warnings);
    }

    private static OpenClawProviderSnapshot MergeWithAevatarPrecedence(
        OpenClawProviderSnapshot aevatar,
        OpenClawProviderSnapshot openClaw)
    {
        return new OpenClawProviderSnapshot(
            ProviderType: FirstNonBlank(aevatar.ProviderType, openClaw.ProviderType),
            Model: FirstNonBlank(aevatar.Model, openClaw.Model),
            Endpoint: FirstNonBlank(aevatar.Endpoint, openClaw.Endpoint),
            ApiKey: FirstNonBlank(aevatar.ApiKey, openClaw.ApiKey));
    }

    private static string ResolveWinner(OpenClawProviderSnapshot aevatar, OpenClawProviderSnapshot openClaw)
    {
        var aHasAny = HasAnyValue(aevatar);
        var oHasAny = HasAnyValue(openClaw);
        if (aHasAny && oHasAny) return "aevatar-preferred";
        if (aHasAny) return "aevatar-only";
        if (oHasAny) return "openclaw-imported";
        return "empty";
    }

    private static bool HasAnyValue(OpenClawProviderSnapshot value) =>
        !string.IsNullOrWhiteSpace(value.ProviderType) ||
        !string.IsNullOrWhiteSpace(value.Model) ||
        !string.IsNullOrWhiteSpace(value.Endpoint) ||
        !string.IsNullOrWhiteSpace(value.ApiKey);

    private static bool Equivalent(OpenClawProviderSet left, OpenClawProviderSet right)
    {
        if (!string.Equals(Normalize(left.DefaultProvider), Normalize(right.DefaultProvider), StringComparison.OrdinalIgnoreCase))
            return false;
        if (left.Providers.Count != right.Providers.Count)
            return false;

        foreach (var (name, leftProvider) in left.Providers)
        {
            if (!right.Providers.TryGetValue(name, out var rightProvider))
                return false;
            if (!ProviderEquivalent(leftProvider, rightProvider))
                return false;
        }

        return true;
    }

    private static bool ProviderEquivalent(OpenClawProviderSnapshot left, OpenClawProviderSnapshot right)
    {
        return string.Equals(Normalize(left.ProviderType), Normalize(right.ProviderType), StringComparison.OrdinalIgnoreCase) &&
               string.Equals(Normalize(left.Model), Normalize(right.Model), StringComparison.Ordinal) &&
               string.Equals(Normalize(left.Endpoint), Normalize(right.Endpoint), StringComparison.OrdinalIgnoreCase) &&
               string.Equals(Normalize(left.ApiKey), Normalize(right.ApiKey), StringComparison.Ordinal);
    }

    private static string FirstNonBlank(string? preferred, string? fallback)
    {
        var preferredNormalized = Normalize(preferred);
        if (!string.IsNullOrWhiteSpace(preferredNormalized))
            return preferredNormalized;
        return Normalize(fallback);
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
