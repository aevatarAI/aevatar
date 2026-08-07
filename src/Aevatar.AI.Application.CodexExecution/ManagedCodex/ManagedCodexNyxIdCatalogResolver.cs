namespace Aevatar.AI.Application.CodexExecution;

internal sealed record ManagedCodexNyxIdEligibility(
    string ManagedCodexUserServiceId,
    string ChronoLlmUserServiceId);

internal sealed class ManagedCodexNyxIdCatalogResolver
{
    public async Task<ManagedCodexNyxIdEligibility> ResolveAsync(
        IManagedCodexNyxIdCredentialPort port,
        string bearerToken,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(port);
        var services = await port.ListUserServicesAsync(bearerToken, ct).ConfigureAwait(false);
        var managedCodexMatches = services
            .Where(static service =>
                string.Equals(
                    service.Slug,
                    ManagedCodexOptions.ManagedCodexServiceSlug,
                    StringComparison.Ordinal) &&
                string.Equals(service.CredentialSourceType, "personal", StringComparison.Ordinal))
            .ToArray();
        var llmMatches = services
            .Where(static service => string.Equals(
                service.Slug,
                ManagedCodexOptions.ChronoLlmServiceSlug,
                StringComparison.Ordinal))
            .ToArray();

        if (managedCodexMatches.Length != 1 || llmMatches.Length != 1)
        {
            throw Failure(
                "managed_user_services_unavailable",
                "The user's required managed Codex services are not uniquely available.");
        }

        var managedCodex = managedCodexMatches[0];
        var llm = llmMatches[0];
        var managedCodexId = managedCodex.Id?.Trim() ?? string.Empty;
        var llmId = llm.Id?.Trim() ?? string.Empty;
        if (!IsUsable(managedCodex) ||
            !IsUsable(llm) ||
            string.IsNullOrWhiteSpace(managedCodexId) ||
            string.IsNullOrWhiteSpace(llmId) ||
            string.Equals(managedCodexId, llmId, StringComparison.Ordinal))
        {
            throw Failure(
                "managed_user_services_unavailable",
                "The user's required managed Codex services are not usable.");
        }
        if (managedCodex.ForwardAccessToken != false ||
            managedCodex.InjectDelegationToken != true ||
            !string.Equals(managedCodex.DelegationTokenScope, "proxy:*", StringComparison.Ordinal))
        {
            throw Failure(
                "managed_codex_delegation_misconfigured",
                "The chrono-managed-codex NyxID delegation policy is not ready for managed Codex.");
        }

        return new ManagedCodexNyxIdEligibility(managedCodexId, llmId);
    }

    private static bool IsUsable(ManagedCodexNyxIdService service) =>
        service.IsActive && service.CredentialSourceAllowed != false;

    private static ManagedCodexCredentialLifecycleException Failure(
        string code,
        string message) =>
        new(code, message);
}
