using System.Text;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Core;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Tools;
using Aevatar.Foundation.Core.Configurations;

using Aevatar.Studio.Application.Scripts.Contracts;
namespace Aevatar.Studio.Hosting.Endpoints;

// App Studio authoring uses an ephemeral chat session per request so it does not
// depend on actor runtime shape or concrete in-memory agent instances.
internal sealed class AppAuthoringChatSessionFactory
{
    private readonly ILLMProviderFactory _llmProviderFactory;
    private readonly IAgentClassDefaultsProvider<AIAgentConfig> _defaultsProvider;
    private readonly IReadOnlyList<IAgentRunMiddleware> _agentMiddlewares;
    private readonly IReadOnlyList<ILLMCallMiddleware> _llmMiddlewares;

    public AppAuthoringChatSessionFactory(
        ILLMProviderFactory llmProviderFactory,
        IAgentClassDefaultsProvider<AIAgentConfig> defaultsProvider,
        IEnumerable<IAgentRunMiddleware>? agentMiddlewares = null,
        IEnumerable<ILLMCallMiddleware>? llmMiddlewares = null)
    {
        _llmProviderFactory = llmProviderFactory ?? throw new ArgumentNullException(nameof(llmProviderFactory));
        _defaultsProvider = defaultsProvider ?? throw new ArgumentNullException(nameof(defaultsProvider));
        _agentMiddlewares = (agentMiddlewares ?? []).ToArray();
        _llmMiddlewares = (llmMiddlewares ?? []).ToArray();
    }

    public async Task<AppAuthoringChatSession> CreateAsync(
        Type generatorType,
        string sessionName,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(generatorType);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionName);
        ct.ThrowIfCancellationRequested();

        var defaults = await _defaultsProvider.GetSnapshotAsync(generatorType, ct);
        var config = CloneAndNormalize(defaults.Defaults);
        var history = new ChatHistory
        {
            MaxMessages = config.MaxHistoryMessages,
        };
        var runtime = new ChatRuntime(
            providerFactory: () => ResolveProvider(config),
            history: history,
            toolLoop: new ToolCallLoop(
                new ToolManager(),
                hooks: null,
                toolMiddlewares: null,
                llmMiddlewares: _llmMiddlewares),
            hooks: null,
            requestBuilder: () => BuildRequest(config),
            agentMiddlewares: _agentMiddlewares,
            llmMiddlewares: _llmMiddlewares,
            agentId: sessionName,
            agentName: sessionName,
            streamBufferCapacity: config.StreamBufferCapacity);

        return new AppAuthoringChatSession(runtime);
    }

    private ILLMProvider ResolveProvider(AIAgentConfig config) =>
        string.IsNullOrWhiteSpace(config.ProviderName)
            ? _llmProviderFactory.GetDefault()
            : _llmProviderFactory.GetProvider(config.ProviderName);

    private static LLMRequest BuildRequest(AIAgentConfig config) => new()
    {
        Messages = string.IsNullOrWhiteSpace(config.SystemPrompt)
            ? []
            : [ChatMessage.System(config.SystemPrompt)],
        Model = config.Model,
        Temperature = config.Temperature,
        MaxTokens = config.MaxTokens,
        Tools = null,
    };

    private static AIAgentConfig CloneAndNormalize(AIAgentConfig? source)
    {
        var config = new AIAgentConfig
        {
            ProviderName = source?.ProviderName ?? string.Empty,
            Model = source?.Model,
            SystemPrompt = source?.SystemPrompt ?? string.Empty,
            Temperature = source?.Temperature,
            MaxTokens = source?.MaxTokens,
            MaxToolRounds = source?.MaxToolRounds ?? 0,
            MaxHistoryMessages = source?.MaxHistoryMessages ?? 0,
            StreamBufferCapacity = source?.StreamBufferCapacity ?? 0,
        };

        config.ProviderName = config.ProviderName.Trim();
        config.Model = string.IsNullOrWhiteSpace(config.Model) ? null : config.Model.Trim();
        config.SystemPrompt ??= string.Empty;
        if (config.MaxToolRounds <= 0)
            config.MaxToolRounds = 10;
        if (config.MaxHistoryMessages <= 0)
            config.MaxHistoryMessages = 100;
        if (config.StreamBufferCapacity <= 0)
            config.StreamBufferCapacity = 256;

        return config;
    }
}

internal sealed class AppAuthoringChatSession
{
    private readonly ChatRuntime _runtime;

    public AppAuthoringChatSession(ChatRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public async Task<string?> GenerateWithReasoningAsync(
        string prompt,
        string requestId,
        IReadOnlyDictionary<string, string>? metadata,
        Func<string, CancellationToken, Task>? onReasoning,
        CancellationToken ct = default)
    {
        var content = new StringBuilder();
        await foreach (var chunk in _runtime.ChatStreamAsync(prompt, requestId, metadata, ct))
        {
            if (!string.IsNullOrEmpty(chunk.DeltaContent))
                content.Append(chunk.DeltaContent);

            if (!string.IsNullOrEmpty(chunk.DeltaReasoningContent) && onReasoning != null)
                await onReasoning(chunk.DeltaReasoningContent, ct);
        }

        return content.ToString();
    }
}
