using System.Text.Json;
using Aevatar.App.Application.Projection.ReadModels;
using Aevatar.App.GAgents;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;
using static Aevatar.App.Application.Projection.Reducers.SyncEntityReducerConversions;

namespace Aevatar.App.Application.Projection.Reducers;

public sealed class EntityCreatedEventReducer
    : AppEventReducerBase<AppSyncEntityReadModel, EntityCreatedEvent>
{
    protected override bool Reduce(
        AppSyncEntityReadModel readModel,
        AppProjectionContext context,
        EventEnvelope envelope,
        EntityCreatedEvent evt,
        DateTimeOffset now)
    {
        var entry = new SyncEntityEntry
        {
            UserId = evt.UserId,
            ClientId = evt.ClientId,
            EntityType = evt.EntityType,
            Revision = evt.Revision,
            Source = SourceToString(evt.Source),
            Position = evt.Position,
            BankEligible = evt.BankEligible,
            BankHash = evt.BankHash,
            Inputs = StructToJson(evt.Inputs),
            Output = StructToJson(evt.Output),
            State = StructToJson(evt.State),
            CreatedAt = TimestampToOffset(evt.CreatedAt),
            Refs = new Dictionary<string, string>(evt.Refs, StringComparer.Ordinal),
        };
        readModel.Entities[entry.ClientId] = entry;
        readModel.UserId = evt.UserId;
        readModel.ServerRevision = Math.Max(readModel.ServerRevision, evt.Revision);
        return true;
    }
}

public sealed class EntityUpdatedEventReducer
    : AppEventReducerBase<AppSyncEntityReadModel, EntityUpdatedEvent>
{
    protected override bool Reduce(
        AppSyncEntityReadModel readModel,
        AppProjectionContext context,
        EventEnvelope envelope,
        EntityUpdatedEvent evt,
        DateTimeOffset now)
    {
        var entry = new SyncEntityEntry
        {
            UserId = evt.UserId,
            ClientId = evt.ClientId,
            EntityType = evt.EntityType,
            Revision = evt.Revision,
            Source = SourceToString(evt.Source),
            Position = evt.Position,
            BankEligible = evt.BankEligible,
            BankHash = evt.BankHash,
            Inputs = StructToJson(evt.Inputs),
            Output = StructToJson(evt.Output),
            State = StructToJson(evt.State),
            UpdatedAt = TimestampToOffset(evt.UpdatedAt),
            Refs = new Dictionary<string, string>(evt.Refs, StringComparer.Ordinal),
        };
        if (readModel.Entities.TryGetValue(entry.ClientId, out var existing))
            entry.CreatedAt = existing.CreatedAt;
        readModel.Entities[entry.ClientId] = entry;
        readModel.UserId = evt.UserId;
        readModel.ServerRevision = Math.Max(readModel.ServerRevision, evt.Revision);
        return true;
    }
}

public sealed class EntityDeletedEventReducer
    : AppEventReducerBase<AppSyncEntityReadModel, EntityDeletedEvent>
{
    protected override bool Reduce(
        AppSyncEntityReadModel readModel,
        AppProjectionContext context,
        EventEnvelope envelope,
        EntityDeletedEvent evt,
        DateTimeOffset now)
    {
        if (readModel.Entities.TryGetValue(evt.ClientId, out var entry))
        {
            entry.DeletedAt = TimestampToOffset(evt.DeletedAt);
            entry.Revision = evt.Revision;
            entry.BankEligible = false;
        }

        readModel.UserId = evt.UserId;
        readModel.ServerRevision = Math.Max(readModel.ServerRevision, evt.Revision);
        return true;
    }
}

public sealed class AccountDeletedEventSyncEntityReducer
    : AppEventReducerBase<AppSyncEntityReadModel, AccountDeletedEvent>
{
    protected override bool Reduce(
        AppSyncEntityReadModel readModel,
        AppProjectionContext context,
        EventEnvelope envelope,
        AccountDeletedEvent evt,
        DateTimeOffset now)
    {
        if (string.Equals(evt.Mode, "hard", StringComparison.OrdinalIgnoreCase))
        {
            readModel.Entities.Clear();
            readModel.UserId = string.Empty;
            readModel.ServerRevision = 0;
            return true;
        }

        if (string.Equals(evt.Mode, "soft", StringComparison.OrdinalIgnoreCase))
        {
            var deletedUserId = $"deleted_{evt.UserId}";
            var deletedAt = TimestampToOffset(evt.DeletedAt);
            foreach (var kv in readModel.Entities)
            {
                var entry = kv.Value;
                entry.UserId = deletedUserId;
                entry.DeletedAt ??= deletedAt;
                entry.BankEligible = false;
                entry.BankHash = string.Empty;

                var deletedInputs = new Dictionary<string, object?> { ["userGoal"] = "[deleted]" };
                entry.Inputs = JsonSerializer.SerializeToElement(deletedInputs);
                entry.Output = null;
            }
            readModel.UserId = deletedUserId;
        }

        return true;
    }
}

internal static class SyncEntityReducerConversions
{
    internal static JsonElement? StructToJson(Struct? s)
    {
        if (s is null || s.Fields.Count == 0) return null;
        var json = Google.Protobuf.JsonFormatter.Default.Format(s);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    internal static DateTimeOffset? TimestampToOffset(Timestamp? ts)
    {
        if (ts is null) return null;
        var dt = ts.ToDateTime();
        if (dt.Kind != DateTimeKind.Utc) dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        return new DateTimeOffset(dt);
    }

    internal static string SourceToString(EntitySource source) => source switch
    {
        EntitySource.Ai => "ai",
        EntitySource.Bank => "bank",
        EntitySource.User => "user",
        EntitySource.Edited => "edited",
        _ => "ai"
    };
}
