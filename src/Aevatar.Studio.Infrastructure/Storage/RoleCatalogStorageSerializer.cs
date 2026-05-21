using System.Text.Json;
using Aevatar.GAgents.RoleCatalog;
using Aevatar.Studio.Application.Studio.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Infrastructure.Storage;

internal static class RoleCatalogStorageSerializer
{
    // Refactor (iter22/cluster-001-studio-json-internal-catalog-storage):
    //   Old pattern: Studio role catalog and draft facts were durable JSON documents.
    //   New principle: Durable storage payloads are protobuf facts; JSON is only import fallback.
    public static async Task<IReadOnlyList<StoredRoleDefinition>> ReadCatalogAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var payload = await ReadPayloadAsync(stream, cancellationToken);
        if (!IsJsonPayload(payload))
        {
            var state = RoleCatalogState.Parser.ParseFrom(payload);
            return state.Roles
                .Select(ToStoredRoleDefinition)
                .ToList()
                .AsReadOnly();
        }

        using var document = JsonDocument.Parse(payload);
        return ParseRoles(document.RootElement);
    }

    public static async Task WriteCatalogAsync(
        Stream stream,
        IReadOnlyList<StoredRoleDefinition> roles,
        CancellationToken cancellationToken)
    {
        var payload = new RoleCatalogState();
        payload.Roles.AddRange(roles.Select(ToProtoRoleDefinition));
        await stream.WriteAsync(payload.ToByteArray(), cancellationToken);
    }

    public static async Task<ParsedRoleDraft> ReadDraftAsync(
        Stream stream,
        DateTimeOffset fallbackUpdatedAtUtc,
        CancellationToken cancellationToken)
    {
        var payload = await ReadPayloadAsync(stream, cancellationToken);
        if (!IsJsonPayload(payload))
        {
            var draftEntry = RoleDraftEntry.Parser.ParseFrom(payload);
            var protobufUpdatedAtUtc = draftEntry.UpdatedAtUtc?.ToDateTimeOffset() ?? fallbackUpdatedAtUtc;
            var protobufDraft = draftEntry.Draft is not null ? ToStoredRoleDefinition(draftEntry.Draft) : null;
            return new ParsedRoleDraft(protobufUpdatedAtUtc, protobufDraft);
        }

        using var document = JsonDocument.Parse(payload);
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
        var payload = new RoleDraftEntry
        {
            Draft = draft is not null ? ToProtoRoleDefinition(draft) : null,
            UpdatedAtUtc = Timestamp.FromDateTimeOffset(updatedAtUtc),
        };

        await stream.WriteAsync(payload.ToByteArray(), cancellationToken);
    }

    internal sealed record ParsedRoleDraft(
        DateTimeOffset UpdatedAtUtc,
        StoredRoleDefinition? Draft);

    private static async Task<byte[]> ReadPayloadAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    private static bool IsJsonPayload(ReadOnlySpan<byte> payload)
    {
        foreach (var value in payload)
        {
            if (value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                continue;
            }

            return value is (byte)'{' or (byte)'[';
        }

        return false;
    }

    private static StoredRoleDefinition ToStoredRoleDefinition(RoleDefinitionEntry entry) =>
        new(
            Id: entry.Id,
            Name: entry.Name,
            SystemPrompt: entry.SystemPrompt,
            Provider: entry.Provider,
            Model: entry.Model,
            Connectors: entry.Connectors.ToList().AsReadOnly());

    private static RoleDefinitionEntry ToProtoRoleDefinition(StoredRoleDefinition role)
    {
        var entry = new RoleDefinitionEntry
        {
            Id = role.Id,
            Name = role.Name,
            SystemPrompt = role.SystemPrompt,
            Provider = role.Provider,
            Model = role.Model,
        };
        entry.Connectors.AddRange(role.Connectors);
        return entry;
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
