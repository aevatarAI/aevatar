using Aevatar.Scripting.Abstractions.Definitions;

namespace Aevatar.Scripting.Core.Compilation;

public sealed record ScriptPackageCompilationResult(
    bool IsSuccess,
    IScriptPackageDefinition? CompiledDefinition,
    ScriptContractManifest? ContractManifest,
    IReadOnlyList<string> Diagnostics);
