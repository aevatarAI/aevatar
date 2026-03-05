using System.Text.Json;
using Aevatar.App.Application.Contracts;
using Aevatar.App.Application.Projection.ReadModels;
using Aevatar.App.GAgents;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.App.Host.Api.Endpoints.Mappers;

public static class EntityMapMapper
{
    public static Dictionary<string, Dictionary<string, EntityDto>> ToDto(
        Dictionary<string, Dictionary<string, SyncEntityEntry>> grouped)
    {
        var result = new Dictionary<string, Dictionary<string, EntityDto>>();
        foreach (var (entityType, entities) in grouped)
        {
            var typeMap = new Dictionary<string, EntityDto>();
            foreach (var (clientId, entry) in entities)
                typeMap[clientId] = EntryToDto(entry);
            result[entityType] = typeMap;
        }
        return result;
    }

    public static Dictionary<string, Dictionary<string, EntityDto>> DeltaToDto(
        Dictionary<string, SyncEntity> delta)
    {
        var grouped = new Dictionary<string, Dictionary<string, EntityDto>>();
        foreach (var (clientId, entity) in delta)
        {
            if (!grouped.TryGetValue(entity.EntityType, out var typeMap))
            {
                typeMap = new Dictionary<string, EntityDto>();
                grouped[entity.EntityType] = typeMap;
            }
            typeMap[clientId] = ToDto(entity);
        }
        return grouped;
    }

    public static SyncEntity FromDto(EntityDto dto)
    {
        var entity = new SyncEntity
        {
            ClientId = dto.ClientId,
            EntityType = dto.EntityType,
            UserId = dto.UserId ?? string.Empty,
            Revision = dto.Revision,
            Position = dto.Position,
            Source = StringToSource(dto.Source),
            BankEligible = dto.BankEligible,
            BankHash = dto.BankHash ?? string.Empty,
            Inputs = JsonElementToStruct(dto.Inputs),
            Output = JsonElementToStruct(dto.Output),
            State = JsonElementToStruct(dto.State),
        };

        foreach (var (k, v) in dto.Refs)
            entity.Refs[k] = v;

        if (!string.IsNullOrEmpty(dto.DeletedAt))
            entity.DeletedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse(dto.DeletedAt));
        if (!string.IsNullOrEmpty(dto.CreatedAt))
            entity.CreatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse(dto.CreatedAt));
        if (!string.IsNullOrEmpty(dto.UpdatedAt))
            entity.UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse(dto.UpdatedAt));

        return entity;
    }

    private static EntityDto EntryToDto(SyncEntityEntry entry) => new()
    {
        ClientId = entry.ClientId,
        EntityType = entry.EntityType,
        UserId = entry.UserId,
        Revision = entry.Revision,
        Refs = new Dictionary<string, string>(entry.Refs),
        Position = entry.Position,
        Inputs = entry.Inputs,
        Output = entry.Output,
        State = entry.State,
        Source = entry.Source,
        BankEligible = entry.BankEligible,
        BankHash = entry.BankHash,
        DeletedAt = entry.DeletedAt?.ToString("O"),
        CreatedAt = entry.CreatedAt?.ToString("O"),
        UpdatedAt = entry.UpdatedAt?.ToString("O"),
    };

    public static EntityDto ToDto(SyncEntity entity) => new()
    {
        ClientId = entity.ClientId,
        EntityType = entity.EntityType,
        UserId = entity.UserId,
        Revision = entity.Revision,
        Refs = new Dictionary<string, string>(entity.Refs),
        Position = entity.Position,
        Inputs = StructToJsonElement(entity.Inputs),
        Output = StructToJsonElement(entity.Output),
        State = StructToJsonElement(entity.State),
        Source = SourceToString(entity.Source),
        BankEligible = entity.BankEligible,
        BankHash = entity.BankHash,
        DeletedAt = entity.DeletedAt is not null
            ? entity.DeletedAt.ToDateTimeOffset().ToString("O")
            : null,
        CreatedAt = entity.CreatedAt?.ToDateTimeOffset().ToString("O"),
        UpdatedAt = entity.UpdatedAt?.ToDateTimeOffset().ToString("O"),
    };

    private static string SourceToString(EntitySource source) => source switch
    {
        EntitySource.Ai => "ai",
        EntitySource.Bank => "bank",
        EntitySource.User => "user",
        EntitySource.Edited => "edited",
        _ => "ai"
    };

    private static EntitySource StringToSource(string? source) => source switch
    {
        "ai" => EntitySource.Ai,
        "bank" => EntitySource.Bank,
        "user" => EntitySource.User,
        "edited" => EntitySource.Edited,
        _ => EntitySource.Ai
    };

    private static JsonElement? StructToJsonElement(Struct? s)
    {
        if (s is null) return null;
        var json = Google.Protobuf.JsonFormatter.Default.Format(s);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    private static Struct? JsonElementToStruct(JsonElement? element)
    {
        if (element is null) return null;
        var json = element.Value.GetRawText();
        return Google.Protobuf.JsonParser.Default.Parse<Struct>(json);
    }
}
