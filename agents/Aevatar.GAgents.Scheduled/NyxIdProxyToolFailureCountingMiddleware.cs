using System.Text.Json;
using Aevatar.AI.Core.Hooks;

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
/// net is scoped to the proxy fan-out that powers fetch-and-summarize skills.
/// </remarks>
internal sealed class NyxIdProxyToolFailureCountingMiddleware : IAIGAgentExecutionHook
{
    private const string ToolName = "nyxid_proxy";

    private readonly SkillRunnerToolFailureCounter _counter;

    public NyxIdProxyToolFailureCountingMiddleware(SkillRunnerToolFailureCounter counter)
    {
        _counter = counter;
    }

    public string Name => nameof(NyxIdProxyToolFailureCountingMiddleware);

    public int Priority => 0;

    public Task OnToolExecuteEndAsync(AIGAgentExecutionHookContext context, CancellationToken ct)
    {
        if (!string.Equals(context.ToolName, ToolName, StringComparison.Ordinal))
            return Task.CompletedTask;

        var classification = ClassifyResult(context.ToolResult);
        switch (classification)
        {
            case ResultClassification.Error:
                _counter.RecordFailure(ExtractFailureSample(context.ToolArguments, context.ToolResult));
                break;
            case ResultClassification.Ok:
                _counter.RecordSuccess();
                break;
            // ResultClassification.Unknown (null/empty/non-JSON) is intentionally
                // ignored — it carries no signal about success or failure.
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Pure classifier exposed for unit testing. Detection rules:
    /// <list type="bullet">
    ///   <item><description>JSON object with truthy <c>error</c> property → <see cref="ResultClassification.Error"/>
    ///     (matches NyxIdApiClient.SendAsync's HTTP non-2xx wrapper and exception wrapper).</description></item>
    ///   <item><description>JSON object with <c>code</c> equal to a known NyxID approval-blocked code
    ///     (7000/7001) → <see cref="ResultClassification.Error"/>. The data was not retrieved.</description></item>
    ///   <item><description>JSON object with numeric non-zero <c>code</c> AND a <c>msg</c> field
    ///     → <see cref="ResultClassification.Error"/> (Lark/Feishu envelope shape
    ///     <c>{code, msg, ...}</c> is the only envelope that pairs <c>code</c> with the
    ///     short-form <c>msg</c> field; generic SaaS APIs that use <c>code + message</c>
    ///     for success envelopes — e.g. <c>{"code": 200, "message": "success"}</c> — do
    ///     not match and are classified ok).</description></item>
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

    internal static SkillRunnerToolFailureSample ExtractFailureSample(
        string? argumentsJson,
        string? result)
    {
        var slug = ReadString(argumentsJson, "slug", "service");
        var path = SanitizePath(ReadString(argumentsJson, "path"));
        var method = ReadString(argumentsJson, "method");
        var status = default(int?);
        var detail = default(string?);

        if (!string.IsNullOrWhiteSpace(result))
        {
            try
            {
                using var doc = JsonDocument.Parse(result);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    status = ReadInt32(doc.RootElement, "status", "http_status", "httpStatus");
                    detail =
                        ReadErrorText(doc.RootElement, "message", "msg", "detail", "error_description", "error") ??
                        ReadBodyDetail(doc.RootElement);
                }
            }
            catch (JsonException)
            {
                detail = result;
            }
        }

        return new SkillRunnerToolFailureSample(
            NormalizeBlank(slug),
            NormalizeBlank(method) ?? (string.IsNullOrWhiteSpace(path) ? null : "GET"),
            NormalizeBlank(path),
            status,
            Truncate(NormalizeDiagnosticText(detail), 240));
    }

    /// <summary>
    /// NyxID approval-required (7000) and approval-rejected (7001). Mirrors the existing
    /// <c>IsApprovalError</c> detection inside NyxIdProxyTool — when this fires, the proxy
    /// did not deliver the requested data, so it counts as a failure.
    /// </summary>
    private static readonly HashSet<long> NyxIdApprovalErrorCodes = new() { 7000, 7001 };

    private static bool LooksLikeCodeBasedErrorEnvelope(JsonElement root)
    {
        if (!root.TryGetProperty("code", out var codeProp)
            || codeProp.ValueKind != JsonValueKind.Number
            || !codeProp.TryGetInt64(out var code))
        {
            return false;
        }

        if (NyxIdApprovalErrorCodes.Contains(code))
            return true;

        if (code == 0)
            return false;

        // Require the Lark/Feishu-specific `msg` short field. Generic SaaS APIs use
        // `message` for success envelopes (e.g., `{"code": 200, "message": "success"}`),
        // so checking `message` here would false-flag normal proxy responses. `msg` is
        // narrower and the only match for Lark's `{code: <int>, msg: "..."}` shape.
        return root.TryGetProperty("msg", out _);
    }

    private static string? ReadString(string? json, params string[] names)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            return ReadString(doc.RootElement, names);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
                continue;

            var value = prop.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static int? ReadInt32(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var prop))
                continue;

            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var number))
                return number;

            if (prop.ValueKind == JsonValueKind.String &&
                int.TryParse(prop.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static string? ReadErrorText(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var prop))
                continue;

            if (prop.ValueKind == JsonValueKind.String)
            {
                var value = prop.GetString();
                if (!string.IsNullOrWhiteSpace(value) &&
                    !string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }
            }

            if (name == "error" && prop.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                return prop.GetRawText();
        }

        return null;
    }

    private static string? ReadBodyDetail(JsonElement root)
    {
        if (!root.TryGetProperty("body", out var bodyProp) || bodyProp.ValueKind != JsonValueKind.String)
            return null;

        var body = bodyProp.GetString();
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var bodyDoc = JsonDocument.Parse(body);
            if (bodyDoc.RootElement.ValueKind == JsonValueKind.Object)
            {
                return ReadErrorText(
                    bodyDoc.RootElement,
                    "message",
                    "msg",
                    "detail",
                    "error_description",
                    "error");
            }
        }
        catch (JsonException)
        {
            // Plain-text upstream bodies are still useful diagnostics after trimming.
        }

        return body;
    }

    private static string? SanitizePath(string? path)
    {
        var normalized = NormalizeBlank(path);
        if (normalized is null)
            return null;

        var marker = normalized.IndexOf('?');
        if (marker < 0)
            return Truncate(normalized, 240);

        var basePath = normalized[..marker];
        var query = normalized[(marker + 1)..];
        var parts = query.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(RedactQueryPart);
        return Truncate($"{basePath}?{string.Join("&", parts)}", 240);
    }

    private static string RedactQueryPart(string part)
    {
        var equals = part.IndexOf('=');
        if (equals <= 0)
            return part;

        var key = part[..equals];
        if (LooksSensitiveQueryKey(key))
            return $"{key}=<redacted>";

        return part;
    }

    private static bool LooksSensitiveQueryKey(string key) =>
        key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("key", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("signature", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeBlank(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string? NormalizeDiagnosticText(string? value)
    {
        var normalized = NormalizeBlank(value);
        if (normalized is null)
            return null;

        return string.Join(
            ' ',
            normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value;

        return value[..(maxLength - 3)] + "...";
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
