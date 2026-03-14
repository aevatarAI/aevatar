using System.Security.Cryptography;
using Aevatar.GAgentService.Abstractions;
using Google.Protobuf;

namespace Aevatar.GAgentService.Core.Assemblers;

public sealed class PreparedServiceRevisionArtifactAssembler
{
    public PreparedServiceRevisionArtifact Assemble(PreparedServiceRevisionArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.Identity == null)
            throw new InvalidOperationException("Prepared artifact identity is required.");
        if (string.IsNullOrWhiteSpace(artifact.RevisionId))
            throw new InvalidOperationException("Prepared artifact revision_id is required.");
        if (artifact.ImplementationKind == ServiceImplementationKind.Unspecified)
            throw new InvalidOperationException("Prepared artifact implementation_kind is required.");
        if (artifact.Endpoints.Count == 0)
            throw new InvalidOperationException("Prepared artifact must declare at least one endpoint.");
        if (artifact.DeploymentPlan == null || artifact.DeploymentPlan.PlanSpecCase == ServiceDeploymentPlan.PlanSpecOneofCase.None)
            throw new InvalidOperationException("Prepared artifact deployment plan is required.");

        var normalized = artifact.Clone();
        normalized.ArtifactHash = string.Empty;
        normalized.ArtifactHash = ComputeHash(normalized);
        return normalized;
    }

    private static string ComputeHash(PreparedServiceRevisionArtifact artifact)
    {
        var payload = artifact.ToByteArray();
        return Convert.ToHexString(SHA256.HashData(payload));
    }
}
