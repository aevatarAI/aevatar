using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Infrastructure.Storage;

internal static class RoleCatalogJsonSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static async Task<IReadOnlyList<StoredRoleDefinition>> ReadCatalogAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseRoles(document.RootElement);
    }

    public static async Task WriteCatalogAsync(
        Stream stream,
        IReadOnlyList<StoredRoleDefinition> roles,
        CancellationToken cancellationToken)
    {
        var payload = new RoleJsonDocument
        {
            Roles = roles
                .Select(ToRoleJsonEntry)
                .ToList(),
        };

        await JsonSerializer.SerializeAsync(stream, payload, JsonOptions, cancellationToken);
    }

    public static async Task<ParsedRoleDraft> ReadDraftAsync(
        Stream stream,
        DateTimeOffset fallbackUpdatedAtUtc,
        CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var updatedAtUtc = TryGetPropertyIgnoreCase(root, "updatedAtUtc", out var updatedAtNode) &&
                           updatedAtNode.ValueKind == JsonValueKind.String &&
                           DateTimeOffset.TryParse(updatedAtNode.GetString(), out var parsedUpdatedAt)
            ? parsedUpdatedAt
            : fallbackUpdatedAtUtc;

        var draftNode = TryGetPropertyIgnoreCase(root, "role", out var roleNode) ? roleNode : root;
        var draft = draftNode.ValueKind == JsonValueKind.Object ? ParseRole(draftNode, null) : null;
        return new ParsedRoleDraft(updatedAtUtc, draft);
    }

    public static async Task WriteDraftAsync(
        Stream stream,
        StoredRoleDefinition? draft,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        var payload = new RoleDraftJsonDocument
        {
            UpdatedAtUtc = updatedAtUtc,
            Role = draft is null ? new RoleJsonEntry() : ToRoleJsonEntry(draft),
        };

        await JsonSerializer.SerializeAsync(stream, payload, JsonOptions, cancellationToken);
    }

    internal sealed record ParsedRoleDraft(
        DateTimeOffset UpdatedAtUtc,
        StoredRoleDefinition? Draft);

    private static RoleJsonEntry ToRoleJsonEntry(StoredRoleDefinition role) =>
        new()
        {
            Id = role.Id,
            Name = role.Name,
            SystemPrompt = role.SystemPrompt,
            Provider = role.Provider,
            Model = role.Model,
            Connectors = role.Connectors.ToArray(),
        };

    private static IReadOnlyList<StoredRoleDefinition> ParseRoles(JsonElement root)
    {
        if (!TryGetPropertyIgnoreCase(root, "roles", out var rolesNode))
        {
            return [];
        }

        var results = new List<StoredRoleDefinition>();
        if (rolesNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in rolesNode.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var role = ParseRole(item, null);
                if (role is not null)
                {
                    results.Add(role);
                }
            }

            return results;
        }

        if (rolesNode.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        if (TryGetPropertyIgnoreCase(rolesNode, "definitions", out var definitionsNode) &&
            definitionsNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in definitionsNode.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var role = ParseRole(item, null);
                if (role is not null)
                {
                    results.Add(role);
                }
            }

            return results;
        }

        foreach (var property in rolesNode.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var role = ParseRole(property.Value, property.Name);
            if (role is not null)
            {
                results.Add(role);
            }
        }

        return results;
    }

    private static StoredRoleDefinition? ParseRole(JsonElement roleNode, string? fallbackId)
    {
        var id = ReadString(roleNode, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            id = fallbackId ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var name = ReadString(roleNode, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            name = id;
        }

        return new StoredRoleDefinition(
            Id: id,
            Name: name,
            SystemPrompt: ReadString(roleNode, "systemPrompt", "system_prompt"),
            Provider: ReadString(roleNode, "provider"),
            Model: ReadString(roleNode, "model"),
            Connectors: ReadStringArray(roleNode, "connectors"));
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string ReadString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (TryGetPropertyIgnoreCase(element, propertyName, out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();
    }

    private sealed class RoleJsonDocument
    {
        [JsonPropertyName("roles")]
        public List<RoleJsonEntry> Roles { get; set; } = [];
    }

    private sealed class RoleDraftJsonDocument
    {
        [JsonPropertyName("updatedAtUtc")]
        public DateTimeOffset UpdatedAtUtc { get; set; }

        [JsonPropertyName("role")]
        public RoleJsonEntry Role { get; set; } = new();
    }

    private sealed class RoleJsonEntry
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("systemPrompt")]
        public string SystemPrompt { get; set; } = string.Empty;

        [JsonPropertyName("provider")]
        public string Provider { get; set; } = string.Empty;

        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("connectors")]
        public string[] Connectors { get; set; } = [];
    }
}
