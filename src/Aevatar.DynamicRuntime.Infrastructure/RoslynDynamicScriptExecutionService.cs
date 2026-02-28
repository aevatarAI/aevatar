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
    private readonly ScriptRoleAgentChatClient? _chatClient;

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
            chatClient: null)
    {
    }

    public RoslynDynamicScriptExecutionService(
        IScriptCompilationPolicy compilationPolicy,
        IScriptAssemblyLoadPolicy assemblyLoadPolicy,
        IScriptSandboxPolicy sandboxPolicy,
        IScriptResourceQuotaPolicy resourceQuotaPolicy,
        ScriptRoleAgentChatClient? chatClient)
    {
        _compilationPolicy = compilationPolicy;
        _assemblyLoadPolicy = assemblyLoadPolicy;
        _sandboxPolicy = sandboxPolicy;
        _resourceQuotaPolicy = resourceQuotaPolicy;
        _chatClient = chatClient;
    }

    public async Task<DynamicScriptExecutionResult> ExecuteAsync(DynamicScriptExecutionRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.ScriptCode))
            return new DynamicScriptExecutionResult(false, string.Empty, null, "ScriptCode is required.");
        if (request.Envelope == null)
            return new DynamicScriptExecutionResult(false, string.Empty, null, "Envelope is required.");

        var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var bundle = new ScriptSourceBundle("dynamic-runtime.exec", request.ScriptCode, request.EntrypointType);

        var compilationValidation = await _compilationPolicy.ValidateAsync(bundle, ct);
        if (!compilationValidation.Allowed)
            return new DynamicScriptExecutionResult(false, string.Empty, null, compilationValidation.ErrorCode ?? compilationValidation.Reason ?? "SCRIPT_POLICY_REJECTED");

        var context = new ScriptExecutionContext(bundle.ServiceId, request.EntrypointType, request.Envelope, nowUnixMs);
        var sandboxResult = await _sandboxPolicy.PrepareAsync(context, ct);
        if (!sandboxResult.Allowed)
            return new DynamicScriptExecutionResult(false, string.Empty, null, sandboxResult.ErrorCode ?? sandboxResult.Reason ?? "SCRIPT_SANDBOX_REJECTED");

        var quotaResult = await _resourceQuotaPolicy.EvaluateAsync(context, ct);
        if (!quotaResult.Allowed)
            return new DynamicScriptExecutionResult(false, string.Empty, null, quotaResult.ErrorCode ?? quotaResult.Reason ?? "RESOURCE_QUOTA_EXCEEDED");

        var artifactDigest = ComputeArtifactDigest(request.ScriptCode, request.EntrypointType);
        var artifact = new CompiledScriptArtifact(artifactDigest, bundle.ServiceId, request.ScriptCode, request.EntrypointType);

        ScriptAssemblyHandle? handle = null;
        try
        {
            handle = await _assemblyLoadPolicy.LoadAsync(artifact, ct);
            Func<string, string?, string?, string?, CancellationToken, Task<string>> chatAsync =
                _chatClient is null ? NotConfiguredChatAsync : _chatClient.ChatAsync;
            var runtime = new ScriptRoleAgentRuntime(chatAsync, request.Envelope);
            using var scope = ScriptRoleAgentContext.BeginScope(runtime);
            var execution = await handle.Entrypoint.HandleEventAsync(request.Envelope, ct);
            return new DynamicScriptExecutionResult(true, execution.Output ?? string.Empty, runtime.PublishedEnvelopes.ToArray());
        }
        catch (Exception ex)
        {
            return new DynamicScriptExecutionResult(false, string.Empty, null, ex.Message);
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

    private static Task<string> NotConfiguredChatAsync(
        string prompt,
        string? systemPrompt,
        string? providerName,
        string? model,
        CancellationToken ct)
    {
        _ = prompt;
        _ = systemPrompt;
        _ = providerName;
        _ = model;
        ct.ThrowIfCancellationRequested();
        throw new InvalidOperationException("RoleAgent chat client is not configured for script runtime.");
    }
}
