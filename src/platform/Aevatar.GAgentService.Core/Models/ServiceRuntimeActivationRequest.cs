using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions;

namespace Aevatar.GAgentService.Core;

/// <summary>
/// Requests one logical runtime activation. Implementations must make repeated calls with the
/// same non-empty <paramref name="ActivationOperationId"/> deterministic and idempotent: they
/// must return the same deployment/primary actor result without duplicating runtime side effects.
/// </summary>
public sealed record ServiceRuntimeActivationRequest(
    ServiceIdentity Identity,
    PreparedServiceRevisionArtifact Artifact,
    string RevisionId,
    string DeploymentActorId,
    ActivationCapabilityView? CapabilityView = null,
    string ActivationAttemptId = "",
    string ActivationOperationId = "");
