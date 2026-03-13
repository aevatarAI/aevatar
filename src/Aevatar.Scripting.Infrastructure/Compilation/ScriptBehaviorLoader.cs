using Aevatar.Scripting.Abstractions.Behaviors;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Reflection;
using System.Runtime.Loader;

namespace Aevatar.Scripting.Infrastructure.Compilation;

internal static class ScriptBehaviorLoader
{
    public static LoadedScriptBehavior LoadFromCompilation(CSharpCompilation compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);

        using var assemblyStream = new MemoryStream();
        var emitResult = compilation.Emit(assemblyStream);
        if (!emitResult.Success)
        {
            var diagnostics = string.Join(
                Environment.NewLine,
                emitResult.Diagnostics
                    .Where(x => x.Severity == DiagnosticSeverity.Error)
                    .Select(x => x.ToString()));
            throw new InvalidOperationException("Script behavior compilation failed: " + diagnostics);
        }

        assemblyStream.Position = 0;
        var loadContext = new AssemblyLoadContext(
            "Aevatar.DynamicScript.BehaviorLoadContext." + Guid.NewGuid().ToString("N"),
            isCollectible: true);
        loadContext.Resolving += ResolveFromDefault;

        try
        {
            var assembly = loadContext.LoadFromStream(assemblyStream);
            var behaviorType = assembly
                .GetTypes()
                .Where(type =>
                    !type.IsAbstract &&
                    !type.IsInterface &&
                    typeof(IScriptBehaviorBridge).IsAssignableFrom(type))
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .FirstOrDefault();
            if (behaviorType == null)
                throw new InvalidOperationException("Script must define a non-abstract type implementing IScriptBehaviorBridge.");

            if (Activator.CreateInstance(behaviorType) is not IScriptBehaviorBridge behavior)
                throw new InvalidOperationException($"Failed to instantiate script behavior type `{behaviorType.FullName}`.");

            var descriptor = behavior.Descriptor ?? throw new InvalidOperationException(
                $"Script behavior type `{behaviorType.FullName}` returned a null descriptor.");
            var contract = descriptor.ToContract();
            DisposeBehavior(behavior);
            return new LoadedScriptBehavior(behaviorType, descriptor, contract, loadContext);
        }
        catch
        {
            loadContext.Resolving -= ResolveFromDefault;
            loadContext.Unload();
            throw;
        }
    }

    private static void DisposeBehavior(IScriptBehaviorBridge behavior)
    {
        if (behavior is IDisposable disposable)
            disposable.Dispose();
    }

    private static Assembly? ResolveFromDefault(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        _ = context;
        return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(
            x => string.Equals(x.GetName().Name, assemblyName.Name, StringComparison.Ordinal));
    }

    internal sealed class LoadedScriptBehavior : IAsyncDisposable
    {
        private readonly Type _behaviorType;
        private readonly AssemblyLoadContext _loadContext;
        private int _disposed;

        public LoadedScriptBehavior(
            Type behaviorType,
            ScriptBehaviorDescriptor descriptor,
            ScriptGAgentContract contract,
            AssemblyLoadContext loadContext)
        {
            _behaviorType = behaviorType ?? throw new ArgumentNullException(nameof(behaviorType));
            Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
            Contract = contract ?? throw new ArgumentNullException(nameof(contract));
            _loadContext = loadContext ?? throw new ArgumentNullException(nameof(loadContext));
        }

        public ScriptBehaviorDescriptor Descriptor { get; }

        public ScriptGAgentContract Contract { get; }

        public IScriptBehaviorBridge CreateBehavior()
        {
            ThrowIfDisposed();
            if (Activator.CreateInstance(_behaviorType) is not IScriptBehaviorBridge behavior)
                throw new InvalidOperationException($"Failed to instantiate script behavior type `{_behaviorType.FullName}`.");

            return behavior;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return ValueTask.CompletedTask;

            _loadContext.Resolving -= ResolveFromDefault;
            _loadContext.Unload();
            return ValueTask.CompletedTask;
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(GetType().Name);
        }
    }
}
