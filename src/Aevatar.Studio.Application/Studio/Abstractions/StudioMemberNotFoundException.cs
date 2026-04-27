namespace Aevatar.Studio.Application.Studio.Abstractions;

/// <summary>
/// Thrown when a member-centric operation targets an id that has no
/// corresponding member document in the requested scope. Endpoints map this
/// to HTTP 404 — distinct from validation errors that map to 400.
/// </summary>
public sealed class StudioMemberNotFoundException : InvalidOperationException
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
