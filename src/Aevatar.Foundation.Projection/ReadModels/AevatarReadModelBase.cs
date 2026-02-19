namespace Aevatar.Foundation.Projection.ReadModels;

/// <summary>
/// Minimal cross-domain read model envelope metadata.
/// </summary>
public abstract class AevatarReadModelBase
{
    public string RootActorId { get; set; } = string.Empty;
    public string CommandId { get; set; } = string.Empty;
    public long StateVersion { get; set; }
    public string LastEventId { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }
}
