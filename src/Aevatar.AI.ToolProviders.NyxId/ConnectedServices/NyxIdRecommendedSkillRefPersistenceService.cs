using System.Text.Json;

namespace Aevatar.AI.ToolProviders.NyxId.ConnectedServices;

public sealed class NyxIdRecommendedSkillRefPersistenceService
{
    private readonly NyxIdApiClient _client;

    public NyxIdRecommendedSkillRefPersistenceService(NyxIdApiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<IReadOnlyList<NyxIdRecommendedSkillRef>> PersistRecommendedSkillRefsAsync(
        string serverToken,
        NyxIdServiceInstance instance,
        IReadOnlyList<NyxIdRecommendedSkillRef> refs,
        CancellationToken ct)
    {
        if (refs.Count == 0)
            return [];

        var currentResponse = await _client.GetServiceAsync(
            serverToken,
            instance.UserServiceId,
            ct).ConfigureAwait(false);
        if (IsErrorResponse(currentResponse))
            return [];

        var currentRefs = ParseRecommendedSkillRefs(currentResponse);
        var mergedRefs = MergeRefs(currentRefs, refs);
        var body = JsonSerializer.Serialize(new
        {
            recommended_skill_refs = mergedRefs.Select(ToJsonContract).ToArray(),
        });

        var updateResponse = await _client.UpdateServiceAsync(
            serverToken,
            instance.UserServiceId,
            body,
            ct).ConfigureAwait(false);
        return IsErrorResponse(updateResponse) ? [] : mergedRefs;
    }

    private static IReadOnlyList<NyxIdRecommendedSkillRef> ParseRecommendedSkillRefs(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = UnwrapServiceNode(document.RootElement);
        if (!root.TryGetProperty("recommended_skill_refs", out var refsElement) ||
            refsElement.ValueKind == JsonValueKind.Null)
        {
            return [];
        }
        if (refsElement.ValueKind != JsonValueKind.Array)
            return [];

        var refs = new List<NyxIdRecommendedSkillRef>();
        foreach (var refElement in refsElement.EnumerateArray())
        {
            var skillRef = ParseRecommendedSkillRef(refElement);
            if (skillRef is not null)
                refs.Add(skillRef);
        }

        return refs;
    }

    private static JsonElement UnwrapServiceNode(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return root;
        foreach (var property in new[] { "key", "service", "data" })
        {
            if (root.TryGetProperty(property, out var nested) && nested.ValueKind == JsonValueKind.Object)
                return nested;
        }

        return root;
    }

    private static NyxIdRecommendedSkillRef? ParseRecommendedSkillRef(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;
        if (!TryParseRecommendedSkillSource(ReadString(item, "source") ?? ReadString(item, "provider"), out var source))
            return null;
        var skillId = ReadString(item, "skill_id") ?? ReadString(item, "id") ?? ReadString(item, "guid");
        var literalVersion = ReadString(item, "literal_version") ?? ReadString(item, "version");
        var manifestDigest = ReadString(item, "manifest_digest") ?? ReadString(item, "digest") ?? ReadString(item, "skill_hash");
        if (string.IsNullOrWhiteSpace(skillId) ||
            string.IsNullOrWhiteSpace(literalVersion) ||
            string.IsNullOrWhiteSpace(manifestDigest))
        {
            return null;
        }

        return new NyxIdRecommendedSkillRef
        {
            Source = source,
            SkillId = skillId.Trim(),
            LiteralVersion = literalVersion.Trim(),
            ManifestDigest = manifestDigest.Trim(),
            DisplayName = ReadString(item, "display_name") ?? ReadString(item, "name") ?? string.Empty,
            RecommendationName = ReadString(item, "recommendation_name") ?? ReadString(item, "recommended_name") ?? string.Empty,
            Revision = ReadString(item, "revision") ?? string.Empty,
        };
    }

    private static IReadOnlyList<NyxIdRecommendedSkillRef> MergeRefs(
        IReadOnlyList<NyxIdRecommendedSkillRef> currentRefs,
        IReadOnlyList<NyxIdRecommendedSkillRef> createdRefs)
    {
        var mergedRefs = new List<NyxIdRecommendedSkillRef>(currentRefs.Count + createdRefs.Count);
        foreach (var skillRef in currentRefs.Concat(createdRefs))
        {
            if (mergedRefs.Any(existing => SameRef(existing, skillRef)))
                continue;
            mergedRefs.Add(skillRef.Clone());
        }

        return mergedRefs;
    }

    private static bool SameRef(NyxIdRecommendedSkillRef left, NyxIdRecommendedSkillRef right) =>
        left.Source == right.Source &&
        string.Equals(left.SkillId, right.SkillId, StringComparison.Ordinal) &&
        string.Equals(left.LiteralVersion, right.LiteralVersion, StringComparison.Ordinal) &&
        string.Equals(left.ManifestDigest, right.ManifestDigest, StringComparison.Ordinal);

    private static object ToJsonContract(NyxIdRecommendedSkillRef skillRef) => new
    {
        source = SourceToJson(skillRef.Source),
        skill_id = skillRef.SkillId,
        literal_version = skillRef.LiteralVersion,
        manifest_digest = skillRef.ManifestDigest,
        display_name = skillRef.DisplayName,
        recommendation_name = skillRef.RecommendationName,
        revision = skillRef.Revision,
    };

    private static bool IsErrorResponse(string response)
    {
        try
        {
            using var document = JsonDocument.Parse(response);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("error", out var error) &&
                   error.ValueKind is not (JsonValueKind.False or JsonValueKind.Null);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string SourceToJson(NyxIdRecommendedSkillSource source) => source switch
    {
        NyxIdRecommendedSkillSource.Ornn => "ornn",
        _ => string.Empty,
    };

    private static bool TryParseRecommendedSkillSource(string? value, out NyxIdRecommendedSkillSource source)
    {
        source = value switch
        {
            "ornn" => NyxIdRecommendedSkillSource.Ornn,
            _ => NyxIdRecommendedSkillSource.Unspecified,
        };
        return source != NyxIdRecommendedSkillSource.Unspecified;
    }

    private static string? ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
