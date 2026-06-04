using Aevatar.AI.ToolProviders.Ornn.Publishing;
using Aevatar.Scripting.Core.Compilation;

namespace Aevatar.Bootstrap.Extensions.AI.OrnnPublishing;

public sealed class ScriptOrnnSkillPublishAssetValidator : IOrnnSkillPublishAssetValidator
{
    private readonly IScriptBehaviorCompiler? _compiler;

    public ScriptOrnnSkillPublishAssetValidator(IScriptBehaviorCompiler? compiler = null)
    {
        _compiler = compiler;
    }

    public string AssetKind => OrnnSkillPublishValidationPipeline.ScriptAssetKind;

    public Task<IReadOnlyList<OrnnSkillPublishDiagnostic>> ValidateAsync(
        OrnnSkillPublishRequest request,
        CancellationToken ct = default)
    {
        if (request.Scripts.Count == 0)
            return Task.FromResult<IReadOnlyList<OrnnSkillPublishDiagnostic>>([]);

        if (_compiler == null)
        {
            return Task.FromResult<IReadOnlyList<OrnnSkillPublishDiagnostic>>(
            [
                new OrnnSkillPublishDiagnostic(
                    "missing_script_compiler",
                    "No script compiler is registered for Ornn script publish validation.")
            ]);
        }

        ct.ThrowIfCancellationRequested();
        var package = new ScriptSourcePackage(
            ScriptSourcePackage.CurrentFormat,
            request.Scripts
                .Where(static script => script.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .Select(static script => new ScriptSourceFile(script.Path, script.Content))
                .ToArray(),
            request.Scripts
                .Where(static script => script.Path.EndsWith(".proto", StringComparison.OrdinalIgnoreCase))
                .Select(static script => new ScriptSourceFile(script.Path, script.Content))
                .ToArray(),
            string.Empty);

        var compilation = _compiler.Compile(new ScriptBehaviorCompilationRequest(
            request.Name,
            request.Version,
            package));

        var diagnostics = compilation.Diagnostics
            .Select(static diagnostic => new OrnnSkillPublishDiagnostic(
                "invalid_script",
                diagnostic,
                "scripts"))
            .ToArray();
        return Task.FromResult<IReadOnlyList<OrnnSkillPublishDiagnostic>>(diagnostics);
    }
}
