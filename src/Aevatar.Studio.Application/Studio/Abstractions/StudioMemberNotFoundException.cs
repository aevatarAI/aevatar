namespace Aevatar.Studio.Application.Studio.Abstractions;

/// <summary>
/// Thrown when a member-centric operation targets an id that has no
/// corresponding member document in the requested scope. Endpoints map this
/// to HTTP 404 — distinct from validation errors that map to 400.
///
/// Inherits <see cref="KeyNotFoundException"/> so the 404 / 400 mapping in
/// endpoints is type-disjoint from <see cref="InvalidOperationException"/>
/// (validation throws). Reordering catch blocks cannot silently downgrade a
/// 404 to a 400.
/// </summary>
public sealed class StudioMemberNotFoundException : KeyNotFoundException
{
    public StudioMemberNotFoundException(string scopeId, string memberId)
        : base($"member '{memberId}' not found in scope '{scopeId}'.")
    {
        ScopeId = scopeId;
        MemberId = memberId;
    }

    public string ScopeId { get; }

    public string MemberId { get; }
}
