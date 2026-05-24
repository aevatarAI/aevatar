using Aevatar.GAgents.RoleCatalog;
using Aevatar.Studio.Application.Studio.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Infrastructure.Storage;

internal static class RoleCatalogStorageSerializer
{
    public static async Task<IReadOnlyList<StoredRoleDefinition>> ReadCatalogAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var payload = await ReadPayloadAsync(stream, cancellationToken);
        var state = RoleCatalogState.Parser.ParseFrom(payload);
        return state.Roles
            .Select(ToStoredRoleDefinition)
            .ToList()
            .AsReadOnly();
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
        var draftEntry = RoleDraftEntry.Parser.ParseFrom(payload);
        var protobufUpdatedAtUtc = draftEntry.UpdatedAtUtc?.ToDateTimeOffset() ?? fallbackUpdatedAtUtc;
        var protobufDraft = draftEntry.Draft is not null ? ToStoredRoleDefinition(draftEntry.Draft) : null;
        return new ParsedRoleDraft(protobufUpdatedAtUtc, protobufDraft);
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
}
