namespace Aevatar.Scripting.Infrastructure.Compilation;

public sealed record ScriptSandboxValidationResult(
    bool IsValid,
    IReadOnlyList<string> Violations);
