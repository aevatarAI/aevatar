using System.Text.Json;
using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Infrastructure.Storage;

internal sealed class RoleCatalogImportParser : IRoleCatalogImportParser
{
    public async Task<IReadOnlyList<StoredRoleDefinition>> ParseCatalogAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseRoles(document.RootElement);
    }

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
}
