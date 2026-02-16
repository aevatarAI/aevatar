// ─────────────────────────────────────────────────────────────
// IGAgentExecutionHook - foundation-level GAgent hook contract.
//
// Provides extension points before/after handler execution for any GAgent.
// AI-level hooks can extend this contract with LLM/tool hook points.
//
// All methods provide default no-op implementations.
// ─────────────────────────────────────────────────────────────

namespace Aevatar.Foundation.Abstractions.Hooks;

/// <summary>
/// Foundation-level GAgent hook for event-handler lifecycle callbacks.
/// Any GAgent can register this hook without depending on the AI layer.
/// </summary>
public interface IGAgentExecutionHook
{
    /// <summary>Hook name used for logging and configuration.</summary>
    string Name { get; }

    /// <summary>Execution priority. Lower values run first.</summary>
    int Priority { get; }

    /// <summary>Called before event handler execution.</summary>
    Task OnEventHandlerStartAsync(GAgentExecutionHookContext ctx, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Called after event handler execution.</summary>
    Task OnEventHandlerEndAsync(GAgentExecutionHookContext ctx, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Called when event handler execution fails.</summary>
    Task OnErrorAsync(GAgentExecutionHookContext ctx, Exception ex, CancellationToken ct) => Task.CompletedTask;
}
