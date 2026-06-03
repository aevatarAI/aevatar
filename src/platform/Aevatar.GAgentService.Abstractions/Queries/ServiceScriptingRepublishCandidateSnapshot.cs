namespace Aevatar.GAgentService.Abstractions.Queries;

public sealed record ServiceScriptingRepublishCandidateSnapshot(
    ServiceIdentity Identity,
    string CurrentServingRevisionId,
    string CurrentServingDeploymentId,
    ServiceRevisionScriptingSnapshot Scripting,
    PreparedServiceRevisionArtifact? PreparedArtifact);
