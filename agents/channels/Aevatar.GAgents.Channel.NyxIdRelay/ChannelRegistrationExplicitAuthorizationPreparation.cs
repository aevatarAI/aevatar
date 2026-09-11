using Aevatar.GAgents.Channel.Runtime;
using Microsoft.Extensions.Logging;
using static Aevatar.GAgents.Channel.NyxIdRelay.VerifiedChannelRegistrationServiceSelection;
using static Aevatar.GAgents.Channel.NyxIdRelay.VerifiedChannelRegistrationServiceSelection.VerifiedChannelRegistrationAuthorizationPlan;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

public sealed record ChannelRegistrationDependencies(
    IReadOnlyList<string> RequiredServiceIds,
    bool RequiresBotProxyConnection);

/// <summary>
/// Reads exact service dependencies from authoritative Skill/Workflow/LLM/Channel configuration.
/// Names, provider slugs and inventory membership are not dependency evidence.
/// </summary>
public interface IChannelRegistrationDependencyResolver
{
    Task<ChannelRegistrationDependencies> ResolveAsync(
        string scopeId, string platform, string defaultSkillName, CancellationToken ct);
}

public sealed class ChannelRegistrationConfiguredDependencyResolver : IChannelRegistrationDependencyResolver
{
    public Task<ChannelRegistrationDependencies> ResolveAsync(
        string scopeId, string platform, string defaultSkillName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Current registration configuration declares no exact Skill/Workflow/LLM service IDs.
        // Lark's connection is separately resolved from this registration's creation provenance.
        return Task.FromResult(new ChannelRegistrationDependencies([], platform == NyxLarkProvisioningService.PlatformId));
    }
}

public sealed record ChannelRegistrationExplicitAuthorizationResult(
    VerifiedChannelRegistrationExplicitAuthorization? Preparation,
    string ErrorCode)
{
    public bool Succeeded => Preparation is not null && string.IsNullOrEmpty(ErrorCode);
}

public sealed class VerifiedChannelRegistrationExplicitAuthorization
{
    // Only the nested preparation flow can mint this final Key-provisioning handoff.
    private VerifiedChannelRegistrationExplicitAuthorization(
        VerifiedChannelRegistrationAuthorizationPlan plan,
        VerifiedChannelBotServiceConnection? connection)
    {
        Plan = plan;
        Connection = connection;
    }

    public VerifiedChannelRegistrationAuthorizationPlan Plan { get; }
    public VerifiedChannelBotServiceConnection? Connection { get; }

    /// <summary>
    /// Prepares the immutable handoff to restricted Key provisioning. It never creates a Key.
    /// No request state survives this call except the explicit verified handoff.
    /// </summary>
    public sealed class ChannelRegistrationExplicitAuthorizationPreparation(
        ChannelRegistrationAuthorizationPlanner planner,
        IChannelRegistrationDependencyResolver dependencies,
        IChannelRegistrationBotConnectionPort connections,
        ILogger<ChannelRegistrationExplicitAuthorizationPreparation> logger)
    {
        private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);

        public async Task<ChannelRegistrationExplicitAuthorizationResult> PrepareAsync(
            NyxChannelBotProvisioningRequest request,
            string registrationId,
            VerifiedChannelRegistrationOwner registrationOwner,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(registrationOwner);
            if (request.ServiceSelection.AuthorizationMode != ChannelRegistrationAuthorizationMode.ExplicitServiceAllowlist)
                return new(null, "channel_authorization_contract_invalid");

            VerifiedChannelBotServiceConnection? connection = null;
            var completed = false;
            var failureCode = "nyxid_scope_plan_unavailable";
            try
            {
                // This stage reads business selection and owner, but MUST NOT call scope-plan.
                var verified = await planner.VerifySelectionAsync(new(
                    request.AccessToken, registrationOwner,
                    request.ServiceSelection.ServiceIds, []), ct);
                if (verified.Selection is null)
                    return new(null, verified.ErrorCode);

                if (request.Platform == NyxLarkProvisioningService.PlatformId)
                {
                    failureCode = "channel_service_connection_unavailable";
                    if (request.Lark is null)
                        return new(null, failureCode);
                    connection = await connections.CreateLarkAsync(
                        verified.Selection, registrationId, request.NyxProviderSlug, request.Lark, ct);
                    if (connection is null)
                        return new(null, failureCode);
                }

                failureCode = "nyxid_scope_plan_unavailable";
                var required = await dependencies.ResolveAsync(
                    request.ScopeId, request.Platform, request.DefaultSkillName, ct);
                if (required.RequiresBotProxyConnection && connection is null)
                {
                    // Relay-only Telegram has no proxy dependency. A configuration requiring one
                    // must supply a future authoritative bot-identity contract; token/slug guesses
                    // cannot establish that identity with the current public NyxID surface.
                    return new(null, "channel_service_connection_unavailable");
                }
                var serviceIds = required.RequiredServiceIds
                    .Concat(connection is null ? [] : new[] { connection.UserServiceId })
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                var planned = await planner.PlanAsync(verified.Selection, serviceIds, ct);
                if (!planned.Succeeded)
                    return new(null, planned.ErrorCode);

                completed = true;
                return new(new VerifiedChannelRegistrationExplicitAuthorization(planned.Plan!, connection), string.Empty);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning("Explicit channel authorization preparation failed: code={FailureCode}, type={FailureType}",
                    failureCode, ex.GetType().Name);
                return new(null, failureCode);
            }
            finally
            {
                if (!completed && connection is not null)
                    await CleanupConnectionAsync(request.AccessToken, connection);
            }
        }

        /// <summary>
        /// Call only after route/bot/Key/Vault compensation, and only when mirror acceptance is
        /// known not to have occurred. The caller cancellation must never suppress compensation.
        /// </summary>
        public async Task CleanupConnectionAsync(string accessToken, VerifiedChannelBotServiceConnection connection)
        {
            using var cleanup = new CancellationTokenSource(CleanupTimeout);
            try
            {
                await connections.DeleteOwnedAsync(accessToken, connection, cleanup.Token);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    "Exclusive channel connection cleanup failed: code={FailureCode}, userServiceId={UserServiceId}, registrationId={RegistrationId}, type={FailureType}",
                    "channel_service_connection_cleanup_failed", connection.UserServiceId, connection.RegistrationId, ex.GetType().Name);
            }
        }
    }
}
