using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Core.Artifacts;
using Aevatar.Scripting.Core.Compilation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Aevatar.Scripting.Infrastructure.Compilation;

public sealed class RoslynScriptBehaviorCompiler : IScriptBehaviorCompiler
{
    private readonly ScriptSandboxPolicy _sandboxPolicy;
    private readonly IScriptProtoCompiler _protoCompiler;

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

        var diagnostics = new List<string>();
        if (string.IsNullOrWhiteSpace(request.ScriptId))
            diagnostics.Add("ScriptId is required.");
        if (string.IsNullOrWhiteSpace(request.Revision))
            diagnostics.Add("Revision is required.");
        var normalizedPackage = request.Package.Normalize();
        var csharpSources = normalizedPackage.CSharpSources;
        if (csharpSources.Count == 0)
            diagnostics.Add("At least one C# source file is required in the script package.");
        if (diagnostics.Count > 0)
            return new ScriptBehaviorCompilationResult(false, null, diagnostics);

        foreach (var sourceFile in csharpSources)
        {
            var sandbox = _sandboxPolicy.Validate(sourceFile.Content);
            if (!sandbox.IsValid)
            {
                return new ScriptBehaviorCompilationResult(
                    false,
                    null,
                    sandbox.Violations.Select(x => $"{sourceFile.Path}: {x}").ToArray());
            }
        }

        var syntaxTrees = new List<SyntaxTree>(csharpSources.Count);
        foreach (var sourceFile in csharpSources)
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(sourceFile.Content, path: sourceFile.Path);
            var syntaxErrors = syntaxTree
                .GetDiagnostics()
                .Where(x => x.Severity == DiagnosticSeverity.Error)
                .Select(x => x.ToString())
                .ToArray();
            if (syntaxErrors.Length > 0)
                return new ScriptBehaviorCompilationResult(false, null, syntaxErrors);
            syntaxTrees.Add(syntaxTree);
        }

        var protoCompilation = _protoCompiler.Compile(request);
        if (!protoCompilation.IsSuccess)
            return new ScriptBehaviorCompilationResult(false, null, protoCompilation.Diagnostics);
        foreach (var generatedSource in protoCompilation.GeneratedSources)
            syntaxTrees.Add(CSharpSyntaxTree.ParseText(generatedSource.Content, path: generatedSource.Path));

        var semanticCompilation = CSharpCompilation.Create(
            assemblyName: "Aevatar.DynamicScript.Validation." + Guid.NewGuid().ToString("N"),
            syntaxTrees: syntaxTrees,
            references: ScriptCompilationMetadataReferences.Build(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var semanticErrors = semanticCompilation
            .GetDiagnostics()
            .Where(x => x.Severity == DiagnosticSeverity.Error)
            .Select(x => x.ToString())
            .ToArray();
        if (semanticErrors.Length > 0)
            return new ScriptBehaviorCompilationResult(false, null, semanticErrors);

        if (!TryEnsureBehaviorImplementation(semanticCompilation, out var behaviorDiagnostic))
            return new ScriptBehaviorCompilationResult(false, null, [behaviorDiagnostic]);

        ScriptBehaviorLoader.LoadedScriptBehavior loadedBehavior;
        try
        {
            loadedBehavior = ScriptBehaviorLoader.LoadFromCompilation(
                semanticCompilation,
                protoCompilation.DescriptorSet,
                request.Package.EntryBehaviorTypeName);
        }
        catch (Exception ex)
        {
            return new ScriptBehaviorCompilationResult(false, null, [ex.Message]);
        }

        var artifact = new ScriptBehaviorArtifact(
            request.ScriptId,
            request.Revision,
            request.ResolvedPackageHash,
            loadedBehavior.Descriptor,
            loadedBehavior.Contract ?? ScriptGAgentContract.Empty,
            loadedBehavior.CreateBehavior,
            loadedBehavior.DisposeAsync);
        return new ScriptBehaviorCompilationResult(true, artifact, Array.Empty<string>());
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
}
