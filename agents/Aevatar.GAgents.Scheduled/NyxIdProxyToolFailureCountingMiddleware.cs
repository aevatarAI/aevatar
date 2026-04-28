using System.Text.Json;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.ToolProviders.NyxId.Tools;

namespace Aevatar.GAgents.Scheduled;

/// <summary>
/// Tool-call middleware that classifies <c>nyxid_proxy</c> results by reading the
/// <see cref="NyxIdProxyTool.ToolStatusFieldName"/> marker the tool injects on every
/// JSON-object response. Increments the per-run counter so
/// <see cref="SkillRunnerGAgent.EnsureToolStatusAllowsCompletion"/> can downgrade an
/// all-failures run to <c>SkillRunnerExecutionFailedEvent</c> instead of letting the LLM's
/// plain-text fallback land as a clean success (issue #439).
/// </summary>
/// <remarks>
/// Only counts <c>nyxid_proxy</c> calls. Other tools may have their own success semantics
/// (e.g., a search tool that returns 0 hits is not a failure), and the safety net is
/// scoped to the proxy fan-out that powers the daily-report skill.
/// </remarks>
internal sealed class NyxIdProxyToolFailureCountingMiddleware : IToolCallMiddleware
{
    private readonly SkillRunnerToolFailureCounter _counter;

    public NyxIdProxyToolFailureCountingMiddleware(SkillRunnerToolFailureCounter counter)
    {
        _counter = counter;
    }

    public async Task InvokeAsync(ToolCallContext context, Func<Task> next)
    {
        await next();

        // Only the proxy tool's results carry the structural marker; other tools opt out.
        if (!string.Equals(context.ToolName, "nyxid_proxy", StringComparison.Ordinal))
            return;

        var result = context.Result;
        if (string.IsNullOrEmpty(result))
            return;

        switch (TryReadToolStatus(result))
        {
            case NyxIdProxyTool.ToolStatusError:
                _counter.RecordFailure();
                break;
            case NyxIdProxyTool.ToolStatusOk:
                _counter.RecordSuccess();
                break;
            // Unmarked responses (non-JSON-object, e.g. discovery arrays) are ignored —
            // they are not data-fetch calls the safety net is meant to protect.
        }
    }

    private static string? TryReadToolStatus(string result)
    {
        try
        {
            using var doc = JsonDocument.Parse(result);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            if (!doc.RootElement.TryGetProperty(NyxIdProxyTool.ToolStatusFieldName, out var statusProp))
                return null;
            return statusProp.ValueKind == JsonValueKind.String ? statusProp.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
