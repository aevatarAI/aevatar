namespace Aevatar.Scripting.Core.Compilation;

public sealed record ScriptBehaviorCompilationRequest(
    string ScriptId,
    string Revision,
    string Source);
