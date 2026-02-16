// ─────────────────────────────────────────────────────────────
// GenAIObservabilityMiddleware — 内置可观测性中间件
// 实现 IAgentRunMiddleware / IToolCallMiddleware / ILLMCallMiddleware
// 自动为每次 Agent Run、LLM Call、Tool Call 创建 GenAI span + 记录 metrics
// ─────────────────────────────────────────────────────────────

using System.Diagnostics;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;

namespace Aevatar.AI.Core.Observability;

/// <summary>
/// Built-in observability middleware that emits OpenTelemetry GenAI spans and metrics.
/// Register all three interfaces via DI to enable full observability.
/// </summary>
public sealed class GenAIObservabilityMiddleware : IAgentRunMiddleware, IToolCallMiddleware, ILLMCallMiddleware
{
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

            if (context.Metadata.TryGetValue("gen_ai.provider.name", out var providerObj) &&
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

        if (GenAIActivitySource.EnableSensitiveData)
        {
            var msgCount = context.Request.Messages?.Count ?? 0;
            activity?.SetTag("gen_ai.request.message_count", msgCount);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            await next();

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

    // ─── Tool Call ───

    public async Task InvokeAsync(ToolCallContext context, Func<Task> next)
    {
        using var activity = GenAIActivitySource.StartExecuteTool(context.ToolName, context.ToolCallId);

        if (GenAIActivitySource.EnableSensitiveData)
            activity?.SetTag("gen_ai.tool.arguments", context.ArgumentsJson);

        var sw = Stopwatch.StartNew();
        try
        {
            await next();
            activity?.SetTag("gen_ai.tool.status", context.Terminate ? "terminated" : "ok");

            if (GenAIActivitySource.EnableSensitiveData && context.Result != null)
                activity?.SetTag("gen_ai.tool.result", context.Result);
        }
        catch (Exception ex)
        {
            activity?.SetTag("gen_ai.tool.status", "error");
            activity?.SetTag("error.message", ex.Message);
            activity?.SetTag("error.type", ex.GetType().FullName);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            sw.Stop();
            GenAIActivitySource.ToolInvocationDuration.Record(sw.Elapsed.TotalMilliseconds,
                new TagList { { "gen_ai.tool.name", context.ToolName } });
        }
    }
}
