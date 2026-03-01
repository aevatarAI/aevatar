namespace Aevatar.Scripting.Core.Compilation;

public sealed record ScriptSandboxValidationResult(
    bool IsValid,
    IReadOnlyList<string> Violations);
