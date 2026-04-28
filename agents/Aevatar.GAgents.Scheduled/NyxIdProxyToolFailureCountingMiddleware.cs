using System.Text.Json;
using Aevatar.AI.Abstractions.Middleware;

namespace Aevatar.GAgents.Scheduled;

/// <summary>
/// Tool-call middleware that classifies <c>nyxid_proxy</c> results and increments the
/// per-run counter so <see cref="SkillRunnerGAgent.EnsureToolStatusAllowsCompletion"/> can
/// downgrade an all-failures run to <c>SkillRunnerExecutionFailedEvent</c> instead of
/// letting the LLM's plain-text fallback land as a clean success (issue #439).
/// </summary>
/// <remarks>
/// Classification happens here (not in the tool) on purpose — the previous design injected
/// a marker field into the response body, which leaked into the LLM context and risked
/// being echoed by weaker models. The middleware reads the raw response without mutating
/// it, so the LLM still sees the same JSON it would without the safety net.
///
/// Only counts <c>nyxid_proxy</c> calls — other tools may have their own success
/// semantics (e.g., a search tool that returns 0 hits is not a failure), and the safety
/// net is scoped to the proxy fan-out that powers the daily-report skill.
/// </remarks>
internal sealed class NyxIdProxyToolFailureCountingMiddleware : IToolCallMiddleware
{
    private const string ToolName = "nyxid_proxy";

    private readonly SkillRunnerToolFailureCounter _counter;

    public NyxIdProxyToolFailureCountingMiddleware(SkillRunnerToolFailureCounter counter)
    {
        _counter = counter;
    }

    public async Task InvokeAsync(ToolCallContext context, Func<Task> next)
    {
        await next();

        if (!string.Equals(context.ToolName, ToolName, StringComparison.Ordinal))
            return;

        var classification = ClassifyResult(context.Result);
        switch (classification)
        {
            case ResultClassification.Error:
                _counter.RecordFailure();
                break;
            case ResultClassification.Ok:
                _counter.RecordSuccess();
                break;
            // ResultClassification.Unknown (null/empty/non-JSON) is intentionally
            // ignored — it carries no signal about success or failure.
        }
    }

    /// <summary>
    /// Pure classifier exposed for unit testing. Detection rules:
    /// <list type="bullet">
    ///   <item><description>JSON object with truthy <c>error</c> property → <see cref="ResultClassification.Error"/>
    ///     (matches NyxIdApiClient.SendAsync's HTTP non-2xx wrapper and exception wrapper).</description></item>
    ///   <item><description>JSON object with numeric non-zero <c>code</c> AND a recognized message field
    ///     (<c>msg</c>, <c>message</c>, or <c>error_msg</c>) → <see cref="ResultClassification.Error"/>
    ///     (matches Lark/Feishu error envelopes <c>{code, msg}</c> and NyxID approval codes
    ///     7000/7001 which carry <c>message</c>). The message-field requirement narrows the
    ///     check so legitimate domain responses with a <c>code</c> field — e.g.
    ///     <c>{"code": 42, "data": ...}</c> — aren't false-flagged.</description></item>
    ///   <item><description>Any other valid JSON (objects without error markers, arrays, primitives)
    ///     → <see cref="ResultClassification.Ok"/>. Arrays specifically cover discovery-style
    ///     responses (<c>nyxid_proxy</c> with no slug, list endpoints) so they count as
    ///     successful data fetches in mixed runs.</description></item>
    ///   <item><description>Null, empty, or non-JSON text → <see cref="ResultClassification.Unknown"/>;
    ///     the safety net stays out of cases it can't read.</description></item>
    /// </list>
    /// </summary>
    internal static ResultClassification ClassifyResult(string? result)
    {
        if (string.IsNullOrEmpty(result))
            return ResultClassification.Unknown;

        try
        {
            using var doc = JsonDocument.Parse(result);

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return ResultClassification.Ok;

            if (doc.RootElement.TryGetProperty("error", out var errorProp) && IsTruthy(errorProp))
                return ResultClassification.Error;

            if (LooksLikeCodeBasedErrorEnvelope(doc.RootElement))
                return ResultClassification.Error;

            return ResultClassification.Ok;
        }
        catch (JsonException)
        {
            return ResultClassification.Unknown;
        }
    }

    private static bool LooksLikeCodeBasedErrorEnvelope(JsonElement root)
    {
        if (!root.TryGetProperty("code", out var codeProp)
            || codeProp.ValueKind != JsonValueKind.Number
            || !codeProp.TryGetInt64(out var code)
            || code == 0)
        {
            return false;
        }

        // Require a paired message field so we don't false-flag domain responses that
        // happen to use `code` for a non-error meaning. The Lark/Feishu envelope is
        // {code, msg, ...}; NyxID approval is {code, message, approval_request_id, ...}.
        return root.TryGetProperty("msg", out _)
            || root.TryGetProperty("message", out _)
            || root.TryGetProperty("error_msg", out _);
    }

    private static bool IsTruthy(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => false,
        // Strings/numbers/objects/arrays under "error" all indicate an error envelope of
        // some kind — the bare presence of a non-false "error" payload is the signal.
        _ => true,
    };

    internal enum ResultClassification
    {
        Unknown,
        Ok,
        Error,
    }
}
