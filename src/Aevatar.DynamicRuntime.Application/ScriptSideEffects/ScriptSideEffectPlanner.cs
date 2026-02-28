using Aevatar.DynamicRuntime.Abstractions;
using Aevatar.DynamicRuntime.Abstractions.Contracts;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.DynamicRuntime.Application;

internal interface IScriptSideEffectPlanner
{
    Task<ScriptSideEffectPlan> BuildAsync(
        string runId,
        string serviceId,
        ScriptServiceDefinitionState serviceState,
        DynamicScriptExecutionResult scriptResult,
        DateTime now,
        CancellationToken ct = default);
}

internal sealed record ScriptSideEffectPlan(
    Any? CustomState,
    long CustomStateUpdatedAtUnixMs,
    IReadOnlyList<IMessage> Events);

internal sealed class ScriptSideEffectPlanner : IScriptSideEffectPlanner
{
    public Task<ScriptSideEffectPlan> BuildAsync(
        string runId,
        string serviceId,
        ScriptServiceDefinitionState serviceState,
        DynamicScriptExecutionResult scriptResult,
        DateTime now,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var customState = scriptResult.CustomState?.Clone();
        var currentStateTypeUrl = serviceState.CustomState?.TypeUrl;
        var nextStateTypeUrl = customState?.TypeUrl;
        if (customState != null && string.IsNullOrWhiteSpace(nextStateTypeUrl))
            throw new InvalidOperationException("SCRIPT_EVENT_SOURCING_CONFLICT: custom state type url is required.");
        if (!string.IsNullOrWhiteSpace(currentStateTypeUrl) &&
            !string.IsNullOrWhiteSpace(nextStateTypeUrl) &&
            !string.Equals(currentStateTypeUrl, nextStateTypeUrl, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"SCRIPT_STATE_SCHEMA_CONFLICT: existing='{currentStateTypeUrl}', next='{nextStateTypeUrl}'.");
        }

        IReadOnlyList<IMessage> events =
            customState == null
                ? []
                : [
                    new ScriptCustomStateUpdatedEvent
                    {
                        ServiceId = serviceId,
                        RunId = runId,
                        CustomState = customState.Clone(),
                    },
                ];

        var plan = new ScriptSideEffectPlan(
            customState,
            customState == null ? 0 : new DateTimeOffset(now).ToUnixTimeMilliseconds(),
            events);
        return Task.FromResult(plan);
    }
}
