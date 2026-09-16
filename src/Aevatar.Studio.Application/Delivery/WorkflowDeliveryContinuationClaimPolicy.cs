using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Application.Delivery;

internal static class WorkflowDeliveryContinuationClaimPolicy
{
    public static string NormalizeClaimantId(string claimantId) =>
        string.IsNullOrWhiteSpace(claimantId)
            ? throw new ArgumentException(
                "Continuation claimant identity is required.",
                nameof(claimantId))
            : claimantId.Trim();

    public static bool IsActiveFor(
        WorkflowInstallationSnapshot installation,
        WorkflowInstallationContinuationClaimSnapshot? claim,
        WorkflowInstallationStatus expectedStatus,
        string claimantId,
        DateTimeOffset now) =>
        claim != null &&
        claim.ExpectedStatus == expectedStatus &&
        claim.Attempt == installation.Attempt &&
        string.Equals(claim.OperationId, installation.OperationId, StringComparison.Ordinal) &&
        string.Equals(claim.ClaimantId, claimantId, StringComparison.Ordinal) &&
        claim.ExpiresAtUtc > now;
}
