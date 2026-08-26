namespace Aevatar.AI.Application.CodexExecution;

internal sealed record ManagedCodexNyxIdEligibility(
    string ManagedCodexUserServiceId,
    string ChronoLlmUserServiceId);

internal sealed class ManagedCodexNyxIdCatalogResolver
{
    private const string SharedDelegationScope = "proxy:*";
    private const string CodeDelegationScope = "sandbox:execute";

    public async Task<ManagedCodexNyxIdEligibility> ResolveAsync(
        IManagedCodexNyxIdCredentialPort port,
        string bearerToken,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(port);
        var services = await port.ListUserServicesAsync(bearerToken, ct).ConfigureAwait(false);
        var sandboxMatches = services
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

        if (sandboxMatches.Length != 1 || llmMatches.Length != 1)
        {
            throw Failure(
                "managed_user_services_unavailable",
                "The user's required managed Codex services are not uniquely available.");
        }

        var sandbox = sandboxMatches[0];
        var llm = llmMatches[0];
        var sandboxId = sandbox.Id?.Trim() ?? string.Empty;
        var llmId = llm.Id?.Trim() ?? string.Empty;
        if (!IsUsable(sandbox) ||
            !IsUsable(llm) ||
            string.IsNullOrWhiteSpace(sandboxId) ||
            string.IsNullOrWhiteSpace(llmId) ||
            string.Equals(sandboxId, llmId, StringComparison.Ordinal))
        {
            throw Failure(
                "managed_user_services_unavailable",
                "The user's required managed Codex services are not usable.");
        }
        // Managed execution uses X-API-Key and sends no Authorization header, so the shared
        // service's bearer-forwarding policy only applies to ordinary /execute calls.
        if (sandbox.InjectDelegationToken != true ||
            !HasAllowedDelegationScopes(sandbox.DelegationTokenScope))
        {
            throw Failure(
                "chrono_sandbox_delegation_misconfigured",
                "The chrono-sandbox NyxID delegation policy is not ready for managed Codex.");
        }

        return new ManagedCodexNyxIdEligibility(sandboxId, llmId);
    }

    private static bool HasAllowedDelegationScopes(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
            return false;

        var scopes = scope.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return scopes.Contains(SharedDelegationScope, StringComparer.Ordinal) &&
               scopes.Contains(CodeDelegationScope, StringComparer.Ordinal);
    }

    private static bool IsUsable(ManagedCodexNyxIdService service) =>
        service.IsActive && service.CredentialSourceAllowed != false;

    private static ManagedCodexCredentialLifecycleException Failure(
        string code,
        string message) =>
        new(code, message);
}
