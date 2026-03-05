using System.Text.Json;

namespace Aevatar.App.Application.Projection.ReadModels;

/// <summary>
/// Pure POCO representation of a sync entity for projection storage.
/// Unlike the protobuf-generated <c>SyncEntity</c>, this class is fully
/// compatible with System.Text.Json serialization (required by Elasticsearch store).
/// </summary>
public sealed class SyncEntityEntry
{
    public string ClientId { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string UserId { get; set; } = "";
    public int Revision { get; set; }
    public Dictionary<string, string> Refs { get; set; } = new(StringComparer.Ordinal);
    public int Position { get; set; }
    public JsonElement? Inputs { get; set; }
    public JsonElement? Output { get; set; }
    public JsonElement? State { get; set; }
    public string Source { get; set; } = "ai";
    public bool BankEligible { get; set; }
    public string BankHash { get; set; } = "";
    public DateTimeOffset? DeletedAt { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    public SyncEntityEntry Clone() => new()
    {
        ClientId = ClientId,
        EntityType = EntityType,
        UserId = UserId,
        Revision = Revision,
        Refs = new Dictionary<string, string>(Refs, StringComparer.Ordinal),
        Position = Position,
        Inputs = Inputs,
        Output = Output,
        State = State,
        Source = Source,
        BankEligible = BankEligible,
        BankHash = BankHash,
        DeletedAt = DeletedAt,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
    };
}

public sealed class RejectedEntityEntry
{
    public string ClientId { get; set; } = "";
    public int ServerRevision { get; set; }
    public string Reason { get; set; } = "";
}
