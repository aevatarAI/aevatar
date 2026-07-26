using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.Mainnet.Host.Api.AgentProfiles;
using Google.Protobuf;
using Microsoft.Extensions.Configuration;

namespace Aevatar.Mainnet.Host.Api.Profiles;

public sealed class MainnetAgentProfileRolloutSelector
{
    private const int FullCohortBasisPoints = 10_000;
    private readonly AgentProfileRolloutReleaseSpec? _releaseSpec;

    internal MainnetAgentProfileRolloutSelector(AgentProfileRolloutReleaseSpec? releaseSpec)
    {
        if (releaseSpec is not null)
            ValidateReleaseSpec(releaseSpec);
        _releaseSpec = releaseSpec?.Clone();
    }

    public static MainnetAgentProfileRolloutSelector Create(
        IConfiguration configuration,
        string contentRootPath)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        var options = configuration.GetSection(NyxIdChatAgentProfileOptions.SectionName)
            .Get<NyxIdChatAgentProfileOptions>() ?? new NyxIdChatAgentProfileOptions();
        ValidateGate(options);
        if (!options.Enabled)
            return new MainnetAgentProfileRolloutSelector(null);

        var releaseSpecPath = Path.IsPathRooted(options.ReleaseSpecPath)
            ? options.ReleaseSpecPath
            : Path.GetFullPath(Path.Combine(contentRootPath, options.ReleaseSpecPath));
        if (!File.Exists(releaseSpecPath))
        {
            throw new InvalidOperationException(
                $"Agent Profile rollout release spec does not exist: {releaseSpecPath}");
        }

        AgentProfileRolloutReleaseSpec releaseSpec;
        try
        {
            releaseSpec = JsonParser.Default.Parse<AgentProfileRolloutReleaseSpec>(
                File.ReadAllText(releaseSpecPath));
        }
        catch (InvalidProtocolBufferException exception)
        {
            throw new InvalidOperationException(
                "Agent Profile rollout release spec must be valid ProtoJSON.",
                exception);
        }

        return new MainnetAgentProfileRolloutSelector(releaseSpec);
    }

    public AgentProfileRolloutReleaseSpec? SelectForNewConversation(string actorId)
    {
        if (_releaseSpec is null || string.IsNullOrWhiteSpace(actorId))
            return null;

        var bucket = ComputeBucket(
            _releaseSpec.ReleaseId,
            _releaseSpec.Stage,
            _releaseSpec.CohortSalt,
            actorId);
        return bucket < _releaseSpec.CohortBasisPoints
            ? _releaseSpec.Clone()
            : null;
    }

    public static int ComputeBucket(
        string releaseId,
        string stage,
        string cohortSalt,
        string actorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentException.ThrowIfNullOrWhiteSpace(cohortSalt);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        var input = Encoding.UTF8.GetBytes(
            $"{releaseId}\0{stage}\0{cohortSalt}\0{actorId}");
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(input, digest);
        return (int)(BinaryPrimitives.ReadUInt32BigEndian(digest) % FullCohortBasisPoints);
    }

    internal static ByteString ComputeAdmissionSha256(AgentProfileRolloutReleaseSpec releaseSpec)
    {
        ArgumentNullException.ThrowIfNull(releaseSpec);
        using var stream = new MemoryStream(releaseSpec.CalculateSize());
        using (var output = new CodedOutputStream(stream, leaveOpen: true))
        {
            output.Deterministic = true;
            releaseSpec.WriteTo(output);
        }

        return ByteString.CopyFrom(SHA256.HashData(stream.ToArray()));
    }

    internal static void ValidateReleaseSpec(AgentProfileRolloutReleaseSpec releaseSpec)
    {
        ArgumentNullException.ThrowIfNull(releaseSpec);
        RequireCanonicalValue(releaseSpec.ReleaseId, nameof(releaseSpec.ReleaseId));
        RequireCanonicalValue(releaseSpec.Stage, nameof(releaseSpec.Stage));
        RequireCanonicalValue(releaseSpec.CohortSalt, nameof(releaseSpec.CohortSalt));

        if (releaseSpec.ProfileReference is null ||
            !string.Equals(
                releaseSpec.ProfileReference.OwnerHandle,
                AgentProfilePolicies.SystemOwnerHandle,
                StringComparison.Ordinal) ||
            !string.Equals(
                releaseSpec.ProfileReference.ProfileSlug,
                NyxIdChatAgentProfileOptions.StableProfileSlug,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "NyxID chat rollout requires the typed Profile reference 'system/nyxid-chat'.");
        }

        if (releaseSpec.ActivationMode is not (
                AgentProfileRolloutActivationMode.Shadow or
                AgentProfileRolloutActivationMode.Enforced))
        {
            throw new InvalidOperationException(
                "Agent Profile rollout activation mode must be SHADOW or ENFORCED.");
        }

        if (releaseSpec.CohortBasisPoints is <= 0 or > FullCohortBasisPoints)
        {
            throw new InvalidOperationException(
                "Agent Profile rollout cohort basis points must be in 1..10000.");
        }

        if (releaseSpec.ExpectedPublishedRevision <= 0 ||
            releaseSpec.ExpectedPublishedSnapshotSha256.Length != SHA256.HashSizeInBytes)
        {
            throw new InvalidOperationException(
                "Agent Profile rollout requires one published revision and 32-byte snapshot digest pin.");
        }

        ValidateExactClosure(releaseSpec.ExpectedExactSkillClosure);
        ValidateRuntimeBounds(releaseSpec.RuntimeBounds);
    }

    private static void ValidateGate(NyxIdChatAgentProfileOptions options)
    {
        if (!options.Enabled)
        {
            if (!string.IsNullOrWhiteSpace(options.ReleaseSpecPath))
            {
                throw new InvalidOperationException(
                    "A disabled NyxID chat Agent Profile rollout cannot configure ReleaseSpecPath.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(options.ReleaseSpecPath))
        {
            throw new InvalidOperationException(
                "An enabled NyxID chat Agent Profile rollout requires ReleaseSpecPath.");
        }
    }

    private static void ValidateExactClosure(
        IEnumerable<ExactOrnnSkillReference> exactClosure)
    {
        var entries = exactClosure.ToArray();
        if (entries.Length is < 1 or > 32)
        {
            throw new InvalidOperationException(
                "Agent Profile rollout exact closure must contain between 1 and 32 skills.");
        }

        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var exactReference in entries)
        {
            if (AgentProfilePolicies.ValidateExactSkillReference(exactReference).Count > 0)
                throw new InvalidOperationException("Agent Profile rollout exact closure is invalid.");
            if (!identities.Add(ExactIdentity(exactReference)))
                throw new InvalidOperationException("Agent Profile rollout exact closure must be unique.");
        }

        if (!entries.Select(ExactIdentity).SequenceEqual(
                entries.Select(ExactIdentity).Order(StringComparer.Ordinal),
                StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "Agent Profile rollout exact closure must use canonical order.");
        }
    }

    private static void ValidateRuntimeBounds(AgentProfileRolloutRuntimeBounds? bounds)
    {
        if (bounds is null ||
            bounds.MaxPlanSteps != 4 ||
            bounds.HandoffTtlSeconds != 900 ||
            bounds.ClassifierTimeoutMs != 600 ||
            bounds.MaxSelectedSkillBytes != 24_576)
        {
            throw new InvalidOperationException(
                "NyxID chat rollout runtime bounds must be 4/900/600/24576.");
        }
    }

    private static void RequireCanonicalValue(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                $"Agent Profile rollout field '{fieldName}' must be canonical.");
        }
    }

    private static string ExactIdentity(ExactOrnnSkillReference reference) =>
        $"{reference.SkillGuid}\0{reference.LiteralVersion}\0{reference.ExpectedName}\0{reference.ExpectedPublisherId}";
}
