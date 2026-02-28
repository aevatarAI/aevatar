using Aevatar.DynamicRuntime.Abstractions.Contracts;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Aevatar.DynamicRuntime.Infrastructure;

public sealed class DefaultScriptAssemblyLoadPolicy : IScriptAssemblyLoadPolicy
{
    public async Task<ScriptAssemblyHandle> LoadAsync(CompiledScriptArtifact artifact, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(artifact.ScriptCode))
            throw new InvalidOperationException("SCRIPT_COMPILE_FAILED");

        var options = ScriptOptions.Default
            .WithReferences(
                typeof(IScriptRoleEntrypoint).Assembly,
                typeof(Aevatar.Foundation.Abstractions.EventEnvelope).Assembly,
                typeof(Google.Protobuf.WellKnownTypes.Any).Assembly)
            .WithImports(
                "System",
                "System.Threading",
                "System.Threading.Tasks",
                "Aevatar.DynamicRuntime.Abstractions",
                "Aevatar.DynamicRuntime.Abstractions.Contracts",
                "Aevatar.Foundation.Abstractions",
                "Google.Protobuf",
                "Google.Protobuf.WellKnownTypes");

        try
        {
            var script = CSharpScript.Create<object>(artifact.ScriptCode, options);
            var state = await script.RunAsync(cancellationToken: ct);

            if (state.Exception != null)
                throw new InvalidOperationException(state.Exception.Message);

            var candidate = state.Variables.FirstOrDefault(v => string.Equals(v.Name, "entrypoint", StringComparison.Ordinal))?.Value;
            var entrypoint = candidate as IScriptRoleEntrypoint;
            if (entrypoint == null)
            {
                entrypoint = state.Variables
                    .Select(variable => variable.Value)
                    .OfType<IScriptRoleEntrypoint>()
                    .FirstOrDefault(value => string.Equals(value.GetType().Name, artifact.EntrypointType, StringComparison.Ordinal));
            }

            if (entrypoint == null)
                throw new InvalidOperationException("SCRIPT_ENTRYPOINT_NOT_FOUND");

            return new ScriptAssemblyHandle(artifact.ArtifactDigest, entrypoint, state);
        }
        catch (CompilationErrorException cex)
        {
            throw new InvalidOperationException(string.Join("\n", cex.Diagnostics.Select(item => item.ToString())));
        }
    }

    public Task<UnloadResult> UnloadAsync(ScriptAssemblyHandle handle, TimeSpan timeout, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _ = handle;
        _ = timeout;
        return Task.FromResult(new UnloadResult(true));
    }
}
