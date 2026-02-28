using Aevatar.DynamicRuntime.Abstractions.Contracts;

namespace Aevatar.DynamicRuntime.Infrastructure;

public sealed class DefaultScriptResourceQuotaPolicy : IScriptResourceQuotaPolicy
{
    private const int MaxInputLength = 8_192;

    public Task<ResourceQuotaDecision> EvaluateAsync(ScriptExecutionContext context, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if ((context.Envelope?.CalculateSize() ?? 0) > MaxInputLength)
            return Task.FromResult(new ResourceQuotaDecision(false, "RESOURCE_QUOTA_EXCEEDED", $"input exceeds {MaxInputLength} characters"));

        return Task.FromResult(new ResourceQuotaDecision(true));
    }
}
