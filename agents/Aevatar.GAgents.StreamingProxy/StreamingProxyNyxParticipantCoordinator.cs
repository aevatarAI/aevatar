using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Studio.Application.Studio.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.StreamingProxy;

internal sealed class StreamingProxyNyxParticipantCoordinator
{
    private const string NyxIdProviderName = "nyxid";
    private const string GatewaySuffix = "/api/v1/llm/gateway/v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly IReadOnlyDictionary<string, string[]> FallbackModels =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic"] =
            [
                "claude-sonnet-4-5-20250929",
                "claude-opus-4-20250514",
                "claude-sonnet-4-20250514",
                "claude-haiku-4-5-20251001",
            ],
            ["google-ai"] =
            [
                "gemini-2.5-pro-preview-06-05",
                "gemini-2.5-flash-preview-05-20",
                "gemini-2.0-flash",
            ],
            ["cohere"] =
            [
                "command-r-plus",
                "command-r",
                "command-a-03-2025",
            ],
        };

    private static readonly string[] NonLlmServiceKeywords =
    [
        "sisyphus",
        "chrono-storage",
        "chrono-sandbox",
        "chrono-graph",
        "ornn",
        "admin",
        "webhook",
        "n8n",
        "grafana",
        "prometheus",
    ];

    private readonly ILLMProviderFactory _llmProviderFactory;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly INyxIdUserLlmPreferencesStore? _preferencesStore;
    private readonly ILogger<StreamingProxyNyxParticipantCoordinator> _logger;

    public StreamingProxyNyxParticipantCoordinator(
        ILLMProviderFactory llmProviderFactory,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<StreamingProxyNyxParticipantCoordinator> logger,
        INyxIdUserLlmPreferencesStore? preferencesStore = null)
    {
        _llmProviderFactory = llmProviderFactory;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _preferencesStore = preferencesStore;
    }

    public async Task<IReadOnlyList<StreamingProxyNyxParticipantDefinition>> EnsureParticipantsJoinedAsync(
        string scopeId,
        string roomId,
        IActor actor,
        IStreamingProxyParticipantStore participantStore,
        string accessToken,
        CancellationToken ct)
    {
        var participants = await ResolveParticipantsAsync(accessToken, ct);
        if (participants.Count == 0)
            return participants;

        var existing = await participantStore.ListAsync(roomId, ct);
        var existingIds = existing
            .Select(entry => entry.AgentId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var participant in participants)
        {
            if (existingIds.Contains(participant.RouteSlug))
                continue;

            await DispatchAsync(actor, new GroupChatParticipantJoinedEvent
            {
                AgentId = participant.RouteSlug,
                DisplayName = participant.DisplayName,
            }, ct);

            await participantStore.AddAsync(roomId, participant.RouteSlug, participant.DisplayName, ct);
        }

        return participants;
    }

    public async Task GenerateRepliesAsync(
        IReadOnlyList<StreamingProxyNyxParticipantDefinition> participants,
        IActor actor,
        string prompt,
        string sessionId,
        string accessToken,
        CancellationToken ct)
    {
        if (participants.Count == 0)
            return;

        if (!_llmProviderFactory.GetAvailableProviders().Contains(NyxIdProviderName, StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogWarning("NyxID provider '{ProviderName}' is not registered; skip Streaming Proxy participants.", NyxIdProviderName);
            return;
        }

        var provider = _llmProviderFactory.GetProvider(NyxIdProviderName);
        var transcript = new List<(string Speaker, string Content)>();
        var activeParticipants = participants.ToList();
        var rounds = activeParticipants.Count > 1 ? StreamingProxyDefaults.MaxDiscussionRounds : 1;

        for (var round = 1; round <= rounds && activeParticipants.Count > 0; round++)
        {
            var successfulReplies = 0;
            var failedParticipants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var roundParticipants = activeParticipants.ToList();

            foreach (var participant in roundParticipants)
            {
                ct.ThrowIfCancellationRequested();

                if (failedParticipants.Contains(participant.RouteSlug))
                    continue;

                var availableParticipants = activeParticipants
                    .Where(candidate => !failedParticipants.Contains(candidate.RouteSlug))
                    .ToList();

                if (availableParticipants.Count < 2)
                    break;

                try
                {
                    var request = BuildParticipantRequest(
                        participant,
                        availableParticipants,
                        prompt,
                        sessionId,
                        accessToken,
                        transcript,
                        round,
                        rounds);
                    var response = await provider.ChatAsync(request, ct);
                    var content = NormalizeParticipantReply(
                        participant,
                        availableParticipants,
                        response.Content);
                    if (string.IsNullOrWhiteSpace(content))
                        continue;

                    transcript.Add((participant.DisplayName, content));
                    successfulReplies++;
                    await DispatchAsync(actor, new GroupChatMessageEvent
                    {
                        AgentId = participant.RouteSlug,
                        AgentName = participant.DisplayName,
                        Content = content,
                        SessionId = $"{sessionId}:{participant.RouteSlug}:round-{round}",
                    }, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failedParticipants.Add(participant.RouteSlug);
                    _logger.LogWarning(ex,
                        "Streaming Proxy participant '{Participant}' failed for route '{RouteSlug}' in round {Round}.",
                        participant.DisplayName,
                        participant.RouteSlug,
                        round);

                    await DispatchAsync(actor, new GroupChatMessageEvent
                    {
                        AgentId = participant.RouteSlug,
                        AgentName = participant.DisplayName,
                        Content = $"当前暂时不可用: {SanitizeFailureMessage(ex.Message)}",
                        SessionId = $"{sessionId}:{participant.RouteSlug}:round-{round}:error",
                    }, ct);
                }
            }

            if (failedParticipants.Count > 0)
            {
                activeParticipants = activeParticipants
                    .Where(participant => !failedParticipants.Contains(participant.RouteSlug))
                    .ToList();
            }

            if (successfulReplies == 0)
                break;

            if (activeParticipants.Count < 2)
                break;
        }
    }

    private async Task<IReadOnlyList<StreamingProxyNyxParticipantDefinition>> ResolveParticipantsAsync(
        string accessToken,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            return [];

        var authorityBase = ResolveNyxIdAuthorityBase();
        if (string.IsNullOrWhiteSpace(authorityBase))
            return [];

        var preferencesTask = GetPreferencesAsync(ct);
        var readyProviders = await FetchReadyProvidersAsync(authorityBase, accessToken, ct);
        if (readyProviders.Count == 0)
            return [];

        var modelsByProvider = await FetchModelsFromProvidersAsync(authorityBase, accessToken, readyProviders, ct);
        var preferences = await preferencesTask;

        var ordered = readyProviders
            .DistinctBy(provider => provider.ProviderSlug, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(provider => IsPreferredRoute(provider.ProviderSlug, preferences.PreferredRoute))
            .ThenBy(provider => provider.ProviderName ?? provider.ProviderSlug, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var participants = new List<StreamingProxyNyxParticipantDefinition>(ordered.Count);
        foreach (var provider in ordered)
        {
            var slug = provider.ProviderSlug?.Trim();
            if (string.IsNullOrWhiteSpace(slug))
                continue;

            modelsByProvider.TryGetValue(slug, out var availableModels);
            var model = ResolveParticipantModel(preferences.DefaultModel, availableModels);
            participants.Add(new StreamingProxyNyxParticipantDefinition(
                slug,
                string.IsNullOrWhiteSpace(provider.ProviderName) ? slug : provider.ProviderName.Trim(),
                model));
        }

        return participants;
    }

    private async Task<List<StreamingProxyNyxProviderStatus>> FetchReadyProvidersAsync(
        string authorityBase,
        string accessToken,
        CancellationToken ct)
    {
        var providers = new Dictionary<string, StreamingProxyNyxProviderStatus>(StringComparer.OrdinalIgnoreCase);

        var gatewayProvidersTask = FetchReadyGatewayProvidersAsync(authorityBase, accessToken, ct);
        var userServicesTask = FetchUserServicesAsync(authorityBase, accessToken, ct);

        try
        {
            var gatewayProviders = await gatewayProvidersTask;
            foreach (var provider in gatewayProviders)
            {
                var slug = provider.ProviderSlug?.Trim();
                if (string.IsNullOrWhiteSpace(slug))
                    continue;

                providers[slug] = provider;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch ready gateway providers for Streaming Proxy participants.");
        }

        try
        {
            var userServices = await userServicesTask;
            foreach (var service in userServices)
            {
                if (IsNonLlmService(service.Slug))
                    continue;

                providers[service.Slug] = new StreamingProxyNyxProviderStatus
                {
                    ProviderSlug = service.Slug,
                    ProviderName = service.Label,
                    Status = "ready",
                    ProxyUrl = $"{authorityBase}/api/v1/proxy/s/{Uri.EscapeDataString(service.Slug)}",
                    Source = "user_service",
                };
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch ready user services for Streaming Proxy participants.");
        }

        return providers.Values.ToList();
    }

    private async Task<StreamingProxyNyxProviderStatus[]> FetchReadyGatewayProvidersAsync(
        string authorityBase,
        string accessToken,
        CancellationToken ct)
    {
        var statusUrl = $"{authorityBase}/api/v1/llm/status";
        using var request = new HttpRequestMessage(HttpMethod.Get, statusUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _httpClientFactory.CreateClient().SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return [];

        var body = await response.Content.ReadAsStringAsync(ct);
        var status = JsonSerializer.Deserialize<StreamingProxyNyxLlmStatusResponse>(body, JsonOptions);
        return (status?.Providers ?? [])
            .Where(provider =>
                string.Equals(provider.Status, "ready", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(provider.ProviderSlug))
            .ToArray();
    }

    private async Task<List<StreamingProxyNyxUserService>> FetchUserServicesAsync(
        string authorityBase,
        string accessToken,
        CancellationToken ct)
    {
        var keysUrl = $"{authorityBase}/api/v1/keys";
        using var request = new HttpRequestMessage(HttpMethod.Get, keysUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _httpClientFactory.CreateClient().SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return [];

        var body = await response.Content.ReadAsStringAsync(ct);
        var envelope = JsonSerializer.Deserialize<StreamingProxyNyxKeysResponse>(body, JsonOptions);
        return (envelope?.Keys ?? [])
            .Where(service =>
                string.Equals(service.Status, "active", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(service.Slug) &&
                !string.IsNullOrWhiteSpace(service.EndpointUrl))
            .ToList();
    }

    private async Task<Dictionary<string, List<string>>> FetchModelsFromProvidersAsync(
        string authorityBase,
        string accessToken,
        List<StreamingProxyNyxProviderStatus> readyProviders,
        CancellationToken ct)
    {
        var tasks = readyProviders
            .Where(provider => !string.IsNullOrWhiteSpace(provider.ProxyUrl))
            .Select(async provider =>
            {
                var slug = provider.ProviderSlug?.Trim() ?? string.Empty;
                try
                {
                    var proxyUrl = provider.ProxyUrl!.Trim().TrimEnd('/');
                    var baseUrl = proxyUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        ? proxyUrl
                        : $"{authorityBase}{proxyUrl}";

                    var modelsUrl = $"{baseUrl}/models";
                    using var request = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                    using var response = await _httpClientFactory.CreateClient().SendAsync(request, ct);
                    if (response.IsSuccessStatusCode)
                    {
                        var body = await response.Content.ReadAsStringAsync(ct);
                        var envelope = JsonSerializer.Deserialize<StreamingProxyOpenAIModelsResponse>(body, JsonOptions);
                        var models = (envelope?.Data ?? [])
                            .Where(model => !string.IsNullOrWhiteSpace(model.Id))
                            .Select(model => model.Id!.Trim())
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        return new KeyValuePair<string, List<string>>(slug, models);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to fetch models for Streaming Proxy participant '{ProviderSlug}'.", slug);
                }

                var fallback = FallbackModels.TryGetValue(slug, out var fallbackModels)
                    ? fallbackModels.ToList()
                    : [];
                return new KeyValuePair<string, List<string>>(slug, fallback);
            });

        var results = await Task.WhenAll(tasks);
        return results
            .Where(result => !string.IsNullOrWhiteSpace(result.Key))
            .ToDictionary(result => result.Key, result => result.Value, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<NyxIdUserLlmPreferences> GetPreferencesAsync(CancellationToken ct)
    {
        if (_preferencesStore == null)
            return new NyxIdUserLlmPreferences(string.Empty, string.Empty);

        try
        {
            return await _preferencesStore.GetAsync(ct);
        }
        catch
        {
            return new NyxIdUserLlmPreferences(string.Empty, string.Empty);
        }
    }

    private static string? ResolveParticipantModel(
        string? defaultModel,
        IReadOnlyList<string>? availableModels)
    {
        if (!string.IsNullOrWhiteSpace(defaultModel) &&
            (availableModels == null || availableModels.Count == 0 ||
             availableModels.Contains(defaultModel.Trim(), StringComparer.OrdinalIgnoreCase)))
        {
            return defaultModel.Trim();
        }

        return availableModels?.FirstOrDefault(model => !string.IsNullOrWhiteSpace(model))?.Trim();
    }

    private static bool IsPreferredRoute(string? candidate, string? preferredRoute) =>
        !string.IsNullOrWhiteSpace(candidate) &&
        !string.IsNullOrWhiteSpace(preferredRoute) &&
        !string.Equals(preferredRoute.Trim(), "auto", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(preferredRoute.Trim(), "gateway", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(candidate.Trim(), preferredRoute.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool IsNonLlmService(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return true;

        var lower = slug.Trim().ToLowerInvariant();
        return NonLlmServiceKeywords.Any(keyword => lower.Contains(keyword, StringComparison.Ordinal));
    }

    private string? ResolveNyxIdAuthorityBase()
    {
        var authority = _configuration["Cli:App:NyxId:Authority"]
            ?? _configuration["Aevatar:NyxId:Authority"]
            ?? _configuration["Aevatar:Authentication:Authority"];

        if (string.IsNullOrWhiteSpace(authority))
            return null;

        var trimmed = authority.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out _))
            return null;

        return trimmed.EndsWith(GatewaySuffix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^GatewaySuffix.Length]
            : trimmed;
    }

    private static LLMRequest BuildParticipantRequest(
        StreamingProxyNyxParticipantDefinition participant,
        IReadOnlyList<StreamingProxyNyxParticipantDefinition> allParticipants,
        string prompt,
        string sessionId,
        string accessToken,
        IReadOnlyList<(string Speaker, string Content)> transcript,
        int round,
        int totalRounds)
    {
        var others = string.Join(", ",
            allParticipants
                .Where(candidate => !string.Equals(candidate.RouteSlug, participant.RouteSlug, StringComparison.OrdinalIgnoreCase))
                .Select(candidate => candidate.DisplayName));

        var nextPreferredResponder = allParticipants
            .FirstOrDefault(candidate => !string.Equals(candidate.RouteSlug, participant.RouteSlug, StringComparison.OrdinalIgnoreCase))
            ?.DisplayName;

        var activeSpeakerNames = allParticipants
            .Select(candidate => candidate.DisplayName)
            .Where(displayName => !string.IsNullOrWhiteSpace(displayName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var promptTranscript = transcript
            .TakeLast(StreamingProxyDefaults.MaxTranscriptMessagesPerPrompt)
            .Where(entry => activeSpeakerNames.Contains(entry.Speaker))
            .ToList();

        var latestOtherTurn = promptTranscript
            .LastOrDefault(entry => !string.Equals(entry.Speaker, participant.DisplayName, StringComparison.OrdinalIgnoreCase));
        var hasLatestOtherTurn =
            !string.IsNullOrWhiteSpace(latestOtherTurn.Speaker) &&
            !string.IsNullOrWhiteSpace(latestOtherTurn.Content);

        var transcriptText = promptTranscript.Count == 0
            ? "暂无其他 participant 回复。"
            : string.Join("\n", promptTranscript.Select(entry => $"- {entry.Speaker}: {entry.Content}"));

        var turnDirective = hasLatestOtherTurn
            ? $"""
Your primary job in this turn is to reply to {latestOtherTurn.Speaker}'s latest point.
Treat this as an internal room discussion between participants. The human is observing, not filling in blanks right now.
Do not ask the human follow-up questions. Instead, make a reasonable assumption, react to {latestOtherTurn.Speaker}, and move the discussion forward.
Mention or clearly reference {latestOtherTurn.Speaker}'s point early in your reply so it feels like a real back-and-forth.
End your turn by pressing one concrete challenge, rebuttal, or counterexample back to the room, preferably aimed at {latestOtherTurn.Speaker}.
Only address participants who are explicitly listed as currently active in this room. If someone is unavailable, failed earlier, or is not listed, do not ask them anything and do not hand the turn to them.
"""
            : """
This is the opening turn for you in the room.
Treat this as an internal room discussion between participants. The human is observing, not filling in blanks right now.
Do not ask the human follow-up questions. Make reasonable assumptions from the topic and start the discussion with a concrete point of view.
State a clear stance immediately and end with one pointed challenge to another currently active participant so the debate can continue without waiting for the human.
Only address participants who are explicitly listed as currently active in this room. If someone is unavailable, failed earlier, or is not listed, do not ask them anything and do not hand the turn to them.
""";

        var turnContext = hasLatestOtherTurn
            ? $"""
Latest other-participant message you should respond to:
{latestOtherTurn.Speaker}: {latestOtherTurn.Content}
"""
            : "No other participant has spoken before your turn.";

        var systemPrompt = $"""
You are {participant.DisplayName}, one participant in a multi-agent discussion room.
Other participants in this room: {(string.IsNullOrWhiteSpace(others) ? "none" : others)}.
This is round {round} of {totalRounds}.
Write exactly one chat turn as {participant.DisplayName}.
You are speaking to the other participants in the room, not interviewing the user.
Speak only for yourself and never simulate, script, or continue another participant's reply.
Do not output speaker labels, role-play transcripts, markdown headings for speakers, or sections named after other participants.
Only treat these names as valid live participants: {(string.IsNullOrWhiteSpace(others) ? "none" : others)}.
Do not mention, ask, tag, or wait for any unavailable participant outside that list.
If another participant just made a point, engage with it directly instead of restarting from scratch.
Do not mention provider routing, service slugs, or proxy internals.
Keep the response concise, useful, and conversational so other participants can build on it.
Avoid repeating your earlier points unless you are refining or challenging them.
Use 2-3 short paragraphs or 2-4 bullet points at most.
{turnDirective}
""";

        var userPrompt = $"""
Original room topic from the human:
{prompt}

Conversation transcript so far:
{transcriptText}

{turnContext}

Add your next reply to move the participant discussion forward.
If you want to hand the turn to someone, only hand it to this currently active participant set: {(string.IsNullOrWhiteSpace(others) ? "none" : others)}{(string.IsNullOrWhiteSpace(nextPreferredResponder) ? string.Empty : $". A good default is {nextPreferredResponder}")}.
Return only {participant.DisplayName}'s reply text, with no prefixed name and no extra transcript formatting.
""";

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = accessToken,
            [LLMRequestMetadataKeys.NyxIdRoutePreference] = participant.RouteSlug,
        };

        if (!string.IsNullOrWhiteSpace(participant.Model))
            metadata[LLMRequestMetadataKeys.ModelOverride] = participant.Model;

        return new LLMRequest
        {
            RequestId = $"{sessionId}:{participant.RouteSlug}:round-{round}",
            Messages =
            [
                ChatMessage.System(systemPrompt),
                ChatMessage.User(userPrompt),
            ],
            Metadata = metadata,
            Model = participant.Model,
            Temperature = 0.7,
            MaxTokens = 220,
        };
    }

    private static string SanitizeFailureMessage(string? message) =>
        string.IsNullOrWhiteSpace(message) ? "unknown error" : message.Trim();

    private static string? NormalizeParticipantReply(
        StreamingProxyNyxParticipantDefinition participant,
        IReadOnlyList<StreamingProxyNyxParticipantDefinition> allParticipants,
        string? rawContent)
    {
        var trimmed = rawContent?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return null;

        var lines = trimmed
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .ToList();

        var ownLabels = BuildSpeakerLabels(participant.DisplayName);
        while (lines.Count > 0 && ownLabels.Contains(lines[0].Trim(), StringComparer.OrdinalIgnoreCase))
        {
            lines.RemoveAt(0);
        }

        var otherSpeakerLabels = allParticipants
            .Where(candidate => !string.Equals(candidate.RouteSlug, participant.RouteSlug, StringComparison.OrdinalIgnoreCase))
            .SelectMany(candidate => BuildSpeakerLabels(candidate.DisplayName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var cutoff = lines.FindIndex(line => otherSpeakerLabels.Contains(line.Trim()));
        if (cutoff >= 0)
        {
            lines = lines.Take(cutoff).ToList();
        }

        return string.Join('\n', lines).Trim();
    }

    private static IEnumerable<string> BuildSpeakerLabels(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            yield break;

        var trimmed = displayName.Trim();
        yield return trimmed;
        yield return $"{trimmed}:";
        yield return $"{trimmed}：";
        yield return $"# {trimmed}";
        yield return $"## {trimmed}";
        yield return $"### {trimmed}";
        yield return $"**{trimmed}**";
    }

    private static async Task DispatchAsync(IActor actor, IMessage payload, CancellationToken ct)
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(payload),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = actor.Id },
            },
        };

        await actor.HandleEventAsync(envelope, ct);
    }
}

internal sealed record StreamingProxyNyxParticipantDefinition(
    string RouteSlug,
    string DisplayName,
    string? Model);

internal sealed class StreamingProxyNyxLlmStatusResponse
{
    [JsonPropertyName("providers")]
    public List<StreamingProxyNyxProviderStatus>? Providers { get; set; }
}

internal sealed class StreamingProxyNyxProviderStatus
{
    [JsonPropertyName("provider_slug")]
    public string? ProviderSlug { get; set; }

    [JsonPropertyName("provider_name")]
    public string? ProviderName { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("proxy_url")]
    public string? ProxyUrl { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }
}

internal sealed class StreamingProxyNyxKeysResponse
{
    [JsonPropertyName("keys")]
    public List<StreamingProxyNyxUserService>? Keys { get; set; }
}

internal sealed class StreamingProxyNyxUserService
{
    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("endpoint_url")]
    public string? EndpointUrl { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }
}

internal sealed class StreamingProxyOpenAIModelsResponse
{
    [JsonPropertyName("data")]
    public List<StreamingProxyOpenAIModelEntry>? Data { get; set; }
}

internal sealed class StreamingProxyOpenAIModelEntry
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
}
