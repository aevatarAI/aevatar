using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.GAgents.NyxidChat;

public sealed class NyxIdConversationReplyGenerator : IConversationReplyGenerator
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
    private readonly global::Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions? _relayOptions;
    private readonly INyxIdUserLlmPreferencesStore? _preferencesStore;
    private readonly IUserMemoryStore? _userMemoryStore;

    public NyxIdConversationReplyGenerator(
        ILLMProviderFactory llmProviderFactory,
        IEnumerable<IAgentToolSource>? toolSources = null,
        IEnumerable<IAgentRunMiddleware>? agentMiddlewares = null,
        IEnumerable<IToolCallMiddleware>? toolMiddlewares = null,
        IEnumerable<ILLMCallMiddleware>? llmMiddlewares = null,
        SkillRegistry? skillRegistry = null,
        global::Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions? relayOptions = null,
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
        IStreamingReplySink? streamingSink,
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

        // Emit a placeholder immediately so the user sees a message within the outbound RTT,
        // regardless of LLM cold-start, router selection, or tool-call latency before the
        // first real delta. The first real delta overwrites this placeholder via edit-in-place;
        // if no delta ever arrives (tool-only or empty turn), the caller's FinalizeAsync edits
        // the placeholder to the final text. Disabled by setting the option to empty/whitespace.
        if (streamingSink is not null)
        {
            var placeholder = _relayOptions?.StreamingPlaceholderText;
            if (!string.IsNullOrWhiteSpace(placeholder))
                await streamingSink.OnDeltaAsync(placeholder, ct);
        }

        var output = new StringBuilder();
        await foreach (var chunk in runtime.ChatStreamAsync(
                           activity.Content.Text,
                           MaxToolRounds,
                           activity.Id,
                           effectiveMetadata,
                           ct))
        {
            if (string.IsNullOrEmpty(chunk.DeltaContent))
                continue;

            output.Append(chunk.DeltaContent);
            if (streamingSink is not null)
                await streamingSink.OnDeltaAsync(output.ToString(), ct);
        }

        return output.ToString();
    }

    private async Task<IReadOnlyDictionary<string, string>> BuildEffectiveMetadataAsync(
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken ct)
    {
        var effective = new Dictionary<string, string>(metadata, StringComparer.Ordinal);

        // Issue #513 phase 3: prefs override chain is sender → bot-owner →
        // provider default. The bot owner's prefs are already pinned upstream
        // by OwnerLlmConfigApplier (channel inbound) or by direct
        // INyxIdUserLlmPreferencesStore reads (Studio API / streaming proxy),
        // so this generator only has to layer sender overrides on top when
        // the inbound carries a binding-id. SetIfFilled is field-level, so a
        // sender who set DefaultModel but not PreferredRoute still inherits
        // the bot owner's route from the upstream-pinned metadata.
        if (_preferencesStore is not null &&
            metadata.TryGetValue(LLMRequestMetadataKeys.SenderBindingId, out var senderBindingId) &&
            !string.IsNullOrWhiteSpace(senderBindingId))
        {
            await ApplyPreferencesAsync(senderBindingId, effective, ct);
        }

        if (_userMemoryStore is not null)
        {
            try
            {
                var promptSection = await _userMemoryStore.BuildPromptSectionAsync(2000, ct);
                if (!string.IsNullOrWhiteSpace(promptSection))
                    effective[LLMRequestMetadataKeys.UserMemoryPrompt] = promptSection;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // User memory is best-effort context and must not break the main reply path.
            }
        }

        return effective;
    }

    /// <summary>
    /// Read prefs for the bound sender and overwrite the matching metadata
    /// keys. Field-level: empty fields on the sender's record are skipped so
    /// the bot owner's value stays intact. User-config failures degrade to
    /// "no sender override" rather than failing the LLM turn.
    /// </summary>
    private async Task ApplyPreferencesAsync(
        string senderBindingId,
        Dictionary<string, string> effective,
        CancellationToken ct)
    {
        if (_preferencesStore is null)
            return;

        NyxIdUserLlmPreferences preferences;
        try
        {
            preferences = await _preferencesStore.GetForBindingAsync(senderBindingId, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return;
        }

        SetIfFilled(effective, LLMRequestMetadataKeys.ModelOverride, preferences.DefaultModel?.Trim());
        SetIfFilled(effective, LLMRequestMetadataKeys.NyxIdRoutePreference, preferences.PreferredRoute?.Trim());
        SetIfFilled(
            effective,
            LLMRequestMetadataKeys.MaxToolRoundsOverride,
            preferences.MaxToolRounds > 0 ? preferences.MaxToolRounds.ToString() : null);
    }

    private static void SetIfFilled(Dictionary<string, string> map, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        map[key] = value;
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
