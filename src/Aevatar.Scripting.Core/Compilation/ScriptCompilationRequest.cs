namespace Aevatar.Scripting.Core.Compilation;

public sealed record ScriptCompilationRequest(
    string ScriptId,
    string Revision,
    string Source);
