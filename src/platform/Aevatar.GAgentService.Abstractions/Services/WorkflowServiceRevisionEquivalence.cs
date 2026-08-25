using System.Security.Cryptography;
using Aevatar.Workflow.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Abstractions.Services;

public static class WorkflowServiceRevisionEquivalence
{
    /// <summary>
    /// Compares complete revision specs, ignoring only renewable workflow admission source stamp
    /// values and their derived digest after both admission digests have been verified. Source kind
    /// and source id remain exact revision identity inputs.
    /// </summary>
    public static bool AreEquivalent(ServiceRevisionSpec left, ServiceRevisionSpec right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (left.Equals(right))
            return true;
        if (left.ImplementationKind != ServiceImplementationKind.Workflow ||
            right.ImplementationKind != ServiceImplementationKind.Workflow)
        {
            return false;
        }

        var normalizedLeft = left.Clone();
        var normalizedRight = right.Clone();
        return TryClearRenewableAdmissionEvidence(
                   normalizedLeft.WorkflowSpec?.CapabilityAdmissionPlan) &&
               TryClearRenewableAdmissionEvidence(
                   normalizedRight.WorkflowSpec?.CapabilityAdmissionPlan) &&
               normalizedLeft.Equals(normalizedRight);
    }

    /// <summary>
    /// Compares complete prepared artifacts, ignoring only a verified artifact hash plus renewable
    /// workflow admission source stamp values and their derived digest after both admission digests
    /// are verified. Source kind and source id remain exact revision identity inputs.
    /// </summary>
    public static bool AreEquivalent(
        PreparedServiceRevisionArtifact left,
        PreparedServiceRevisionArtifact right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (left.ImplementationKind != ServiceImplementationKind.Workflow ||
            right.ImplementationKind != ServiceImplementationKind.Workflow)
        {
            return left.Equals(right);
        }

        if (!HasValidArtifactHash(left) || !HasValidArtifactHash(right))
            return false;
        if (left.Equals(right))
            return true;

        var normalizedLeft = left.Clone();
        var normalizedRight = right.Clone();
        normalizedLeft.ArtifactHash = string.Empty;
        normalizedRight.ArtifactHash = string.Empty;
        return TryClearRenewableAdmissionEvidence(
                   normalizedLeft.DeploymentPlan?.WorkflowPlan?.CapabilityAdmissionPlan) &&
               TryClearRenewableAdmissionEvidence(
                   normalizedRight.DeploymentPlan?.WorkflowPlan?.CapabilityAdmissionPlan) &&
               normalizedLeft.Equals(normalizedRight);
    }

    public static bool HasValidArtifactHash(PreparedServiceRevisionArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (string.IsNullOrWhiteSpace(artifact.ArtifactHash))
            return false;

        var canonical = artifact.Clone();
        canonical.ArtifactHash = string.Empty;
        var expectedHash = Convert.ToHexString(SHA256.HashData(canonical.ToByteArray()));
        return string.Equals(artifact.ArtifactHash, expectedHash, StringComparison.Ordinal);
    }

    public static void EnsureRenewableAdmissionEvidenceMovesForward(
        WorkflowCapabilityAdmissionPlan current,
        WorkflowCapabilityAdmissionPlan refreshed)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(refreshed);
        if (current.SourceStamps.Count != refreshed.SourceStamps.Count)
        {
            throw new InvalidOperationException(
                "Workflow capability admission evidence source identity changed.");
        }

        var advanced = false;
        for (var index = 0; index < current.SourceStamps.Count; index++)
        {
            var previous = current.SourceStamps[index];
            var next = refreshed.SourceStamps[index];
            if (previous.SourceKind != next.SourceKind ||
                !string.Equals(previous.SourceId, next.SourceId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Workflow capability admission evidence source identity changed.");
            }

            if (next.SourceVersion < previous.SourceVersion)
            {
                throw new InvalidOperationException(
                    $"Workflow capability admission evidence source '{previous.SourceId}' version moved backwards.");
            }

            if (next.SourceVersion == previous.SourceVersion &&
                !string.Equals(next.ContentDigest, previous.ContentDigest, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Workflow capability admission evidence source '{previous.SourceId}' changed content at the same version.");
            }

            var observedAtComparison = Compare(next.ObservedAt, previous.ObservedAt);
            if (observedAtComparison < 0)
            {
                throw new InvalidOperationException(
                    $"Workflow capability admission evidence source '{previous.SourceId}' observed_at moved backwards.");
            }

            var freshUntilComparison = Compare(next.FreshUntil, previous.FreshUntil);
            if (freshUntilComparison < 0)
            {
                throw new InvalidOperationException(
                    $"Workflow capability admission evidence source '{previous.SourceId}' fresh_until moved backwards.");
            }

            advanced |= next.SourceVersion > previous.SourceVersion ||
                        observedAtComparison > 0 ||
                        freshUntilComparison > 0;
        }

        if (!advanced)
        {
            throw new InvalidOperationException(
                "Workflow capability admission evidence refresh did not move any source forward.");
        }
    }

    private static bool TryClearRenewableAdmissionEvidence(
        WorkflowCapabilityAdmissionPlan? plan)
    {
        if (plan == null ||
            !WorkflowCapabilityAdmissionPlanIntegrity.IsSupportedSchemaVersion(plan.SchemaVersion) ||
            !string.Equals(
                plan.AdmissionDigest,
                WorkflowCapabilityAdmissionPlanIntegrity.ComputeAdmissionDigest(plan),
                StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var sourceStamp in plan.SourceStamps)
        {
            sourceStamp.SourceVersion = 0;
            sourceStamp.ObservedAt = null;
            sourceStamp.FreshUntil = null;
            sourceStamp.ContentDigest = string.Empty;
        }
        plan.AdmissionDigest = string.Empty;
        return true;
    }

    private static int Compare(Timestamp? left, Timestamp? right)
    {
        if (left == null)
            return right == null ? 0 : -1;
        if (right == null)
            return 1;

        var secondsComparison = left.Seconds.CompareTo(right.Seconds);
        return secondsComparison != 0
            ? secondsComparison
            : left.Nanos.CompareTo(right.Nanos);
    }
}
