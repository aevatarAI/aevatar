using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Aevatar.Scripting.Core.Compilation;

public sealed class RoslynScriptAgentCompiler : IScriptAgentCompiler
{
    private readonly ScriptSandboxPolicy _sandboxPolicy;

    public RoslynScriptAgentCompiler(ScriptSandboxPolicy sandboxPolicy)
    {
        _sandboxPolicy = sandboxPolicy;
    }

    public Task<ScriptCompilationResult> CompileAsync(
        ScriptCompilationRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var diagnostics = new List<string>();
        if (string.IsNullOrWhiteSpace(request.ScriptId))
            diagnostics.Add("ScriptId is required.");
        if (string.IsNullOrWhiteSpace(request.Revision))
            diagnostics.Add("Revision is required.");
        if (string.IsNullOrWhiteSpace(request.Source))
            diagnostics.Add("Source is required.");
        if (diagnostics.Count > 0)
            return Task.FromResult(new ScriptCompilationResult(false, null, diagnostics));

        var sandbox = _sandboxPolicy.Validate(request.Source);
        if (!sandbox.IsValid)
            return Task.FromResult(new ScriptCompilationResult(false, null, sandbox.Violations));

        var syntaxTree = CSharpSyntaxTree.ParseText(request.Source);
        var syntaxErrors = syntaxTree
            .GetDiagnostics(ct)
            .Where(x => x.Severity == DiagnosticSeverity.Error)
            .Select(x => x.ToString())
            .ToArray();

        if (syntaxErrors.Length > 0)
            return Task.FromResult(new ScriptCompilationResult(false, null, syntaxErrors));

        IScriptAgentDefinition compiledDefinition = new CompiledScriptAgentDefinition(
            request.ScriptId,
            request.Revision);
        return Task.FromResult(
            new ScriptCompilationResult(
                true,
                compiledDefinition,
                Array.Empty<string>()));
    }

    private sealed class CompiledScriptAgentDefinition : IScriptAgentDefinition
    {
        public CompiledScriptAgentDefinition(
            string scriptId,
            string revision)
        {
            ScriptId = scriptId;
            Revision = revision;
        }

        public string ScriptId { get; }
        public string Revision { get; }

        public Task<ScriptDecisionResult> DecideAsync(
            ScriptExecutionContext context,
            CancellationToken ct)
        {
            _ = context;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ScriptDecisionResult(Array.Empty<IMessage>()));
        }
    }
}
