using Aevatar.Scripting.Abstractions.Definitions;

namespace Aevatar.Scripting.Core.Compilation;

public sealed record ScriptCompilationResult(
    bool IsSuccess,
    IScriptAgentDefinition? CompiledDefinition,
    IReadOnlyList<string> Diagnostics);
