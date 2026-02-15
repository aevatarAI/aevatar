namespace Aevatar.Demos.CaseProjection.Abstractions;

/// <summary>
/// Run-scoped case projection session.
/// </summary>
public sealed class CaseProjectionSession
{
    public required string RunId { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public CaseProjectionContext? Context { get; init; }

    public bool Enabled => Context != null;
}
