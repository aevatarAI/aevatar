// ─────────────────────────────────────────────────────────────
// TwitterPublishModule — 把 social_media 模板批准后的内容发布到 X (Twitter)
// 通过 NyxID `api-twitter` 代理调用 POST /tweets，结果同步回 Lark。
// 见 issue aevatarAI/aevatar#216 — 接续 #418 的 PreflightTwitterProxyAsync。
// ─────────────────────────────────────────────────────────────

using System.Net;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.GAgents.ChannelRuntime;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime.WorkflowModules;

/// <summary>
/// Twitter (X) 发布模块。处理 <c>step_type == "twitter_publish"</c>。
/// 用 social_media agent 在 NyxID 中预先 mint 的 api-key 调 <c>api-twitter</c> 代理把已批准
/// 的草稿发布到 Twitter，并把结果（推文 URL 或分类好的错误文案）回写到原始 Lark 会话。
/// </summary>
/// <remarks>
/// 与 LLM/工具调用路径不同——发布是确定性的：批准的内容直接进入 <c>POST /2/tweets</c>，没有
/// 模型重写余地。把这一段建在工作流 module 而不是 LLM step 里也更可重入：模型偶尔丢工具调用、
/// 或返回非结构化文本，但发布行为必须严格 1:1。
/// </remarks>
public sealed class TwitterPublishModule : IEventModule<IWorkflowExecutionContext>
{
    public string Name => "twitter_publish";
    public int Priority => 5;

    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(StepRequestEvent.Descriptor) == true;

    public async Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var request = envelope.Payload!.Unpack<StepRequestEvent>();
        if (request.StepType != "twitter_publish") return;

        var content = (request.Input ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(content))
        {
            await PublishFailureAsync(
                ctx,
                request,
                code: "twitter_publish_empty_content",
                message: "Approved content was empty; nothing to publish.",
                logger: ctx.Logger,
                ct);
            return;
        }

        var nyxClient = ctx.Services.GetService<NyxIdApiClient>();
        if (nyxClient is null)
        {
            await PublishFailureAsync(
                ctx,
                request,
                code: "twitter_publish_client_missing",
                message: "NyxIdApiClient is not registered; cannot publish.",
                logger: ctx.Logger,
                ct);
            return;
        }

        if (!WorkflowExecutionItemsAccess.TryGetItem<string>(
                ctx,
                LLMRequestMetadataKeys.NyxIdAccessToken,
                out var apiKeyValue) ||
            string.IsNullOrWhiteSpace(apiKeyValue))
        {
            await PublishFailureAsync(
                ctx,
                request,
                code: "twitter_publish_api_key_missing",
                message: "Workflow execution context did not carry a NyxID api-key. Re-create the agent so the new outbound config propagates.",
                logger: ctx.Logger,
                ct);
            return;
        }

        var requestMetadata = new Dictionary<string, string>(StringComparer.Ordinal);
        WorkflowRequestMetadataItemsAccess.CopyRequestMetadata(ctx, requestMetadata);

        var publishSlug = WorkflowParameterValueParser.GetString(
            request.Parameters,
            "api-twitter",
            "publish_provider_slug",
            "nyx_publish_provider_slug",
            "publish_slug");

        var deliveryTargetId = WorkflowParameterValueParser.GetString(
            request.Parameters,
            string.Empty,
            "delivery_target_id");

        // Twitter v2 endpoint requires `text` payload only for plain-text posts (#216 v1 scope:
        // no media, no thread, no poll). Body is JSON, content-type is set by NyxIdApiClient.
        var tweetBody = JsonSerializer.Serialize(new { text = content });

        string proxyResponse;
        try
        {
            proxyResponse = await nyxClient!.ProxyRequestAsync(
                apiKeyValue!,
                publishSlug,
                "/2/tweets",
                "POST",
                tweetBody,
                extraHeaders: null,
                ct);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(
                ex,
                "TwitterPublish: run={RunId} step={StepId} unhandled exception while calling api-twitter",
                request.RunId,
                request.StepId);
            await PublishFailureAsync(
                ctx,
                request,
                code: "twitter_publish_transport_error",
                message: $"NyxID proxy transport error: {ex.Message}",
                logger: ctx.Logger,
                ct);
            await TrySendLarkAsync(
                nyxClient,
                requestMetadata,
                apiKeyValue!,
                deliveryTargetId,
                $"Twitter 发布失败（网络错误）：{ex.Message}",
                ctx.Logger,
                ct);
            return;
        }

        var outcome = ClassifyTwitterResponse(proxyResponse);

        if (outcome.Success && !string.IsNullOrEmpty(outcome.TweetUrl))
        {
            ctx.Logger.LogInformation(
                "TwitterPublish: run={RunId} step={StepId} published tweet={TweetUrl}",
                request.RunId,
                request.StepId,
                outcome.TweetUrl);

            var successMessage = $"已发布: {outcome.TweetUrl}";
            await TrySendLarkAsync(
                nyxClient,
                requestMetadata,
                apiKeyValue!,
                deliveryTargetId,
                successMessage,
                ctx.Logger,
                ct);

            var completed = new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = request.RunId,
                Success = true,
                Output = outcome.TweetUrl!,
            };
            await ctx.PublishAsync(completed, TopologyAudience.Self, ct);
            return;
        }

        ctx.Logger.LogWarning(
            "TwitterPublish: run={RunId} step={StepId} publish failed code={Code} status={Status} detail={Detail}",
            request.RunId,
            request.StepId,
            outcome.ErrorCode,
            outcome.HttpStatus,
            outcome.Detail);

        await TrySendLarkAsync(
            nyxClient,
            requestMetadata,
            apiKeyValue!,
            deliveryTargetId,
            outcome.LarkMessage,
            ctx.Logger,
            ct);

        await PublishFailureAsync(
            ctx,
            request,
            code: outcome.ErrorCode,
            message: outcome.Detail,
            logger: ctx.Logger,
            ct);
    }

    private static Task PublishFailureAsync(
        IWorkflowExecutionContext ctx,
        StepRequestEvent request,
        string code,
        string message,
        ILogger logger,
        CancellationToken ct)
    {
        // The social_media template's `publish_to_twitter` step routes its failure into the
        // `done` terminal so the run finishes cleanly even if Twitter rejected the post —
        // the failure is surfaced to Lark independently. Mark Success=false so callers /
        // observability see the failed publish, but emit the error string verbatim so the
        // workflow output preserves the categorized code.
        var failed = new StepCompletedEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            Success = false,
            Output = $"{code}: {message}",
            Error = $"{code}: {message}",
        };
        return ctx.PublishAsync(failed, TopologyAudience.Self, ct);
    }

    private static async Task TrySendLarkAsync(
        NyxIdApiClient nyxClient,
        IReadOnlyDictionary<string, string> requestMetadata,
        string apiKey,
        string fallbackReceiveId,
        string text,
        ILogger logger,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var receiveId = TryGet(requestMetadata, ChannelMetadataKeys.LarkReceiveId);
        var receiveIdType = TryGet(requestMetadata, ChannelMetadataKeys.LarkReceiveIdType);
        var larkSlug = TryGet(requestMetadata, ChannelMetadataKeys.LarkProxySlug) ?? "api-lark-bot";

        // Fallback: when the workflow agent's outbound metadata is unavailable, treat the
        // step's `delivery_target_id` (which is the agent_id, i.e. the Lark receive_id under
        // open_id naming for p2p chats) as a best-effort target.
        if (string.IsNullOrWhiteSpace(receiveId))
        {
            receiveId = fallbackReceiveId;
            receiveIdType = string.IsNullOrWhiteSpace(receiveIdType) ? "open_id" : receiveIdType;
        }

        if (string.IsNullOrWhiteSpace(receiveId) || string.IsNullOrWhiteSpace(receiveIdType))
        {
            logger.LogWarning(
                "TwitterPublish: skipping Lark surfacing — outbound delivery target metadata missing (receive_id/type empty).");
            return;
        }

        try
        {
            var body = JsonSerializer.Serialize(new
            {
                receive_id = receiveId,
                msg_type = "text",
                content = JsonSerializer.Serialize(new { text }),
            });

            var response = await nyxClient.ProxyRequestAsync(
                apiKey,
                larkSlug,
                $"open-apis/im/v1/messages?receive_id_type={receiveIdType}",
                "POST",
                body,
                extraHeaders: null,
                ct);

            if (LarkProxyResponse.TryGetError(response, out var larkCode, out var detail))
            {
                logger.LogWarning(
                    "TwitterPublish: Lark surfacing rejected (code={Code}): {Detail}",
                    larkCode,
                    detail);
            }
        }
        catch (Exception ex)
        {
            // Lark surfacing is best-effort: a failure here must not abort the workflow's
            // own bookkeeping (which is what publishes StepCompletedEvent). Log and move on.
            logger.LogWarning(ex, "TwitterPublish: Lark surfacing threw");
        }
    }

    private static string? TryGet(IReadOnlyDictionary<string, string> map, string key)
    {
        if (!map.TryGetValue(key, out var value))
            return null;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Classifies a NyxID proxy response from <c>POST /api/v1/proxy/s/api-twitter/2/tweets</c>
    /// into a publish outcome. Twitter v2 returns 201 on success with <c>{ "data": { "id":
    /// "&lt;tweet-id&gt;" } }</c>; NyxID forwards 4xx/5xx as
    /// <c>{ "error": true, "status": &lt;http&gt;, "body": "&lt;raw downstream body&gt;" }</c>
    /// (NyxIdApiClient.cs:680). Both shapes are recognized here.
    /// </summary>
    internal static TwitterPublishOutcome ClassifyTwitterResponse(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return TwitterPublishOutcome.Failure(
                "twitter_publish_empty_response",
                "NyxID proxy returned an empty response.",
                httpStatus: 0,
                larkMessage: "Twitter 发布失败：NyxID 代理返回空响应");
        }

        try
        {
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return TwitterPublishOutcome.Failure(
                    "twitter_publish_unexpected_shape",
                    "Response root was not a JSON object.",
                    httpStatus: 0,
                    larkMessage: "Twitter 发布失败：响应格式异常");
            }

            // Success path: Twitter returns `{ "data": { "id": "...", "text": "..." } }`. NyxID
            // forwards 2xx bodies verbatim, so the absence of an `error` field combined with a
            // present `data.id` is the success signal.
            var hasErrorFlag = root.TryGetProperty("error", out var errorProp) &&
                               (errorProp.ValueKind == JsonValueKind.True ||
                                errorProp.ValueKind == JsonValueKind.String);

            if (!hasErrorFlag &&
                root.TryGetProperty("data", out var dataProp) &&
                dataProp.ValueKind == JsonValueKind.Object &&
                dataProp.TryGetProperty("id", out var idProp) &&
                idProp.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(idProp.GetString()))
            {
                var tweetId = idProp.GetString()!;
                // Twitter accepts `https://x.com/i/web/status/<id>` without a handle; resolves
                // to the canonical `<handle>/status/<id>` URL after redirect. The issue calls
                // for a `users/me` lookup to resolve the handle, but that's an extra round-trip
                // that can also 401 (and we already have a tweet id at this point). Fall back
                // to the no-handle URL — the user always lands on the right tweet either way.
                return TwitterPublishOutcome.Successful($"https://x.com/i/web/status/{tweetId}");
            }

            // Failure: NyxID wraps non-2xx as { error: true, status: <http>, body: <raw> }.
            var status = TryReadInt32(root, "status") ?? TryReadInt32(root, "code") ?? 0;
            var detail = TryReadString(root, "message") ?? TryReadString(root, "body") ?? "Twitter publish failed";
            var rawBody = TryReadString(root, "body");

            return ClassifyByStatus(status, detail, rawBody);
        }
        catch (JsonException)
        {
            return TwitterPublishOutcome.Failure(
                "twitter_publish_unparseable_response",
                "NyxID proxy returned a non-JSON response.",
                httpStatus: 0,
                larkMessage: "Twitter 发布失败：响应不是合法 JSON");
        }
    }

    private static TwitterPublishOutcome ClassifyByStatus(int status, string detail, string? rawBody)
    {
        // Categorization matches issue #216's surfacing matrix:
        //   201 → success (handled in caller)
        //   401 → OAuth expired/missing — actionable, no retry
        //   403 → scope downgraded or seed misconfig — actionable, no retry
        //   429 → rate-limited — could retry, but #216 v1 scope says fail with hint
        //   5xx → upstream/proxy fault — could retry; v1 scope: fail with hint
        //   4xx other → unknown rejection — surface verbatim so user can debug
        return status switch
        {
            (int)HttpStatusCode.Unauthorized => TwitterPublishOutcome.Failure(
                "twitter_oauth_required",
                detail,
                status,
                "Twitter OAuth 过期或未授权，请到 NyxID 重新授权 Twitter（providers/twitter）后再试。"),
            (int)HttpStatusCode.Forbidden => TwitterPublishOutcome.Failure(
                "twitter_proxy_access_denied",
                detail,
                status,
                "Twitter 拒绝发布（403）：scope 不足或推文内容被策略拦截。请联系 ops 检查 tweet.write scope。"),
            (int)HttpStatusCode.TooManyRequests => TwitterPublishOutcome.Failure(
                "twitter_rate_limited",
                detail,
                status,
                "Twitter 发布命中速率限制（429），请稍后重试。"),
            >= 500 and <= 599 => TwitterPublishOutcome.Failure(
                "twitter_upstream_error",
                detail,
                status,
                $"Twitter 上游服务异常（HTTP {status}），请稍后重试。"),
            _ => TwitterPublishOutcome.Failure(
                "twitter_publish_rejected",
                detail,
                status,
                BuildGenericFailureMessage(status, detail, rawBody)),
        };
    }

    private static string BuildGenericFailureMessage(int status, string detail, string? rawBody)
    {
        var truncated = rawBody is { Length: > 200 } ? rawBody.Substring(0, 200) + "…" : rawBody;
        return string.IsNullOrEmpty(truncated)
            ? $"Twitter 发布失败（HTTP {status}）：{detail}"
            : $"Twitter 发布失败（HTTP {status}）：{detail}（body: {truncated}）";
    }

    private static int? TryReadInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop) ||
            prop.ValueKind != JsonValueKind.Number ||
            !prop.TryGetInt32(out var value))
        {
            return null;
        }
        return value;
    }

    private static string? TryReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.String)
            return null;
        var raw = prop.GetString();
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }
}

internal readonly record struct TwitterPublishOutcome(
    bool Success,
    string? TweetUrl,
    string ErrorCode,
    string Detail,
    int HttpStatus,
    string LarkMessage)
{
    public static TwitterPublishOutcome Successful(string tweetUrl) =>
        new(true, tweetUrl, string.Empty, string.Empty, 201, string.Empty);

    public static TwitterPublishOutcome Failure(string code, string detail, int httpStatus, string larkMessage) =>
        new(false, null, code, detail, httpStatus, larkMessage);
}
