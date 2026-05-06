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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.NyxidChat;

public sealed class NyxIdConversationReplyGenerator : IConversationReplyGenerator
{
    private const int MaxToolRounds = 40;
    private const int MaxHistoryMessages = 100;
    private const int StreamBufferCapacity = 256;
    private const int DefaultRemoteSkillAutoLoadMaxSkills = 2;
    private const int MaxRemoteSkillAutoLoadMaxSkills = 5;
    private const int MaxRemoteSkillSearchQueryChars = 500;
    private const int DefaultRemoteSkillAutoLoadTimeoutSeconds = 3;
    private const int MaxRemoteSkillAutoLoadTimeoutSeconds = 30;

    private readonly ILLMProviderFactory _llmProviderFactory;
    private readonly IReadOnlyList<IAgentToolSource> _toolSources;
    private readonly IReadOnlyList<IAgentRunMiddleware> _agentMiddlewares;
    private readonly IReadOnlyList<IToolCallMiddleware> _toolMiddlewares;
    private readonly IReadOnlyList<ILLMCallMiddleware> _llmMiddlewares;
    private readonly SkillRegistry? _skillRegistry;
    private readonly IReadOnlyList<IRemoteSkillDiscovery> _remoteSkillDiscoveries;
    private readonly IRemoteSkillFetcher? _remoteSkillFetcher;
    private readonly NyxIdChatOptions? _chatOptions;
    private readonly global::Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions? _relayOptions;
    private readonly INyxIdUserLlmPreferencesStore? _preferencesStore;
    private readonly IUserMemoryStore? _userMemoryStore;
    private readonly ILogger<NyxIdConversationReplyGenerator> _logger;

    private sealed record EffectiveMetadataPlan(
        IReadOnlyDictionary<string, string> Primary,
        IReadOnlyDictionary<string, string>? OwnerFallback);

    private sealed record SenderPreferenceApplication(bool AnyApplied, bool RouteApplied);

    public NyxIdConversationReplyGenerator(
        ILLMProviderFactory llmProviderFactory,
        IEnumerable<IAgentToolSource>? toolSources = null,
        IEnumerable<IAgentRunMiddleware>? agentMiddlewares = null,
        IEnumerable<IToolCallMiddleware>? toolMiddlewares = null,
        IEnumerable<ILLMCallMiddleware>? llmMiddlewares = null,
        SkillRegistry? skillRegistry = null,
        IEnumerable<IRemoteSkillDiscovery>? remoteSkillDiscoveries = null,
        IRemoteSkillFetcher? remoteSkillFetcher = null,
        NyxIdChatOptions? chatOptions = null,
        global::Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions? relayOptions = null,
        INyxIdUserLlmPreferencesStore? preferencesStore = null,
        IUserMemoryStore? userMemoryStore = null,
        ILogger<NyxIdConversationReplyGenerator>? logger = null)
    {
        _llmProviderFactory = llmProviderFactory ?? throw new ArgumentNullException(nameof(llmProviderFactory));
        _toolSources = (toolSources ?? []).ToArray();
        _agentMiddlewares = (agentMiddlewares ?? []).ToArray();
        _toolMiddlewares = (toolMiddlewares ?? []).ToArray();
        _llmMiddlewares = (llmMiddlewares ?? []).ToArray();
        _skillRegistry = skillRegistry;
        _remoteSkillDiscoveries = (remoteSkillDiscoveries ?? []).ToArray();
        _remoteSkillFetcher = remoteSkillFetcher;
        _chatOptions = chatOptions;
        _relayOptions = relayOptions;
        _preferencesStore = preferencesStore;
        _userMemoryStore = userMemoryStore;
        _logger = logger ?? NullLogger<NyxIdConversationReplyGenerator>.Instance;
    }

    public async Task<string?> GenerateReplyAsync(
        ChatActivity activity,
        IReadOnlyDictionary<string, string> metadata,
        IStreamingReplySink? streamingSink,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(metadata);

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

        var metadataPlan = await BuildEffectiveMetadataPlanAsync(metadata, ct);
        var primaryTurn = await BuildTurnToolContextAsync(activity, metadataPlan.Primary, ct);

        try
        {
            return await GenerateWithMetadataAsync(
                    activity,
                    metadataPlan.Primary,
                    primaryTurn.Tools,
                    streamingSink,
                    primaryTurn.SkillRegistry,
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (metadataPlan.OwnerFallback is not null)
        {
            _logger.LogWarning(
                ex,
                "Sender LLM route failed; retrying with bot owner LLM config. activity={ActivityId}",
                activity.Id);

            var fallbackTurn = await BuildTurnToolContextAsync(activity, metadataPlan.OwnerFallback, ct);
            return await GenerateWithMetadataAsync(
                    activity,
                    metadataPlan.OwnerFallback,
                    fallbackTurn.Tools,
                    streamingSink,
                    fallbackTurn.SkillRegistry,
                    ct)
                .ConfigureAwait(false);
        }
    }

    private sealed record TurnToolContext(ToolManager Tools, SkillRegistry? SkillRegistry);

    private async Task<TurnToolContext> BuildTurnToolContextAsync(
        ChatActivity activity,
        IReadOnlyDictionary<string, string> effectiveMetadata,
        CancellationToken ct)
    {
        var tools = new ToolManager();
        foreach (var tool in await DiscoverToolsAsync(ct))
            tools.Register(tool);

        var remoteSkills = await AutoLoadRemoteSkillsAsync(activity, effectiveMetadata, ct);
        var turnSkillRegistry = BuildTurnSkillRegistry(remoteSkills);
        if (turnSkillRegistry is not null || _remoteSkillFetcher is not null)
            tools.Register(new UseSkillTool(turnSkillRegistry ?? new SkillRegistry(), _remoteSkillFetcher));

        return new TurnToolContext(tools, turnSkillRegistry);
    }

    private SkillRegistry? BuildTurnSkillRegistry(IReadOnlyList<SkillDefinition> remoteSkills)
    {
        if (_skillRegistry is null && remoteSkills.Count == 0)
            return null;

        var registry = new SkillRegistry();
        if (_skillRegistry is not null)
        {
            var localSkills = _skillRegistry.GetAll()
                .Where(skill => skill.Source == SkillSource.Local);
            registry.RegisterRange(localSkills);
        }

        if (remoteSkills.Count > 0)
            registry.RegisterRange(remoteSkills);

        return registry;
    }

    private async Task<IReadOnlyList<SkillDefinition>> AutoLoadRemoteSkillsAsync(
        ChatActivity activity,
        IReadOnlyDictionary<string, string> effectiveMetadata,
        CancellationToken ct)
    {
        if (!ShouldAutoLoadRemoteSkills(activity, effectiveMetadata))
            return [];

        if (_remoteSkillFetcher is null || _remoteSkillDiscoveries.Count == 0)
            return [];

        if (!effectiveMetadata.TryGetValue(LLMRequestMetadataKeys.NyxIdAccessToken, out var token) ||
            string.IsNullOrWhiteSpace(token))
        {
            return [];
        }

        var query = BuildRemoteSkillSearchQuery(activity);
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var maxSkills = ResolveRemoteSkillAutoLoadMaxSkills();
        if (maxSkills == 0)
            return [];

        using var timeoutCts = CreateRemoteSkillAutoLoadCancellation(ct);
        var loadCt = timeoutCts.Token;

        var request = new RemoteSkillSearchRequest(
            AccessToken: token.Trim(),
            Query: query,
            Scope: "mixed",
            Mode: ResolveRemoteSkillSearchMode(),
            PageSize: maxSkills);

        var loaded = new List<SkillDefinition>(maxSkills);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var discovery in _remoteSkillDiscoveries)
            {
                IReadOnlyList<RemoteSkillSummary> candidates;
                try
                {
                    candidates = await discovery.SearchSkillsAsync(request, loadCt).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Remote skill discovery failed for Lark turn. activity={ActivityId}", activity.Id);
                    continue;
                }

                foreach (var candidate in candidates)
                {
                    if (loaded.Count >= maxSkills)
                        return loaded;

                    var key = string.IsNullOrWhiteSpace(candidate.RemoteId) ? candidate.Name : candidate.RemoteId;
                    if (string.IsNullOrWhiteSpace(key) || !seen.Add(key))
                        continue;

                    try
                    {
                        var skill = await _remoteSkillFetcher.FetchSkillAsync(token.Trim(), key.Trim(), loadCt)
                            .ConfigureAwait(false);
                        if (skill is not null)
                            loaded.Add(skill);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Remote skill fetch failed for Lark turn. activity={ActivityId} skill={Skill}",
                            activity.Id,
                            candidate.Name);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Remote skill auto-load timed out for Lark turn. activity={ActivityId}", activity.Id);
        }

        return loaded;
    }

    private bool ShouldAutoLoadRemoteSkills(
        ChatActivity activity,
        IReadOnlyDictionary<string, string> effectiveMetadata)
    {
        if (_chatOptions?.LarkRemoteSkillAutoLoadEnabled == false)
            return false;

        if (!effectiveMetadata.TryGetValue(ChannelMetadataKeys.Platform, out var platform) ||
            string.IsNullOrWhiteSpace(platform))
        {
            platform = activity.Conversation?.CanonicalKey ?? activity.ChannelId?.Value ?? string.Empty;
        }

        return platform.Contains("lark", StringComparison.OrdinalIgnoreCase) ||
               platform.Contains("feishu", StringComparison.OrdinalIgnoreCase);
    }

    private int ResolveRemoteSkillAutoLoadMaxSkills()
    {
        var configured = _chatOptions?.LarkRemoteSkillAutoLoadMaxSkills ?? DefaultRemoteSkillAutoLoadMaxSkills;
        return Math.Clamp(configured, 0, MaxRemoteSkillAutoLoadMaxSkills);
    }

    private string ResolveRemoteSkillSearchMode()
    {
        var configured = _chatOptions?.LarkRemoteSkillAutoLoadSearchMode;
        return string.Equals(configured, "keyword", StringComparison.OrdinalIgnoreCase)
            ? "keyword"
            : "semantic";
    }

    private CancellationTokenSource CreateRemoteSkillAutoLoadCancellation(CancellationToken ct)
    {
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var configured = _chatOptions?.LarkRemoteSkillAutoLoadTimeoutSeconds ??
                         DefaultRemoteSkillAutoLoadTimeoutSeconds;
        var seconds = Math.Clamp(configured, 1, MaxRemoteSkillAutoLoadTimeoutSeconds);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(seconds));
        return timeoutCts;
    }

    private static string BuildRemoteSkillSearchQuery(ChatActivity activity)
    {
        var query = activity.Content?.Text?.Trim() ?? string.Empty;
        return query.Length <= MaxRemoteSkillSearchQueryChars
            ? query
            : query[..MaxRemoteSkillSearchQueryChars];
    }

    private async Task<string?> GenerateWithMetadataAsync(
        ChatActivity activity,
        IReadOnlyDictionary<string, string> effectiveMetadata,
        ToolManager tools,
        IStreamingReplySink? streamingSink,
        SkillRegistry? turnSkillRegistry,
        CancellationToken ct)
    {
        var history = new global::Aevatar.AI.Core.Chat.ChatHistory
        {
            MaxMessages = MaxHistoryMessages,
        };
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
                    ChatMessage.System(BuildSystemPrompt(turnSkillRegistry)),
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
            if (string.IsNullOrEmpty(chunk.DeltaContent))
                continue;

            output.Append(chunk.DeltaContent);
            if (streamingSink is not null)
                await streamingSink.OnDeltaAsync(output.ToString(), ct);
        }

        return output.ToString();
    }

    private async Task<EffectiveMetadataPlan> BuildEffectiveMetadataPlanAsync(
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken ct)
    {
        var effective = new Dictionary<string, string>(metadata, StringComparer.Ordinal);
        effective.Remove(LLMRequestMetadataKeys.SenderNyxIdAccessToken);
        Dictionary<string, string>? ownerFallback = null;

        // Issue #513 phase 3: prefs override chain is sender → bot-owner →
        // provider default. The bot owner's prefs are already pinned upstream
        // by OwnerLlmConfigApplier (channel inbound) or by direct
        // INyxIdUserLlmPreferencesStore reads (Studio API / streaming proxy),
        // so this generator only has to layer sender overrides on top when
        // the inbound carries a binding-id. SetIfFilled is field-level, so a
        // sender who set DefaultModel but not PreferredRoute still inherits
        // the bot owner's route from the upstream-pinned metadata. If a
        // sender-owned attempt fails, we retry once with this owner snapshot.
        if (_preferencesStore is not null &&
            metadata.TryGetValue(LLMRequestMetadataKeys.SenderBindingId, out var senderBindingId) &&
            !string.IsNullOrWhiteSpace(senderBindingId))
        {
            var ownerSnapshot = CreateOwnerFallbackSnapshot(effective);
            var applied = await ApplyPreferencesAsync(senderBindingId, effective, ct);
            if (applied.RouteApplied)
            {
                if (metadata.TryGetValue(LLMRequestMetadataKeys.SenderNyxIdAccessToken, out var senderAccessToken) &&
                    !string.IsNullOrWhiteSpace(senderAccessToken))
                {
                    var trimmedToken = senderAccessToken.Trim();
                    effective[LLMRequestMetadataKeys.NyxIdAccessToken] = trimmedToken;
                    effective[LLMRequestMetadataKeys.NyxIdOrgToken] = trimmedToken;
                    ownerFallback = ownerSnapshot;
                }
                else
                {
                    effective = ownerSnapshot;
                }
            }
            else if (applied.AnyApplied)
            {
                ownerFallback = ownerSnapshot;
            }
        }

        if (_userMemoryStore is not null)
        {
            try
            {
                var promptSection = await _userMemoryStore.BuildPromptSectionAsync(2000, ct);
                if (!string.IsNullOrWhiteSpace(promptSection))
                {
                    effective[LLMRequestMetadataKeys.UserMemoryPrompt] = promptSection;
                    if (ownerFallback is not null)
                        ownerFallback[LLMRequestMetadataKeys.UserMemoryPrompt] = promptSection;
                }
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

        return new EffectiveMetadataPlan(effective, ownerFallback);
    }

    /// <summary>
    /// Read prefs for the bound sender and overwrite the matching metadata
    /// keys. Field-level: empty fields on the sender's record are skipped so
    /// the bot owner's value stays intact. User-config failures degrade to
    /// "no sender override" rather than failing the LLM turn.
    /// </summary>
    private async Task<SenderPreferenceApplication> ApplyPreferencesAsync(
        string senderBindingId,
        Dictionary<string, string> effective,
        CancellationToken ct)
    {
        if (_preferencesStore is null)
            return new SenderPreferenceApplication(false, false);

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
            return new SenderPreferenceApplication(false, false);
        }

        var modelApplied = SetIfFilled(effective, LLMRequestMetadataKeys.ModelOverride, preferences.DefaultModel?.Trim());
        var routeApplied = SetIfFilled(effective, LLMRequestMetadataKeys.NyxIdRoutePreference, preferences.PreferredRoute?.Trim());
        var roundsApplied = SetIfFilled(
            effective,
            LLMRequestMetadataKeys.MaxToolRoundsOverride,
            preferences.MaxToolRounds > 0 ? preferences.MaxToolRounds.ToString() : null);
        return new SenderPreferenceApplication(modelApplied || routeApplied || roundsApplied, routeApplied);
    }

    private static Dictionary<string, string> CreateOwnerFallbackSnapshot(Dictionary<string, string> effective)
    {
        var snapshot = new Dictionary<string, string>(effective, StringComparer.Ordinal);
        snapshot.Remove(LLMRequestMetadataKeys.SenderBindingId);
        snapshot.Remove(LLMRequestMetadataKeys.SenderNyxIdAccessToken);
        return snapshot;
    }

    private static bool SetIfFilled(Dictionary<string, string> map, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        map[key] = value;
        return true;
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

    private string BuildSystemPrompt(SkillRegistry? turnSkillRegistry = null)
    {
        var prompt = LoadBaseSystemPrompt();
        prompt += NyxIdRelayPromptConfiguration.BuildChannelRuntimeConfigurationSection(_relayOptions);

        var registry = turnSkillRegistry ?? _skillRegistry;
        if (registry != null && registry.Count > 0)
        {
            var skillSection = registry.BuildSystemPromptSection();
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
