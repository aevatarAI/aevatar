namespace Aevatar.AI.Application.CodexExecution;

internal sealed record ManagedCodexNyxIdEligibility(string ChronoSandboxUserServiceId);

internal sealed class ManagedCodexNyxIdCatalogResolver
{
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
                    ManagedCodexOptions.ChronoSandboxServiceSlug,
                    StringComparison.Ordinal) &&
                string.Equals(service.CredentialSourceType, "personal", StringComparison.Ordinal))
            .ToArray();
        var llmMatches = services
            .Where(static service => string.Equals(
                service.Slug,
                ManagedCodexOptions.ChronoLlmServiceSlug,
                StringComparison.Ordinal))
            .ToArray();

        if (sandboxMatches.Length != 1)
        {
            throw Failure(
                "chrono_sandbox_service_unavailable",
                "The user's chrono-sandbox service is not uniquely available.");
        }
        if (llmMatches.Length != 1 || !IsUsable(llmMatches[0]))
        {
            throw Failure(
                "chrono_llm_route_unavailable",
                "The user's chrono-llm-public route is not usable.");
        }

        var sandbox = sandboxMatches[0];
        if (!IsUsable(sandbox) ||
            sandbox.ForwardAccessToken != false ||
            sandbox.InjectDelegationToken != true ||
            !string.Equals(sandbox.DelegationTokenScope, "proxy:*", StringComparison.Ordinal))
        {
            throw Failure(
                "chrono_sandbox_delegation_misconfigured",
                "The chrono-sandbox NyxID delegation policy is not ready for managed Codex.");
        }
        if (string.IsNullOrWhiteSpace(sandbox.Id))
        {
            throw Failure(
                "chrono_sandbox_service_invalid",
                "The user's chrono-sandbox service has no stable UserService ID.");
        }

        return new ManagedCodexNyxIdEligibility(sandbox.Id.Trim());
    }

    private static bool IsUsable(ManagedCodexNyxIdService service) =>
        service.IsActive && service.CredentialSourceAllowed != false;

    private static ManagedCodexCredentialLifecycleException Failure(
        string code,
        string message) =>
        new(code, message);
}
