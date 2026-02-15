namespace Aevatar.Demos.CaseProjections.Orchestration;

public sealed class DefaultCaseProjectionContextFactory : ICaseProjectionContextFactory
{
    public CaseProjectionContext Create(
        string runId,
        string rootActorId,
        string caseId,
        string caseType,
        string input,
        DateTimeOffset startedAt) =>
        new()
        {
            RunId = runId,
            RootActorId = rootActorId,
            CaseId = caseId,
            CaseType = caseType,
            Input = input,
            StartedAt = startedAt,
        };
}
