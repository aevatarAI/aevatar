using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Compilation;

namespace Aevatar.Scripting.Infrastructure.Compilation;

public sealed class CachedScriptBehaviorArtifactResolver : IScriptBehaviorArtifactResolver
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ScriptBehaviorArtifact> _artifacts = new(StringComparer.Ordinal);
    private readonly IScriptBehaviorCompiler _compiler;

    public CachedScriptBehaviorArtifactResolver(IScriptBehaviorCompiler compiler)
    {
        _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
    }

    public ScriptBehaviorArtifact Resolve(ScriptBehaviorArtifactRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var cacheKey = BuildCacheKey(request);
        lock (_gate)
        {
            if (_artifacts.TryGetValue(cacheKey, out var cached))
                return cached;
        }

        var compilation = _compiler.Compile(request.ToCompilationRequest());
        if (!compilation.IsSuccess || compilation.Artifact == null)
        {
            throw new InvalidOperationException(
                "Script artifact resolution failed: " + string.Join("; ", compilation.Diagnostics));
        }

        lock (_gate)
        {
            if (_artifacts.TryGetValue(cacheKey, out var cached))
            {
                _ = compilation.Artifact.DisposeAsync();
                return cached;
            }

            _artifacts[cacheKey] = compilation.Artifact;
            return compilation.Artifact;
        }
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
