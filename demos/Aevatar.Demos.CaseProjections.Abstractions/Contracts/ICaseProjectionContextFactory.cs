namespace Aevatar.Demos.CaseProjections.Abstractions;

/// <summary>
/// Builds case projection context from request inputs.
/// </summary>
public interface ICaseProjectionContextFactory
{
    CaseProjectionContext Create(
        string runId,
        string rootActorId,
        string caseId,
        string caseType,
        string input,
        DateTimeOffset startedAt);
}
