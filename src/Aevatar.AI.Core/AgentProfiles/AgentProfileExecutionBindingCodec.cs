using System.Security.Cryptography;
using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Prompting;
using Google.Protobuf;

namespace Aevatar.AI.Core.AgentProfiles;

public static class AgentProfileExecutionBindingCodec
{
    private const int Sha256Length = 32;

    public static AgentProfileExecutionBinding Seal(AgentProfileExecutionBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!binding.DeterministicBindingSha256.IsEmpty)
            throw new ArgumentException("The execution binding digest must be empty before sealing.", nameof(binding));

        var validationError = GetExecutionBindingValidationError(binding);
        if (validationError is not null)
            throw new ArgumentException(validationError, nameof(binding));

        var sealedBinding = binding.Clone();
        sealedBinding.DeterministicBindingSha256 = ByteString.CopyFrom(
            SHA256.HashData(SerializeWithoutDigest(sealedBinding)));
        return sealedBinding;
    }

    public static bool Verify(AgentProfileExecutionBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (binding.DeterministicBindingSha256.Length != Sha256Length ||
            GetExecutionBindingValidationError(binding) is not null)
        {
            return false;
        }

        var expected = SHA256.HashData(SerializeWithoutDigest(binding));
        return CryptographicOperations.FixedTimeEquals(
            expected,
            binding.DeterministicBindingSha256.Span);
    }

    public static bool ByteEquivalent(
        AgentProfileExecutionBinding left,
        AgentProfileExecutionBinding right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return SerializeDeterministically(left).AsSpan()
            .SequenceEqual(SerializeDeterministically(right));
    }

    private static byte[] SerializeWithoutDigest(AgentProfileExecutionBinding binding)
    {
        var hashInput = binding.Clone();
        hashInput.DeterministicBindingSha256 = ByteString.Empty;
        return SerializeDeterministically(hashInput);
    }

    private static string? GetExecutionBindingValidationError(AgentProfileExecutionBinding binding)
    {
        if (!HasCompleteSourceProvenance(binding.Source))
            return "The execution binding source provenance is incomplete.";

        if (!HasCompleteAdmissionProvenance(binding.Admission))
            return "The execution binding rollout admission provenance is incomplete.";

        if (!HasValidEffectivePolicies(binding))
            return "The execution binding recovery policy must be within the effective maximum policy.";

        if (Encoding.UTF8.GetByteCount(binding.ProfileInstructions) >
            AgentProfileExecutionBindingLimits.ProfileInstructionsMaxUtf8Bytes)
        {
            return "The execution binding profile instructions exceed the authoritative UTF-8 limit.";
        }

        if (!HasCompleteRuntimeBounds(binding.RuntimeBounds))
            return "The execution binding runtime bounds are incomplete.";

        var memberValidationError = GetExecutionMemberValidationError(binding);
        if (memberValidationError is not null)
            return memberValidationError;

        var aggregatePromptBytes = (long)Encoding.UTF8.GetByteCount(binding.ProfileInstructions) +
            binding.Members.Sum(static member => (long)Encoding.UTF8.GetByteCount(member.InstructionBody));
        if (aggregatePromptBytes >
            AgentProfileExecutionBindingLimits.RawAuthoritativeAggregateContentMaxUtf8Bytes)
        {
            return "The execution binding raw aggregate profile prompt content exceeds the authoritative UTF-8 limit.";
        }

        var profileLayer = ProfilePromptLayerRenderer.Render(
            binding.ProfileInstructions,
            binding.Members
                .Where(static member =>
                    member.ActivationMode == AgentProfileExecutionMemberActivationMode.Always)
                .Select(static member => member.InstructionBody)
                .ToArray());
        return profileLayer.ActualUtf8Bytes >
            AgentProfileExecutionBindingLimits.MaterializedProfileLayerMaxUtf8Bytes
            ? "The execution binding materialized profile layer exceeds the authoritative UTF-8 limit."
            : null;
    }

    private static bool HasCompleteSourceProvenance(AgentProfileExecutionSourceProvenance? source) =>
        source is not null &&
        !string.IsNullOrWhiteSpace(source.ProfileId) &&
        source.StateVersion > 0 &&
        source.PublishedRevision > 0 &&
        source.PublishedSnapshotSha256.Length == Sha256Length;

    private static bool HasCompleteAdmissionProvenance(AgentProfileExecutionAdmissionProvenance? admission) =>
        admission is not null &&
        IsBoundedCanonicalIdentifier(admission.RolloutRelease) &&
        IsBoundedCanonicalIdentifier(admission.RolloutStage) &&
        !string.IsNullOrWhiteSpace(admission.RouteToolSetRef) &&
        admission.AdmissionSha256.Length == Sha256Length &&
        admission.ActivationMode is AgentProfileActivationMode.Shadow or AgentProfileActivationMode.Enforced;

    private static bool IsBoundedCanonicalIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !char.IsWhiteSpace(value[0]) &&
        !char.IsWhiteSpace(value[^1]) &&
        !value.Any(char.IsControl) &&
        Encoding.UTF8.GetByteCount(value) <= AgentProfileExecutionBindingLimits.CanonicalIdentifierMaxUtf8Bytes;

    private static bool HasValidEffectivePolicies(AgentProfileExecutionBinding binding) =>
        binding.EffectiveMaximumToolPolicy is not null &&
        binding.EffectiveRecoveryToolPolicy is not null &&
        IsPolicySubset(binding.EffectiveRecoveryToolPolicy, binding.EffectiveMaximumToolPolicy);

    private static bool HasCompleteRuntimeBounds(AgentProfileExecutionRuntimeBounds? bounds) =>
        bounds is not null &&
        bounds.MaxPlanSteps > 0 &&
        bounds.HandoffTtlSeconds > 0 &&
        bounds.ClassifierTimeoutMs > 0 &&
        bounds.MaxSelectedSkillBytes > 0;

    private static string? GetExecutionMemberValidationError(AgentProfileExecutionBinding binding)
    {
        var maximumPolicy = binding.EffectiveMaximumToolPolicy;
        var bounds = binding.RuntimeBounds;

        var intents = new HashSet<string>(StringComparer.Ordinal);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var defaultMemberCount = 0;
        foreach (var member in binding.Members)
        {
            if (member.ActivationMode is not (
                    AgentProfileExecutionMemberActivationMode.Always or
                    AgentProfileExecutionMemberActivationMode.Routed or
                    AgentProfileExecutionMemberActivationMode.DefaultForUnmatchedTurn))
            {
                return "The execution binding member activation mode is invalid.";
            }

            if (member.ActivationMode == AgentProfileExecutionMemberActivationMode.Always)
            {
                if (!HasEmptyRoutingPolicy(member))
                    return "The execution binding always member cannot carry routing policy fields.";
            }
            else
            {
                if (!HasValidRoutingPolicy(member, maximumPolicy, intents))
                    return "The execution binding member routing policy is invalid.";

                if (!HasUniqueAliases(member, aliases))
                    return "The execution binding member aliases must be non-empty and unique.";
            }

            if (member.ActivationMode ==
                    AgentProfileExecutionMemberActivationMode.DefaultForUnmatchedTurn &&
                ++defaultMemberCount > 1)
            {
                return "The execution binding can contain only one default-for-unmatched-turn member.";
            }

            if (!HasCompleteSkillProvenance(member.SkillProvenance))
                return "The execution binding sealed skill provenance is incomplete.";

            var instructionBodyMaxBytes = member.ActivationMode ==
                    AgentProfileExecutionMemberActivationMode.Always
                ? AgentProfileExecutionBindingLimits.RawAuthoritativeAggregateContentMaxUtf8Bytes
                : bounds.MaxSelectedSkillBytes;
            if (!HasValidInstructionBody(member, instructionBodyMaxBytes))
                return "The sealed instruction body digest is invalid.";
        }

        return null;
    }

    private static bool HasEmptyRoutingPolicy(AgentProfileExecutionMember member) =>
        string.IsNullOrEmpty(member.IntentId) &&
        string.IsNullOrEmpty(member.RoutingDescription) &&
        member.ExplicitTriggerAliases.Count == 0 &&
        member.TaskToolPolicy is null &&
        member.SideEffectClass == AgentProfileSideEffectClass.Unspecified;

    private static bool HasValidRoutingPolicy(
        AgentProfileExecutionMember member,
        AgentProfileToolPolicy maximumPolicy,
        HashSet<string> intents) =>
        !string.IsNullOrWhiteSpace(member.IntentId) &&
        !string.IsNullOrWhiteSpace(member.RoutingDescription) &&
        intents.Add(member.IntentId) &&
        member.TaskToolPolicy is not null &&
        IsPolicySubset(member.TaskToolPolicy, maximumPolicy) &&
        member.SideEffectClass is
            AgentProfileSideEffectClass.ReadOnly or
            AgentProfileSideEffectClass.ExternalHandoff or
            AgentProfileSideEffectClass.ServiceCall or
            AgentProfileSideEffectClass.Maintenance;

    private static bool HasUniqueAliases(
        AgentProfileExecutionMember member,
        HashSet<string> aliases)
    {
        foreach (var alias in member.ExplicitTriggerAliases)
        {
            var canonicalAlias = alias.Trim();
            if (canonicalAlias.Length == 0 ||
                !string.Equals(alias, canonicalAlias, StringComparison.Ordinal) ||
                !aliases.Add(canonicalAlias))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasCompleteSkillProvenance(AgentProfileExecutionSkillProvenance? provenance) =>
        provenance?.ExactSkillRef is not null &&
        !string.IsNullOrWhiteSpace(provenance.ExactSkillRef.Guid) &&
        !string.IsNullOrWhiteSpace(provenance.ExactSkillRef.LiteralVersion) &&
        !string.IsNullOrWhiteSpace(provenance.ExpectedSkillName) &&
        !string.IsNullOrWhiteSpace(provenance.ExpectedPublisherId) &&
        string.Equals(provenance.ExpectedSkillName, provenance.CanonicalSkillName, StringComparison.Ordinal) &&
        string.Equals(provenance.ExpectedPublisherId, provenance.PublisherId, StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(provenance.UpstreamSkillHash) &&
        provenance.SourceSealedSkillSha256.Length == Sha256Length;

    private static bool HasValidInstructionBody(
        AgentProfileExecutionMember member,
        int maxUtf8Bytes) =>
        !string.IsNullOrWhiteSpace(member.InstructionBody) &&
        Encoding.UTF8.GetByteCount(member.InstructionBody) <= maxUtf8Bytes &&
        member.InstructionBodySha256.Length == Sha256Length &&
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(member.InstructionBody)),
            member.InstructionBodySha256.Span);

    private static bool IsPolicySubset(AgentProfileToolPolicy subset, AgentProfileToolPolicy maximum)
    {
        var maximumNames = maximum.ToolNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var maximumSetRefs = maximum.ToolSetRefs.ToHashSet(StringComparer.Ordinal);
        return subset.ToolNames.All(maximumNames.Contains) &&
               subset.ToolSetRefs.All(maximumSetRefs.Contains);
    }

    private static byte[] SerializeDeterministically(IMessage message)
    {
        using var stream = new MemoryStream(message.CalculateSize());
        using (var output = new CodedOutputStream(stream, leaveOpen: true))
        {
            output.Deterministic = true;
            message.WriteTo(output);
            output.Flush();
        }

        return stream.ToArray();
    }
}
