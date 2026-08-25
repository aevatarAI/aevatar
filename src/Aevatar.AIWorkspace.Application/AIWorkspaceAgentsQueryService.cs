using Aevatar.AIWorkspace.Application.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AIWorkspace.Application;

public sealed class AIWorkspaceAgentsQueryService(
    IAgentProfileCatalogApplicationService catalog,
    ILogger<AIWorkspaceAgentsQueryService>? logger = null)
    : IAIWorkspaceAgentsQueryService
{
    private readonly ILogger<AIWorkspaceAgentsQueryService> _logger =
        logger ?? NullLogger<AIWorkspaceAgentsQueryService>.Instance;

    public async Task<AIWorkspaceQueryResult<AIWorkspaceAgentsView>> QueryAsync(
        string scopeId,
        AIWorkspaceAgentsQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!AIWorkspaceQueryPolicy.IsValidPageSize(query.Take))
            return AIWorkspaceQueryPolicy.InvalidPageSize<AIWorkspaceAgentsView>();

        try
        {
            var ownedTask = ReadAsync(
                AgentProfileOwners.ForScope(scopeId),
                query.OwnedCursor,
                query.Take,
                publishedOnly: false,
                "scope",
                "OWNED_AGENT_PROFILES_UNAVAILABLE",
                ct);
            var systemTask = ReadAsync(
                AgentProfileOwners.ForSystem(),
                query.SystemCursor,
                query.Take,
                publishedOnly: true,
                "system",
                "SYSTEM_AGENT_TEMPLATES_UNAVAILABLE",
                ct);
            await Task.WhenAll(ownedTask, systemTask).ConfigureAwait(false);
            return AIWorkspaceQueryResult<AIWorkspaceAgentsView>.Success(new AIWorkspaceAgentsView(
                "independent_read_models",
                await ownedTask.ConfigureAwait(false),
                await systemTask.ConfigureAwait(false)));
        }
        catch (AgentProfileInvalidCursorException ex)
        {
            return AIWorkspaceQueryResult<AIWorkspaceAgentsView>.Fail(
                AIWorkspaceQueryFailureKind.InvalidCursor,
                "INVALID_CURSOR",
                ex.Message);
        }
    }

    private async Task<AIWorkspaceAgentCollectionView> ReadAsync(
        AgentProfileOwner owner,
        string? cursor,
        int pageSize,
        bool publishedOnly,
        string ownerKind,
        string unavailableCode,
        CancellationToken ct)
    {
        try
        {
            var page = publishedOnly
                ? await catalog.ListPublishedAsync(owner, cursor, pageSize, ct).ConfigureAwait(false)
                : await catalog.ListAsync(owner, cursor, pageSize, ct).ConfigureAwait(false);
            return new AIWorkspaceAgentCollectionView(
                "agent_profile_catalog",
                page.IsMaterialized
                    ? AIWorkspaceSourceAvailability.Available
                    : AIWorkspaceSourceAvailability.NotMaterialized,
                page.Items.Select(ToSummary).ToArray(),
                page.NextCursor,
                page.IsMaterialized ? page.TotalCount : null,
                page.IsMaterialized ? page.AuthorityStateVersion : null,
                page.IsMaterialized ? page.UpdatedAt : null,
                null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (AgentProfileInvalidCursorException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "AI workspace Agent Profile catalog source {OwnerKind} is unavailable.",
                ownerKind);
            return new AIWorkspaceAgentCollectionView(
                "agent_profile_catalog",
                AIWorkspaceSourceAvailability.Unavailable,
                [],
                null,
                null,
                null,
                null,
                new AIWorkspaceSourceErrorView(
                    unavailableCode,
                    "Agent Profile catalog is temporarily unavailable."));
        }
    }

    private static AIWorkspaceAgentSummaryView ToSummary(AgentProfileCatalogEntry item) =>
        new(
            item.ProfileId,
            item.ProfileSlug,
            item.DisplayName,
            item.Purpose,
            item.PublishedRevision,
            Digest(item.SnapshotSha256),
            IsPublished(item),
            ProvisioningStatus(item.Status));

    private static bool IsPublished(AgentProfileCatalogEntry entry) =>
        entry.Status == AgentProfileProvisioningStatus.Active &&
        entry.PublishedRevision > 0 &&
        entry.SnapshotSha256.Length == 32;

    private static string? Digest(ByteString? digest) =>
        digest is null || digest.Length == 0
            ? null
            : Convert.ToHexString(digest.Span).ToLowerInvariant();

    private static string ProvisioningStatus(AgentProfileProvisioningStatus status) => status switch
    {
        AgentProfileProvisioningStatus.Provisioning => "provisioning",
        AgentProfileProvisioningStatus.Active => "active",
        AgentProfileProvisioningStatus.Failed => "failed",
        _ => "unspecified",
    };
}
