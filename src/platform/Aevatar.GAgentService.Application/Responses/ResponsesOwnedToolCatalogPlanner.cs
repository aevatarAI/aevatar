using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Application.AgentProfiles;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgentService.Application.Responses;

public sealed record ResponsesOwnedToolCatalogPlan(
    AgentTurnToolCatalog Catalog,
    AgentProfileSnapshot? ProfileSnapshot,
    string ResolvedToolSetName,
    ResponsesCommandError? Error,
    AgentTurnToolCatalogProof? ShadowCandidateProof = null)
{
    public bool IsSuccess => Error is null;

    public static ResponsesOwnedToolCatalogPlan Failed(ResponsesCommandError error) =>
        new(AgentTurnToolCatalogFactory.RestrictedEmpty(), null, string.Empty, error);
}

public interface IResponsesOwnedToolCatalogPlanner
{
    Task<ResponsesOwnedToolCatalogPlan> PlanAsync(
        ChatRouteAction? routeAction,
        string scopeId,
        string turnIdentity,
        string userMessage,
        AgentToolExecutionContext toolContext,
        CancellationToken ct = default);
}

/// <summary>
/// Shared pre-classification planner for Responses, Messages and Chat Completions. The selected
/// immutable profile and its final exact catalog are frozen before caller-forwarded declarations
/// are classified.
/// </summary>
public sealed class ResponsesOwnedToolCatalogPlanner(
    IAgentProfileTurnSnapshotResolver? snapshotResolver,
    IAgentProfileTurnToolCatalogPlanner? profilePlanner,
    ILogger<ResponsesOwnedToolCatalogPlanner> logger) : IResponsesOwnedToolCatalogPlanner
{
    public const string PolicyVersion = "agent-turn-tool-catalog/v1";

    public async Task<ResponsesOwnedToolCatalogPlan> PlanAsync(
        ChatRouteAction? routeAction,
        string scopeId,
        string turnIdentity,
        string userMessage,
        AgentToolExecutionContext toolContext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(toolContext);
        var forward = routeAction?.ForwardToModel;
        if (forward is null || forward.ProfileKind == ChatRouteAgentProfileKind.Unspecified)
            return Unprofiled();

        if (snapshotResolver is null)
        {
            return ResponsesOwnedToolCatalogPlan.Failed(new ResponsesCommandError(
                503,
                "agent_profile_read_model_unavailable",
                "The agent profile read model is unavailable."));
        }

        var resolution = await snapshotResolver.ResolveAsync(
            scopeId,
            turnIdentity,
            forward.ProfileKind,
            forward.ProfileRef,
            ct).ConfigureAwait(false);
        if (resolution.Status == AgentProfileTurnSnapshotResolutionStatus.Unprofiled)
            return Unprofiled();
        if (!resolution.IsSelected || resolution.Profile is null)
        {
            return ResponsesOwnedToolCatalogPlan.Failed(new ResponsesCommandError(
                503,
                ProfileFailureCode(resolution.Status),
                "The reviewed agent profile could not be resolved for this turn."));
        }
        if (profilePlanner is null)
        {
            return ResponsesOwnedToolCatalogPlan.Failed(new ResponsesCommandError(
                503,
                "agent_profile_planner_unavailable",
                "The agent profile catalog planner is unavailable."));
        }

        var profile = resolution.Profile.Clone();
        var routeToolSetName = forward.ToolSetRef?.Name?.Trim();
        if (!string.IsNullOrWhiteSpace(routeToolSetName) &&
            !string.Equals(routeToolSetName, profile.RouteToolSetRef, StringComparison.Ordinal))
        {
            return ResponsesOwnedToolCatalogPlan.Failed(new ResponsesCommandError(
                409,
                "agent_profile_route_mismatch",
                "The route tool-set ceiling does not match the pinned agent profile."));
        }

        try
        {
            var preparation = await profilePlanner.PrepareAsync(
                profile,
                turnIdentity,
                userMessage ?? string.Empty,
                [],
                toolContext,
                ct).ConfigureAwait(false);
            if (profile.ActivationMode == AgentProfileActivationMode.Shadow)
            {
                var proof = preparation.ShadowCandidateProof;
                logger.LogInformation(
                    "Agent turn shadow catalog observed without changing model or executor tools. policy={PolicyVersion} profile={ProfileId} revision={PublishedRevision} intent={IntentId} candidateOwned={OwnedCount} candidateSchemaBytes={SchemaBytes} candidateDigest={CatalogDigest}",
                    PolicyVersion,
                    profile.ProfileId,
                    profile.PublishedRevision,
                    preparation.Authority.CandidateRoute?.IntentId ?? string.Empty,
                    proof?.ToolCount ?? 0,
                    proof?.SchemaBytes ?? 0,
                    proof?.CatalogDigest ?? string.Empty);
                return new ResponsesOwnedToolCatalogPlan(
                    AgentTurnToolCatalogFactory.RestrictedEmpty(),
                    profile,
                    profile.RouteToolSetRef,
                    null,
                    proof);
            }

            var materialization = await profilePlanner.MaterializeCommittedAsync(
                profile,
                preparation.Authority,
                toolContext.Credentials.NyxIdAccessToken,
                [],
                toolContext,
                ct).ConfigureAwait(false);
            var catalog = materialization.Catalog;
            logger.LogInformation(
                "Agent turn tool catalog planned. policy={PolicyVersion} profile={ProfileId} revision={PublishedRevision} intent={IntentId} owned={OwnedCount} schemaBytes={SchemaBytes} digest={CatalogDigest}",
                PolicyVersion,
                profile.ProfileId,
                profile.PublishedRevision,
                catalog.SelectedIntentId ?? catalog.CandidateIntentId ?? string.Empty,
                catalog.Proof.ToolCount,
                catalog.Proof.SchemaBytes,
                catalog.Proof.CatalogDigest);
            return new ResponsesOwnedToolCatalogPlan(
                catalog,
                profile,
                profile.RouteToolSetRef,
                null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (AgentTurnToolCatalogException exception)
        {
            logger.LogWarning(
                exception,
                "Agent profile catalog materialization failed closed. code={FailureCode}",
                exception.Failure.Code);
            return ResponsesOwnedToolCatalogPlan.Failed(new ResponsesCommandError(
                422,
                CatalogFailureCode(exception.Failure.Code),
                exception.Failure.Detail));
        }
        catch (AgentToolDiscoveryException exception)
        {
            logger.LogWarning(
                exception,
                "Agent profile tool discovery failed closed. code={FailureCode}",
                exception.Failure.Code);
            return ResponsesOwnedToolCatalogPlan.Failed(new ResponsesCommandError(
                503,
                "agent_tool_discovery_failed",
                exception.Failure.Detail));
        }
    }

    private static ResponsesOwnedToolCatalogPlan Unprofiled() =>
        new(
            AgentTurnToolCatalogFactory.RestrictedEmpty(),
            null,
            string.Empty,
            null);

    private static string ProfileFailureCode(AgentProfileTurnSnapshotResolutionStatus status) => status switch
    {
        AgentProfileTurnSnapshotResolutionStatus.ExplicitReferenceInvalid => "agent_profile_reference_invalid",
        AgentProfileTurnSnapshotResolutionStatus.BindingUnavailable => "agent_profile_binding_unavailable",
        AgentProfileTurnSnapshotResolutionStatus.ProfileUnavailable => "agent_profile_unavailable",
        AgentProfileTurnSnapshotResolutionStatus.ProfileNotPublished => "agent_profile_not_published",
        AgentProfileTurnSnapshotResolutionStatus.ReadModelUnavailable => "agent_profile_read_model_unavailable",
        AgentProfileTurnSnapshotResolutionStatus.SnapshotDigestMismatch => "agent_profile_snapshot_mismatch",
        _ => "agent_profile_unavailable",
    };

    private static string CatalogFailureCode(AgentTurnToolCatalogFailureCode code) => code switch
    {
        AgentTurnToolCatalogFailureCode.CatalogOverBudget => "agent_tool_catalog_over_budget",
        AgentTurnToolCatalogFailureCode.CatalogNeedsDisambiguation => "agent_tool_catalog_needs_disambiguation",
        AgentTurnToolCatalogFailureCode.ToolNameCollision => "agent_tool_name_collision",
        AgentTurnToolCatalogFailureCode.SchemaInvalid => "agent_tool_schema_invalid",
        AgentTurnToolCatalogFailureCode.CatalogProofMismatch => "agent_tool_catalog_proof_mismatch",
        _ => "agent_tool_catalog_invalid",
    };
}
