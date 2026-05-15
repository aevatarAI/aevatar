using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Application.Studio.Abstractions;

/// <summary>
/// Pure-read query port for a StudioMember binding run actor's current-state
/// read model. Reads run-owned status; does not infer run state from the
/// member projection.
/// </summary>
public interface IStudioMemberBindingRunQueryPort
{
    Task<StudioMemberBindingRunStatusResponse?> GetAsync(
        string scopeId,
        string memberId,
        string bindingRunId,
        CancellationToken ct = default);
}
