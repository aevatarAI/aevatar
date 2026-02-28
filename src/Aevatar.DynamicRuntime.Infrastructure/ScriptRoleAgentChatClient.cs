using Aevatar.AI.Abstractions.Agents;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using Aevatar.AI.Core.Hooks;
using Aevatar.DynamicRuntime.Abstractions.Contracts;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.DynamicRuntime.Infrastructure;

public sealed class ScriptRoleAgentChatClient
{
    private readonly ILLMProviderFactory _providerFactory;
    private readonly IReadOnlyList<IAIGAgentExecutionHook> _additionalHooks;
    private readonly IReadOnlyList<IAgentRunMiddleware> _agentMiddlewares;
    private readonly IReadOnlyList<IToolCallMiddleware> _toolMiddlewares;
    private readonly IReadOnlyList<ILLMCallMiddleware> _llmMiddlewares;
    private readonly IReadOnlyList<IAgentToolSource> _toolSources;

    public ScriptRoleAgentChatClient(
        ILLMProviderFactory providerFactory,
        IEnumerable<IAIGAgentExecutionHook>? additionalHooks = null,
        IEnumerable<IAgentRunMiddleware>? agentMiddlewares = null,
        IEnumerable<IToolCallMiddleware>? toolMiddlewares = null,
        IEnumerable<ILLMCallMiddleware>? llmMiddlewares = null,
        IEnumerable<IAgentToolSource>? toolSources = null)
    {
        _providerFactory = providerFactory;
        _additionalHooks = (additionalHooks ?? []).ToArray();
        _agentMiddlewares = (agentMiddlewares ?? []).ToArray();
        _toolMiddlewares = (toolMiddlewares ?? []).ToArray();
        _llmMiddlewares = (llmMiddlewares ?? []).ToArray();
        _toolSources = (toolSources ?? []).ToArray();
    }

    public async Task<string> ChatAsync(
        string prompt,
        string? systemPrompt = null,
        string? providerName = null,
        string? model = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("LLM prompt is required.");

        var roleAgent = CreateRoleAgentHost();
        var config = new RoleAgentConfig
        {
            ProviderName = string.IsNullOrWhiteSpace(providerName) ? string.Empty : providerName,
            Model = model,
            SystemPrompt = systemPrompt ?? string.Empty,
        };
        return await roleAgent.ExecutePromptAsync(prompt, config, ct);
    }

    private ScriptRoleAgentHost CreateRoleAgentHost()
    {
        return new ScriptRoleAgentHost(
            _providerFactory,
            _additionalHooks,
            _agentMiddlewares,
            _toolMiddlewares,
            _llmMiddlewares,
            _toolSources);
    }

    private sealed class ScriptRoleAgentHost : RoleGAgent
    {
        public ScriptRoleAgentHost(
            ILLMProviderFactory providerFactory,
            IEnumerable<IAIGAgentExecutionHook> additionalHooks,
            IEnumerable<IAgentRunMiddleware> agentMiddlewares,
            IEnumerable<IToolCallMiddleware> toolMiddlewares,
            IEnumerable<ILLMCallMiddleware> llmMiddlewares,
            IEnumerable<IAgentToolSource> toolSources)
            : base(
                providerFactory,
                additionalHooks,
                agentMiddlewares,
                toolMiddlewares,
                llmMiddlewares,
                toolSources)
        {
        }

        public async Task<string> ExecutePromptAsync(string prompt, RoleAgentConfig config, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ClearHistory();
            await ((IRoleAgent)this).ConfigureAsync(config, ct);
            var response = await ChatAsync(prompt, ct);
            return response ?? string.Empty;
        }
    }
}

internal sealed class ScriptRoleAgentRuntime : IScriptRoleAgentRuntime
{
    private readonly Func<string, string?, string?, string?, CancellationToken, Task<string>> _chatAsync;
    private readonly List<EventEnvelope> _publishedEnvelopes = [];

    public ScriptRoleAgentRuntime(
        Func<string, string?, string?, string?, CancellationToken, Task<string>> chatAsync,
        EventEnvelope currentEnvelope)
    {
        _chatAsync = chatAsync ?? throw new ArgumentNullException(nameof(chatAsync));
        CurrentEnvelope = currentEnvelope ?? throw new ArgumentNullException(nameof(currentEnvelope));
    }

    public EventEnvelope CurrentEnvelope { get; }
    public IReadOnlyList<EventEnvelope> PublishedEnvelopes => _publishedEnvelopes;
    public Task<string> ChatAsync(
        string prompt,
        string? systemPrompt = null,
        string? providerName = null,
        string? model = null,
        CancellationToken ct = default) =>
        _chatAsync(prompt, systemPrompt, providerName, model, ct);

    public Task PublishAsync(
        IMessage payload,
        EventDirection direction = EventDirection.Self,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (payload == null)
            throw new ArgumentNullException(nameof(payload));

        var now = DateTime.UtcNow;
        var packedPayload = Any.Pack(payload);
        var traceId = Guid.NewGuid().ToString("N");
        var correlationId = string.IsNullOrWhiteSpace(CurrentEnvelope.CorrelationId)
            ? Guid.NewGuid().ToString("N")
            : CurrentEnvelope.CorrelationId;
        var causationId = string.IsNullOrWhiteSpace(CurrentEnvelope.Id)
            ? Guid.NewGuid().ToString("N")
            : CurrentEnvelope.Id;

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(now),
            Payload = packedPayload,
            PublisherId = string.IsNullOrWhiteSpace(CurrentEnvelope.PublisherId) ? "dynamic-runtime.script" : CurrentEnvelope.PublisherId,
            Direction = direction,
            CorrelationId = correlationId,
            Metadata =
            {
                ["trace_id"] = traceId,
                ["correlation_id"] = correlationId,
                ["causation_id"] = causationId,
                ["dedup_key"] = $"{packedPayload.TypeUrl}:{Guid.NewGuid():N}",
                ["type_url"] = packedPayload.TypeUrl,
                ["occurred_at"] = now.ToString("O"),
            },
        };

        if (metadata != null)
        {
            foreach (var pair in metadata)
                envelope.Metadata[pair.Key] = pair.Value;
        }

        _publishedEnvelopes.Add(envelope);
        return Task.CompletedTask;
    }
}
