using Aevatar.DynamicRuntime.Abstractions.Contracts;

namespace Aevatar.DynamicRuntime.Infrastructure;

public sealed class DefaultScriptSandboxPolicy : IScriptSandboxPolicy
{
    public Task<SandboxPrepareResult> PrepareAsync(ScriptExecutionContext context, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(context.ServiceId))
            return Task.FromResult(new SandboxPrepareResult(false, "SCRIPT_SANDBOX_REJECTED", "service id is required"));

        return Task.FromResult(new SandboxPrepareResult(true));
    }
}
