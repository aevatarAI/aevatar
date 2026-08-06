using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgents.NyxidChat.AgentProfiles;
using Microsoft.Extensions.Logging;

namespace Aevatar.Mainnet.Host.Api.AgentProfiles;

public sealed class MainnetNyxIdChatAgentProfileResolver : INyxIdChatAgentProfileResolver
{
    private readonly IAgentProfileCatalogQueryPort _catalogQueryPort;
    private readonly IAgentProfileExecutionQueryPort _executionQueryPort;
    private readonly ILogger<MainnetNyxIdChatAgentProfileResolver> _logger;

    public MainnetNyxIdChatAgentProfileResolver(
        IAgentProfileCatalogQueryPort catalogQueryPort,
        IAgentProfileExecutionQueryPort executionQueryPort,
        ILogger<MainnetNyxIdChatAgentProfileResolver> logger)
    {
        _catalogQueryPort = catalogQueryPort ?? throw new ArgumentNullException(nameof(catalogQueryPort));
        _executionQueryPort = executionQueryPort ?? throw new ArgumentNullException(nameof(executionQueryPort));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<NyxIdChatAgentProfileResolution> ResolveAsync(
        NyxIdChatAgentProfileSelectionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.ScopeId) ||
            string.IsNullOrWhiteSpace(request.ConversationActorId))
        {
            return NyxIdChatAgentProfileResolution.Failure(
                NyxIdChatAgentProfileResolutionStatus.ExplicitReferenceInvalid);
        }

        var scopeOwner = AgentProfileOwners.ForScope(request.ScopeId);
        if (request.ExplicitReference is not null)
            return await ResolveExplicitAsync(request.ExplicitReference, scopeOwner, ct);

        var scopeCatalog = await GetCatalogAsync(scopeOwner, ct);
        var scopeBinding = FindNyxIdChatBinding(scopeCatalog);
        if (scopeBinding is not null)
        {
            if (scopeBinding.AdmissionCase != AgentProfileDefaultBinding.AdmissionOneofCase.Scope)
            {
                return NyxIdChatAgentProfileResolution.Failure(
                    NyxIdChatAgentProfileResolutionStatus.BindingUnavailable);
            }

            return await ResolveBindingAsync(
                scopeCatalog!,
                scopeBinding.Target,
                NyxIdChatAgentProfileSelectionSource.ScopeDefault,
                ct);
        }

        var systemOwner = AgentProfileOwners.ForSystem();
        var systemCatalog = await GetCatalogAsync(systemOwner, ct);
        var systemBinding = FindNyxIdChatBinding(systemCatalog);
        if (systemBinding is null)
            return NyxIdChatAgentProfileResolution.Unprofiled();
        if (systemBinding.AdmissionCase != AgentProfileDefaultBinding.AdmissionOneofCase.System ||
            systemBinding.System is null)
        {
            return NyxIdChatAgentProfileResolution.Failure(
                NyxIdChatAgentProfileResolutionStatus.BindingUnavailable);
        }

        var admission = systemBinding.System;
        if (!admission.Enabled || admission.CohortBasisPoints is <= 0 or > AgentProfilePolicies.FullCohortBasisPoints)
        {
            return NyxIdChatAgentProfileResolution.Failure(
                NyxIdChatAgentProfileResolutionStatus.BindingUnavailable);
        }
        if (systemBinding.Target is null ||
            ComputeCohortBucket(systemBinding.Target, request.ConversationActorId) >= admission.CohortBasisPoints)
        {
            return systemBinding.Target is null
                ? NyxIdChatAgentProfileResolution.Failure(NyxIdChatAgentProfileResolutionStatus.BindingUnavailable)
                : NyxIdChatAgentProfileResolution.Unprofiled();
        }

        return await ResolveBindingAsync(
            systemCatalog!,
            systemBinding.Target,
            NyxIdChatAgentProfileSelectionSource.SystemDefault,
            ct);
    }

    private async Task<NyxIdChatAgentProfileResolution> ResolveExplicitAsync(
        AgentProfileReference reference,
        AgentProfileOwner scopeOwner,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reference.ProfileSlug))
        {
            return NyxIdChatAgentProfileResolution.Failure(
                NyxIdChatAgentProfileResolutionStatus.ExplicitReferenceInvalid);
        }

        var (owner, source) = reference.OwnerKind switch
        {
            AgentProfileReferenceOwnerKind.Caller => (scopeOwner, NyxIdChatAgentProfileSelectionSource.ExplicitCallerReference),
            AgentProfileReferenceOwnerKind.System => (AgentProfileOwners.ForSystem(), NyxIdChatAgentProfileSelectionSource.ExplicitSystemReference),
            _ => (null, NyxIdChatAgentProfileSelectionSource.None),
        };
        if (owner is null)
        {
            return NyxIdChatAgentProfileResolution.Failure(
                NyxIdChatAgentProfileResolutionStatus.ExplicitReferenceInvalid);
        }

        var catalog = await GetCatalogAsync(owner, ct);
        var entries = catalog?.Profiles
            .Where(entry => string.Equals(entry.ProfileSlug, reference.ProfileSlug, StringComparison.Ordinal))
            .ToArray() ?? [];
        if (entries.Length != 1)
        {
            return NyxIdChatAgentProfileResolution.Failure(
                NyxIdChatAgentProfileResolutionStatus.ProfileUnavailable,
                source);
        }

        var entry = entries[0];
        if (entry.Status != AgentProfileProvisioningStatus.Active ||
            entry.PublishedRevision <= 0 || entry.SnapshotSha256.Length != 32)
        {
            return NyxIdChatAgentProfileResolution.Failure(
                NyxIdChatAgentProfileResolutionStatus.ProfileNotPublished,
                source);
        }

        return await ResolveBindingAsync(
            catalog!,
            new AgentProfileBindingTarget
            {
                Owner = owner.Clone(),
                ProfileId = entry.ProfileId,
                PublishedRevision = entry.PublishedRevision,
                SnapshotSha256 = entry.SnapshotSha256,
            },
            source,
            ct);
    }

    private async Task<NyxIdChatAgentProfileResolution> ResolveBindingAsync(
        AgentProfileCatalogSnapshot catalog,
        AgentProfileBindingTarget? target,
        NyxIdChatAgentProfileSelectionSource source,
        CancellationToken ct)
    {
        if (!IsValidTarget(target) || !AgentProfileDeterminism.SameOwner(target!.Owner, catalog.Owner))
        {
            return NyxIdChatAgentProfileResolution.Failure(
                NyxIdChatAgentProfileResolutionStatus.BindingUnavailable,
                source);
        }

        var entry = catalog.Profiles.SingleOrDefault(profile => profile.ProfileId == target.ProfileId);
        if (entry is null || entry.Status != AgentProfileProvisioningStatus.Active)
        {
            return NyxIdChatAgentProfileResolution.Failure(
                NyxIdChatAgentProfileResolutionStatus.ProfileUnavailable,
                source);
        }
        if (entry.PublishedRevision != target.PublishedRevision ||
            !entry.SnapshotSha256.Equals(target.SnapshotSha256))
        {
            return NyxIdChatAgentProfileResolution.Failure(
                NyxIdChatAgentProfileResolutionStatus.ProfileNotPublished,
                source);
        }

        var execution = await GetExecutionAsync(target, ct);
        if (execution is null)
        {
            return NyxIdChatAgentProfileResolution.Failure(
                NyxIdChatAgentProfileResolutionStatus.ReadModelUnavailable,
                source);
        }
        if (!AgentProfileDeterminism.SameOwner(execution.Identity.Owner, target.Owner) ||
            !string.Equals(execution.Identity.ProfileId, target.ProfileId, StringComparison.Ordinal) ||
            execution.Snapshot.PublishedRevision != target.PublishedRevision ||
            !execution.Snapshot.SnapshotSha256.Equals(target.SnapshotSha256) ||
            execution.Snapshot.RuntimeProfile is null ||
            !AgentProfileSnapshotCodec.Verify(execution.Snapshot.RuntimeProfile) ||
            !AgentProfilePolicies.IsSupportedAgentKind(execution.Snapshot.RuntimeProfile.AgentKind) ||
            !string.Equals(
                execution.Snapshot.RuntimeProfile.RouteToolSetRef,
                AgentProfilePolicies.NyxIdChatRouteToolSet,
                StringComparison.Ordinal))
        {
            return NyxIdChatAgentProfileResolution.Failure(
                NyxIdChatAgentProfileResolutionStatus.SnapshotDigestMismatch,
                source);
        }

        return NyxIdChatAgentProfileResolution.Selected(execution.Snapshot.RuntimeProfile, source);
    }

    private async Task<AgentProfileCatalogSnapshot?> GetCatalogAsync(
        AgentProfileOwner owner,
        CancellationToken ct)
    {
        try
        {
            return await _catalogQueryPort.GetAsync(owner, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Agent Profile catalog query failed for owner {OwnerKind}/{OwnerId}.",
                owner.OwnerCase,
                OwnerId(owner));
            return null;
        }
    }

    private async Task<AgentProfileExecutionSnapshot?> GetExecutionAsync(
        AgentProfileBindingTarget target,
        CancellationToken ct)
    {
        try
        {
            return await _executionQueryPort.GetAsync(target, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Agent Profile execution query failed for profile {ProfileId} revision {PublishedRevision}.",
                target.ProfileId,
                target.PublishedRevision);
            return null;
        }
    }

    private static AgentProfileDefaultBinding? FindNyxIdChatBinding(AgentProfileCatalogSnapshot? catalog) =>
        catalog?.DefaultBindings.SingleOrDefault(binding =>
            string.Equals(binding.AgentKind, AgentProfilePolicies.NyxIdChatAgentKind, StringComparison.Ordinal));

    private static bool IsValidTarget(AgentProfileBindingTarget? target) =>
        target?.Owner is not null &&
        target.Owner.OwnerCase != AgentProfileOwner.OwnerOneofCase.None &&
        !string.IsNullOrWhiteSpace(target.ProfileId) &&
        target.PublishedRevision > 0 &&
        target.SnapshotSha256.Length == 32;

    private static string OwnerId(AgentProfileOwner owner) => owner.OwnerCase switch
    {
        AgentProfileOwner.OwnerOneofCase.Scope => owner.Scope.ScopeId,
        AgentProfileOwner.OwnerOneofCase.System => "system",
        _ => string.Empty,
    };

    private static int ComputeCohortBucket(AgentProfileBindingTarget target, string actorId)
    {
        var bytes = Encoding.UTF8.GetBytes(
            $"{target.ProfileId}\0{target.PublishedRevision}\0{actorId.Trim()}");
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(bytes, hash);
        return (int)(BinaryPrimitives.ReadUInt32BigEndian(hash) % AgentProfilePolicies.FullCohortBasisPoints);
    }
}
