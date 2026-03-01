using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Reflection;
using System.Runtime.Loader;

namespace Aevatar.Scripting.Core.Compilation;

public sealed class RoslynScriptExecutionEngine : IScriptExecutionEngine
{
    public async Task<ScriptHandlerResult> HandleRequestedEventAsync(
        string source,
        ScriptRequestedEventEnvelope requestedEvent,
        ScriptExecutionContext context,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source))
            return new ScriptHandlerResult(Array.Empty<IMessage>());

        await using var loaded = await LoadRuntimeAsync(source, ct);
        return await loaded.Runtime.HandleRequestedEventAsync(requestedEvent, context, ct)
            .ConfigureAwait(false);
    }

    public async ValueTask<string> ApplyDomainEventAsync(
        string source,
        string currentStateJson,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source))
            return currentStateJson ?? string.Empty;

        await using var loaded = await LoadRuntimeAsync(source, ct);
        var next = await loaded.Runtime.ApplyDomainEventAsync(
            currentStateJson ?? string.Empty,
            domainEvent,
            ct).ConfigureAwait(false);
        return next ?? string.Empty;
    }

    public async ValueTask<string> ReduceReadModelAsync(
        string source,
        string currentReadModelJson,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source))
            return currentReadModelJson ?? string.Empty;

        await using var loaded = await LoadRuntimeAsync(source, ct);
        var next = await loaded.Runtime.ReduceReadModelAsync(
            currentReadModelJson ?? string.Empty,
            domainEvent,
            ct).ConfigureAwait(false);
        return next ?? string.Empty;
    }

    private static async Task<LoadedRuntime> LoadRuntimeAsync(string source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            assemblyName: "Aevatar.DynamicScript." + Guid.NewGuid().ToString("N"),
            syntaxTrees: [syntaxTree],
            references: ScriptCompilationMetadataReferences.Build(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        await using var assemblyStream = new MemoryStream();
        var emitResult = compilation.Emit(assemblyStream, cancellationToken: ct);
        if (!emitResult.Success)
        {
            var diagnostics = string.Join(
                Environment.NewLine,
                emitResult.Diagnostics
                    .Where(x => x.Severity == DiagnosticSeverity.Error)
                    .Select(x => x.ToString()));
            throw new InvalidOperationException("Script execution compilation failed: " + diagnostics);
        }

        assemblyStream.Position = 0;
        var loadContext = new AssemblyLoadContext(
            "Aevatar.DynamicScript.LoadContext." + Guid.NewGuid().ToString("N"),
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
                throw new InvalidOperationException(
                    "Script must define a non-abstract type implementing IScriptPackageRuntime.");

            if (Activator.CreateInstance(runtimeType) is not IScriptPackageRuntime runtime)
                throw new InvalidOperationException(
                    $"Failed to instantiate script runtime type `{runtimeType.FullName}`.");

            return new LoadedRuntime(runtime, loadContext);
        }
        catch
        {
            loadContext.Resolving -= ResolveFromDefault;
            loadContext.Unload();
            throw;
        }
    }

    private static Assembly? ResolveFromDefault(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        _ = context;
        return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(
            x => string.Equals(x.GetName().Name, assemblyName.Name, StringComparison.Ordinal));
    }

    private sealed class LoadedRuntime : IAsyncDisposable
    {
        private readonly AssemblyLoadContext _loadContext;

        public LoadedRuntime(IScriptPackageRuntime runtime, AssemblyLoadContext loadContext)
        {
            Runtime = runtime;
            _loadContext = loadContext;
        }

        public IScriptPackageRuntime Runtime { get; }

        public ValueTask DisposeAsync()
        {
            _loadContext.Resolving -= ResolveFromDefault;
            _loadContext.Unload();
            return ValueTask.CompletedTask;
        }
    }
}
