using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.GAgents.Channel.Runtime;
using Microsoft.Extensions.Logging;
using static Aevatar.GAgents.Channel.NyxIdRelay.VerifiedChannelRegistrationServiceSelection;
using static Aevatar.GAgents.Channel.NyxIdRelay.VerifiedChannelRegistrationServiceSelection.VerifiedChannelRegistrationAuthorizationPlan;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

public sealed record ChannelRegistrationExplicitAuthorizationResult(
    VerifiedChannelRegistrationExplicitAuthorization? Authorization,
    string ErrorCode)
{
    public bool Succeeded => Authorization is not null && string.IsNullOrEmpty(ErrorCode);
}

public sealed class VerifiedChannelRegistrationExplicitAuthorization
{
    // Only the nested planning flow can mint this final Key-provisioning handoff.
    private VerifiedChannelRegistrationExplicitAuthorization(
        VerifiedChannelRegistrationAuthorizationPlan plan,
        IReadOnlyList<ChannelBotRuntimeNyxIdServiceSelector> runtimeSelectors)
    {
        Plan = plan;
        RuntimeSelectors = runtimeSelectors.Select(static selector => selector.Clone()).ToArray();
    }

    public VerifiedChannelRegistrationAuthorizationPlan Plan { get; }
    public IReadOnlyList<ChannelBotRuntimeNyxIdServiceSelector> RuntimeSelectors { get; }

    /// <summary>
    /// Plans the immutable handoff to restricted Key provisioning. It never creates a Key.
    /// No request state survives this call except the explicit verified handoff.
    /// </summary>
    public sealed class ChannelRegistrationExplicitAuthorizationPlanner(
        ChannelRegistrationAuthorizationPlanner planner,
        ILogger<ChannelRegistrationExplicitAuthorizationPlanner> logger)
    {
        public async Task<ChannelRegistrationExplicitAuthorizationResult> PlanAsync(
            ChannelRelayRegistrationRequest request,
            VerifiedChannelRegistrationOwner registrationOwner,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(registrationOwner);
            if (request.ServiceSelection.AuthorizationMode != ChannelRegistrationAuthorizationMode.ExplicitServiceAllowlist)
                return new(null, "channel_authorization_contract_invalid");
            try
            {
                var verified = await planner.VerifySelectionAsync(new(
                    request.AccessToken, registrationOwner,
                    request.ServiceSelection.ServiceIds, []), ct);
                if (verified.Selection is null)
                    return new(null, verified.ErrorCode);
                // Only explicitly selected existing services enter the grant. Platform and
                // provider slug never imply another service or a connection to create.
                var planned = await planner.PlanAsync(verified.Selection, [], ct);
                if (!planned.Succeeded)
                    return new(null, planned.ErrorCode);
                return new(new VerifiedChannelRegistrationExplicitAuthorization(
                    planned.Plan!, BuildRuntimeSelectors(verified.Selection)), string.Empty);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning("Explicit channel authorization planning failed: code={FailureCode}, type={FailureType}",
                    "nyxid_scope_plan_unavailable", ex.GetType().Name);
                return new(null, "nyxid_scope_plan_unavailable");
            }
        }

        private static IReadOnlyList<ChannelBotRuntimeNyxIdServiceSelector> BuildRuntimeSelectors(
            VerifiedChannelRegistrationServiceSelection selection)
        {
            var selectedSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var selectors = new List<ChannelBotRuntimeNyxIdServiceSelector>();
            foreach (var service in selection.RegistrationServices)
            {
                var serviceSlug = service.Slug.Trim();
                if (string.IsNullOrWhiteSpace(serviceSlug) || !selectedSlugs.Add(serviceSlug))
                    continue;

                selectors.Add(new ChannelBotRuntimeNyxIdServiceSelector
                {
                    ServiceSlug = serviceSlug,
                });
            }

            return selectors;
        }
    }
}

internal static class ChannelRegistrationLocalMirrorRuntimeConfig
{
    public static ChannelBotRuntimeConfig? Build(
        ChannelBotRuntimeConfig? runtimeConfig,
        string? defaultSkillName,
        VerifiedChannelRegistrationExplicitAuthorization? authorization)
    {
        var config = runtimeConfig?.Clone();
        var normalizedDefaultSkillName = defaultSkillName?.Trim();
        if (config is null && !string.IsNullOrWhiteSpace(normalizedDefaultSkillName))
        {
            config = new ChannelBotRuntimeConfig
            {
                DefaultSkill = new ChannelBotRuntimeDefaultSkillConfig
                {
                    Name = normalizedDefaultSkillName,
                },
                CredentialSourceMode = ChannelBotRuntimeCredentialSourceMode.RegistrationAgentKey,
                ToolSetRefs = { ToolSetNames.ChannelReplyDefault },
            };
        }

        if (authorization is null)
            return config;

        if (config is null && authorization.RuntimeSelectors.Count == 0)
            return null;

        config ??= new ChannelBotRuntimeConfig();
        config.NyxidServiceSelectors.Clear();
        config.NyxidServiceSelectors.AddRange(authorization.RuntimeSelectors.Select(static selector => selector.Clone()));
        if (config.CredentialSourceMode == ChannelBotRuntimeCredentialSourceMode.Unspecified)
            config.CredentialSourceMode = ChannelBotRuntimeCredentialSourceMode.RegistrationAgentKey;
        return config;
    }
}
