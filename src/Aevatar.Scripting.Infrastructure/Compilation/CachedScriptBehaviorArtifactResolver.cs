using System.Collections.Concurrent;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Compilation;

namespace Aevatar.Scripting.Infrastructure.Compilation;

public sealed class CachedScriptBehaviorArtifactResolver : IScriptBehaviorArtifactResolver
{
    private readonly ConcurrentDictionary<string, Lazy<ScriptBehaviorArtifact>> _artifacts = new(StringComparer.Ordinal);
    private readonly IScriptBehaviorCompiler _compiler;

    public CachedScriptBehaviorArtifactResolver(IScriptBehaviorCompiler compiler)
    {
        _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
    }

    public ScriptBehaviorArtifact Resolve(ScriptBehaviorArtifactRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var cacheKey = BuildCacheKey(request);
        var lazy = _artifacts.GetOrAdd(
            cacheKey,
            _ => new Lazy<ScriptBehaviorArtifact>(() => CompileOrThrow(request)));

        return lazy.Value;
    }

    private ScriptBehaviorArtifact CompileOrThrow(ScriptBehaviorArtifactRequest request)
    {
        var compilation = _compiler.Compile(request.ToCompilationRequest());
        if (!compilation.IsSuccess || compilation.Artifact == null)
        {
            throw new InvalidOperationException(
                "Script artifact resolution failed: " + string.Join("; ", compilation.Diagnostics));
        }

        return compilation.Artifact;
    }

    private static string BuildCacheKey(ScriptBehaviorArtifactRequest request)
    {
        return string.Concat(
            request.ScriptId,
            "|",
            request.Revision,
            "|",
            request.ResolvedPackageHash,
            "|",
            request.Package.EntryBehaviorTypeName ?? string.Empty);
    }
}
