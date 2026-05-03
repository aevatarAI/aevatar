namespace Aevatar.Studio.Application.Studio.Abstractions;

public sealed class StudioMemberBindingRunNotFoundException : KeyNotFoundException
{
    public StudioMemberBindingRunNotFoundException(string scopeId, string memberId, string bindingRunId)
        : base($"binding run '{bindingRunId}' for member '{memberId}' not found in scope '{scopeId}'.")
    {
        ScopeId = scopeId;
        MemberId = memberId;
        BindingRunId = bindingRunId;
    }

    public string ScopeId { get; }

    public string MemberId { get; }

    public string BindingRunId { get; }
}
