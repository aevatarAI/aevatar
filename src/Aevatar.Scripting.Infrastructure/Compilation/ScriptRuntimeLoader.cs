using Aevatar.Scripting.Abstractions.Definitions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Reflection;
using System.Runtime.Loader;

namespace Aevatar.Scripting.Infrastructure.Compilation;

internal static class ScriptRuntimeLoader
{
    public static Task<LoadedScriptRuntime> LoadFromSourceAsync(string source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var syntaxTree = CSharpSyntaxTree.ParseText(source ?? string.Empty);
        var compilation = CSharpCompilation.Create(
            assemblyName: "Aevatar.DynamicScript." + Guid.NewGuid().ToString("N"),
            syntaxTrees: [syntaxTree],
            references: ScriptCompilationMetadataReferences.Build(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return LoadFromCompilationAsync(compilation, ct);
    }

    public static async Task<LoadedScriptRuntime> LoadFromCompilationAsync(
        CSharpCompilation compilation,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ct.ThrowIfCancellationRequested();

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
            {
                throw new InvalidOperationException(
                    "Script must define a non-abstract type implementing IScriptPackageRuntime.");
            }

            if (Activator.CreateInstance(runtimeType) is not IScriptPackageRuntime runtime)
            {
                throw new InvalidOperationException(
                    $"Failed to instantiate script runtime type `{runtimeType.FullName}`.");
            }

            return new LoadedScriptRuntime(runtime, loadContext);
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

    internal sealed class LoadedScriptRuntime : IAsyncDisposable
    {
        private readonly AssemblyLoadContext _loadContext;
        private int _disposed;

        public LoadedScriptRuntime(IScriptPackageRuntime runtime, AssemblyLoadContext loadContext)
        {
            Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _loadContext = loadContext ?? throw new ArgumentNullException(nameof(loadContext));
        }

        public IScriptPackageRuntime Runtime { get; }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            try
            {
                if (Runtime is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync();
                    return;
                }

                if (Runtime is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            finally
            {
                _loadContext.Resolving -= ResolveFromDefault;
                _loadContext.Unload();
            }
        }
    }
}
