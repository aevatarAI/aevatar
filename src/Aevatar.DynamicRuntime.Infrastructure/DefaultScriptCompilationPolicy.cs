using Aevatar.DynamicRuntime.Abstractions.Contracts;

namespace Aevatar.DynamicRuntime.Infrastructure;

public sealed class DefaultScriptCompilationPolicy : IScriptCompilationPolicy
{
    public IReadOnlySet<string> AllowedReferences { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "System.Runtime",
        "System.Threading.Tasks",
        "Aevatar.DynamicRuntime.Abstractions",
    };

    public IReadOnlySet<string> BlockedNamespacePrefixes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "System.Diagnostics",
        "System.IO",
        "System.Reflection",
        "System.Runtime.InteropServices",
    };

    public Task<PolicyValidationResult> ValidateAsync(ScriptSourceBundle bundle, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(bundle.ScriptCode))
            return Task.FromResult(new PolicyValidationResult(false, "SCRIPT_POLICY_REJECTED", "script code is required"));
        if (string.IsNullOrWhiteSpace(bundle.EntrypointType))
            return Task.FromResult(new PolicyValidationResult(false, "SCRIPT_POLICY_REJECTED", "entrypoint type is required"));

        foreach (var blockedPrefix in BlockedNamespacePrefixes)
        {
            if (bundle.ScriptCode.Contains(blockedPrefix, StringComparison.Ordinal))
                return Task.FromResult(new PolicyValidationResult(false, "SCRIPT_POLICY_REJECTED", $"blocked namespace prefix: {blockedPrefix}"));
        }

        return Task.FromResult(new PolicyValidationResult(true));
    }
}
