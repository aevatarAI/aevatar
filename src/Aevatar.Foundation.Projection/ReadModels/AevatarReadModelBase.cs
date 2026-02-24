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
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
