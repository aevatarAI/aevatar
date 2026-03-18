using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Infrastructure.Compilation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Aevatar.Tools.Cli.Hosting;

internal sealed class ScriptEditorValidationService
{
    private readonly ScriptSandboxPolicy _sandboxPolicy;
    private readonly IScriptProtoCompiler _protoCompiler;

    public ScriptEditorValidationService(
        ScriptSandboxPolicy? sandboxPolicy = null,
        IScriptProtoCompiler? protoCompiler = null)
    {
        _sandboxPolicy = sandboxPolicy ?? new ScriptSandboxPolicy();
        _protoCompiler = protoCompiler ?? new GrpcToolsScriptProtoCompiler();
    }

    public ScriptEditorValidationResult Validate(
        string scriptId,
        string revision,
        string source)
    {
        var normalizedScriptId = AppStudioEndpoints.NormalizeStudioDocumentId(scriptId, "script");
        var normalizedRevision = AppStudioEndpoints.NormalizeStudioDocumentId(revision, "draft");

        try
        {
            var request = ScriptBehaviorCompilationRequest.FromPersistedSource(
                normalizedScriptId,
                normalizedRevision,
                source ?? string.Empty);
            var package = request.Package.Normalize();
            var primarySourcePath = package.CSharpSources.FirstOrDefault()?.NormalizedPath ?? "Behavior.cs";
            var diagnostics = new List<ScriptEditorValidationDiagnostic>();

            if (string.IsNullOrWhiteSpace(source))
            {
                diagnostics.Add(new ScriptEditorValidationDiagnostic(
                    "error",
                    "SCRIPT_SOURCE_REQUIRED",
                    "Script source is required.",
                    primarySourcePath,
                    null,
                    null,
                    null,
                    null,
                    "host"));
                return BuildResult(normalizedScriptId, normalizedRevision, primarySourcePath, diagnostics);
            }

            if (package.CSharpSources.Count == 0)
            {
                diagnostics.Add(new ScriptEditorValidationDiagnostic(
                    "error",
                    "SCRIPT_SOURCE_MISSING",
                    "At least one C# source file is required in the script package.",
                    primarySourcePath,
                    null,
                    null,
                    null,
                    null,
                    "host"));
                return BuildResult(normalizedScriptId, normalizedRevision, primarySourcePath, diagnostics);
            }

            var syntaxTrees = new List<SyntaxTree>(package.CSharpSources.Count);
            var syntaxDiagnostics = new List<Diagnostic>();

            foreach (var sourceFile in package.CSharpSources)
            {
                var sandboxResult = _sandboxPolicy.Validate(sourceFile.Content ?? string.Empty);
                diagnostics.AddRange(sandboxResult.Violations.Select(violation => new ScriptEditorValidationDiagnostic(
                    "error",
                    "SCRIPT_SANDBOX",
                    violation,
                    sourceFile.NormalizedPath,
                    null,
                    null,
                    null,
                    null,
                    "sandbox")));

                var syntaxTree = CSharpSyntaxTree.ParseText(
                    sourceFile.Content ?? string.Empty,
                    path: sourceFile.NormalizedPath);
                syntaxTrees.Add(syntaxTree);
                syntaxDiagnostics.AddRange(syntaxTree.GetDiagnostics().Where(ShouldExposeDiagnostic));
            }

            if (syntaxDiagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            {
                diagnostics.AddRange(ConvertDiagnostics(syntaxDiagnostics));
                return BuildResult(normalizedScriptId, normalizedRevision, primarySourcePath, diagnostics);
            }

            var protoCompilation = _protoCompiler.Compile(request);
            if (!protoCompilation.IsSuccess)
            {
                diagnostics.AddRange(protoCompilation.Diagnostics.Select(message => new ScriptEditorValidationDiagnostic(
                    "error",
                    "SCRIPT_PROTO",
                    message,
                    string.Empty,
                    null,
                    null,
                    null,
                    null,
                    "proto")));
                return BuildResult(normalizedScriptId, normalizedRevision, primarySourcePath, diagnostics);
            }

            var compilation = CreateCompilation(syntaxTrees, protoCompilation.GeneratedSources);
            var compilationDiagnostics = compilation
                .GetDiagnostics()
                .Where(ShouldExposeDiagnostic)
                .ToArray();

            diagnostics.AddRange(ConvertDiagnostics(compilationDiagnostics));

            if (!compilationDiagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) &&
                !TryEnsureBehaviorImplementation(compilation, out var behaviorDiagnostic))
            {
                diagnostics.Add(new ScriptEditorValidationDiagnostic(
                    "error",
                    "SCRIPT_BEHAVIOR_REQUIRED",
                    behaviorDiagnostic,
                    primarySourcePath,
                    null,
                    null,
                    null,
                    null,
                    "runtime"));
            }

            return BuildResult(normalizedScriptId, normalizedRevision, primarySourcePath, diagnostics);
        }
        catch (Exception ex)
        {
            return BuildResult(
                normalizedScriptId,
                normalizedRevision,
                "Behavior.cs",
                [
                    new ScriptEditorValidationDiagnostic(
                        "error",
                        "SCRIPT_VALIDATION_UNEXPECTED",
                        ex.Message,
                        string.Empty,
                        null,
                        null,
                        null,
                        null,
                        "host"),
                ]);
        }
    }

    private static ScriptEditorValidationResult BuildResult(
        string scriptId,
        string revision,
        string primarySourcePath,
        IReadOnlyList<ScriptEditorValidationDiagnostic> diagnostics)
    {
        var normalizedDiagnostics = diagnostics
            .Where(static diagnostic => diagnostic != null)
            .Distinct(ScriptEditorValidationDiagnosticComparer.Instance)
            .OrderBy(static diagnostic => SeverityRank(diagnostic.Severity))
            .ThenBy(static diagnostic => diagnostic.StartLine ?? int.MaxValue)
            .ThenBy(static diagnostic => diagnostic.StartColumn ?? int.MaxValue)
            .ThenBy(static diagnostic => diagnostic.FilePath, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .ToArray();

        var errorCount = normalizedDiagnostics.Count(static diagnostic => string.Equals(diagnostic.Severity, "error", StringComparison.OrdinalIgnoreCase));
        var warningCount = normalizedDiagnostics.Count(static diagnostic => string.Equals(diagnostic.Severity, "warning", StringComparison.OrdinalIgnoreCase));

        return new ScriptEditorValidationResult(
            errorCount == 0,
            scriptId,
            revision,
            primarySourcePath,
            errorCount,
            warningCount,
            normalizedDiagnostics);
    }

    private static int SeverityRank(string severity) =>
        string.Equals(severity, "error", StringComparison.OrdinalIgnoreCase) ? 0 :
        string.Equals(severity, "warning", StringComparison.OrdinalIgnoreCase) ? 1 :
        2;

    private static bool ShouldExposeDiagnostic(Diagnostic diagnostic) =>
        diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning or DiagnosticSeverity.Info;

    private static CSharpCompilation CreateCompilation(
        IReadOnlyList<SyntaxTree> syntaxTrees,
        IReadOnlyList<ScriptSourceFile> generatedSources)
    {
        var allSyntaxTrees = new List<SyntaxTree>(syntaxTrees.Count + generatedSources.Count);
        allSyntaxTrees.AddRange(syntaxTrees);
        foreach (var generatedSource in generatedSources)
        {
            allSyntaxTrees.Add(CSharpSyntaxTree.ParseText(
                generatedSource.Content ?? string.Empty,
                path: generatedSource.NormalizedPath));
        }

        return CSharpCompilation.Create(
            assemblyName: "Aevatar.DynamicScript.EditorValidation." + Guid.NewGuid().ToString("N"),
            syntaxTrees: allSyntaxTrees,
            references: ScriptCompilationMetadataReferences.Build(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static IReadOnlyList<ScriptEditorValidationDiagnostic> ConvertDiagnostics(
        IReadOnlyCollection<Diagnostic> diagnostics)
    {
        return diagnostics
            .Select(ConvertDiagnostic)
            .ToArray();
    }

    private static ScriptEditorValidationDiagnostic ConvertDiagnostic(Diagnostic diagnostic)
    {
        var severity = diagnostic.Severity switch
        {
            DiagnosticSeverity.Error => "error",
            DiagnosticSeverity.Warning => "warning",
            _ => "info",
        };

        var filePath = string.Empty;
        int? startLine = null;
        int? startColumn = null;
        int? endLine = null;
        int? endColumn = null;

        if (diagnostic.Location != Location.None && diagnostic.Location.IsInSource)
        {
            var span = diagnostic.Location.GetLineSpan();
            if (span.IsValid)
            {
                filePath = ScriptSourceFile.NormalizePath(span.Path);
                startLine = span.StartLinePosition.Line + 1;
                startColumn = span.StartLinePosition.Character + 1;
                endLine = span.EndLinePosition.Line + 1;
                endColumn = span.EndLinePosition.Character + 1;

                if (startLine == endLine && startColumn == endColumn)
                    endColumn += 1;
            }
        }

        return new ScriptEditorValidationDiagnostic(
            severity,
            diagnostic.Id,
            diagnostic.GetMessage(),
            filePath,
            startLine,
            startColumn,
            endLine,
            endColumn,
            "compiler");
    }

    private static bool TryEnsureBehaviorImplementation(
        CSharpCompilation compilation,
        out string diagnostic)
    {
        var behaviorInterface = compilation.GetTypeByMetadataName(typeof(IScriptBehaviorBridge).FullName!);
        if (behaviorInterface == null)
        {
            diagnostic = "Failed to resolve IScriptBehaviorBridge in script compilation context.";
            return false;
        }

        var hasBehavior = EnumerateNamedTypes(compilation.Assembly.GlobalNamespace)
            .Any(type =>
                type.TypeKind == TypeKind.Class &&
                !type.IsAbstract &&
                type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, behaviorInterface)));
        if (!hasBehavior)
        {
            diagnostic = "Script must define a non-abstract type implementing IScriptBehaviorBridge.";
            return false;
        }

        diagnostic = string.Empty;
        return true;
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateNamedTypes(INamespaceSymbol ns)
    {
        foreach (var member in ns.GetMembers())
        {
            switch (member)
            {
                case INamespaceSymbol nestedNamespace:
                    foreach (var nestedType in EnumerateNamedTypes(nestedNamespace))
                        yield return nestedType;
                    break;
                case INamedTypeSymbol namedType:
                    foreach (var type in EnumerateNamedTypes(namedType))
                        yield return type;
                    break;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateNamedTypes(INamedTypeSymbol type)
    {
        yield return type;
        foreach (var nested in type.GetTypeMembers())
        {
            foreach (var item in EnumerateNamedTypes(nested))
                yield return item;
        }
    }

    private sealed class ScriptEditorValidationDiagnosticComparer : IEqualityComparer<ScriptEditorValidationDiagnostic>
    {
        public static ScriptEditorValidationDiagnosticComparer Instance { get; } = new();

        public bool Equals(ScriptEditorValidationDiagnostic? x, ScriptEditorValidationDiagnostic? y)
        {
            if (ReferenceEquals(x, y))
                return true;
            if (x is null || y is null)
                return false;

            return string.Equals(x.Severity, y.Severity, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(x.Code, y.Code, StringComparison.Ordinal) &&
                   string.Equals(x.Message, y.Message, StringComparison.Ordinal) &&
                   string.Equals(x.FilePath, y.FilePath, StringComparison.Ordinal) &&
                   x.StartLine == y.StartLine &&
                   x.StartColumn == y.StartColumn &&
                   x.EndLine == y.EndLine &&
                   x.EndColumn == y.EndColumn &&
                   string.Equals(x.Origin, y.Origin, StringComparison.Ordinal);
        }

        public int GetHashCode(ScriptEditorValidationDiagnostic obj)
        {
            var hash = new HashCode();
            hash.Add(obj.Severity, StringComparer.OrdinalIgnoreCase);
            hash.Add(obj.Code, StringComparer.Ordinal);
            hash.Add(obj.Message, StringComparer.Ordinal);
            hash.Add(obj.FilePath, StringComparer.Ordinal);
            hash.Add(obj.StartLine);
            hash.Add(obj.StartColumn);
            hash.Add(obj.EndLine);
            hash.Add(obj.EndColumn);
            hash.Add(obj.Origin, StringComparer.Ordinal);
            return hash.ToHashCode();
        }
    }
}

internal sealed record ScriptEditorValidationResult(
    bool Success,
    string ScriptId,
    string ScriptRevision,
    string PrimarySourcePath,
    int ErrorCount,
    int WarningCount,
    IReadOnlyList<ScriptEditorValidationDiagnostic> Diagnostics);

internal sealed record ScriptEditorValidationDiagnostic(
    string Severity,
    string Code,
    string Message,
    string FilePath,
    int? StartLine,
    int? StartColumn,
    int? EndLine,
    int? EndColumn,
    string Origin);
