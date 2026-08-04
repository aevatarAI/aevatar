using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.GAgents.StreamingProxy.Application.Rooms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.StreamingProxy;

// Refactor (iter56/cluster-894-nyx-coordinator-adapter-only): old=coordinator-owned facts, new=adapter-only + room-actor-owned facts
// Nyx-specific HTTP/status parsing and ChatStreamAsync reading stay outside the room actor turn.
// Room effects are submitted only through IStreamingProxyRoomCommandService typed requests.
// The adapter keeps no authority to create committed room facts.
internal sealed class StreamingProxyNyxParticipantCoordinator
{
    private const string NyxIdProviderName = "nyxid";
    private const string GatewaySuffix = "/api/v1/llm/gateway/v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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

    public Task<IReadOnlyList<StreamingProxyNyxParticipantDefinition>> ResolveParticipantsAsync(
        string scopeId,
        string roomId,
        string accessToken,
        CancellationToken ct,
        string? preferredRoute = null,
        string? defaultModel = null)
    {
        _ = scopeId;
        _ = roomId;
        return ResolveParticipantsAsync(accessToken, preferredRoute, defaultModel, ct);
    }

    public async Task<StreamingProxyParticipantReplyOutcome> GenerateReplyAsync(
        StreamingProxyChatParticipantReplyRequested request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_llmProviderFactory.GetAvailableProviders().Contains(NyxIdProviderName, StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogWarning("NyxID provider '{ProviderName}' is not registered; skip Streaming Proxy participants.", NyxIdProviderName);
            return StreamingProxyParticipantReplyOutcome.Failed(
                StreamingProxyChatParticipantReplyFailureKind.ProviderUnavailable,
                $"NyxID provider '{NyxIdProviderName}' is not registered.");
        }

        var provider = _llmProviderFactory.GetProvider(NyxIdProviderName);
        var participant = new StreamingProxyNyxParticipantDefinition(
            request.ParticipantId,
            request.RoutePreference,
            request.DisplayName,
            request.Model);
        var activeParticipants = request.ActiveParticipants
            .Select(candidate => new StreamingProxyNyxParticipantDefinition(
                candidate.ParticipantId,
                candidate.RoutePreference,
                candidate.DisplayName,
                string.IsNullOrWhiteSpace(candidate.Model) ? null : candidate.Model))
            .ToList();
        var transcript = request.Transcript
            .Select(entry => (entry.Speaker, entry.Content))
            .ToList();

        try
        {
            var llmRequest = BuildParticipantRequest(
                participant,
                activeParticipants,
                request.Prompt,
                request.SessionId,
                request.AccessToken,
                transcript,
                request.Round,
                request.MaxRounds);
            var response = await ReadParticipantResponseAsync(provider, llmRequest, ct);
            if (IsUnavailableResponse(response))
            {
                _logger.LogWarning(
                    "Streaming Proxy participant '{Participant}' returned an unavailable response for route '{RoutePreference}' in round {Round}.",
                    participant.DisplayName,
                    participant.RoutePreference,
                    request.Round);
                return StreamingProxyParticipantReplyOutcome.Failed(
                    StreamingProxyChatParticipantReplyFailureKind.ParticipantUnavailable,
                    "Nyx participant unavailable.");
            }

            var content = NormalizeParticipantReply(
                participant,
                activeParticipants,
                response.Content);
            return string.IsNullOrWhiteSpace(content)
                ? StreamingProxyParticipantReplyOutcome.Failed(
                    StreamingProxyChatParticipantReplyFailureKind.EmptyReply,
                    "Nyx participant returned an empty reply.")
                : StreamingProxyParticipantReplyOutcome.Succeeded(content);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Streaming Proxy participant '{Participant}' failed for route '{RoutePreference}' in round {Round}.",
                participant.DisplayName,
                participant.RoutePreference,
                request.Round);
            return StreamingProxyParticipantReplyOutcome.Failed(
                StreamingProxyChatParticipantReplyFailureKind.Error,
                ex.Message);
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

        var preferenceControl = (await preferencesTask).ApplyTo(LLMControlContext.Empty);
        if (preferredRoute is not null || defaultModel is not null)
        {
            preferenceControl = preferenceControl with
            {
                NyxIdRoutePreference = preferredRoute is null
                    ? preferenceControl.NyxIdRoutePreference
                    : preferredRoute.Trim(),
                ModelOverride = defaultModel is null
                    ? preferenceControl.ModelOverride
                    : defaultModel.Trim(),
            };
        }

        var ordered = candidates
            .OrderByDescending(candidate => IsPreferredParticipant(candidate, preferenceControl.NyxIdRoutePreference))
            .ThenBy(candidate => candidate.Provider.ProviderName ?? candidate.Provider.ProviderSlug ?? candidate.ParticipantId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.ParticipantId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var participants = new List<StreamingProxyNyxParticipantDefinition>(ordered.Count);
        var seenParticipantIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in ordered)
        {
            if (!seenParticipantIds.Add(candidate.ParticipantId))
                continue;

            var model = ResolveParticipantModel(preferenceControl.ModelOverride, ResolveProviderModels(candidate.Provider));
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
        using var response = await SendNyxIdLlmCatalogRequestAsync(authorityBase, accessToken, "/api/v1/llm/services", ct);
        if (!response.IsSuccessStatusCode &&
            response.StatusCode != HttpStatusCode.NotFound)
            return [];

        var body = response.StatusCode == HttpStatusCode.NotFound
            ? await FetchLegacyLlmStatusAsync(authorityBase, accessToken, ct)
            : await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(body))
            return [];

        return ParseLlmServices(body)
            .Select(NormalizeProviderStatus)
            .Where(provider =>
                provider.Allowed == true &&
                string.Equals(provider.Status, "ready", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(provider.ProviderSlug) &&
                !string.IsNullOrWhiteSpace(provider.ProxyUrl))
            .ToList();
    }

    private async Task<string?> FetchLegacyLlmStatusAsync(
        string authorityBase,
        string accessToken,
        CancellationToken ct)
    {
        using var response = await SendNyxIdLlmCatalogRequestAsync(authorityBase, accessToken, "/api/v1/llm/status", ct);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadAsStringAsync(ct)
            : null;
    }

    private async Task<HttpResponseMessage> SendNyxIdLlmCatalogRequestAsync(
        string authorityBase,
        string accessToken,
        string path,
        CancellationToken ct)
    {
        var statusUrl = $"{authorityBase}{path}";
        using var request = new HttpRequestMessage(HttpMethod.Get, statusUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await _httpClientFactory.CreateClient().SendAsync(request, ct).ConfigureAwait(false);
    }

    private static IReadOnlyList<StreamingProxyNyxProviderStatus> ParseLlmServices(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return [];

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
            return ParseLlmServicesArray(root);

        if (root.ValueKind != JsonValueKind.Object)
            return [];

        if (root.TryGetProperty("services", out var servicesElement) &&
            servicesElement.ValueKind == JsonValueKind.Array)
        {
            return ParseLlmServicesArray(servicesElement);
        }

        if (root.TryGetProperty("items", out var itemsElement) &&
            itemsElement.ValueKind == JsonValueKind.Array)
        {
            return ParseLlmServicesArray(itemsElement);
        }

        if (root.TryGetProperty("providers", out var providersElement) &&
            providersElement.ValueKind == JsonValueKind.Array)
        {
            return ParseLegacyLlmStatus(root, providersElement);
        }

        return [];
    }

    private static IReadOnlyList<StreamingProxyNyxProviderStatus> ParseLegacyLlmStatus(
        JsonElement root,
        JsonElement providersElement)
    {
        var supportedModels = ReadStringArray(root, "supported_models", "supportedModels");
        var modelsByProvider = ReadStringMapArray(root, "models_by_provider", "modelsByProvider");
        var providers = ParseLlmServicesArray(providersElement)
            .Select(NormalizeProviderStatus)
            .ToList();

        foreach (var provider in providers)
        {
            if (provider.Models is { Count: > 0 } ||
                string.IsNullOrWhiteSpace(provider.ProviderSlug))
            {
                continue;
            }

            provider.Models = modelsByProvider.TryGetValue(provider.ProviderSlug, out var providerModels) &&
                              providerModels.Count > 0
                ? providerModels.ToList()
                : supportedModels.ToList();
        }

        return providers;
    }

    private static IReadOnlyList<StreamingProxyNyxProviderStatus> ParseLlmServicesArray(JsonElement element)
    {
        var services = new List<StreamingProxyNyxProviderStatus>();
        foreach (var item in element.EnumerateArray())
        {
            var service = JsonSerializer.Deserialize<StreamingProxyNyxProviderStatus>(
                item.GetRawText(),
                JsonOptions);
            if (service is not null)
                services.Add(service);
        }

        return services;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property) ||
                property.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            return property
                .EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()?.Trim())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return [];
    }

    private static Dictionary<string, IReadOnlyList<string>> ReadStringMapArray(
        JsonElement element,
        params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property) ||
                property.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in property.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Array)
                    continue;

                result[entry.Name] = entry.Value
                    .EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()?.Trim())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            return result;
        }

        return new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<NyxIdUserLlmPreferences> GetPreferencesAsync(CancellationToken ct)
    {
        if (_preferencesStore == null)
            return NyxIdUserLlmPreferences.Empty;

        try
        {
            return await _preferencesStore.GetOwnerAsync(ct);
        }
        catch
        {
            return NyxIdUserLlmPreferences.Empty;
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
            provider.UserServiceId,
            TryGetAdditionalString(provider.AdditionalProperties, "user_service_id"),
            TryGetAdditionalString(provider.AdditionalProperties, "userServiceId"),
            provider.NodeId,
            TryGetAdditionalString(provider.AdditionalProperties, "node_id"),
            TryGetAdditionalString(provider.AdditionalProperties, "nodeId"),
            provider.ServiceId,
            TryGetAdditionalString(provider.AdditionalProperties, "service_id"),
            TryGetAdditionalString(provider.AdditionalProperties, "serviceId"),
            provider.ApiKeyId,
            TryGetAdditionalString(provider.AdditionalProperties, "api_key_id"),
            TryGetAdditionalString(provider.AdditionalProperties, "apiKeyId"),
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
            provider.DisplayName,
            provider.ProviderName,
            provider.ServiceName,
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

    private static StreamingProxyNyxProviderStatus NormalizeProviderStatus(
        StreamingProxyNyxProviderStatus provider)
    {
        provider.ProviderSlug = FirstNonEmpty(
            provider.ServiceSlug,
            provider.ProviderSlug,
            TryGetAdditionalString(provider.AdditionalProperties, "serviceSlug"),
            TryGetAdditionalString(provider.AdditionalProperties, "slug"));
        provider.ProviderName = FirstNonEmpty(
            provider.DisplayName,
            provider.ProviderName,
            provider.ServiceName,
            TryGetAdditionalString(provider.AdditionalProperties, "displayName"),
            TryGetAdditionalString(provider.AdditionalProperties, "serviceName"),
            TryGetAdditionalString(provider.AdditionalProperties, "label"));
        provider.ProxyUrl = FirstNonEmpty(
            provider.RouteValue,
            provider.ProxyUrl,
            TryGetAdditionalString(provider.AdditionalProperties, "routeValue"),
            TryGetAdditionalString(provider.AdditionalProperties, "proxyUrl"));
        provider.Id = FirstNonEmpty(
            provider.UserServiceId,
            provider.Id,
            TryGetAdditionalString(provider.AdditionalProperties, "userServiceId"));
        provider.Allowed ??= TryGetAdditionalBool(provider.AdditionalProperties, "allowed");
        provider.Allowed ??= string.Equals(provider.Status, "ready", StringComparison.OrdinalIgnoreCase);
        return provider;
    }

    private static IReadOnlyList<string> ResolveProviderModels(StreamingProxyNyxProviderStatus provider)
    {
        var models = provider.Models ?? provider.AvailableModels ?? [];
        return models
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Select(model => model.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
            .ToList();
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

    private static bool? TryGetAdditionalBool(
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
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
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

        var llmControl = new LLMControlContext(
            Normalize(accessToken),
            NyxIdOrgToken: null,
            SenderNyxIdAccessToken: null,
            Normalize(participant.Model),
            Normalize(participant.RoutePreference),
            MaxToolRoundsOverride: null,
            UserMemoryPrompt: null);
        return new LLMRequest
        {
            RequestId = $"{sessionId}:{participant.ParticipantId}:round-{round}",
            Messages =
            [
                ChatMessage.System(systemPrompt),
                ChatMessage.User(userPrompt),
            ],
            Metadata = null,
            CallerContext = new LLMRequestCallerContext(
                ScopeId: string.Empty,
                OwnerSubject: participant.ParticipantId,
                ResponseId: sessionId,
                Credentials: new LLMRequestCallerCredentials(Normalize(accessToken))),
            LlmControl = llmControl,
            RoutingContext = llmControl.ToRoutingContext(),
            Model = participant.Model,
            Temperature = 0.7,
            MaxTokens = 220,
        };
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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

}

internal sealed record StreamingProxyNyxParticipantDefinition(
    string ParticipantId,
    string RoutePreference,
    string DisplayName,
    string? Model);

internal sealed record StreamingProxyParticipantReplyOutcome(
    bool IsSuccess,
    string? Content,
    StreamingProxyChatParticipantReplyFailureKind FailureKind,
    string? ErrorMessage)
{
    public static StreamingProxyParticipantReplyOutcome Succeeded(string content) =>
        new(true, content, StreamingProxyChatParticipantReplyFailureKind.Unspecified, null);

    public static StreamingProxyParticipantReplyOutcome Failed(
        StreamingProxyChatParticipantReplyFailureKind failureKind,
        string? errorMessage) =>
        new(false, null, failureKind, errorMessage);
}

internal sealed record StreamingProxyNyxParticipantCandidate(
    StreamingProxyNyxProviderStatus Provider,
    string ParticipantId,
    string RoutePreference);

internal sealed record ParticipantDisplayNameEntry(
    StreamingProxyNyxParticipantDefinition Participant,
    int Index,
    string DisplayName);

internal sealed class StreamingProxyNyxProviderStatus
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("user_service_id")]
    public string? UserServiceId { get; set; }

    [JsonPropertyName("service_id")]
    public string? ServiceId { get; set; }

    [JsonPropertyName("api_key_id")]
    public string? ApiKeyId { get; set; }

    [JsonPropertyName("node_id")]
    public string? NodeId { get; set; }

    [JsonPropertyName("service_slug")]
    public string? ServiceSlug { get; set; }

    [JsonPropertyName("provider_slug")]
    public string? ProviderSlug { get; set; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("service_name")]
    public string? ServiceName { get; set; }

    [JsonPropertyName("provider_name")]
    public string? ProviderName { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("route_value")]
    public string? RouteValue { get; set; }

    [JsonPropertyName("proxy_url")]
    public string? ProxyUrl { get; set; }

    [JsonPropertyName("models")]
    public List<string>? Models { get; set; }

    [JsonPropertyName("available_models")]
    public List<string>? AvailableModels { get; set; }

    [JsonPropertyName("allowed")]
    public bool? Allowed { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
