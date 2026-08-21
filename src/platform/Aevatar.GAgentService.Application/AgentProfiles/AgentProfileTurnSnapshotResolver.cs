using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgentService.Application.AgentProfiles;

/// <summary>
/// Resolves a published immutable profile only from catalog/execution read models. A genuinely
/// unbound route remains unprofiled; malformed explicit references and stale bindings fail closed.
/// </summary>
public sealed class AgentProfileTurnSnapshotResolver(
    IAgentProfileCatalogQueryPort catalogQueryPort,
    IAgentProfileExecutionQueryPort executionQueryPort,
    ILogger<AgentProfileTurnSnapshotResolver> logger) : IAgentProfileTurnSnapshotResolver
{
    public async Task<AgentProfileTurnSnapshotResolution> ResolveAsync(
        string scopeId,
        string turnIdentity,
        ChatRouteAgentProfileKind profileKind,
        ChatRouteAgentProfileRef? explicitReference,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(scopeId) || string.IsNullOrWhiteSpace(turnIdentity))
            return AgentProfileTurnSnapshotResolution.Failure(
                AgentProfileTurnSnapshotResolutionStatus.ExplicitReferenceInvalid);

        var agentKind = AgentKind(profileKind);
        if (agentKind is null)
            return AgentProfileTurnSnapshotResolution.Failure(
                AgentProfileTurnSnapshotResolutionStatus.ExplicitReferenceInvalid);

        var scopeOwner = AgentProfileOwners.ForScope(scopeId.Trim());
        if (explicitReference is not null)
            return await ResolveExplicitAsync(explicitReference, scopeOwner, agentKind, ct);

        var scopeCatalogRead = await GetCatalogAsync(scopeOwner, ct);
        if (!scopeCatalogRead.Succeeded)
            return AgentProfileTurnSnapshotResolution.Failure(
                AgentProfileTurnSnapshotResolutionStatus.ReadModelUnavailable);
        var scopeCatalog = scopeCatalogRead.Value;
        var scopeBinding = scopeCatalog is null ? null : FindBinding(scopeCatalog, agentKind);
        if (scopeBinding is not null)
        {
            if (scopeBinding.AdmissionCase != AgentProfileDefaultBinding.AdmissionOneofCase.Scope)
                return AgentProfileTurnSnapshotResolution.Failure(
                    AgentProfileTurnSnapshotResolutionStatus.BindingUnavailable);
            return await ResolveBindingAsync(scopeCatalog, scopeBinding.Target, agentKind, ct);
        }

        var systemOwner = AgentProfileOwners.ForSystem();
        var systemCatalogRead = await GetCatalogAsync(systemOwner, ct);
        if (!systemCatalogRead.Succeeded)
            return AgentProfileTurnSnapshotResolution.Failure(
                AgentProfileTurnSnapshotResolutionStatus.ReadModelUnavailable);
        var systemCatalog = systemCatalogRead.Value;
        if (systemCatalog is null)
            return AgentProfileTurnSnapshotResolution.Unprofiled();
        var systemBinding = FindBinding(systemCatalog, agentKind);
        if (systemBinding is null)
            return AgentProfileTurnSnapshotResolution.Unprofiled();
        if (systemBinding.AdmissionCase != AgentProfileDefaultBinding.AdmissionOneofCase.System ||
            systemBinding.System is null ||
            !systemBinding.System.Enabled ||
            !AgentProfilePolicies.IsReviewedRolloutCohort(systemBinding.System.CohortBasisPoints))
        {
            return AgentProfileTurnSnapshotResolution.Failure(
                AgentProfileTurnSnapshotResolutionStatus.BindingUnavailable);
        }
        if (systemBinding.Target is null)
            return AgentProfileTurnSnapshotResolution.Failure(
                AgentProfileTurnSnapshotResolutionStatus.BindingUnavailable);
        var selectedTarget = ComputeCohortBucket(systemBinding.Target, turnIdentity) <
            systemBinding.System.CohortBasisPoints
                ? systemBinding.Target
                : systemBinding.System.PreviousReviewedTarget;
        if (selectedTarget is null)
        {
            return AgentProfileTurnSnapshotResolution.Failure(
                AgentProfileTurnSnapshotResolutionStatus.BindingUnavailable);
        }

        return await ResolveBindingAsync(systemCatalog, selectedTarget, agentKind, ct);
    }

    private async Task<AgentProfileTurnSnapshotResolution> ResolveExplicitAsync(
        ChatRouteAgentProfileRef reference,
        AgentProfileOwner scopeOwner,
        string agentKind,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reference.ProfileSlug))
            return AgentProfileTurnSnapshotResolution.Failure(
                AgentProfileTurnSnapshotResolutionStatus.ExplicitReferenceInvalid);

        var owner = reference.OwnerKind switch
        {
            ChatRouteAgentProfileReferenceOwnerKind.Caller => scopeOwner,
            ChatRouteAgentProfileReferenceOwnerKind.System => AgentProfileOwners.ForSystem(),
            _ => null,
        };
        if (owner is null)
            return AgentProfileTurnSnapshotResolution.Failure(
                AgentProfileTurnSnapshotResolutionStatus.ExplicitReferenceInvalid);

        var catalogRead = await GetCatalogAsync(owner, ct);
        if (!catalogRead.Succeeded)
            return AgentProfileTurnSnapshotResolution.Failure(
                AgentProfileTurnSnapshotResolutionStatus.ReadModelUnavailable);
        var catalog = catalogRead.Value;
        if (catalog is null)
            return AgentProfileTurnSnapshotResolution.Failure(
                AgentProfileTurnSnapshotResolutionStatus.ProfileUnavailable);
        var entries = catalog.Profiles
            .Where(entry => string.Equals(entry.ProfileSlug, reference.ProfileSlug.Trim(), StringComparison.Ordinal))
            .ToArray();
        if (entries.Length != 1)
            return AgentProfileTurnSnapshotResolution.Failure(
                AgentProfileTurnSnapshotResolutionStatus.ProfileUnavailable);

        var entry = entries[0];
        if (entry.Status != AgentProfileProvisioningStatus.Active ||
            entry.PublishedRevision <= 0 || entry.SnapshotSha256.Length != 32)
        {
            return AgentProfileTurnSnapshotResolution.Failure(
                AgentProfileTurnSnapshotResolutionStatus.ProfileNotPublished);
        }

        return await ResolveBindingAsync(catalog, new AgentProfileBindingTarget
        {
            Owner = owner.Clone(),
            ProfileId = entry.ProfileId,
            PublishedRevision = entry.PublishedRevision,
            SnapshotSha256 = entry.SnapshotSha256,
        }, agentKind, ct);
    }

    private async Task<AgentProfileTurnSnapshotResolution> ResolveBindingAsync(
        AgentProfileCatalogSnapshot catalog,
        AgentProfileBindingTarget? target,
        string agentKind,
        CancellationToken ct)
    {
        if (!IsValidTarget(target) || !AgentProfileDeterminism.SameOwner(target!.Owner, catalog.Owner))
            return AgentProfileTurnSnapshotResolution.Failure(
                AgentProfileTurnSnapshotResolutionStatus.BindingUnavailable);

        var entry = catalog.Profiles.SingleOrDefault(profile => profile.ProfileId == target.ProfileId);
        if (entry is null || entry.Status != AgentProfileProvisioningStatus.Active)
            return AgentProfileTurnSnapshotResolution.Failure(
                AgentProfileTurnSnapshotResolutionStatus.ProfileUnavailable);
        if (entry.PublishedRevision != target.PublishedRevision ||
            !entry.SnapshotSha256.Equals(target.SnapshotSha256))
        {
            return AgentProfileTurnSnapshotResolution.Failure(
                AgentProfileTurnSnapshotResolutionStatus.ProfileNotPublished);
        }

        var executionRead = await GetExecutionAsync(target, ct);
        if (!executionRead.Succeeded)
            return AgentProfileTurnSnapshotResolution.Failure(
                AgentProfileTurnSnapshotResolutionStatus.ReadModelUnavailable);
        var execution = executionRead.Value;
        if (execution is null)
            return AgentProfileTurnSnapshotResolution.Failure(
                AgentProfileTurnSnapshotResolutionStatus.ProfileUnavailable);
        var profile = execution?.Snapshot?.RuntimeProfile;
        if (profile is null ||
            !AgentProfileDeterminism.SameOwner(execution.Identity.Owner, target.Owner) ||
            !string.Equals(execution.Identity.ProfileId, target.ProfileId, StringComparison.Ordinal) ||
            execution.Snapshot.PublishedRevision != target.PublishedRevision ||
            !execution.Snapshot.SnapshotSha256.Equals(target.SnapshotSha256) ||
            !AgentProfileSnapshotCodec.Verify(profile) ||
            !string.Equals(profile.AgentKind, agentKind, StringComparison.Ordinal) ||
            !AgentProfilePolicies.IsSupportedRouteToolSet(agentKind, profile.RouteToolSetRef))
        {
            return AgentProfileTurnSnapshotResolution.Failure(
                AgentProfileTurnSnapshotResolutionStatus.SnapshotDigestMismatch);
        }

        return AgentProfileTurnSnapshotResolution.Selected(profile);
    }

    private async Task<ReadModelResult<AgentProfileCatalogSnapshot>> GetCatalogAsync(
        AgentProfileOwner owner,
        CancellationToken ct)
    {
        try
        {
            return ReadModelResult<AgentProfileCatalogSnapshot>.Success(
                await catalogQueryPort.GetAsync(owner, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Agent Profile catalog read failed for {OwnerCase}.", owner.OwnerCase);
            return ReadModelResult<AgentProfileCatalogSnapshot>.Failed();
        }
    }

    private async Task<ReadModelResult<AgentProfileExecutionSnapshot>> GetExecutionAsync(
        AgentProfileBindingTarget target,
        CancellationToken ct)
    {
        try
        {
            return ReadModelResult<AgentProfileExecutionSnapshot>.Success(
                await executionQueryPort.GetAsync(target, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Agent Profile execution read failed for {ProfileId}/{PublishedRevision}.",
                target.ProfileId,
                target.PublishedRevision);
            return ReadModelResult<AgentProfileExecutionSnapshot>.Failed();
        }
    }

    private static string? AgentKind(ChatRouteAgentProfileKind kind) => kind switch
    {
        ChatRouteAgentProfileKind.WorkspaceChat => AgentProfilePolicies.WorkspaceChatAgentKind,
        ChatRouteAgentProfileKind.ChannelReply => AgentProfilePolicies.ChannelReplyAgentKind,
        ChatRouteAgentProfileKind.NyxidChat => AgentProfilePolicies.NyxIdChatAgentKind,
        _ => null,
    };

    private static AgentProfileDefaultBinding? FindBinding(
        AgentProfileCatalogSnapshot catalog,
        string agentKind) => catalog.DefaultBindings.SingleOrDefault(binding =>
            string.Equals(binding.AgentKind, agentKind, StringComparison.Ordinal));

    private static bool IsValidTarget(AgentProfileBindingTarget? target) =>
        target?.Owner is not null &&
        target.Owner.OwnerCase != AgentProfileOwner.OwnerOneofCase.None &&
        !string.IsNullOrWhiteSpace(target.ProfileId) &&
        target.PublishedRevision > 0 &&
        target.SnapshotSha256.Length == 32;

    private static int ComputeCohortBucket(AgentProfileBindingTarget target, string identity)
    {
        var bytes = Encoding.UTF8.GetBytes(
            $"{target.ProfileId}\0{target.PublishedRevision}\0{identity.Trim()}");
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(bytes, hash);
        return (int)(BinaryPrimitives.ReadUInt32BigEndian(hash) % AgentProfilePolicies.FullCohortBasisPoints);
    }

    private sealed record ReadModelResult<T>(bool Succeeded, T? Value)
        where T : class
    {
        public static ReadModelResult<T> Success(T? value) => new(true, value);
        public static ReadModelResult<T> Failed() => new(false, null);
    }
}
