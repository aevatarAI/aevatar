using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

internal static class ProjectionScopeStatusActivationSealPolicy
{
    private static readonly ProjectionScopeStatusActorRole[] RequiredRoles =
    [
        ProjectionScopeStatusActorRole.Source,
        ProjectionScopeStatusActorRole.LegacyWriter,
        ProjectionScopeStatusActorRole.TerminalWriter,
    ];

    public static bool TryCreate(
        IRuntimeActorStateSchemaContextReader? contextReader,
        ProjectionScopeStatusActorRole role,
        string actorId,
        string expectedAgentKind,
        out ProjectionScopeStatusActorSeal seal)
    {
        seal = null!;
        var context = contextReader?.Current;
        if (context == null ||
            role == ProjectionScopeStatusActorRole.Unspecified ||
            string.IsNullOrWhiteSpace(actorId) ||
            string.IsNullOrWhiteSpace(expectedAgentKind) ||
            !string.Equals(context.AgentKind, expectedAgentKind, StringComparison.Ordinal))
        {
            return false;
        }

        var receipts = context.AdoptionReceipts
            .Where(IsExactReceipt)
            .ToArray();
        if (receipts.Length != 1 || context.StateSchemaVersion < receipts[0].StateSchemaVersion)
            return false;

        seal = new ProjectionScopeStatusActorSeal
        {
            Role = role,
            ActorId = actorId,
            AgentKind = context.AgentKind,
            AdoptionReceipt = receipts[0].Clone(),
        };
        return true;
    }

    public static bool IsExactReceipt(RuntimeActorStateSchemaAdoptionReceipt? receipt) =>
        receipt != null &&
        receipt.StateSchemaVersion > 0 &&
        receipt.RequiredCapability == RuntimeFleetCapability.ProjectionScopeStatusTerminalV3 &&
        string.Equals(
            receipt.RequiredContractId,
            RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealV1,
            StringComparison.Ordinal) &&
        receipt.RequiredContractVersion ==
            RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealReaderVersion &&
        receipt.EvidenceStatus == RuntimeFleetCapabilityGateStatus.Open &&
        receipt.CapabilityEpoch > 0 &&
        receipt.AuthorityStateVersion > 0 &&
        receipt.MembershipEpoch > 0 &&
        string.Equals(
            receipt.AuthorityActorId,
            RuntimeFleetCapabilityAuthorityIdentity.ActorId,
            StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(receipt.MembershipDigest) &&
        !string.IsNullOrWhiteSpace(receipt.DeploymentRevision) &&
        receipt.AdoptedAt != null &&
        string.IsNullOrWhiteSpace(receipt.QuiescenceTransitionId);

    public static bool IsExactSeal(
        ProjectionScopeStatusActorSeal? seal,
        ProjectionScopeStatusActorRole role,
        string actorId,
        string agentKind) =>
        seal != null &&
        seal.Role == role &&
        string.Equals(seal.ActorId, actorId, StringComparison.Ordinal) &&
        string.Equals(seal.AgentKind, agentKind, StringComparison.Ordinal) &&
        IsExactReceipt(seal.AdoptionReceipt);

    public static bool HasAllRequiredSeals(
        IEnumerable<ProjectionScopeStatusActorSeal> seals,
        ProjectionScopeStatusRoutePreparation preparation)
    {
        ArgumentNullException.ThrowIfNull(seals);
        ArgumentNullException.ThrowIfNull(preparation);
        var snapshot = seals.ToArray();
        if (!HasRequiredReceiptSet(snapshot))
            return false;

        return IsExactSeal(
                   Find(snapshot, ProjectionScopeStatusActorRole.Source),
                   ProjectionScopeStatusActorRole.Source,
                   preparation.SourceScopeActorId,
                   preparation.SourceAgentKind) &&
               IsExactSeal(
                   Find(snapshot, ProjectionScopeStatusActorRole.LegacyWriter),
                   ProjectionScopeStatusActorRole.LegacyWriter,
                   preparation.LegacyWriterActorId,
                   preparation.LegacyWriterAgentKind) &&
               IsExactSeal(
                   Find(snapshot, ProjectionScopeStatusActorRole.TerminalWriter),
                   ProjectionScopeStatusActorRole.TerminalWriter,
                   preparation.TerminalWriterActorId,
                   preparation.TerminalWriterAgentKind);
    }

    public static bool HasExactSourceSeal(ProjectionScopeStatusRoutePreparation preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        return preparation.ActivationSeals.Count == 1 &&
               IsExactSeal(
                   preparation.ActivationSeals[0],
                   ProjectionScopeStatusActorRole.Source,
                   preparation.SourceScopeActorId,
                   preparation.SourceAgentKind);
    }

    public static bool IsExpectedWriterSeal(
        ProjectionScopeStatusRoutePreparation preparation,
        ProjectionScopeStatusActorSeal? seal)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        return seal?.Role switch
        {
            ProjectionScopeStatusActorRole.LegacyWriter =>
                IsExactSeal(
                    seal,
                    seal.Role,
                    preparation.LegacyWriterActorId,
                    preparation.LegacyWriterAgentKind),
            ProjectionScopeStatusActorRole.TerminalWriter =>
                IsExactSeal(
                    seal,
                    seal.Role,
                    preparation.TerminalWriterActorId,
                    preparation.TerminalWriterAgentKind),
            _ => false,
        };
    }

    public static bool RouteHasAllRequiredSeals(
        ProjectionScopeStatusRoute? route,
        string sourceActorId,
        string sourceAgentKind,
        string legacyWriterActorId,
        string legacyWriterAgentKind,
        string terminalWriterActorId,
        string terminalWriterAgentKind) =>
        route != null &&
        HasAllRequiredSeals(
            route.ActivationSeals,
            new ProjectionScopeStatusRoutePreparation
            {
                SourceScopeActorId = sourceActorId,
                SourceAgentKind = sourceAgentKind,
                LegacyWriterActorId = legacyWriterActorId,
                LegacyWriterAgentKind = legacyWriterAgentKind,
                TerminalWriterActorId = terminalWriterActorId,
                TerminalWriterAgentKind = terminalWriterAgentKind,
            });

    public static bool RouteHasAllRequiredWriterSeals(
        ProjectionScopeStatusRoute? route,
        string sourceActorId,
        string legacyWriterActorId,
        string terminalWriterActorId,
        ProjectionScopeStatusActorSeal currentWriterSeal)
    {
        ArgumentNullException.ThrowIfNull(currentWriterSeal);
        if (route == null || !HasRequiredReceiptSet(route.ActivationSeals))
            return false;

        var sourceSeal = Find(route.ActivationSeals, ProjectionScopeStatusActorRole.Source);
        var legacySeal = Find(route.ActivationSeals, ProjectionScopeStatusActorRole.LegacyWriter);
        var terminalSeal = Find(route.ActivationSeals, ProjectionScopeStatusActorRole.TerminalWriter);
        if (sourceSeal == null ||
            legacySeal == null ||
            terminalSeal == null ||
            string.IsNullOrWhiteSpace(sourceSeal.AgentKind) ||
            string.IsNullOrWhiteSpace(legacySeal.AgentKind))
        {
            return false;
        }

        return IsExactSeal(
                   sourceSeal,
                   ProjectionScopeStatusActorRole.Source,
                   sourceActorId,
                   sourceSeal.AgentKind) &&
               IsExactSeal(
                   legacySeal,
                   ProjectionScopeStatusActorRole.LegacyWriter,
                   legacyWriterActorId,
                   legacySeal.AgentKind) &&
               IsExactSeal(
                   terminalSeal,
                   ProjectionScopeStatusActorRole.TerminalWriter,
                   terminalWriterActorId,
                   ProjectionScopeStatusGAgent.AgentKind) &&
               currentWriterSeal.Equals(Find(route.ActivationSeals, currentWriterSeal.Role));
    }

    public static bool HasRequiredReceiptSet(
        IEnumerable<ProjectionScopeStatusActorSeal> seals)
    {
        ArgumentNullException.ThrowIfNull(seals);
        var snapshot = seals.ToArray();
        return snapshot.Length == RequiredRoles.Length &&
               snapshot.Select(static seal => seal.Role).Distinct().Count() == RequiredRoles.Length &&
               RequiredRoles.All(role =>
                   IsExactReceipt(Find(snapshot, role)?.AdoptionReceipt));
    }

    public static ProjectionScopeStatusActorSeal? Find(
        IEnumerable<ProjectionScopeStatusActorSeal> seals,
        ProjectionScopeStatusActorRole role) =>
        seals.FirstOrDefault(seal => seal.Role == role);

    public static Task<RuntimeFleetCapabilityAdmissionGrant?> ReadFreshAdmissionAsync(
        IServiceProvider services,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);
        var admissionReader = services.GetService<IRuntimeFleetCapabilityAdmissionReader>();
        var membershipReader = services.GetService<IRuntimeLocalMembershipIdentityReader>();
        if (admissionReader == null || membershipReader == null)
            return Task.FromResult<RuntimeFleetCapabilityAdmissionGrant?>(null);

        return RuntimeFleetCapabilityAdmissionValidation.GetGrantedAdmissionAsync(
            RuntimeFleetCapability.ProjectionScopeStatusTerminalV3,
            RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealV1,
            RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealReaderVersion,
            admissionReader,
            membershipReader,
            services.GetService<TimeProvider>(),
            services.GetService<RuntimeActorStateMigrationAdmissionOptions>(),
            ct);
    }
}
