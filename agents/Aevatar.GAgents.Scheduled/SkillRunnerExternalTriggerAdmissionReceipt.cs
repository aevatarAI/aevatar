namespace Aevatar.GAgents.Scheduled;

public sealed record SkillRunnerExternalTriggerAdmissionReceipt(
    string ActorId,
    string CommandId,
    string CorrelationId,
    string AdmissionId,
    string SourceId,
    string DeliveryId);

public enum SkillRunnerExternalTriggerAdmissionError
{
    Unspecified = 0,
    RunnerNotFound = 1,
}

public sealed class SkillRunnerExternalTriggerAdmissionException : InvalidOperationException
{
    public SkillRunnerExternalTriggerAdmissionException(
        SkillRunnerExternalTriggerAdmissionError error,
        string agentId)
        : base(BuildMessage(error, agentId))
    {
        Error = error;
        AgentId = agentId;
    }

    public SkillRunnerExternalTriggerAdmissionError Error { get; }

    public string AgentId { get; }

    private static string BuildMessage(SkillRunnerExternalTriggerAdmissionError error, string agentId) =>
        error switch
        {
            SkillRunnerExternalTriggerAdmissionError.RunnerNotFound =>
                $"Skill runner '{agentId}' was not found.",
            _ => $"Skill runner external trigger admission failed for '{agentId}'.",
        };
}
