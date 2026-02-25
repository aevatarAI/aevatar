namespace Aevatar.Foundation.Projection.ReadModels;

/// <summary>
/// Generic projection read-model metadata base.
/// Domain-specific identity fields should live in domain models.
/// </summary>
public abstract class ProjectionReadModelBase<TKey>
    where TKey : notnull
{
    public TKey Id { get; set; } = default!;
    public long StateVersion { get; set; }
    public string LastEventId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
