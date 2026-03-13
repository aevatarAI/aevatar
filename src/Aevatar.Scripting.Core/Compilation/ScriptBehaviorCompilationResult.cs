using Aevatar.Scripting.Core.Artifacts;

namespace Aevatar.Scripting.Core.Compilation;

public sealed record ScriptBehaviorCompilationResult(
    bool IsSuccess,
    ScriptBehaviorArtifact? Artifact,
    IReadOnlyList<string> Diagnostics);
