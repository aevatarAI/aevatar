namespace Aevatar.Scripting.Core.Compilation;

public sealed record ScriptPackageCompilationRequest(
    string ScriptId,
    string Revision,
    string Source);
