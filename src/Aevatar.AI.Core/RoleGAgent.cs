// ─────────────────────────────────────────────────────────────
// RoleGAgent - role-based AI GAgent.
//
// Handles ChatRequestEvent:
// 1. Calls LLM via ChatStreamAsync (streaming)
// 2. Publishes AG-UI events: TextMessageStart → Content* → ToolCall* → End
// 3. Logs prompt and full LLM response for observability
// ─────────────────────────────────────────────────────────────

using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Agents;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Hooks;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.Core;

/// <summary>
/// Role-based AI GAgent. Receives ChatRequestEvent and streams LLM response.
/// </summary>
public class RoleGAgent : AIGAgentBase<RoleGAgentState>, IRoleAgent
{
    public RoleGAgent(
        ILLMProviderFactory? llmProviderFactory = null,
        IEnumerable<IAIGAgentExecutionHook>? additionalHooks = null,
        IEnumerable<IAgentRunMiddleware>? agentMiddlewares = null,
        IEnumerable<IToolCallMiddleware>? toolMiddlewares = null,
        IEnumerable<ILLMCallMiddleware>? llmMiddlewares = null,
        IEnumerable<IAgentToolSource>? toolSources = null)
        : base(
            llmProviderFactory,
            additionalHooks,
            agentMiddlewares,
            toolMiddlewares,
            llmMiddlewares,
            toolSources)
    {
    }

    /// <summary>Role name.</summary>
    public string RoleName { get; private set; } = "";

    /// <summary>Sets role name.</summary>
    public void SetRoleName(string name) => RoleName = name;

    Task IRoleAgent.ConfigureAsync(RoleAgentConfig config, CancellationToken ct) =>
        ConfigureAsync(new AIAgentConfig
        {
            ProviderName = config.ProviderName,
            Model = config.Model,
            SystemPrompt = config.SystemPrompt,
            Temperature = config.Temperature,
            MaxTokens = config.MaxTokens,
            MaxToolRounds = config.MaxToolRounds,
            MaxHistoryMessages = config.MaxHistoryMessages,
            StreamBufferCapacity = config.StreamBufferCapacity,
        }, ct);

    [EventHandler]
    public async Task HandleConfigureRoleAgent(ConfigureRoleAgentEvent evt)
    {
        await PersistDomainEventAsync(evt);
        await ((IRoleAgent)this).ConfigureAsync(new RoleAgentConfig
        {
            ProviderName = string.IsNullOrWhiteSpace(evt.ProviderName) ? string.Empty : evt.ProviderName,
            Model = string.IsNullOrWhiteSpace(evt.Model) ? null : evt.Model,
            SystemPrompt = evt.SystemPrompt ?? string.Empty,
            Temperature = evt.HasTemperature ? evt.Temperature : null,
            MaxTokens = evt.MaxTokens == 0 ? null : evt.MaxTokens,
            MaxToolRounds = evt.MaxToolRounds <= 0 ? 10 : evt.MaxToolRounds,
            MaxHistoryMessages = evt.MaxHistoryMessages <= 0 ? 100 : evt.MaxHistoryMessages,
            StreamBufferCapacity = evt.StreamBufferCapacity <= 0 ? 256 : evt.StreamBufferCapacity,
        });

        RoleGAgentFactory.ApplyModuleExtensions(this, evt.EventModules, evt.EventRoutes, Services);
    }

    /// <summary>Returns agent description.</summary>
    public override Task<string> GetDescriptionAsync() =>
        Task.FromResult($"RoleGAgent[{RoleName}]:{Id}");

    protected override RoleGAgentState TransitionState(RoleGAgentState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ConfigureRoleAgentEvent>(ApplyConfigureRoleAgent)
            .OrCurrent();

    protected override Task OnStateChangedAsync(RoleGAgentState state, CancellationToken ct)
    {
        _ = ct;
        RoleName = state.RoleName ?? string.Empty;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles ChatRequestEvent via streaming LLM call.
    /// Publishes text stream events and tool call events.
    /// </summary>
    [EventHandler]
    public async Task HandleChatRequest(ChatRequestEvent request)
    {
        var promptPreview = request.Prompt.Length > 200
            ? request.Prompt[..200] + "..."
            : request.Prompt;
        Logger.LogInformation("[{Role}] LLM request: {Preview}", RoleName, promptPreview);

        // ─── AG-UI: TEXT_MESSAGE_START ───
        await PublishAsync(new TextMessageStartEvent
        {
            SessionId = request.SessionId,
            AgentId = Id,
        }, EventDirection.Up);

        // ─── AG-UI: TEXT_MESSAGE_CONTENT — streaming chunks ───
        var fullContent = new StringBuilder();
        var toolCalls = new StreamToolCallAccumulator();

        await foreach (var chunk in ChatStreamAsync(request.Prompt))
        {
            if (!string.IsNullOrEmpty(chunk.DeltaContent))
            {
                fullContent.Append(chunk.DeltaContent);
                await PublishAsync(new TextMessageContentEvent
                {
                    Delta = chunk.DeltaContent,
                    SessionId = request.SessionId,
                }, EventDirection.Up);
            }

            if (chunk.DeltaToolCall != null)
                toolCalls.TrackDelta(chunk.DeltaToolCall);
        }

        foreach (var toolCall in toolCalls.BuildToolCalls())
        {
            await PublishAsync(new ToolCallEvent
            {
                CallId = toolCall.Id,
                ToolName = toolCall.Name,
                ArgumentsJson = toolCall.ArgumentsJson,
            }, EventDirection.Up);
        }

        var response = fullContent.ToString();
        var responsePreview = response.Length > 300
            ? response[..300] + "..."
            : response;
        Logger.LogInformation("[{Role}] LLM response ({Len} chars): {Preview}",
            RoleName, response.Length, responsePreview);

        // ─── AG-UI: TEXT_MESSAGE_END ───
        await PublishAsync(new TextMessageEndEvent
        {
            Content = response,
            SessionId = request.SessionId,
        }, EventDirection.Up);
    }

    private sealed class StreamToolCallAccumulator
    {
        private readonly Dictionary<string, ToolCallAggregate> _aggregates = new(StringComparer.Ordinal);
        private readonly List<string> _order = [];
        private int _anonymousCounter;
        private string? _activeAnonymousKey;

        public void TrackDelta(ToolCall delta)
        {
            var key = ResolveKey(delta);
            if (!_aggregates.TryGetValue(key, out var aggregate))
            {
                aggregate = new ToolCallAggregate(ResolveId(delta));
                _aggregates[key] = aggregate;
                _order.Add(key);
            }

            if (!string.IsNullOrWhiteSpace(delta.Name))
                aggregate.Name = delta.Name;

            if (!string.IsNullOrEmpty(delta.ArgumentsJson))
                aggregate.Arguments.Append(delta.ArgumentsJson);
        }

        public IReadOnlyList<ToolCall> BuildToolCalls()
        {
            var result = new List<ToolCall>(_order.Count);
            foreach (var key in _order)
            {
                var aggregate = _aggregates[key];
                result.Add(new ToolCall
                {
                    Id = aggregate.Id,
                    Name = aggregate.Name ?? string.Empty,
                    ArgumentsJson = aggregate.Arguments.ToString(),
                });
            }

            return result;
        }

        private string ResolveKey(ToolCall delta)
        {
            if (!string.IsNullOrWhiteSpace(delta.Id))
            {
                _activeAnonymousKey = null;
                return $"id:{delta.Id}";
            }

            if (!string.IsNullOrWhiteSpace(_activeAnonymousKey))
                return _activeAnonymousKey;

            _anonymousCounter++;
            _activeAnonymousKey = $"anon:{_anonymousCounter}";
            return _activeAnonymousKey;
        }

        private string ResolveId(ToolCall delta)
        {
            if (!string.IsNullOrWhiteSpace(delta.Id))
                return delta.Id;

            return $"stream-tool-call-{_anonymousCounter}";
        }

        private sealed class ToolCallAggregate
        {
            public ToolCallAggregate(string id)
            {
                Id = id;
            }

            public string Id { get; }

            public string? Name { get; set; }

            public StringBuilder Arguments { get; } = new();
        }
    }

    private static RoleGAgentState ApplyConfigureRoleAgent(
        RoleGAgentState current,
        ConfigureRoleAgentEvent evt)
    {
        var next = current.Clone();
        next.RoleName = evt.RoleName ?? string.Empty;
        return next;
    }
}
