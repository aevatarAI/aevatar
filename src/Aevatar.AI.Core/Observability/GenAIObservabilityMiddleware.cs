// ─────────────────────────────────────────────────────────────
// GenAIObservabilityMiddleware — 内置可观测性中间件
// 实现 IAgentRunMiddleware / ILLMCallMiddleware
// 自动为每次 Agent Run、LLM Call 创建 GenAI span + 记录 metrics
// ─────────────────────────────────────────────────────────────

using System.Diagnostics;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Core.Auditing;

namespace Aevatar.AI.Core.Observability;

/// <summary>
/// Built-in observability middleware that emits OpenTelemetry GenAI spans and metrics.
/// Register both interfaces via DI to enable observability.
/// </summary>
public sealed class GenAIObservabilityMiddleware : IAgentRunMiddleware, ILLMCallMiddleware
{
    private const string SafeToolFailureMessage = "The tool request failed.";

    // ─── Agent Run ───

    public async Task InvokeAsync(AgentRunContext context, Func<Task> next)
    {
        using var activity = GenAIActivitySource.StartInvokeAgent(context.AgentId, context.AgentName);
        if (GenAIActivitySource.EnableSensitiveData)
            activity?.SetTag("gen_ai.request.input", context.UserMessage);

        var sw = Stopwatch.StartNew();
        try
        {
            await next();
            activity?.SetTag("gen_ai.response.status", "ok");

            if (GenAIActivitySource.EnableSensitiveData && context.Result != null)
                activity?.SetTag("gen_ai.response.output", context.Result);

            if (context.Items.TryGetValue("gen_ai.provider.name", out var providerObj) &&
                providerObj is string providerName &&
                !string.IsNullOrWhiteSpace(providerName))
            {
                activity?.SetTag("gen_ai.provider.name", providerName);
            }
        }
        catch (Exception ex)
        {
            activity?.SetTag("gen_ai.response.status", "error");
            activity?.SetTag("error.message", ex.Message);
            activity?.SetTag("error.type", ex.GetType().FullName);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            sw.Stop();
        }
    }

    // ─── LLM Call ───

    public async Task InvokeAsync(LLMCallContext context, Func<Task> next)
    {
        var model = context.Request.Model;
        using var activity = GenAIActivitySource.StartChat(model);
        activity?.SetTag("gen_ai.provider.name", string.IsNullOrWhiteSpace(context.Provider.Name) ? "unknown" : context.Provider.Name);
        SetRequestIdTag(activity, context);

        if (GenAIActivitySource.EnableSensitiveData)
        {
            var msgCount = context.Request.Messages?.Count ?? 0;
            activity?.SetTag("gen_ai.request.message_count", msgCount);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            await next();
            SetRequestIdTag(activity, context);

            var response = context.Response;
            if (response?.Usage != null)
            {
                activity?.SetTag("gen_ai.usage.input_tokens", response.Usage.PromptTokens);
                activity?.SetTag("gen_ai.usage.output_tokens", response.Usage.CompletionTokens);

                var tags = new TagList
                {
                    { "gen_ai.request.model", model ?? "unknown" },
                    { "gen_ai.token.type", "total" },
                };
                GenAIActivitySource.TokenUsage.Record(response.Usage.TotalTokens, tags);
            }

            if (response?.FinishReason != null)
                activity?.SetTag("gen_ai.response.finish_reason", response.FinishReason);

            if (GenAIActivitySource.EnableSensitiveData && response?.Content != null)
                activity?.SetTag("gen_ai.response.content", response.Content);
        }
        catch (Exception ex)
        {
            activity?.SetTag("error.message", ex.Message);
            activity?.SetTag("error.type", ex.GetType().FullName);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            sw.Stop();
            GenAIActivitySource.OperationDuration.Record(sw.Elapsed.TotalMilliseconds,
                new TagList { { "gen_ai.request.model", model ?? "unknown" } });
        }
    }

    private static void SetRequestIdTag(Activity? activity, LLMCallContext context)
    {
        if (activity == null)
            return;

        var requestId = context.Request.RequestId;
        if (string.IsNullOrWhiteSpace(requestId) &&
            context.Items.TryGetValue(LLMRequestMetadataKeys.RequestId, out var requestIdObj) &&
            requestIdObj is string metadataRequestId)
        {
            requestId = metadataRequestId;
        }

        if (!string.IsNullOrWhiteSpace(requestId))
            activity.SetTag("gen_ai.request.id", requestId);
    }

}
