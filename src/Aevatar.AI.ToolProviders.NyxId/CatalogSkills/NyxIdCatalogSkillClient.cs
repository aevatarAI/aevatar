using System.Text.Json;
using Aevatar.GAgentService.Abstractions.CatalogSkills;

namespace Aevatar.AI.ToolProviders.NyxId.CatalogSkills;

public sealed record NyxIdCatalogSkillDescriptor(string Id, string Slug, string Name, string? Description);

/// <summary>Adapts NyxID's exact catalog identity and revision-conditional curation contracts.</summary>
public sealed class NyxIdCatalogSkillClient(NyxIdApiClient client)
{
    public async Task<NyxIdCatalogSkillDescriptor> ReadCatalogAsync(string token, string catalogServiceId, CancellationToken ct = default)
    {
        var response = await client.ReadCatalogMetadataAsync(token, catalogServiceId, ct).ConfigureAwait(false);
        using var document = Parse(response, CatalogRecommendedSkillUpdateStage.ReadContract);
        var root = document.RootElement;
        if (ReadString(root, "resource_type") != "catalog_service" ||
            ReadString(root, "id") != catalogServiceId || ReadString(root, "catalog_service_id") != catalogServiceId)
            throw Invalid(CatalogRecommendedSkillUpdateStage.ReadContract, "Catalog metadata did not prove the requested catalog identity.");
        var slug = RequiredString(root, "slug", CatalogRecommendedSkillUpdateStage.ReadContract);
        var name = RequiredString(root, "name", CatalogRecommendedSkillUpdateStage.ReadContract);
        if (ReadString(root, "catalog_service_slug") != slug)
            throw Invalid(CatalogRecommendedSkillUpdateStage.ReadContract, "Catalog metadata contained inconsistent catalog slugs.");
        return new(catalogServiceId, slug, name, ReadString(root, "description"));
    }

    public async Task<string> ReadOpenApiAsync(string token, string catalogServiceId, CancellationToken ct = default)
    {
        var response = await client.ReadCatalogSkillOpenApiAsync(token, catalogServiceId, ct).ConfigureAwait(false);
        using var document = Parse(response, CatalogRecommendedSkillUpdateStage.ReadContract);
        if (ReadString(document.RootElement, "openapi") is null && ReadString(document.RootElement, "swagger") is null)
            throw Invalid(CatalogRecommendedSkillUpdateStage.ReadContract, "Catalog document is not an OpenAPI contract.");
        return response.Content;
    }

    public async Task<CatalogRecommendedSkillUpdateTarget> ReadRecommendationsAsync(string token, string catalogServiceId, CancellationToken ct = default,
        CatalogRecommendedSkillUpdateStage stage = CatalogRecommendedSkillUpdateStage.ResolveTarget)
    {
        var response = await client.ReadCatalogSkillRecommendationsAsync(token, catalogServiceId, ct).ConfigureAwait(false);
        using var document = Parse(response, stage);
        return ParseRecommendations(document.RootElement, catalogServiceId, stage);
    }

    public async Task<CatalogRecommendedSkillUpdateTarget> ReplaceRecommendationsAsync(
        string token, string catalogServiceId, long baseRevision, string operationId,
        IEnumerable<CatalogRecommendedSkillReference> recommendations, CancellationToken ct = default)
    {
        if (baseRevision < 0 || !Guid.TryParse(operationId, out _))
            throw new ArgumentException("A nonnegative catalog revision and UUID operation ID are required.");
        var body = JsonSerializer.Serialize(new
        {
            base_revision = baseRevision,
            request_id = operationId,
            recommended_skill_refs = recommendations.Select(ToContract).ToArray(),
        });
        var response = await client.WriteCatalogSkillRecommendationsAsync(token, catalogServiceId, body, ct).ConfigureAwait(false);
        using var document = Parse(response, CatalogRecommendedSkillUpdateStage.PersistReference);
        return ParseRecommendations(document.RootElement, catalogServiceId, CatalogRecommendedSkillUpdateStage.PersistReference);
    }

    private static object ToContract(CatalogRecommendedSkillReference reference) => new
    {
        source = reference.Source, skill_id = reference.SkillId, name = reference.Name,
        version = reference.Version, sha256 = reference.ManifestDigest,
        dependencies = reference.Dependencies.Select(dependency => new
        {
            source = dependency.Source, skill_id = dependency.SkillId, name = dependency.Name,
            version = dependency.Version, sha256 = dependency.ManifestDigest,
        }).ToArray(),
    };

    private static CatalogRecommendedSkillUpdateTarget ParseRecommendations(JsonElement root, string catalogServiceId, CatalogRecommendedSkillUpdateStage stage)
    {
        if (ReadString(root, "service_id") != catalogServiceId ||
            !root.TryGetProperty("skills_revision", out var revision) || revision.ValueKind != JsonValueKind.Number || !revision.TryGetInt64(out var value) || value < 0)
            throw Invalid(stage, "Catalog recommendation identity or revision is missing.");
        var target = new CatalogRecommendedSkillUpdateTarget { CatalogSkillsRevision = value };
        if (!root.TryGetProperty("recommended_skill_refs", out var references))
            throw Invalid(stage, "Catalog recommendation references are missing.");
        if (references.ValueKind == JsonValueKind.Null)
            return target;
        if (references.ValueKind != JsonValueKind.Array)
            throw Invalid(stage, "Catalog recommendations have an invalid shape.");
        foreach (var item in references.EnumerateArray())
        {
            var reference = new CatalogRecommendedSkillReference
            {
                Source = RequiredString(item, "source", stage), SkillId = RequiredString(item, "skill_id", stage),
                Name = RequiredString(item, "name", stage), Version = RequiredString(item, "version", stage),
                ManifestDigest = RequiredString(item, "sha256", stage),
            };
            if (!IsDigest(reference.ManifestDigest))
                throw Invalid(stage, "Catalog reference digest is invalid.");
            if (item.TryGetProperty("dependencies", out var dependencies))
            {
                if (dependencies.ValueKind != JsonValueKind.Array)
                    throw Invalid(stage, "Catalog dependency pins have an invalid shape.");
                foreach (var dependency in dependencies.EnumerateArray())
                    reference.Dependencies.Add(new CatalogRecommendedSkillDependency
                    {
                        Source = RequiredString(dependency, "source", stage), SkillId = RequiredString(dependency, "skill_id", stage),
                        Name = RequiredString(dependency, "name", stage), Version = RequiredString(dependency, "version", stage),
                        ManifestDigest = RequiredString(dependency, "sha256", stage),
                    });
            }
            if (target.Recommendations.Any(existing => existing.Source == reference.Source && existing.SkillId == reference.SkillId))
                throw Invalid(stage, "Catalog contains duplicate recommendation identities.");
            target.Recommendations.Add(reference);
        }
        return target;
    }

    public static bool IsDigest(string? digest) =>
        digest is { Length: 64 } && digest.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static JsonDocument Parse(NyxIdProxyTextResponse response, CatalogRecommendedSkillUpdateStage stage)
    {
        if (!response.Succeeded)
        {
            var code = response.HttpStatus switch
            {
                401 or 403 => CatalogRecommendedSkillUpdateErrorCode.Forbidden,
                404 => CatalogRecommendedSkillUpdateErrorCode.TargetNotFound,
                409 => CatalogRecommendedSkillUpdateErrorCode.ReferenceConflict,
                _ => stage == CatalogRecommendedSkillUpdateStage.PersistReference
                    ? CatalogRecommendedSkillUpdateErrorCode.ReferenceFailed : CatalogRecommendedSkillUpdateErrorCode.ContractUnavailable,
            };
            throw new CatalogRecommendedSkillUpdateException(code, stage, "NyxID catalog request failed.", response.HttpStatus);
        }
        try
        {
            var document = JsonDocument.Parse(response.Content);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw Invalid(stage, "NyxID catalog response is not an object.");
            }
            return document;
        }
        catch (JsonException)
        {
            throw Invalid(stage, "NyxID catalog response is not valid JSON.");
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;

    private static string RequiredString(JsonElement root, string name, CatalogRecommendedSkillUpdateStage stage) =>
        ReadString(root, name) is { } value && !string.IsNullOrWhiteSpace(value) && value == value.Trim() ? value
            : throw Invalid(stage, "NyxID catalog response is missing a valid required field.");

    private static CatalogRecommendedSkillUpdateException Invalid(CatalogRecommendedSkillUpdateStage stage, string message) =>
        new(stage is CatalogRecommendedSkillUpdateStage.PersistReference or CatalogRecommendedSkillUpdateStage.VerifyReference
            ? CatalogRecommendedSkillUpdateErrorCode.ReferenceInvalid : CatalogRecommendedSkillUpdateErrorCode.ContractInvalid, stage, message);
}
