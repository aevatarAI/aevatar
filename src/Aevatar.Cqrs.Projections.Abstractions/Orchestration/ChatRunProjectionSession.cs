namespace Aevatar.Cqrs.Projections.Abstractions;

/// <summary>
/// Run-scoped projection session.
/// </summary>
public sealed class ChatRunProjectionSession
{
    public required string RunId { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public ChatProjectionContext? Context { get; init; }

    public bool Enabled => Context != null;
}
