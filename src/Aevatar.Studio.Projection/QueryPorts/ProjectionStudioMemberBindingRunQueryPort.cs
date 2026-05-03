using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.StudioMember;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Projection.ReadModels;

namespace Aevatar.Studio.Projection.QueryPorts;

/// <summary>
/// Reads binding-run status from the run actor's current-state read model.
/// </summary>
public sealed class ProjectionStudioMemberBindingRunQueryPort : IStudioMemberBindingRunQueryPort
{
    private readonly IProjectionDocumentReader<StudioMemberBindingRunCurrentStateDocument, string> _documentReader;

    public ProjectionStudioMemberBindingRunQueryPort(
        IProjectionDocumentReader<StudioMemberBindingRunCurrentStateDocument, string> documentReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
    }

    public async Task<StudioMemberBindingRunStatusResponse?> GetAsync(
        string scopeId,
        string memberId,
        string bindingRunId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = StudioMemberConventions.NormalizeScopeId(scopeId);
        var normalizedMemberId = StudioMemberConventions.NormalizeMemberId(memberId);
        var normalizedBindingRunId = StudioMemberConventions.NormalizeBindingRunId(bindingRunId);
        var actorId = StudioMemberConventions.BuildBindingRunActorId(normalizedBindingRunId);

        var document = await _documentReader.GetAsync(actorId, ct);
        if (document == null)
            return null;

        if (!string.Equals(document.ScopeId, normalizedScopeId, StringComparison.Ordinal)
            || !string.Equals(document.MemberId, normalizedMemberId, StringComparison.Ordinal)
            || !string.Equals(document.BindingRunId, normalizedBindingRunId, StringComparison.Ordinal))
        {
            return null;
        }

        StudioMemberBindingFailureResponse? failure = null;
        if (!string.IsNullOrEmpty(document.FailureCode))
        {
            failure = new StudioMemberBindingFailureResponse(
                Code: document.FailureCode,
                Message: document.FailureMessage,
                FailedAt: document.FailureAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue);
        }

        return new StudioMemberBindingRunStatusResponse(
            BindingRunId: document.BindingRunId,
            ScopeId: document.ScopeId,
            MemberId: document.MemberId,
            Status: NormalizeBindingRunStatusWire(document.Status),
            Failure: failure,
            UpdatedAt: document.UpdatedAt?.ToDateTimeOffset())
        {
            PlatformBindingCommandId = string.IsNullOrEmpty(document.PlatformBindingCommandId)
                ? null
                : document.PlatformBindingCommandId,
        };
    }

    private static string NormalizeBindingRunStatusWire(string? wire) => wire switch
    {
        StudioMemberBindingRunStatusNames.Accepted => StudioMemberBindingRunStatusNames.Accepted,
        StudioMemberBindingRunStatusNames.AdmissionPending => StudioMemberBindingRunStatusNames.AdmissionPending,
        StudioMemberBindingRunStatusNames.Admitted => StudioMemberBindingRunStatusNames.Admitted,
        StudioMemberBindingRunStatusNames.PlatformBindingPending => StudioMemberBindingRunStatusNames.PlatformBindingPending,
        StudioMemberBindingRunStatusNames.Succeeded => StudioMemberBindingRunStatusNames.Succeeded,
        StudioMemberBindingRunStatusNames.Failed => StudioMemberBindingRunStatusNames.Failed,
        StudioMemberBindingRunStatusNames.Rejected => StudioMemberBindingRunStatusNames.Rejected,
        _ => string.Empty,
    };
}
