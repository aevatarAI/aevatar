using System.Net.Http.Headers;
using System.Text;
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
        CancellationToken ct,
        string? preferredRoute = null,
        string? defaultModel = null)
    {
        var participants = await ResolveParticipantsAsync(accessToken, preferredRoute, defaultModel, ct);
        if (participants.Count == 0)
            return participants;

        var existing = await participantStore.ListAsync(roomId, ct);
        var existingIds = existing
            .Select(entry => entry.AgentId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var participant in participants)
        {
            if (existingIds.Contains(participant.ParticipantId))
                continue;

            await DispatchAsync(actor, new GroupChatParticipantJoinedEvent
            {
                AgentId = participant.ParticipantId,
                DisplayName = participant.DisplayName,
            }, ct);

            await participantStore.AddAsync(roomId, participant.ParticipantId, participant.DisplayName, ct);
        }

        return participants;
    }

    public async Task GenerateRepliesAsync(
        IReadOnlyList<StreamingProxyNyxParticipantDefinition> participants,
        IActor actor,
        string prompt,
        string sessionId,
        string accessToken,
        CancellationToken ct,
        IStreamingProxyParticipantStore? participantStore = null,
        string? roomId = null)
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

                if (failedParticipants.Contains(participant.ParticipantId))
                    continue;

                var availableParticipants = activeParticipants
                    .Where(candidate => !failedParticipants.Contains(candidate.ParticipantId))
                    .ToList();

                if (availableParticipants.Count == 0)
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
                    var response = await ReadParticipantResponseAsync(provider, request, ct);
                    if (IsUnavailableResponse(response))
                    {
                        failedParticipants.Add(participant.ParticipantId);
                        await MarkParticipantLeftAsync(
                            actor,
                            participantStore,
                            roomId,
                            participant.ParticipantId,
                            ct);
                        _logger.LogWarning(
                            "Streaming Proxy participant '{Participant}' returned an unavailable response for route '{RoutePreference}' in round {Round}.",
                            participant.DisplayName,
                            participant.RoutePreference,
                            round);
                        continue;
                    }

                    var content = NormalizeParticipantReply(
                        participant,
                        availableParticipants,
                        response.Content);
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        failedParticipants.Add(participant.ParticipantId);
                        await MarkParticipantLeftAsync(
                            actor,
                            participantStore,
                            roomId,
                            participant.ParticipantId,
                            ct);
                        continue;
                    }

                    transcript.Add((participant.DisplayName, content));
                    successfulReplies++;
                    await DispatchAsync(actor, new GroupChatMessageEvent
                    {
                        AgentId = participant.ParticipantId,
                        AgentName = participant.DisplayName,
                        Content = content,
                        SessionId = $"{sessionId}:{participant.ParticipantId}:round-{round}",
                    }, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failedParticipants.Add(participant.ParticipantId);
                    await MarkParticipantLeftAsync(
                        actor,
                        participantStore,
                        roomId,
                        participant.ParticipantId,
                        ct);
                    _logger.LogWarning(ex,
                        "Streaming Proxy participant '{Participant}' failed for route '{RoutePreference}' in round {Round}.",
                        participant.DisplayName,
                        participant.RoutePreference,
                        round);
                }
            }

            if (failedParticipants.Count > 0)
            {
                activeParticipants = activeParticipants
                    .Where(participant => !failedParticipants.Contains(participant.ParticipantId))
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
        string? preferredRoute,
        string? defaultModel,
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

        var candidates = readyProviders
            .Select(provider =>
            {
                var routePreference = ResolveParticipantRoutePreference(provider, authorityBase);
                var participantId = ResolveParticipantIdentity(provider, routePreference);
                return new StreamingProxyNyxParticipantCandidate(provider, participantId, routePreference);
            })
            .ToList();

        var modelsByProvider = await FetchModelsFromProvidersAsync(authorityBase, accessToken, candidates, ct);
        var preferences = await preferencesTask;
        if (preferredRoute is not null || defaultModel is not null)
        {
            preferences = preferences with
            {
                PreferredRoute = preferredRoute is null ? preferences.PreferredRoute : preferredRoute.Trim(),
                DefaultModel = defaultModel is null ? preferences.DefaultModel : defaultModel.Trim(),
            };
        }

        var ordered = candidates
            .OrderByDescending(candidate => IsPreferredParticipant(candidate, preferences.PreferredRoute))
            .ThenBy(candidate => candidate.Provider.ProviderName ?? candidate.Provider.ProviderSlug ?? candidate.ParticipantId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.ParticipantId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var participants = new List<StreamingProxyNyxParticipantDefinition>(ordered.Count);
        var seenParticipantIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in ordered)
        {
            if (!seenParticipantIds.Add(candidate.ParticipantId))
                continue;

            modelsByProvider.TryGetValue(candidate.ParticipantId, out var availableModels);
            var model = ResolveParticipantModel(preferences.DefaultModel, availableModels);
            participants.Add(new StreamingProxyNyxParticipantDefinition(
                candidate.ParticipantId,
                candidate.RoutePreference,
                ResolveParticipantDisplayName(candidate.Provider, candidate.ParticipantId),
                model));
        }

        return EnsureDistinctDisplayNames(participants);
    }

    private async Task<List<StreamingProxyNyxProviderStatus>> FetchReadyProvidersAsync(
        string authorityBase,
        string accessToken,
        CancellationToken ct)
    {
        var providers = new List<StreamingProxyNyxProviderStatus>();

        var gatewayProvidersTask = FetchReadyGatewayProvidersAsync(authorityBase, accessToken, ct);
        var userServicesTask = FetchUserServicesAsync(authorityBase, accessToken, ct);

        try
        {
            var gatewayProviders = await gatewayProvidersTask;
            providers.AddRange(gatewayProviders.Where(provider => !string.IsNullOrWhiteSpace(provider.ProviderSlug)));
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

                providers.Add(new StreamingProxyNyxProviderStatus
                {
                    ProviderSlug = service.Slug,
                    ProviderName = service.Label,
                    Status = "ready",
                    ProxyUrl = $"{authorityBase}/api/v1/proxy/s/{Uri.EscapeDataString(service.Slug)}",
                    Source = "user_service",
                    Id = service.Id,
                    ServiceId = service.ServiceId,
                    NodeId = service.NodeId,
                    ApiKeyId = service.ApiKeyId,
                });
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

        return providers;
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
        IReadOnlyList<StreamingProxyNyxParticipantCandidate> readyProviders,
        CancellationToken ct)
    {
        var tasks = readyProviders
            .DistinctBy(candidate => candidate.ParticipantId, StringComparer.OrdinalIgnoreCase)
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Provider.ProxyUrl))
            .Select(async candidate =>
            {
                var participantId = candidate.ParticipantId;
                try
                {
                    var proxyUrl = candidate.Provider.ProxyUrl!.Trim().TrimEnd('/');
                    if (!proxyUrl.StartsWith("/", StringComparison.Ordinal) &&
                        !proxyUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        proxyUrl = "/" + proxyUrl;
                    }

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
                        return new KeyValuePair<string, List<string>>(participantId, models);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to fetch models for Streaming Proxy participant '{ParticipantId}'.", participantId);
                }

                var fallbackKey = candidate.Provider.ProviderSlug?.Trim() ?? string.Empty;
                var fallback = FallbackModels.TryGetValue(fallbackKey, out var fallbackModels)
                    ? fallbackModels.ToList()
                    : [];
                return new KeyValuePair<string, List<string>>(participantId, fallback);
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

    private static bool IsPreferredParticipant(
        StreamingProxyNyxParticipantCandidate candidate,
        string? preferredRoute)
    {
        if (string.IsNullOrWhiteSpace(preferredRoute))
            return false;

        return IsPreferredRoute(candidate.Provider.ProviderSlug, preferredRoute) ||
               IsPreferredRoute(candidate.Provider.ProviderName, preferredRoute) ||
               IsPreferredRoute(candidate.RoutePreference, preferredRoute) ||
               IsPreferredRoute(candidate.ParticipantId, preferredRoute);
    }

    private static string ResolveParticipantIdentity(
        StreamingProxyNyxProviderStatus provider,
        string routePreference)
    {
        var identityParts = new[]
        {
            provider.NodeId,
            TryGetAdditionalString(provider.AdditionalProperties, "node_id"),
            provider.ServiceId,
            TryGetAdditionalString(provider.AdditionalProperties, "service_id"),
            provider.ApiKeyId,
            TryGetAdditionalString(provider.AdditionalProperties, "api_key_id"),
            provider.Id,
            TryGetAdditionalString(provider.AdditionalProperties, "id"),
            routePreference,
            provider.ProviderSlug,
            provider.ProviderName
        }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .ToList();

        return identityParts.Count == 0
            ? "participant"
            : string.Join("|", identityParts);
    }

    private static string ResolveParticipantRoutePreference(
        StreamingProxyNyxProviderStatus provider,
        string authorityBase)
    {
        var route = NormalizeRoutePreference(provider.ProxyUrl, authorityBase);
        if (!string.IsNullOrWhiteSpace(route))
            return route!;

        var fallback = provider.ProviderSlug?.Trim();
        return string.IsNullOrWhiteSpace(fallback) ? "participant" : fallback;
    }

    private static string ResolveParticipantDisplayName(
        StreamingProxyNyxProviderStatus provider,
        string participantId)
    {
        var displayName = FirstNonEmpty(
            provider.ProviderName,
            provider.ProviderSlug,
            provider.Source);

        return string.IsNullOrWhiteSpace(displayName)
            ? participantId
            : displayName.Trim();
    }

    private static IReadOnlyList<StreamingProxyNyxParticipantDefinition> EnsureDistinctDisplayNames(
        IReadOnlyList<StreamingProxyNyxParticipantDefinition> participants)
    {
        if (participants.Count < 2)
            return participants;

        var normalized = participants
            .Select((participant, index) => new ParticipantDisplayNameEntry(
                participant,
                index,
                string.IsNullOrWhiteSpace(participant.DisplayName)
                    ? participant.ParticipantId
                    : participant.DisplayName.Trim()))
            .ToList();

        var result = participants.ToList();
        foreach (var group in normalized.GroupBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var entries = group.ToList();
            if (entries.Count < 2)
                continue;

            var ordered = entries
                .OrderBy(entry => entry.Participant.ParticipantId, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (var i = 0; i < ordered.Count; i++)
            {
                var entry = ordered[i];
                result[entry.Index] = entry.Participant with
                {
                    DisplayName = $"{entry.DisplayName} ({i + 1})",
                };
            }
        }

        return result;
    }

    private static string? NormalizeRoutePreference(string? value, string authorityBase)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        if (Uri.TryCreate(normalized, UriKind.Absolute, out var absolute))
        {
            if (Uri.TryCreate(authorityBase, UriKind.Absolute, out var authority) &&
                string.Equals(absolute.Scheme, authority.Scheme, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(absolute.Host, authority.Host, StringComparison.OrdinalIgnoreCase) &&
                absolute.Port == authority.Port)
            {
                return absolute.PathAndQuery.TrimEnd('/');
            }

            return absolute.PathAndQuery.TrimEnd('/');
        }

        if (normalized.StartsWith("//", StringComparison.Ordinal) ||
            normalized.Contains("://", StringComparison.Ordinal))
        {
            return null;
        }

        if (normalized.StartsWith("/", StringComparison.Ordinal))
            return normalized.TrimEnd('/');

        return normalized.Contains('/', StringComparison.Ordinal)
            ? "/" + normalized.TrimEnd('/')
            : normalized;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string? TryGetAdditionalString(
        Dictionary<string, JsonElement>? additionalProperties,
        string propertyName)
    {
        if (additionalProperties == null ||
            !additionalProperties.TryGetValue(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.ToString().Trim(),
        };
    }

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
                .Where(candidate => !string.Equals(candidate.ParticipantId, participant.ParticipantId, StringComparison.OrdinalIgnoreCase))
                .Select(candidate => candidate.DisplayName));

        var nextPreferredResponder = allParticipants
            .FirstOrDefault(candidate => !string.Equals(candidate.ParticipantId, participant.ParticipantId, StringComparison.OrdinalIgnoreCase))
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
            [LLMRequestMetadataKeys.NyxIdRoutePreference] = participant.RoutePreference,
        };

        if (!string.IsNullOrWhiteSpace(participant.Model))
            metadata[LLMRequestMetadataKeys.ModelOverride] = participant.Model;

        return new LLMRequest
        {
            RequestId = $"{sessionId}:{participant.ParticipantId}:round-{round}",
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

    private static async Task<LLMResponse> ReadParticipantResponseAsync(
        ILLMProvider provider,
        LLMRequest request,
        CancellationToken ct)
    {
        var content = new StringBuilder();
        List<ContentPart>? contentParts = null;
        TokenUsage? usage = null;
        string? finishReason = null;

        await foreach (var chunk in provider.ChatStreamAsync(request, ct).WithCancellation(ct))
        {
            if (!string.IsNullOrWhiteSpace(chunk.DeltaContent))
                content.Append(chunk.DeltaContent);

            if (chunk.DeltaContentPart != null)
            {
                if (chunk.DeltaContentPart.Kind == ContentPartKind.Text &&
                    !string.IsNullOrWhiteSpace(chunk.DeltaContentPart.Text))
                {
                    content.Append(chunk.DeltaContentPart.Text);
                }
                else
                {
                    contentParts ??= [];
                    contentParts.Add(chunk.DeltaContentPart);
                }
            }

            if (!string.IsNullOrWhiteSpace(chunk.FinishReason))
                finishReason = chunk.FinishReason;

            usage = chunk.Usage ?? usage;
        }

        return new LLMResponse
        {
            Content = content.Length > 0 ? content.ToString() : null,
            ContentParts = contentParts,
            FinishReason = finishReason,
            Usage = usage,
        };
    }

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
            .Where(candidate => !string.Equals(candidate.ParticipantId, participant.ParticipantId, StringComparison.OrdinalIgnoreCase))
            .SelectMany(candidate => BuildSpeakerLabels(candidate.DisplayName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var cutoff = lines.FindIndex(line => otherSpeakerLabels.Contains(line.Trim()));
        if (cutoff >= 0)
        {
            lines = lines.Take(cutoff).ToList();
        }

        return string.Join('\n', lines).Trim();
    }

    private static async Task MarkParticipantLeftAsync(
        IActor actor,
        IStreamingProxyParticipantStore? participantStore,
        string? roomId,
        string participantId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(participantId))
            return;

        if (participantStore is not null &&
            !string.IsNullOrWhiteSpace(roomId))
        {
            await participantStore.RemoveParticipantAsync(roomId, participantId, ct);
        }

        await DispatchAsync(actor, new GroupChatParticipantLeftEvent
        {
            AgentId = participantId,
        }, ct);
    }

    private static bool IsUnavailableResponse(LLMResponse response)
    {
        if (string.Equals(response.FinishReason, "error", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(response.FinishReason, "failed", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (LooksLikeUnavailableContent(response.Content))
            return true;

        return response.ContentParts is { Count: > 0 } &&
            response.ContentParts.Any(part =>
                part.Kind == ContentPartKind.Text &&
                LooksLikeUnavailableContent(part.Text));
    }

    private static bool LooksLikeUnavailableContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        var normalized = content.Trim();
        return normalized.StartsWith("当前暂时不可用", StringComparison.Ordinal) ||
               normalized.StartsWith("Service request failed", StringComparison.OrdinalIgnoreCase) ||
               (normalized.Contains("503", StringComparison.OrdinalIgnoreCase) &&
                normalized.Contains("Service Unavailable", StringComparison.OrdinalIgnoreCase)) ||
               normalized.Contains("HTTP 503", StringComparison.OrdinalIgnoreCase);
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
    string ParticipantId,
    string RoutePreference,
    string DisplayName,
    string? Model);

internal sealed record StreamingProxyNyxParticipantCandidate(
    StreamingProxyNyxProviderStatus Provider,
    string ParticipantId,
    string RoutePreference);

internal sealed record ParticipantDisplayNameEntry(
    StreamingProxyNyxParticipantDefinition Participant,
    int Index,
    string DisplayName);

internal sealed class StreamingProxyNyxLlmStatusResponse
{
    [JsonPropertyName("providers")]
    public List<StreamingProxyNyxProviderStatus>? Providers { get; set; }
}

internal sealed class StreamingProxyNyxProviderStatus
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("service_id")]
    public string? ServiceId { get; set; }

    [JsonPropertyName("api_key_id")]
    public string? ApiKeyId { get; set; }

    [JsonPropertyName("node_id")]
    public string? NodeId { get; set; }

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

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

internal sealed class StreamingProxyNyxKeysResponse
{
    [JsonPropertyName("keys")]
    public List<StreamingProxyNyxUserService>? Keys { get; set; }
}

internal sealed class StreamingProxyNyxUserService
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("service_id")]
    public string? ServiceId { get; set; }

    [JsonPropertyName("api_key_id")]
    public string? ApiKeyId { get; set; }

    [JsonPropertyName("node_id")]
    public string? NodeId { get; set; }

    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("endpoint_url")]
    public string? EndpointUrl { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
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
