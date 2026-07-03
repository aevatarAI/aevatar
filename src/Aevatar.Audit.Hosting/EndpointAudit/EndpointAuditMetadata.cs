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
        AuditOperationKind operationKind = AuditOperationKind.Api)
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
    }

    public string OperationName { get; }

    public AuditSensitivityLevel SensitivityLevel { get; }

    public string TargetKind { get; }

    public EndpointAuditTargetResolver TargetResolver { get; }

    public EndpointAuditSummarySanitizer RequestSanitizer { get; }

    public EndpointAuditSummarySanitizer ResultSanitizer { get; }

    public AuditOperationKind OperationKind { get; }
}
