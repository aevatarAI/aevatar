using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Core;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Tools;
using Aevatar.Studio.Application.Studio.Authoring;

namespace Aevatar.Studio.Infrastructure.Authoring;

// Refactor (iter21/cluster-001):
//   Old pattern: Host AppAuthoringChatSessionFactory constructed ChatRuntime and hid fake GAgent type-token defaults.
//   New principle: Infrastructure adapts ChatRuntime behind the typed Application LLM stream port, using ChatStreamAsync only.
internal sealed class ChatRuntimeStudioAuthoringLLMStreamPort : IStudioAuthoringLLMStreamPort
{
    private readonly ILLMProviderFactory _llmProviderFactory;
    private readonly WorkflowAuthoringPromptCatalog _workflowPrompts;
    private readonly ScriptAuthoringPromptCatalog _scriptPrompts;
    private readonly IReadOnlyList<IAgentRunMiddleware> _agentMiddlewares;
    private readonly IReadOnlyList<ILLMCallMiddleware> _llmMiddlewares;

    public ChatRuntimeStudioAuthoringLLMStreamPort(
        ILLMProviderFactory llmProviderFactory,
        WorkflowAuthoringPromptCatalog workflowPrompts,
        ScriptAuthoringPromptCatalog scriptPrompts,
        IEnumerable<IAgentRunMiddleware>? agentMiddlewares = null,
        IEnumerable<ILLMCallMiddleware>? llmMiddlewares = null)
    {
        _llmProviderFactory = llmProviderFactory ?? throw new ArgumentNullException(nameof(llmProviderFactory));
        _workflowPrompts = workflowPrompts ?? throw new ArgumentNullException(nameof(workflowPrompts));
        _scriptPrompts = scriptPrompts ?? throw new ArgumentNullException(nameof(scriptPrompts));
        _agentMiddlewares = (agentMiddlewares ?? []).ToArray();
        _llmMiddlewares = (llmMiddlewares ?? []).ToArray();
    }

    public async IAsyncEnumerable<StudioAuthoringLLMChunk> StreamAsync(
        StudioAuthoringLLMRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RequestId);

        var config = BuildConfig(request.Kind);
        var runtime = new ChatRuntime(
            providerFactory: () => ResolveProvider(config),
            history: new ChatHistory
            {
                MaxMessages = config.MaxHistoryMessages,
            },
            toolLoop: new ToolCallLoop(
                new ToolManager(),
                hooks: null,
                llmMiddlewares: _llmMiddlewares),
            hooks: null,
            requestBuilder: _ => BuildRequest(config),
            agentMiddlewares: _agentMiddlewares,
            llmMiddlewares: _llmMiddlewares,
            agentId: BuildAgentName(request.Kind),
            agentName: BuildAgentName(request.Kind));

        await foreach (var chunk in runtime.ChatStreamAsync(
                           [ContentPart.TextPart(request.Prompt)],
                           config.MaxToolRounds,
                           request.RequestId,
                           request.LlmControl,
                           toolContext: null,
                           turnCatalog: null,
                           request.Metadata,
                           ct))
        {
            yield return new StudioAuthoringLLMChunk(
                chunk.DeltaContent,
                chunk.DeltaReasoningContent);
        }
    }

    private AIAgentConfig BuildConfig(StudioAuthoringKind kind)
    {
        var config = new AIAgentConfig
        {
            SystemPrompt = kind == StudioAuthoringKind.Script
                ? _scriptPrompts.SystemPrompt
                : _workflowPrompts.SystemPrompt,
            Temperature = 0.1,
            MaxTokens = 4096,
            MaxToolRounds = 1,
            MaxHistoryMessages = 12,
        };

        return CloneAndNormalize(config);
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

    private static string BuildAgentName(StudioAuthoringKind kind) =>
        kind == StudioAuthoringKind.Script
            ? "studio-script-authoring-preview"
            : "studio-workflow-authoring-preview";

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
        };

        config.ProviderName = config.ProviderName.Trim();
        config.Model = string.IsNullOrWhiteSpace(config.Model) ? null : config.Model.Trim();
        config.SystemPrompt ??= string.Empty;
        if (config.MaxToolRounds <= 0)
            config.MaxToolRounds = 10;
        if (config.MaxHistoryMessages <= 0)
            config.MaxHistoryMessages = 100;
        return config;
    }
}
