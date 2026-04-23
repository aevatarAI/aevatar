using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.NyxidChat;

namespace Aevatar.GAgents.ChannelRuntime;

internal interface IConversationReplyGenerator
{
    Task<string?> GenerateReplyAsync(
        ChatActivity activity,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken ct);
}

internal sealed class NyxIdConversationReplyGenerator : IConversationReplyGenerator
{
    private const int MaxToolRounds = 40;
    private const int MaxHistoryMessages = 100;
    private const int StreamBufferCapacity = 256;

    private readonly ILLMProviderFactory _llmProviderFactory;
    private readonly IReadOnlyList<IAgentToolSource> _toolSources;
    private readonly IReadOnlyList<IAgentRunMiddleware> _agentMiddlewares;
    private readonly IReadOnlyList<IToolCallMiddleware> _toolMiddlewares;
    private readonly IReadOnlyList<ILLMCallMiddleware> _llmMiddlewares;
    private readonly SkillRegistry? _skillRegistry;
    private readonly NyxIdRelayOptions? _relayOptions;
    private readonly INyxIdUserLlmPreferencesStore? _preferencesStore;
    private readonly IUserMemoryStore? _userMemoryStore;

    public NyxIdConversationReplyGenerator(
        ILLMProviderFactory llmProviderFactory,
        IEnumerable<IAgentToolSource>? toolSources = null,
        IEnumerable<IAgentRunMiddleware>? agentMiddlewares = null,
        IEnumerable<IToolCallMiddleware>? toolMiddlewares = null,
        IEnumerable<ILLMCallMiddleware>? llmMiddlewares = null,
        SkillRegistry? skillRegistry = null,
        NyxIdRelayOptions? relayOptions = null,
        INyxIdUserLlmPreferencesStore? preferencesStore = null,
        IUserMemoryStore? userMemoryStore = null)
    {
        _llmProviderFactory = llmProviderFactory ?? throw new ArgumentNullException(nameof(llmProviderFactory));
        _toolSources = (toolSources ?? []).ToArray();
        _agentMiddlewares = (agentMiddlewares ?? []).ToArray();
        _toolMiddlewares = (toolMiddlewares ?? []).ToArray();
        _llmMiddlewares = (llmMiddlewares ?? []).ToArray();
        _skillRegistry = skillRegistry;
        _relayOptions = relayOptions;
        _preferencesStore = preferencesStore;
        _userMemoryStore = userMemoryStore;
    }

    public async Task<string?> GenerateReplyAsync(
        ChatActivity activity,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(metadata);

        var effectiveMetadata = await BuildEffectiveMetadataAsync(metadata, ct);
        var history = new global::Aevatar.AI.Core.Chat.ChatHistory
        {
            MaxMessages = MaxHistoryMessages,
        };
        var tools = new ToolManager();
        foreach (var tool in await DiscoverToolsAsync(ct))
            tools.Register(tool);

        var runtime = new ChatRuntime(
            providerFactory: ResolveProvider,
            history: history,
            toolLoop: new ToolCallLoop(
                tools,
                hooks: null,
                toolMiddlewares: _toolMiddlewares,
                llmMiddlewares: _llmMiddlewares),
            hooks: null,
            requestBuilder: () => new LLMRequest
            {
                Messages =
                [
                    ChatMessage.System(BuildSystemPrompt()),
                ],
                Metadata = new Dictionary<string, string>(effectiveMetadata, StringComparer.Ordinal),
                Tools = FilterValidTools(tools),
            },
            agentMiddlewares: _agentMiddlewares,
            llmMiddlewares: _llmMiddlewares,
            agentId: activity.Conversation?.CanonicalKey,
            agentName: "NyxIdConversationReply",
            streamBufferCapacity: StreamBufferCapacity);

        var output = new StringBuilder();
        await foreach (var chunk in runtime.ChatStreamAsync(
                           activity.Content.Text,
                           MaxToolRounds,
                           activity.Id,
                           effectiveMetadata,
                           ct))
        {
            if (!string.IsNullOrEmpty(chunk.DeltaContent))
                output.Append(chunk.DeltaContent);
        }

        return output.ToString();
    }

    private async Task<IReadOnlyDictionary<string, string>> BuildEffectiveMetadataAsync(
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken ct)
    {
        var effective = new Dictionary<string, string>(metadata, StringComparer.Ordinal);

        if (_preferencesStore is not null)
        {
            try
            {
                var preferences = await _preferencesStore.GetAsync(ct);
                if (!string.IsNullOrWhiteSpace(preferences.DefaultModel))
                    effective[LLMRequestMetadataKeys.ModelOverride] = preferences.DefaultModel.Trim();
                if (!string.IsNullOrWhiteSpace(preferences.PreferredRoute))
                    effective[LLMRequestMetadataKeys.NyxIdRoutePreference] = preferences.PreferredRoute.Trim();
                if (preferences.MaxToolRounds > 0)
                    effective[LLMRequestMetadataKeys.MaxToolRoundsOverride] = preferences.MaxToolRounds.ToString();
            }
            catch
            {
                // User config is additive only; channel runtime falls back to server defaults on failure.
            }
        }

        if (_userMemoryStore is not null)
        {
            try
            {
                var promptSection = await _userMemoryStore.BuildPromptSectionAsync(2000, ct);
                if (!string.IsNullOrWhiteSpace(promptSection))
                    effective[LLMRequestMetadataKeys.UserMemoryPrompt] = promptSection;
            }
            catch
            {
                // User memory is best-effort context and must not break the main reply path.
            }
        }

        return effective;
    }

    private async Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct)
    {
        if (_toolSources.Count == 0)
            return [];

        var discovered = new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in _toolSources)
        {
            var tools = await source.DiscoverToolsAsync(ct);
            foreach (var tool in tools)
                discovered[tool.Name] = tool;
        }

        return discovered.Values.ToArray();
    }

    private ILLMProvider ResolveProvider()
    {
        var available = _llmProviderFactory.GetAvailableProviders();
        if (available.Any(name => string.Equals(name, NyxIdChatServiceDefaults.ProviderName, StringComparison.OrdinalIgnoreCase)))
            return _llmProviderFactory.GetProvider(NyxIdChatServiceDefaults.ProviderName);

        return _llmProviderFactory.GetDefault();
    }

    private static IReadOnlyList<IAgentTool>? FilterValidTools(ToolManager tools)
    {
        if (!tools.HasTools)
            return null;

        var valid = tools.GetAll()
            .Where(tool => !string.IsNullOrWhiteSpace(tool.Name))
            .ToArray();
        return valid.Length == 0 ? null : valid;
    }

    private string BuildSystemPrompt()
    {
        var prompt = LoadBaseSystemPrompt();
        prompt += NyxIdRelayPromptConfiguration.BuildChannelRuntimeConfigurationSection(_relayOptions);

        if (_skillRegistry != null && _skillRegistry.Count > 0)
        {
            var skillSection = _skillRegistry.BuildSystemPromptSection();
            if (!string.IsNullOrEmpty(skillSection))
                prompt += "\n" + skillSection;
        }

        return prompt;
    }

    private static string LoadBaseSystemPrompt()
    {
        var assembly = typeof(NyxIdChatGAgent).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith("system-prompt.md", StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
            return "You are a helpful NyxID assistant.";

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            return "You are a helpful NyxID assistant.";

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
