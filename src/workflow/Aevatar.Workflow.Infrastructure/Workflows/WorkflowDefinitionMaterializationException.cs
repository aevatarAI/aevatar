using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Infrastructure.Workflows;

internal sealed class WorkflowDefinitionMaterializationException : InvalidOperationException
{
    public const string InvalidExecutionModeCode = "WORKFLOW_DEFINITION_EXECUTION_MODE_REQUIRED";
    public const string AdmissionModeMismatchCode = "WORKFLOW_DEFINITION_ADMISSION_MODE_MISMATCH";
    public const string ObservationUnavailableCode = "WORKFLOW_DEFINITION_BIND_OBSERVATION_UNAVAILABLE";
    public const string DispatchRejectedCode = "WORKFLOW_DEFINITION_BIND_DISPATCH_REJECTED";
    public const string BindNotCommittedCode = "WORKFLOW_DEFINITION_BIND_NOT_COMMITTED";

    public WorkflowDefinitionMaterializationException(
        string code,
        string workflowName,
        string actorId,
        ExternalCapabilityExecutionMode expectedExecutionMode,
        string message,
        Exception? innerException = null)
        : base($"{code}: {message}", innerException)
    {
        Code = code;
        WorkflowName = workflowName;
        ActorId = actorId;
        ExpectedExecutionMode = expectedExecutionMode;
    }

    public string Code { get; }

    public string WorkflowName { get; }

    public string ActorId { get; }

    public ExternalCapabilityExecutionMode ExpectedExecutionMode { get; }
}
