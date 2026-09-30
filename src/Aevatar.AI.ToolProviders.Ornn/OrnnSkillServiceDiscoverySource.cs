using System.Text.Json;
using Aevatar.AI.Abstractions.Skills;
using Aevatar.AI.ToolProviders.NyxId;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.ToolProviders.Ornn;

// External JSON is decoded only at this adapter boundary. The policy consumes
// protobuf values and never sees raw error bodies or arbitrary files. Credentials
// pass through the service as invocation parameters only.
public sealed class OrnnSkillServiceDiscoverySource(
    OrnnSkillClient skills,
    NyxIdApiClient nyx,
    OrnnOptions options,
    ILogger<OrnnSkillServiceDiscoverySource> logger) : ISkillServiceDiscoverySource
{
    public async Task<SkillServiceDiscoveryInput> ReadAsync(
        string accessToken, string skillName, CancellationToken ct = default)
    {
        using var timeout = new CancellationTokenSource(options.PerCallTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            using var detail = Parse(await nyx.ProxyRequestAsync(
                accessToken, options.NyxIdSlug,
                $"/api/v1/skills/{Uri.EscapeDataString(skillName)}", "GET",
                null, null, linked.Token));
            var data = detail.RootElement.GetProperty("data");
            var guid = RequiredString(data, "guid");
            if (guid is "." or ".." || RequiredString(data, "name") != skillName)
                throw new SkillServiceDiscoveryException();
            var package = await skills.GetSkillJsonAsync(accessToken, guid, linked.Token);
            if (package?.Name != skillName ||
                package.Files is null || !package.Files.TryGetValue("SKILL.md", out var instructions) ||
                instructions is null || instructions.Length > 200_000)
                throw new SkillServiceDiscoveryException();

            using var catalog = Parse(await nyx.ListCatalogAsync(accessToken, linked.Token, includeAll: true));
            var inventory = NyxIdApiAccessResponseParser.ParseUserServices(
                await nyx.ListUserServicesAsync(accessToken, linked.Token));
            if (!inventory.Succeeded)
                throw new SkillServiceDiscoveryException();

            var result = new SkillServiceDiscoveryInput
            {
                SkillName = skillName,
                Description = OptionalString(data, "description"),
                Instructions = instructions,
                LinkedServiceSlug = OptionalString(data, "nyxidServiceSlug").Trim(),
            };
            var slugs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in catalog.RootElement.GetProperty("entries").EnumerateArray())
            {
                var candidate = new SkillServiceCatalogEntry
                {
                    Slug = RequiredString(entry, "slug"),
                    Name = RequiredString(entry, "name"),
                };
                if (!slugs.Add(candidate.Slug))
                    throw new SkillServiceDiscoveryException();
                if (entry.TryGetProperty("recommended_skills", out var recommended) &&
                    recommended.ValueKind != JsonValueKind.Null)
                    foreach (var name in recommended.EnumerateArray())
                        candidate.RecommendedSkillNames.Add(name.GetString()
                            ?? throw new SkillServiceDiscoveryException());
                result.Catalog.Add(candidate);
            }
            foreach (var service in inventory.Value!.Services)
            {
                var source = service.CredentialSource;
                result.Instances.Add(new SkillServiceInstance
                {
                    UserServiceId = service.Id,
                    Slug = service.Slug,
                    Label = FirstLabel(service.Label, service.CatalogServiceName, service.Slug),
                    Active = service.IsActive,
                    AccountAccessAllowed = source.Kind == NyxIdUserServiceCredentialSourceKind.Personal ||
                        source.Kind == NyxIdUserServiceCredentialSourceKind.Organization && source.Allowed,
                    CredentialSource = source.Kind switch
                    {
                        NyxIdUserServiceCredentialSourceKind.Personal => SkillServiceCredentialSource.Personal,
                        NyxIdUserServiceCredentialSourceKind.Organization => SkillServiceCredentialSource.Organization,
                        _ => SkillServiceCredentialSource.Unspecified,
                    },
                    OrganizationName = source.OrganizationName ?? string.Empty,
                });
            }
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Do not log upstream messages, credential material, or private instructions.
            logger.LogWarning("Skill service discovery failed ({FailureType})", ex.GetType().Name);
            throw new SkillServiceDiscoveryException();
        }
    }

    private static JsonDocument Parse(string response)
    {
        var document = JsonDocument.Parse(response);
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            (!document.RootElement.TryGetProperty("error", out var error) ||
             error.ValueKind is JsonValueKind.Null or JsonValueKind.False))
            return document;
        document.Dispose();
        throw new SkillServiceDiscoveryException();
    }

    private static string RequiredString(JsonElement row, string property)
    {
        var value = row.GetProperty(property).GetString();
        return !string.IsNullOrWhiteSpace(value) && value == value.Trim()
            ? value
            : throw new SkillServiceDiscoveryException();
    }

    private static string OptionalString(JsonElement row, string property) =>
        !row.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null
            ? string.Empty
            : value.GetString() ?? string.Empty;

    private static string FirstLabel(string? label, string? catalogName, string slug) =>
        !string.IsNullOrWhiteSpace(label) ? label.Trim()
            : !string.IsNullOrWhiteSpace(catalogName) ? catalogName.Trim() : slug;
}
