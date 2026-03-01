namespace Aevatar.Scripting.Abstractions.Definitions;

public interface IScriptAgentDefinition
{
    string ScriptId { get; }
    string Revision { get; }

    Task<ScriptDecisionResult> DecideAsync(
        ScriptExecutionContext context,
        CancellationToken ct);
}
