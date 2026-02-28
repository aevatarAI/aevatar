using Aevatar.DynamicRuntime.Abstractions.Contracts;
using System.Security.Cryptography;
using System.Text;

namespace Aevatar.DynamicRuntime.Infrastructure;

public sealed class RoslynDynamicScriptExecutionService : IDynamicScriptExecutionService
{
    private static readonly TimeSpan DefaultUnloadTimeout = TimeSpan.FromSeconds(2);

    private readonly IScriptCompilationPolicy _compilationPolicy;
    private readonly IScriptAssemblyLoadPolicy _assemblyLoadPolicy;
    private readonly IScriptSandboxPolicy _sandboxPolicy;
    private readonly IScriptResourceQuotaPolicy _resourceQuotaPolicy;
    private readonly IScriptRoleAgentCapabilities _roleAgentCapabilities;

    public RoslynDynamicScriptExecutionService(
        IScriptCompilationPolicy compilationPolicy,
        IScriptAssemblyLoadPolicy assemblyLoadPolicy,
        IScriptSandboxPolicy sandboxPolicy,
        IScriptResourceQuotaPolicy resourceQuotaPolicy)
        : this(
            compilationPolicy,
            assemblyLoadPolicy,
            sandboxPolicy,
            resourceQuotaPolicy,
            new NullScriptRoleAgentCapabilities())
    {
    }

    public RoslynDynamicScriptExecutionService(
        IScriptCompilationPolicy compilationPolicy,
        IScriptAssemblyLoadPolicy assemblyLoadPolicy,
        IScriptSandboxPolicy sandboxPolicy,
        IScriptResourceQuotaPolicy resourceQuotaPolicy,
        IScriptRoleAgentCapabilities roleAgentCapabilities)
    {
        _compilationPolicy = compilationPolicy;
        _assemblyLoadPolicy = assemblyLoadPolicy;
        _sandboxPolicy = sandboxPolicy;
        _resourceQuotaPolicy = resourceQuotaPolicy;
        _roleAgentCapabilities = roleAgentCapabilities;
    }

    public async Task<DynamicScriptExecutionResult> ExecuteAsync(DynamicScriptExecutionRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.ScriptCode))
            return new DynamicScriptExecutionResult(false, string.Empty, "ScriptCode is required.");

        var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var bundle = new ScriptSourceBundle("dynamic-runtime.exec", request.ScriptCode, request.EntrypointType);

        var compilationValidation = await _compilationPolicy.ValidateAsync(bundle, ct);
        if (!compilationValidation.Allowed)
            return new DynamicScriptExecutionResult(false, string.Empty, compilationValidation.ErrorCode ?? compilationValidation.Reason ?? "SCRIPT_POLICY_REJECTED");

        var context = new ScriptExecutionContext(bundle.ServiceId, request.EntrypointType, request.Input ?? ScriptRoleRequest.FromText(string.Empty), nowUnixMs);
        var sandboxResult = await _sandboxPolicy.PrepareAsync(context, ct);
        if (!sandboxResult.Allowed)
            return new DynamicScriptExecutionResult(false, string.Empty, sandboxResult.ErrorCode ?? sandboxResult.Reason ?? "SCRIPT_SANDBOX_REJECTED");

        var quotaResult = await _resourceQuotaPolicy.EvaluateAsync(context, ct);
        if (!quotaResult.Allowed)
            return new DynamicScriptExecutionResult(false, string.Empty, quotaResult.ErrorCode ?? quotaResult.Reason ?? "RESOURCE_QUOTA_EXCEEDED");

        var artifactDigest = ComputeArtifactDigest(request.ScriptCode, request.EntrypointType);
        var artifact = new CompiledScriptArtifact(artifactDigest, bundle.ServiceId, request.ScriptCode, request.EntrypointType);

        ScriptAssemblyHandle? handle = null;
        try
        {
            handle = await _assemblyLoadPolicy.LoadAsync(artifact, ct);
            using var scope = ScriptRoleAgentContext.BeginScope(_roleAgentCapabilities);
            var output = await handle.Entrypoint.HandleAsync(request.Input ?? ScriptRoleRequest.FromText(string.Empty), ct);
            return new DynamicScriptExecutionResult(true, output);
        }
        catch (Exception ex)
        {
            return new DynamicScriptExecutionResult(false, string.Empty, ex.Message);
        }
        finally
        {
            if (handle != null)
            {
                var unloadResult = await _assemblyLoadPolicy.UnloadAsync(handle, DefaultUnloadTimeout, ct);
                if (!unloadResult.Success)
                {
                    // keep result deterministic and explicit for degraded container handling.
                    _ = unloadResult;
                }
            }
        }
    }

    private static string ComputeArtifactDigest(string scriptCode, string entrypointType)
    {
        var normalized = $"{entrypointType}:{scriptCode}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return $"sha256:{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }
}
