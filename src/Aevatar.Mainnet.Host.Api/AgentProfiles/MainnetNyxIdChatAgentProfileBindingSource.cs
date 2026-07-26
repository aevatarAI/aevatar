using System.Security.Cryptography;
using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.GAgents.NyxidChat.AgentProfiles;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Mainnet.Host.Api.Profiles;
using Google.Protobuf;
using RuntimeAgentProfileToolPolicy = Aevatar.AI.Abstractions.AgentProfileToolPolicy;
using SourceAgentProfileToolPolicy = Aevatar.GAgentService.Abstractions.AgentProfiles.AgentProfileToolPolicy;

namespace Aevatar.Mainnet.Host.Api.AgentProfiles;

public sealed class MainnetNyxIdChatAgentProfileBindingSource
    : INyxIdChatAgentProfileBindingSource
{
    private readonly MainnetAgentProfileRolloutSelector _selector;
    private readonly IAgentProfileNamespaceQueryPort _namespaceQuery;
    private readonly IAgentProfileExecutionSnapshotQueryPort _executionQuery;

    public MainnetNyxIdChatAgentProfileBindingSource(
        MainnetAgentProfileRolloutSelector selector,
        IAgentProfileNamespaceQueryPort namespaceQuery,
        IAgentProfileExecutionSnapshotQueryPort executionQuery)
    {
        _selector = selector ?? throw new ArgumentNullException(nameof(selector));
        _namespaceQuery = namespaceQuery ?? throw new ArgumentNullException(nameof(namespaceQuery));
        _executionQuery = executionQuery ?? throw new ArgumentNullException(nameof(executionQuery));
    }

    public async Task<NyxIdChatAgentProfileBindingResult> ResolveForNewConversationAsync(
        string actorId,
        string routeToolSetName,
        CancellationToken ct = default)
    {
        var releaseSpec = _selector.SelectForNewConversation(actorId);
        if (releaseSpec is null)
            return Result(NyxIdChatAgentProfileBindingStatus.NotSelected);

        if (string.IsNullOrWhiteSpace(routeToolSetName) ||
            !string.Equals(routeToolSetName, routeToolSetName.Trim(), StringComparison.Ordinal))
        {
            return Result(NyxIdChatAgentProfileBindingStatus.AdmissionMismatch);
        }

        var namespaceEntry = await _namespaceQuery.GetByReferenceAsync(
            releaseSpec.ProfileReference,
            ct);
        if (namespaceEntry is null)
            return Result(NyxIdChatAgentProfileBindingStatus.ProfileUnavailable);
        if (!HasExpectedNamespaceIdentity(namespaceEntry, releaseSpec.ProfileReference))
            return Result(NyxIdChatAgentProfileBindingStatus.AdmissionMismatch);

        var execution = await _executionQuery.GetAsync(namespaceEntry.ProfileId, ct);
        if (execution is null)
            return Result(NyxIdChatAgentProfileBindingStatus.ProfileUnavailable);
        if (!ReplicasAgree(namespaceEntry, execution))
            return Result(NyxIdChatAgentProfileBindingStatus.ProfileUnavailable);

        var snapshot = execution.Snapshot;
        if (!HasValidAuthoritativeSnapshot(snapshot) ||
            !MatchesAdmissionPins(snapshot, releaseSpec) ||
            !MatchesExactClosure(snapshot, releaseSpec.ExpectedExactSkillClosure))
        {
            return Result(NyxIdChatAgentProfileBindingStatus.AdmissionMismatch);
        }

        try
        {
            var binding = AgentProfileExecutionBindingCodec.Seal(MapBinding(
                execution,
                releaseSpec,
                routeToolSetName));
            return new NyxIdChatAgentProfileBindingResult(
                NyxIdChatAgentProfileBindingStatus.Bound,
                binding);
        }
        catch (ArgumentException)
        {
            return Result(NyxIdChatAgentProfileBindingStatus.AdmissionMismatch);
        }
        catch (InvalidOperationException)
        {
            return Result(NyxIdChatAgentProfileBindingStatus.AdmissionMismatch);
        }
    }

    private static bool HasExpectedNamespaceIdentity(
        AgentProfileNamespaceEntrySnapshot entry,
        AgentProfileReference expectedReference) =>
        entry.AuthorityStateVersion > 0 &&
        entry.Status == AgentProfileProvisioningStatus.Active &&
        !string.IsNullOrWhiteSpace(entry.ProfileId) &&
        entry.Reference.Equals(expectedReference) &&
        entry.PublishedSummary is not null &&
        entry.PublishedSummary.Reference.Equals(expectedReference);

    private static bool ReplicasAgree(
        AgentProfileNamespaceEntrySnapshot namespaceEntry,
        AgentProfileExecutionSnapshot execution)
    {
        var summary = namespaceEntry.PublishedSummary;
        var snapshot = execution.Snapshot;
        return execution.AuthorityStateVersion > 0 &&
               summary is not null &&
               snapshot.Identity is not null &&
               string.Equals(execution.ProfileId, namespaceEntry.ProfileId, StringComparison.Ordinal) &&
               snapshot.Identity.Reference.Equals(namespaceEntry.Reference) &&
               snapshot.Identity.Owner.Equals(namespaceEntry.Owner) &&
               string.Equals(
                   snapshot.Identity.OwningScopeId,
                   namespaceEntry.OwningScopeId,
                   StringComparison.Ordinal) &&
               snapshot.PublishedRevision == summary.PublishedRevision &&
               DigestEquals(snapshot.SnapshotSha256, summary.SnapshotSha256);
    }

    private static bool HasValidAuthoritativeSnapshot(AgentProfilePublishedSnapshot snapshot)
    {
        if (snapshot.PublishedRevision <= 0 ||
            snapshot.SnapshotSha256.Length != SHA256.HashSizeInBytes ||
            AgentProfilePolicies.ValidatePublishedSnapshot(snapshot).Count > 0 ||
            AgentProfilePolicies.ValidatePublishedSnapshotHardLimits(snapshot).Count > 0)
        {
            return false;
        }

        try
        {
            return DigestEquals(
                snapshot.SnapshotSha256,
                AgentProfileDeterminism.ComputeExecutionSnapshotSha256(snapshot));
        }
        catch (AgentProfileContractValidationException)
        {
            return false;
        }
    }

    private static bool MatchesAdmissionPins(
        AgentProfilePublishedSnapshot snapshot,
        AgentProfileRolloutReleaseSpec releaseSpec) =>
        snapshot.Identity is not null &&
        snapshot.Identity.Reference.Equals(releaseSpec.ProfileReference) &&
        snapshot.PublishedRevision == releaseSpec.ExpectedPublishedRevision &&
        DigestEquals(
            snapshot.SnapshotSha256,
            releaseSpec.ExpectedPublishedSnapshotSha256);

    private static bool MatchesExactClosure(
        AgentProfilePublishedSnapshot snapshot,
        IEnumerable<ExactOrnnSkillReference> expectedClosure)
    {
        var expected = expectedClosure
            .Select(ExactIdentity)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actual = snapshot.SkillBindings
            .Where(static binding => binding.Skill?.ExactReference is not null)
            .Select(static binding => ExactIdentity(binding.Skill.ExactReference))
            .Order(StringComparer.Ordinal)
            .ToArray();
        return expected.SequenceEqual(actual, StringComparer.Ordinal);
    }

    private static AgentProfileExecutionBinding MapBinding(
        AgentProfileExecutionSnapshot execution,
        AgentProfileRolloutReleaseSpec releaseSpec,
        string routeToolSetName)
    {
        var snapshot = execution.Snapshot;
        var binding = new AgentProfileExecutionBinding
        {
            Source = new AgentProfileExecutionSourceProvenance
            {
                ProfileId = execution.ProfileId,
                StateVersion = execution.AuthorityStateVersion,
                PublishedRevision = snapshot.PublishedRevision,
                PublishedSnapshotSha256 = snapshot.SnapshotSha256,
            },
            Admission = new AgentProfileExecutionAdmissionProvenance
            {
                RolloutRelease = releaseSpec.ReleaseId,
                RolloutStage = releaseSpec.Stage,
                ActivationMode = MapActivationMode(releaseSpec.ActivationMode),
                RouteToolSetRef = routeToolSetName,
                AdmissionSha256 = MainnetAgentProfileRolloutSelector.ComputeAdmissionSha256(releaseSpec),
            },
            EffectiveMaximumToolPolicy = MapPolicy(snapshot.ToolPolicy),
            EffectiveRecoveryToolPolicy = MapPolicy(snapshot.RecoveryToolPolicy),
            ProfileInstructions = snapshot.Instructions,
            RuntimeBounds = new AgentProfileExecutionRuntimeBounds
            {
                MaxPlanSteps = releaseSpec.RuntimeBounds.MaxPlanSteps,
                HandoffTtlSeconds = releaseSpec.RuntimeBounds.HandoffTtlSeconds,
                ClassifierTimeoutMs = releaseSpec.RuntimeBounds.ClassifierTimeoutMs,
                MaxSelectedSkillBytes = releaseSpec.RuntimeBounds.MaxSelectedSkillBytes,
            },
        };
        binding.Members.Add(snapshot.SkillBindings.Select(MapMember));
        return binding;
    }

    private static AgentProfileExecutionMember MapMember(
        SealedAgentProfileSkillBinding source)
    {
        var isAlways = source.ActivationMode == AgentProfileSkillActivationMode.Always;
        if (source.ActivationMode is not (
                AgentProfileSkillActivationMode.Always or
                AgentProfileSkillActivationMode.Routed or
                AgentProfileSkillActivationMode.DefaultForUnmatchedTurn) ||
            (isAlways ? source.RoutingPolicy is not null : source.RoutingPolicy is null) ||
            source.Skill?.ExactReference is null ||
            source.Skill.Package is null)
        {
            throw new InvalidOperationException("Published runtime Profile member is incomplete.");
        }

        var exactReference = source.Skill.ExactReference;
        var package = source.Skill.Package;
        var member = new AgentProfileExecutionMember
        {
            ActivationMode = MapMemberActivationMode(source.ActivationMode),
            SkillProvenance = new AgentProfileExecutionSkillProvenance
            {
                ExactSkillRef = new ExactRemoteSkillRef
                {
                    Guid = exactReference.SkillGuid,
                    LiteralVersion = exactReference.LiteralVersion,
                },
                ExpectedSkillName = exactReference.ExpectedName,
                ExpectedPublisherId = exactReference.ExpectedPublisherId,
                CanonicalSkillName = package.CanonicalName,
                PublisherId = package.PublisherId,
                UpstreamSkillHash = package.UpstreamSkillHash,
                SourceSealedSkillSha256 = source.Skill.ContentSha256,
            },
            InstructionBody = package.Instructions,
            InstructionBodySha256 = ByteString.CopyFrom(
                SHA256.HashData(Encoding.UTF8.GetBytes(package.Instructions))),
        };
        if (source.RoutingPolicy is { } routingPolicy)
        {
            member.IntentId = routingPolicy.IntentId;
            member.RoutingDescription = routingPolicy.RoutingDescription;
            member.TaskToolPolicy = MapPolicy(routingPolicy.TaskToolPolicy);
            member.SideEffectClass = MapSideEffectClass(routingPolicy.SideEffectClass);
            member.ExplicitTriggerAliases.Add(routingPolicy.ExplicitTriggerAliases);
        }
        return member;
    }

    private static AgentProfileExecutionMemberActivationMode MapMemberActivationMode(
        AgentProfileSkillActivationMode activationMode) =>
        activationMode switch
        {
            AgentProfileSkillActivationMode.Always =>
                AgentProfileExecutionMemberActivationMode.Always,
            AgentProfileSkillActivationMode.Routed =>
                AgentProfileExecutionMemberActivationMode.Routed,
            AgentProfileSkillActivationMode.DefaultForUnmatchedTurn =>
                AgentProfileExecutionMemberActivationMode.DefaultForUnmatchedTurn,
            _ => throw new InvalidOperationException("Profile member activation mode is invalid."),
        };

    private static RuntimeAgentProfileToolPolicy MapPolicy(
        SourceAgentProfileToolPolicy? source)
    {
        if (source?.Mode != AgentProfileToolPolicyMode.ExplicitAllowlist)
            throw new InvalidOperationException("Runtime Profile policies must be explicit allowlists.");

        var policy = new RuntimeAgentProfileToolPolicy();
        policy.ToolNames.Add(source.ToolNames);
        policy.ToolSetRefs.Add(source.ToolSetRefs);
        return policy;
    }

    private static AgentProfileActivationMode MapActivationMode(
        AgentProfileRolloutActivationMode activationMode) =>
        activationMode switch
        {
            AgentProfileRolloutActivationMode.Shadow => AgentProfileActivationMode.Shadow,
            AgentProfileRolloutActivationMode.Enforced => AgentProfileActivationMode.Enforced,
            _ => throw new InvalidOperationException("Rollout activation mode is invalid."),
        };

    private static AgentProfileSideEffectClass MapSideEffectClass(
        AgentProfileSkillSideEffectClass sideEffectClass) =>
        sideEffectClass switch
        {
            AgentProfileSkillSideEffectClass.ReadOnly => AgentProfileSideEffectClass.ReadOnly,
            AgentProfileSkillSideEffectClass.ExternalHandoff => AgentProfileSideEffectClass.ExternalHandoff,
            AgentProfileSkillSideEffectClass.ServiceCall => AgentProfileSideEffectClass.ServiceCall,
            AgentProfileSkillSideEffectClass.Maintenance => AgentProfileSideEffectClass.Maintenance,
            _ => throw new InvalidOperationException("Profile member side-effect class is invalid."),
        };

    private static NyxIdChatAgentProfileBindingResult Result(
        NyxIdChatAgentProfileBindingStatus status) =>
        new(status, null);

    private static bool DigestEquals(ByteString left, ByteString right) =>
        left.Length == SHA256.HashSizeInBytes &&
        right.Length == SHA256.HashSizeInBytes &&
        CryptographicOperations.FixedTimeEquals(left.Span, right.Span);

    private static string ExactIdentity(ExactOrnnSkillReference reference) =>
        $"{reference.SkillGuid}\0{reference.LiteralVersion}\0{reference.ExpectedName}\0{reference.ExpectedPublisherId}";
}
