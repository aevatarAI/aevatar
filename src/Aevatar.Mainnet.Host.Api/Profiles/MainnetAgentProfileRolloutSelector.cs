using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.GAgents.NyxidChat.AgentProfiles;
using Google.Protobuf;
using Microsoft.Extensions.Configuration;

namespace Aevatar.Mainnet.Host.Api.Profiles;

public sealed class MainnetAgentProfileRolloutOptions
{
    public const string SectionName = "Aevatar:AgentProfileRollout:NyxIdChat";
    public bool NewBindingsEnabled { get; set; }
    public int CohortBasisPoints { get; set; }
    public string ReviewedProfilePath { get; set; } = string.Empty;
}

public sealed class MainnetAgentProfileRolloutSelector : INyxIdChatAgentProfileSnapshotSource
{
    private const int FullCohortBasisPoints = 10_000;
    private const string RequiredAgentKind = "nyxid.chat";
    private readonly MainnetAgentProfileRolloutOptions _options;
    private readonly AgentProfileSnapshot? _reviewedProfile;

    private MainnetAgentProfileRolloutSelector(
        MainnetAgentProfileRolloutOptions options,
        AgentProfileSnapshot? reviewedProfile)
    {
        _options = options;
        _reviewedProfile = reviewedProfile;
    }

    public static MainnetAgentProfileRolloutSelector Create(
        IConfiguration configuration,
        string contentRootPath)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        var options = configuration.GetSection(MainnetAgentProfileRolloutOptions.SectionName)
            .Get<MainnetAgentProfileRolloutOptions>() ?? new MainnetAgentProfileRolloutOptions();
        ValidateGate(options);
        if (!options.NewBindingsEnabled)
            return new MainnetAgentProfileRolloutSelector(options, null);

        var profilePath = Path.IsPathRooted(options.ReviewedProfilePath)
            ? options.ReviewedProfilePath
            : Path.GetFullPath(Path.Combine(contentRootPath, options.ReviewedProfilePath));
        if (!File.Exists(profilePath))
            throw new InvalidOperationException($"Reviewed agent profile does not exist: {profilePath}");

        return new MainnetAgentProfileRolloutSelector(
            options,
            ParseAndValidateProfile(File.ReadAllBytes(profilePath)));
    }

    public AgentProfileSnapshot? GetSnapshotForNewConversation(string actorId)
    {
        if (!_options.NewBindingsEnabled || _reviewedProfile is null || string.IsNullOrWhiteSpace(actorId))
            return null;

        var bucket = ComputeBucket(_reviewedProfile.ProfileVersion, actorId);
        return bucket < _options.CohortBasisPoints
            ? _reviewedProfile.Clone()
            : null;
    }

    internal void AddReviewedRouteToolSet(
        ToolSetRegistryOptions registryOptions,
        string legacyRouteToolSetName)
    {
        ArgumentNullException.ThrowIfNull(registryOptions);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyRouteToolSetName);
        if (_reviewedProfile is null)
            return;

        var includeNames = new[] { legacyRouteToolSetName.Trim() }
            .Concat(_reviewedProfile.MaximumToolPolicy.ToolSetRefs)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (includeNames.Contains(_reviewedProfile.RouteToolSetRef, StringComparer.Ordinal))
            throw new InvalidOperationException("Reviewed profile route tool set cannot include itself.");

        registryOptions.AddToolSet(
            _reviewedProfile.RouteToolSetRef,
            includeNames,
            [],
            "Host-owned reviewed agent-profile route tool composition.");
    }

    public static int ComputeBucket(string profileVersion, string actorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        var input = Encoding.UTF8.GetBytes($"{profileVersion}\0{actorId}");
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(input, digest);
        return (int)(BinaryPrimitives.ReadUInt32BigEndian(digest) % FullCohortBasisPoints);
    }

    private static void ValidateGate(MainnetAgentProfileRolloutOptions options)
    {
        if (!options.NewBindingsEnabled)
        {
            if (options.CohortBasisPoints != 0)
                throw new InvalidOperationException("A disabled agent-profile rollout must have CohortBasisPoints=0.");
            return;
        }

        if (options.CohortBasisPoints is <= 0 or > FullCohortBasisPoints)
            throw new InvalidOperationException("An enabled agent-profile rollout requires CohortBasisPoints in 1..10000.");
        if (string.IsNullOrWhiteSpace(options.ReviewedProfilePath))
            throw new InvalidOperationException("An enabled agent-profile rollout requires one reviewed immutable profile path.");
    }

    private static AgentProfileSnapshot ParseAndValidateProfile(byte[] protoJson)
    {
        RejectPlaceholderValues(protoJson);

        AgentProfileSnapshot profile;
        try
        {
            profile = JsonParser.Default.Parse<AgentProfileSnapshot>(Encoding.UTF8.GetString(protoJson));
        }
        catch (InvalidProtocolBufferException exception)
        {
            throw new InvalidOperationException("Reviewed profile must be valid AgentProfileSnapshot ProtoJSON.", exception);
        }

        ValidateProfile(profile);
        if (!AgentProfileSnapshotCodec.Verify(profile))
            throw new InvalidOperationException("Reviewed profile policy hash must match the typed profile contents.");
        return profile.Clone();
    }

    private static void ValidateProfile(AgentProfileSnapshot profile)
    {
        RequireNonEmpty(profile.ProfileId, "profileId");
        RequireNonEmpty(profile.ProfileVersion, "profileVersion");
        if (!string.Equals(profile.AgentKind, RequiredAgentKind, StringComparison.Ordinal))
            throw new InvalidOperationException($"Reviewed profile agentKind must be '{RequiredAgentKind}'.");
        RequireNonEmpty(profile.PolicyRevision, "policyRevision");
        if (profile.ActivationMode is not (AgentProfileActivationMode.Shadow or AgentProfileActivationMode.Enforced))
            throw new InvalidOperationException("Reviewed profile activationMode must be SHADOW or ENFORCED.");

        _ = ValidateExactReference(profile.SkillsetProvenance, "skillsetProvenance");
        RequireNonEmpty(profile.RouteToolSetRef, "routeToolSetRef");
        var maximumPolicy = ValidatePolicy(profile.MaximumToolPolicy, "maximumToolPolicy");
        var recoveryPolicy = ValidatePolicy(profile.RecoveryToolPolicy, "recoveryToolPolicy");
        RequireSubset(recoveryPolicy.ToolNames, maximumPolicy.ToolNames, "recovery tool names");
        RequireSubset(recoveryPolicy.ToolSetRefs, maximumPolicy.ToolSetRefs, "recovery tool-set refs");

        RequireInteger(profile.MaxPlanSteps, 4, "maxPlanSteps");
        RequireInteger(profile.HandoffTtlSeconds, 900, "handoffTtlSeconds");
        RequireInteger(profile.ClassifierTimeoutMs, 600, "classifierTimeoutMs");
        RequireInteger(profile.ExactSkillFetchTimeoutMs, 1500, "exactSkillFetchTimeoutMs");
        RequireInteger(profile.MaxSelectedSkillBytes, 24576, "maxSelectedSkillBytes");

        if (profile.Members.Count != 4)
            throw new InvalidOperationException("Reviewed profile must contain exactly four members.");

        var intentIds = new HashSet<string>(StringComparer.Ordinal);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var expectedSkillNames = new HashSet<string>(StringComparer.Ordinal);
        var exactSkillReferences = new HashSet<string>(StringComparer.Ordinal);
        var reviewedPublisherIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in profile.Members)
        {
            if (!intentIds.Add(RequireNonEmpty(member.IntentId, "intentId")))
                throw new InvalidOperationException("Reviewed profile intent IDs must be unique.");
            RequireNonEmpty(member.RoutingDescription, "routingDescription");
            if (!expectedSkillNames.Add(RequireNonEmpty(member.ExpectedSkillName, "expectedSkillName")))
                throw new InvalidOperationException("Reviewed profile expected skill names must be unique.");

            var reviewedPublisherId = RequireNonEmpty(member.ReviewedPublisherId, "reviewedPublisherId");
            if (!Guid.TryParseExact(reviewedPublisherId, "D", out var parsedPublisherId) ||
                !string.Equals(parsedPublisherId.ToString("D"), reviewedPublisherId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Reviewed profile reviewedPublisherId must be a canonical GUID.");
            }
            reviewedPublisherIds.Add(reviewedPublisherId);

            var exactSkillReference = ValidateExactReference(member.SkillRef, "skillRef");
            if (!exactSkillReferences.Add(exactSkillReference))
                throw new InvalidOperationException("Reviewed profile exact skill references must be unique.");

            var taskPolicy = ValidatePolicy(member.TaskToolPolicy, "taskToolPolicy");
            RequireSubset(taskPolicy.ToolNames, maximumPolicy.ToolNames, "member tool names");
            RequireSubset(taskPolicy.ToolSetRefs, maximumPolicy.ToolSetRefs, "member tool-set refs");
            if (member.SideEffectClass is not (
                    AgentProfileSideEffectClass.ReadOnly or
                    AgentProfileSideEffectClass.ExternalHandoff or
                    AgentProfileSideEffectClass.ServiceCall or
                    AgentProfileSideEffectClass.Maintenance))
            {
                throw new InvalidOperationException("Reviewed profile sideEffectClass must be typed.");
            }

            foreach (var alias in member.ExplicitTriggerAliases)
            {
                RequireNonEmpty(alias, "explicitTriggerAliases");
                if (!aliases.Add(alias))
                    throw new InvalidOperationException("Reviewed profile trigger aliases must be unique.");
            }
        }

        if (reviewedPublisherIds.Count != 1)
            throw new InvalidOperationException("Reviewed profile members must share one reviewedPublisherId.");
    }

    private static (HashSet<string> ToolNames, HashSet<string> ToolSetRefs) ValidatePolicy(
        AgentProfileToolPolicy? policy,
        string fieldName)
    {
        if (policy is null)
            throw new InvalidOperationException($"Reviewed profile field '{fieldName}' must be present.");

        var toolNames = ValidateUniqueNames(
            policy.ToolNames,
            $"{fieldName}.toolNames",
            StringComparer.OrdinalIgnoreCase);
        var toolSetRefs = ValidateUniqueNames(
            policy.ToolSetRefs,
            $"{fieldName}.toolSetRefs",
            StringComparer.Ordinal);
        if (toolNames.Count == 0)
            throw new InvalidOperationException("Reviewed profile policies must not have an empty toolNames set.");

        var forbidden = toolNames.Where(static name =>
            name is "nyxid_services" or "nyxid_proxy" or "nyxid_external_keys" ||
            name.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("api_key", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (forbidden.Length > 0)
        {
            throw new InvalidOperationException(
                $"Reviewed profile policy contains forbidden broad or credential-bearing names: {string.Join(", ", forbidden)}");
        }

        return (toolNames, toolSetRefs);
    }

    private static HashSet<string> ValidateUniqueNames(
        IEnumerable<string> values,
        string fieldName,
        StringComparer comparer)
    {
        var names = new HashSet<string>(comparer);
        foreach (var value in values)
        {
            RequireNonEmpty(value, fieldName);
            if (!names.Add(value))
                throw new InvalidOperationException($"Reviewed profile field '{fieldName}' must contain unique values.");
        }
        return names;
    }

    private static string ValidateExactReference(IMessage? reference, string fieldName)
    {
        var (guid, version) = reference switch
        {
            ExactRemoteSkillRef skill => (skill.Guid, skill.LiteralVersion),
            ExactRemoteSkillsetRef skillset => (skillset.Guid, skillset.LiteralVersion),
            _ => throw new InvalidOperationException($"Reviewed profile field '{fieldName}' must be present."),
        };

        guid = RequireNonEmpty(guid, $"{fieldName}.guid");
        if (!Guid.TryParseExact(guid, "D", out var parsedGuid) ||
            !string.Equals(parsedGuid.ToString("D"), guid, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Reviewed profile exact references require canonical GUIDs.");
        }

        version = RequireNonEmpty(version, $"{fieldName}.literalVersion");
        if (version.Split('.') is not [var major, var minor] ||
            !int.TryParse(major, out var majorValue) ||
            !int.TryParse(minor, out var minorValue) ||
            !string.Equals(majorValue.ToString(), major, StringComparison.Ordinal) ||
            !string.Equals(minorValue.ToString(), minor, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Reviewed profile exact references require literal major.minor versions.");
        }
        return $"{guid}@{version}";
    }

    private static void RejectPlaceholderValues(byte[] protoJson)
    {
        try
        {
            using var document = JsonDocument.Parse(protoJson, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
                MaxDepth = 32,
            });
            RejectPlaceholderValues(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Reviewed profile must be valid JSON.", exception);
        }
    }

    private static void RejectPlaceholderValues(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString() ?? string.Empty;
            if (value.Equals("latest", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("placeholder", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("todo", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Reviewed profile contains an unresolved or mutable value.");
            }
            return;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
                RejectPlaceholderValues(property.Value);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                RejectPlaceholderValues(item);
        }
    }

    private static string RequireNonEmpty(string? value, string fieldName) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Reviewed profile field '{fieldName}' must be a non-empty string.");

    private static void RequireSubset(HashSet<string> subset, HashSet<string> superset, string label)
    {
        if (!subset.IsSubsetOf(superset))
            throw new InvalidOperationException($"Reviewed profile {label} must be a maximum-policy subset.");
    }

    private static void RequireInteger(int actual, int expected, string fieldName)
    {
        if (actual != expected)
            throw new InvalidOperationException($"Reviewed profile field '{fieldName}' must equal {expected}.");
    }
}
