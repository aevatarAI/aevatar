// ─── ChatRuntime — Chat/ChatStream 执行逻辑 ───
// 组合 LLMProvider + History + ToolCallLoop + Hooks + Middleware。

using Aevatar.AI.Core.Hooks;
using Aevatar.AI.Core.Middleware;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

namespace Aevatar.AI.Core.Chat;

/// <summary>Chat 执行运行时。调 LLM，管理历史，集成 Middleware。</summary>
public sealed class ChatRuntime
{
    private readonly Func<ILLMProvider> _providerFactory;
    private readonly ChatHistory _history;
    private readonly ToolCallLoop _toolLoop;
    private readonly AgentHookPipeline? _hooks;
    private readonly Func<LLMRequest> _requestBuilder;
    private readonly IReadOnlyList<IAgentRunMiddleware> _agentMiddlewares;
    private readonly IReadOnlyList<ILLMCallMiddleware> _llmMiddlewares;
    private readonly string? _agentId;
    private readonly string? _agentName;
    private readonly int _streamBufferCapacity;

    public ChatRuntime(
        Func<ILLMProvider> providerFactory,
        ChatHistory history,
        ToolCallLoop toolLoop,
        AgentHookPipeline? hooks,
        Func<LLMRequest> requestBuilder,
        IReadOnlyList<IAgentRunMiddleware>? agentMiddlewares = null,
        IReadOnlyList<ILLMCallMiddleware>? llmMiddlewares = null,
        string? agentId = null,
        string? agentName = null,
        int streamBufferCapacity = 256)
    {
        _providerFactory = providerFactory;
        _history = history;
        _toolLoop = toolLoop;
        _hooks = hooks;
        _requestBuilder = requestBuilder;
        _agentMiddlewares = agentMiddlewares ?? [];
        _llmMiddlewares = llmMiddlewares ?? [];
        _agentId = string.IsNullOrWhiteSpace(agentId) ? null : agentId;
        _agentName = string.IsNullOrWhiteSpace(agentName) ? null : agentName;
        _streamBufferCapacity = streamBufferCapacity > 0
            ? streamBufferCapacity
            : throw new ArgumentOutOfRangeException(nameof(streamBufferCapacity), "Stream buffer capacity must be greater than zero.");
    }

    /// <summary>单轮 Chat（含 Tool Calling 循环），包裹 Agent Run Middleware。</summary>
    public async Task<string?> ChatAsync(string userMessage, int maxToolRounds = 10, CancellationToken ct = default)
    {
        var runContext = new AgentRunContext
        {
            UserMessage = userMessage,
            AgentId = _agentId,
            AgentName = _agentName,
            CancellationToken = ct,
        };

        await MiddlewarePipeline.RunAgentAsync(_agentMiddlewares, runContext, async () =>
        {
            if (runContext.Terminate) return;

            _history.Add(ChatMessage.User(runContext.UserMessage));
            var baseRequest = _requestBuilder();
            var provider = _providerFactory();
            runContext.Metadata["gen_ai.provider.name"] = provider.Name;
            var messages = _history.BuildMessages(baseRequest.Messages.FirstOrDefault(m => m.Role == "system")?.Content);

            var result = await _toolLoop.ExecuteAsync(provider, messages, baseRequest, maxToolRounds, ct);

            _history.Clear();
            foreach (var m in messages.Where(m => m.Role != "system"))
                _history.Add(m);

            runContext.Result = result;
        });

        return runContext.Result;
    }

    /// <summary>流式 Chat，包裹 LLM Call Middleware。</summary>
    public async IAsyncEnumerable<string> ChatStreamAsync(
        string userMessage, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var runToken = linkedCts.Token;

        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(_streamBufferCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        var runContext = new AgentRunContext
        {
            UserMessage = userMessage,
            AgentId = _agentId,
            AgentName = _agentName,
            CancellationToken = runToken,
        };

        var runTask = Task.Run(async () =>
        {
            var wroteOutput = false;
            try
            {
                await MiddlewarePipeline.RunAgentAsync(_agentMiddlewares, runContext, async () =>
                {
                    if (runContext.Terminate) return;

                    _history.Add(ChatMessage.User(runContext.UserMessage));
                    var baseRequest = _requestBuilder();
                    var provider = _providerFactory();
                    runContext.Metadata["gen_ai.provider.name"] = provider.Name;
                    var messages = _history.BuildMessages(baseRequest.Messages.FirstOrDefault(m => m.Role == "system")?.Content);

                    var request = new LLMRequest
                    {
                        Messages = messages,
                        Tools = baseRequest.Tools,
                        Model = baseRequest.Model,
                        Temperature = baseRequest.Temperature,
                        MaxTokens = baseRequest.MaxTokens,
                    };

                    var llmCallContext = new LLMCallContext
                    {
                        Request = request,
                        Provider = provider,
                        CancellationToken = runToken,
                        IsStreaming = true,
                    };

                    string? streamedContent = null;
                    TokenUsage? streamedUsage = null;

                    await MiddlewarePipeline.RunLLMCallAsync(_llmMiddlewares, llmCallContext, async () =>
                    {
                        if (llmCallContext.Terminate) return;

                        var full = new StringBuilder();
                        TokenUsage? usage = null;

                        await foreach (var chunk in provider.ChatStreamAsync(llmCallContext.Request, runToken))
                        {
                            if (chunk.DeltaContent != null)
                            {
                                full.Append(chunk.DeltaContent);
                                await channel.Writer.WriteAsync(chunk.DeltaContent, runToken);
                                wroteOutput = true;
                            }

                            if (chunk.Usage != null)
                                usage = chunk.Usage;
                        }

                        streamedContent = full.ToString();
                        streamedUsage = usage;
                        llmCallContext.Response = new LLMResponse
                        {
                            Content = streamedContent,
                            Usage = streamedUsage,
                        };
                    });

                    if (llmCallContext.Terminate)
                    {
                        streamedContent = llmCallContext.Response?.Content;
                        streamedUsage = llmCallContext.Response?.Usage;

                        if (streamedContent != null)
                        {
                            await channel.Writer.WriteAsync(streamedContent, runToken);
                            wroteOutput = true;
                        }
                    }

                    if (!string.IsNullOrEmpty(streamedContent))
                        _history.Add(ChatMessage.Assistant(streamedContent));

                    runContext.Result = streamedContent;
                });

                if (runContext.Terminate && runContext.Result != null && !wroteOutput)
                {
                    await channel.Writer.WriteAsync(runContext.Result, runToken);
                }

                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        });

        try
        {
            await foreach (var delta in channel.Reader.ReadAllAsync(runToken))
                yield return delta;
        }
        finally
        {
            linkedCts.Cancel();
            try { await runTask.ConfigureAwait(false); } catch { /* best-effort */ }
        }
    }
}
