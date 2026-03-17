using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Compilation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Aevatar.Scripting.Infrastructure.Compilation;

public sealed class RoslynScriptBehaviorCompiler : IScriptBehaviorCompiler
{
    private readonly ScriptSandboxPolicy _sandboxPolicy;
    private readonly IScriptProtoCompiler _protoCompiler;

    private sealed record ValidationResult(
        bool IsSuccess,
        ScriptSourcePackage? Package,
        IReadOnlyList<ScriptSourceFile> CSharpSources,
        IReadOnlyList<string> Diagnostics);

    public RoslynScriptBehaviorCompiler(
        ScriptSandboxPolicy sandboxPolicy,
        IScriptProtoCompiler? protoCompiler = null)
    {
        _sandboxPolicy = sandboxPolicy ?? throw new ArgumentNullException(nameof(sandboxPolicy));
        _protoCompiler = protoCompiler ?? new GrpcToolsScriptProtoCompiler();
    }

    public ScriptBehaviorCompilationResult Compile(ScriptBehaviorCompilationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validation = ValidateRequest(request);
        if (!validation.IsSuccess)
            return Failure(validation.Diagnostics);

        var sandboxResult = ValidateSandbox(validation.CSharpSources);
        if (sandboxResult != null)
            return sandboxResult;

        var syntaxTreeResult = BuildSourceSyntaxTrees(validation.CSharpSources);
        if (!syntaxTreeResult.IsSuccess)
            return Failure(syntaxTreeResult.Diagnostics);

        var protoCompilation = _protoCompiler.Compile(request);
        if (!protoCompilation.IsSuccess)
            return Failure(protoCompilation.Diagnostics);

        var syntaxTrees = AppendGeneratedSyntaxTrees(syntaxTreeResult.SyntaxTrees, protoCompilation);
        var semanticCompilation = CreateSemanticCompilation(syntaxTrees);
        var semanticErrors = CollectCompilationErrors(semanticCompilation);
        if (semanticErrors.Length > 0)
            return Failure(semanticErrors);

        if (!TryEnsureBehaviorImplementation(semanticCompilation, out var behaviorDiagnostic))
            return Failure([behaviorDiagnostic]);

        var loadedBehaviorResult = TryLoadBehavior(validation.Package!, semanticCompilation, protoCompilation);
        if (!loadedBehaviorResult.IsSuccess)
            return Failure(loadedBehaviorResult.Diagnostics);

        return Success(BuildArtifact(request, loadedBehaviorResult.LoadedBehavior!));
    }

    private static ValidationResult ValidateRequest(ScriptBehaviorCompilationRequest request)
    {
        var diagnostics = new List<string>();
        if (string.IsNullOrWhiteSpace(request.ScriptId))
            diagnostics.Add("ScriptId is required.");
        if (string.IsNullOrWhiteSpace(request.Revision))
            diagnostics.Add("Revision is required.");

        var normalizedPackage = request.Package.Normalize();
        var csharpSources = normalizedPackage.CSharpSources;
        if (csharpSources.Count == 0)
            diagnostics.Add("At least one C# source file is required in the script package.");

        return diagnostics.Count > 0
            ? new ValidationResult(false, null, Array.Empty<ScriptSourceFile>(), diagnostics)
            : new ValidationResult(true, normalizedPackage, csharpSources, Array.Empty<string>());
    }

    private ScriptBehaviorCompilationResult? ValidateSandbox(IReadOnlyList<ScriptSourceFile> csharpSources)
    {
        foreach (var sourceFile in csharpSources)
        {
            var sandbox = _sandboxPolicy.Validate(sourceFile.Content);
            if (!sandbox.IsValid)
            {
                return Failure(sandbox.Violations.Select(x => $"{sourceFile.Path}: {x}").ToArray());
            }
        }

        return null;
    }

    private static (bool IsSuccess, IReadOnlyList<SyntaxTree> SyntaxTrees, IReadOnlyList<string> Diagnostics) BuildSourceSyntaxTrees(
        IReadOnlyList<ScriptSourceFile> csharpSources)
    {
        var syntaxTrees = new List<SyntaxTree>(csharpSources.Count);
        foreach (var sourceFile in csharpSources)
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(sourceFile.Content, path: sourceFile.Path);
            var syntaxErrors = CollectSyntaxErrors(syntaxTree);
            if (syntaxErrors.Length > 0)
                return (false, Array.Empty<SyntaxTree>(), syntaxErrors);

            syntaxTrees.Add(syntaxTree);
        }

        return (true, syntaxTrees, Array.Empty<string>());
    }

    private static string[] CollectSyntaxErrors(SyntaxTree syntaxTree)
    {
        return syntaxTree
            .GetDiagnostics()
            .Where(x => x.Severity == DiagnosticSeverity.Error)
            .Select(x => x.ToString())
            .ToArray();
    }

    private static IReadOnlyList<SyntaxTree> AppendGeneratedSyntaxTrees(
        IReadOnlyList<SyntaxTree> syntaxTrees,
        ScriptProtoCompilationResult protoCompilation)
    {
        var result = new List<SyntaxTree>(syntaxTrees.Count + protoCompilation.GeneratedSources.Count);
        result.AddRange(syntaxTrees);
        foreach (var generatedSource in protoCompilation.GeneratedSources)
            result.Add(CSharpSyntaxTree.ParseText(generatedSource.Content, path: generatedSource.Path));

        return result;
    }

    private static CSharpCompilation CreateSemanticCompilation(IReadOnlyList<SyntaxTree> syntaxTrees)
    {
        return CSharpCompilation.Create(
            assemblyName: "Aevatar.DynamicScript.Validation." + Guid.NewGuid().ToString("N"),
            syntaxTrees: syntaxTrees,
            references: ScriptCompilationMetadataReferences.Build(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static string[] CollectCompilationErrors(CSharpCompilation compilation)
    {
        return compilation
            .GetDiagnostics()
            .Where(x => x.Severity == DiagnosticSeverity.Error)
            .Select(x => x.ToString())
            .ToArray();
    }

    private static (bool IsSuccess, ScriptBehaviorLoader.LoadedScriptBehavior? LoadedBehavior, IReadOnlyList<string> Diagnostics) TryLoadBehavior(
        ScriptSourcePackage package,
        CSharpCompilation semanticCompilation,
        ScriptProtoCompilationResult protoCompilation)
    {
        try
        {
            var loadedBehavior = ScriptBehaviorLoader.LoadFromCompilation(
                semanticCompilation,
                protoCompilation.DescriptorSet,
                package.EntryBehaviorTypeName);
            return (true, loadedBehavior, Array.Empty<string>());
        }
        catch (Exception ex)
        {
            return (false, null, [ex.Message]);
        }
    }

    private static ScriptBehaviorArtifact BuildArtifact(
        ScriptBehaviorCompilationRequest request,
        ScriptBehaviorLoader.LoadedScriptBehavior loadedBehavior)
    {
        return new ScriptBehaviorArtifact(
            request.ScriptId,
            request.Revision,
            request.ResolvedPackageHash,
            loadedBehavior.Descriptor,
            loadedBehavior.Contract ?? ScriptGAgentContract.Empty,
            loadedBehavior.CreateBehavior,
            loadedBehavior.DisposeAsync);
    }

    private static ScriptBehaviorCompilationResult Failure(IReadOnlyList<string> diagnostics) =>
        new(false, null, diagnostics);

    private static ScriptBehaviorCompilationResult Success(ScriptBehaviorArtifact artifact) =>
        new(true, artifact, Array.Empty<string>());

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
}
