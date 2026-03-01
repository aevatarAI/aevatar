using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Compilation;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;

namespace Aevatar.Scripting.Infrastructure.Compilation;

public sealed class RoslynScriptPackageCompiler : IScriptPackageCompiler
{
    private readonly ScriptSandboxPolicy _sandboxPolicy;
    private readonly IScriptExecutionEngine _executionEngine;

    public RoslynScriptPackageCompiler(ScriptSandboxPolicy sandboxPolicy)
        : this(sandboxPolicy, new RoslynScriptExecutionEngine())
    {
    }

    public RoslynScriptPackageCompiler(
        ScriptSandboxPolicy sandboxPolicy,
        IScriptExecutionEngine executionEngine)
    {
        _sandboxPolicy = sandboxPolicy ?? throw new ArgumentNullException(nameof(sandboxPolicy));
        _executionEngine = executionEngine ?? throw new ArgumentNullException(nameof(executionEngine));
    }

    public Task<ScriptPackageCompilationResult> CompileAsync(
        ScriptPackageCompilationRequest request,
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
            return Task.FromResult(new ScriptPackageCompilationResult(false, null, null, diagnostics));

        var sandbox = _sandboxPolicy.Validate(request.Source);
        if (!sandbox.IsValid)
            return Task.FromResult(new ScriptPackageCompilationResult(false, null, null, sandbox.Violations));

        var syntaxTree = CSharpSyntaxTree.ParseText(request.Source);
        var syntaxErrors = syntaxTree
            .GetDiagnostics(ct)
            .Where(x => x.Severity == DiagnosticSeverity.Error)
            .Select(x => x.ToString())
            .ToArray();
        if (syntaxErrors.Length > 0)
            return Task.FromResult(new ScriptPackageCompilationResult(false, null, null, syntaxErrors));

        var semanticCompilation = CSharpCompilation.Create(
            assemblyName: "Aevatar.DynamicScript.Validation." + Guid.NewGuid().ToString("N"),
            syntaxTrees: [syntaxTree],
            references: ScriptCompilationMetadataReferences.Build(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var semanticErrors = semanticCompilation
            .GetDiagnostics(ct)
            .Where(x => x.Severity == DiagnosticSeverity.Error)
            .Select(x => x.ToString())
            .ToArray();
        if (semanticErrors.Length > 0)
            return Task.FromResult(new ScriptPackageCompilationResult(false, null, null, semanticErrors));

        if (!TryEnsureRuntimeImplementation(semanticCompilation, out var runtimeDiagnostic))
        {
            return Task.FromResult(new ScriptPackageCompilationResult(
                false,
                null,
                null,
                [runtimeDiagnostic]));
        }

        var contractManifest = ExtractContractManifest(request.Source, semanticCompilation);
        IScriptPackageDefinition compiledDefinition = new CompiledScriptPackageDefinition(
            request.ScriptId,
            request.Revision,
            request.Source,
            contractManifest,
            _executionEngine);
        return Task.FromResult(new ScriptPackageCompilationResult(
            true,
            compiledDefinition,
            contractManifest,
            Array.Empty<string>()));
    }

    private static ScriptContractManifest ExtractContractManifest(
        string source,
        CSharpCompilation compilation)
    {
        var fallback = ExtractAnnotatedContractManifest(source);
        if (!TryLoadProviderManifest(compilation, out var providerManifest))
            return fallback;

        if (providerManifest == null)
            return fallback;

        return NormalizeManifest(providerManifest);
    }

    private static ScriptContractManifest ExtractAnnotatedContractManifest(string source)
    {
        var inputSchema = MatchSingle(source, @"^\s*//\s*contract\.input\s*:\s*(?<value>.+)\s*$");
        var outputsRaw = MatchSingle(source, @"^\s*//\s*contract\.outputs\s*:\s*(?<value>.+)\s*$");
        var stateSchema = MatchSingle(source, @"^\s*//\s*contract\.state\s*:\s*(?<value>.+)\s*$");
        var readModelSchema = MatchSingle(source, @"^\s*//\s*contract\.readmodel\s*:\s*(?<value>.+)\s*$");

        var outputs = string.IsNullOrWhiteSpace(outputsRaw)
            ? Array.Empty<string>()
            : outputsRaw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToArray();

        return new ScriptContractManifest(
            string.IsNullOrWhiteSpace(inputSchema) ? "unspecified" : inputSchema,
            outputs,
            string.IsNullOrWhiteSpace(stateSchema) ? "unspecified" : stateSchema,
            string.IsNullOrWhiteSpace(readModelSchema) ? "unspecified" : readModelSchema);
    }

    private static bool TryLoadProviderManifest(
        CSharpCompilation compilation,
        out ScriptContractManifest? providerManifest)
    {
        providerManifest = null;

        using var assemblyStream = new MemoryStream();
        var emitResult = compilation.Emit(assemblyStream);
        if (!emitResult.Success)
            return false;

        assemblyStream.Position = 0;
        var loadContext = new AssemblyLoadContext(
            "Aevatar.DynamicScript.ContractManifest." + Guid.NewGuid().ToString("N"),
            isCollectible: true);
        loadContext.Resolving += ResolveFromDefault;

        try
        {
            var assembly = loadContext.LoadFromStream(assemblyStream);
            var runtimeType = assembly
                .GetTypes()
                .Where(type =>
                    !type.IsAbstract &&
                    !type.IsInterface &&
                    typeof(IScriptPackageRuntime).IsAssignableFrom(type))
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .FirstOrDefault();
            if (runtimeType == null)
                return false;

            var instance = Activator.CreateInstance(runtimeType);
            if (instance is IScriptContractProvider provider)
            {
                providerManifest = provider.ContractManifest;
            }

            if (instance is IDisposable disposable)
                disposable.Dispose();

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            loadContext.Resolving -= ResolveFromDefault;
            loadContext.Unload();
        }
    }

    private static ScriptContractManifest NormalizeManifest(ScriptContractManifest manifest)
    {
        var inputSchema = string.IsNullOrWhiteSpace(manifest.InputSchema)
            ? "unspecified"
            : manifest.InputSchema;
        var stateSchema = string.IsNullOrWhiteSpace(manifest.StateSchema)
            ? "unspecified"
            : manifest.StateSchema;
        var readModelSchema = string.IsNullOrWhiteSpace(manifest.ReadModelSchema)
            ? string.IsNullOrWhiteSpace(manifest.ReadModelDefinition?.SchemaId)
                ? "unspecified"
                : manifest.ReadModelDefinition!.SchemaId
            : manifest.ReadModelSchema;
        var outputEvents = manifest.OutputEvents?
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? Array.Empty<string>();
        var readModelStoreCapabilities = manifest.ReadModelStoreCapabilities?
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();

        return new ScriptContractManifest(
            inputSchema,
            outputEvents,
            stateSchema,
            readModelSchema,
            manifest.ReadModelDefinition,
            readModelStoreCapabilities);
    }

    private static Assembly? ResolveFromDefault(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        _ = context;
        return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(
            x => string.Equals(x.GetName().Name, assemblyName.Name, StringComparison.Ordinal));
    }

    private static string MatchSingle(string source, string pattern)
    {
        var match = Regex.Match(source ?? string.Empty, pattern, RegexOptions.Multiline);
        return match.Success ? match.Groups["value"].Value.Trim() : string.Empty;
    }

    private static bool TryEnsureRuntimeImplementation(
        CSharpCompilation compilation,
        out string diagnostic)
    {
        var runtimeInterface = compilation.GetTypeByMetadataName(typeof(IScriptPackageRuntime).FullName!);
        if (runtimeInterface == null)
        {
            diagnostic = "Failed to resolve IScriptPackageRuntime in script compilation context.";
            return false;
        }

        var hasRuntime = EnumerateNamedTypes(compilation.Assembly.GlobalNamespace)
            .Any(type =>
                type.TypeKind == TypeKind.Class &&
                !type.IsAbstract &&
                type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, runtimeInterface)));
        if (!hasRuntime)
        {
            diagnostic = "Script must define a non-abstract type implementing IScriptPackageRuntime.";
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

    private sealed class CompiledScriptPackageDefinition : IScriptPackageDefinition
    {
        private readonly string _source;
        private readonly IScriptExecutionEngine _executionEngine;

        public CompiledScriptPackageDefinition(
            string scriptId,
            string revision,
            string source,
            ScriptContractManifest contractManifest,
            IScriptExecutionEngine executionEngine)
        {
            ScriptId = scriptId;
            Revision = revision;
            _source = source;
            ContractManifest = contractManifest;
            _executionEngine = executionEngine;
        }

        public string ScriptId { get; }
        public string Revision { get; }
        public ScriptContractManifest ContractManifest { get; }

        public Task<ScriptHandlerResult> HandleRequestedEventAsync(
            ScriptRequestedEventEnvelope requestedEvent,
            ScriptExecutionContext context,
            CancellationToken ct)
        {
            return _executionEngine.HandleRequestedEventAsync(_source, requestedEvent, context, ct);
        }

        public ValueTask<IReadOnlyDictionary<string, Any>?> ApplyDomainEventAsync(
            IReadOnlyDictionary<string, Any> currentState,
            ScriptDomainEventEnvelope domainEvent,
            CancellationToken ct)
        {
            return _executionEngine.ApplyDomainEventAsync(_source, currentState, domainEvent, ct);
        }

        public ValueTask<IReadOnlyDictionary<string, Any>?> ReduceReadModelAsync(
            IReadOnlyDictionary<string, Any> currentReadModel,
            ScriptDomainEventEnvelope domainEvent,
            CancellationToken ct)
        {
            return _executionEngine.ReduceReadModelAsync(_source, currentReadModel, domainEvent, ct);
        }
    }
}
