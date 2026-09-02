using Aevatar.Audit;

namespace Aevatar.Audit.Hosting.EndpointAudit;

public sealed class EndpointAuditMetadata
{
    public EndpointAuditMetadata(
        string operationName,
        AuditSensitivityLevel sensitivityLevel,
        string targetKind,
        EndpointAuditTargetResolver targetResolver,
        EndpointAuditSummarySanitizer requestSanitizer,
        EndpointAuditSummarySanitizer resultSanitizer,
        AuditOperationKind operationKind = AuditOperationKind.Api,
        bool captureUnauthenticated = false)
    {
        if (string.IsNullOrWhiteSpace(operationName))
        {
            throw new ArgumentException("Operation name is required.", nameof(operationName));
        }

        if (sensitivityLevel == AuditSensitivityLevel.Unspecified)
        {
            throw new ArgumentException("Sensitivity level must be specified.", nameof(sensitivityLevel));
        }

        if (string.IsNullOrWhiteSpace(targetKind))
        {
            throw new ArgumentException("Target kind is required.", nameof(targetKind));
        }

        if (operationKind == AuditOperationKind.Unspecified)
        {
            throw new ArgumentException("Operation kind must be specified.", nameof(operationKind));
        }

        OperationName = operationName.Trim();
        SensitivityLevel = sensitivityLevel;
        TargetKind = targetKind.Trim();
        TargetResolver = targetResolver ?? throw new ArgumentNullException(nameof(targetResolver));
        RequestSanitizer = requestSanitizer ?? throw new ArgumentNullException(nameof(requestSanitizer));
        ResultSanitizer = resultSanitizer ?? throw new ArgumentNullException(nameof(resultSanitizer));
        OperationKind = operationKind;
        CaptureUnauthenticated = captureUnauthenticated;
    }

    public string OperationName { get; }

    public AuditSensitivityLevel SensitivityLevel { get; }

    public string TargetKind { get; }

    public EndpointAuditTargetResolver TargetResolver { get; }

    public EndpointAuditSummarySanitizer RequestSanitizer { get; }

    public EndpointAuditSummarySanitizer ResultSanitizer { get; }

    public AuditOperationKind OperationKind { get; }

    /// <summary>
    /// When true the boundary capture middleware records this endpoint even for
    /// unauthenticated callers, hashing a fixed anonymous canonical actor key
    /// instead of a caller subject. Reserved for explicitly <c>AllowAnonymous</c>
    /// ingress surfaces (OAuth callbacks, HMAC-signed webhooks, relay ingress)
    /// whose trust boundary is the signature/state-token, not a platform user.
    /// Defaults to false so ordinary endpoints keep the "unauthenticated 401
    /// challenges are not recorded" invariant (docs/canon/audit-trail.md §3.1).
    /// </summary>
    public bool CaptureUnauthenticated { get; }
}
