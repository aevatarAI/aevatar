using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Abstractions.Queries;

public sealed partial class ScriptReadModelSnapshot
{
    public ScriptReadModelSnapshot(
        string ActorId,
        string ScriptId,
        string DefinitionActorId,
        string Revision,
        string ReadModelTypeUrl,
        Any? ReadModelPayload,
        long StateVersion,
        string LastEventId,
        DateTimeOffset UpdatedAt)
    {
        this.ActorId = ActorId ?? string.Empty;
        this.ScriptId = ScriptId ?? string.Empty;
        this.DefinitionActorId = DefinitionActorId ?? string.Empty;
        this.Revision = Revision ?? string.Empty;
        this.ReadModelTypeUrl = ReadModelTypeUrl ?? string.Empty;
        this.ReadModelPayload = ReadModelPayload?.Clone();
        this.StateVersion = StateVersion;
        this.LastEventId = LastEventId ?? string.Empty;
        this.UpdatedAt = UpdatedAt;
    }

    public DateTimeOffset UpdatedAt
    {
        get => UpdatedAtUtc == null ? default : UpdatedAtUtc.ToDateTimeOffset();
        set => UpdatedAtUtc = Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }
}
