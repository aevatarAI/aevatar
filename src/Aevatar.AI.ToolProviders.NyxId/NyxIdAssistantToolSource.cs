using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using Aevatar.Foundation.Abstractions.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId;

/// <summary>
/// Pinned NyxID Assistant surface. Management mutations and secret-bearing
/// argument schemas stay browser-owned and are never exposed to the model.
/// </summary>
public sealed class NyxIdAssistantToolSource : IAgentToolSource
{
    private readonly NyxIdToolOptions _options;
    private readonly NyxIdApiClient _client;
    private readonly INyxIdProxyFileArtifactIngress? _fileArtifactIngress;
    private readonly ILogger _logger;

    public NyxIdAssistantToolSource(
        NyxIdToolOptions options,
        NyxIdApiClient client,
        INyxIdProxyFileArtifactIngress? fileArtifactIngress = null,
        ILogger<NyxIdAssistantToolSource>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _fileArtifactIngress = fileArtifactIngress;
        _logger = logger ?? NullLogger<NyxIdAssistantToolSource>.Instance;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            return Task.FromResult<IReadOnlyList<IAgentTool>>([]);

        IReadOnlyList<IAgentTool> tools =
        [
            new NyxIdAccountTool(_client),
            new NyxIdStatusTool(_client),
            new NyxIdSessionsTool(_client),
            new NyxIdCatalogTool(_client),
            new NyxIdLlmStatusTool(_client),
            new NyxIdRequireServiceTool(_client),
            new NyxIdProxyTool(
                _client,
                _logger,
                _fileArtifactIngress,
                _options.EffectiveProxyFileArtifactMaxBytes,
                _options.ManagedWorkflowAdmissionMode),
            ReadOnly(new NyxIdProfileTool(_client), ["consents"], "consents"),
            ReadOnly(new NyxIdMfaTool(_client), ["status"], "status"),
            ReadOnly(new NyxIdServicesTool(_client), ["list", "show"], "list", "id"),
            ReadOnly(new NyxIdApiKeysTool(_client), ["list", "show"], "list", "id", "org"),
            ReadOnly(new NyxIdNodesTool(_client), ["list", "show"], "list", "id"),
            ReadOnly(
                new NyxIdApprovalsTool(_client),
                ["list", "show", "configs", "grants"],
                "list",
                "id"),
            ReadOnly(new NyxIdEndpointsTool(_client), ["list"], "list"),
            ReadOnly(new NyxIdExternalKeysTool(_client), ["list"], "list"),
            ReadOnly(new NyxIdNotificationsTool(_client), ["settings"], "settings"),
            ReadOnly(new NyxIdProvidersTool(_client), ["list"], "list"),
            ReadOnly(
                new NyxIdOrgTool(_client),
                ["list", "show", "list_members", "list_invites"],
                "list",
                "org_id"),
        ];

        return Task.FromResult(tools);
    }

    private static IAgentTool ReadOnly(
        IAgentTool inner,
        IReadOnlyCollection<string> allowedActions,
        string defaultAction,
        params string[] allowedParameters) =>
        new NyxIdAssistantReadOnlyActionTool(
            inner,
            allowedActions,
            defaultAction,
            allowedParameters);
}

internal sealed class NyxIdAssistantReadOnlyActionTool : IAgentTool
{
    private const string RejectedJson =
        "{\"error\":\"This NyxID management action is not callable from the assistant. Use the NyxID browser action instead.\"}";

    private readonly IAgentTool _inner;
    private readonly HashSet<string> _allowedActions;
    private readonly HashSet<string> _allowedProperties;
    private readonly string _defaultAction;

    public NyxIdAssistantReadOnlyActionTool(
        IAgentTool inner,
        IReadOnlyCollection<string> allowedActions,
        string defaultAction,
        IReadOnlyCollection<string> allowedParameters)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentNullException.ThrowIfNull(allowedActions);
        ArgumentNullException.ThrowIfNull(allowedParameters);
        _allowedActions = allowedActions
            .Where(static action => !string.IsNullOrWhiteSpace(action))
            .Select(static action => action.Trim())
            .ToHashSet(StringComparer.Ordinal);
        _defaultAction = defaultAction?.Trim() ?? string.Empty;
        if (_allowedActions.Count == 0 || !_allowedActions.Contains(_defaultAction))
            throw new ArgumentException("The default action must belong to the pinned allowlist.", nameof(defaultAction));
        _allowedProperties = allowedParameters
            .Where(static parameter => !string.IsNullOrWhiteSpace(parameter))
            .Select(static parameter => parameter.Trim())
            .Append("action")
            .ToHashSet(StringComparer.Ordinal);
        ParametersSchema = BuildSchema(_allowedActions, _defaultAction, _allowedProperties);
    }

    public string Name => _inner.Name;

    public string Description =>
        $"Read NyxID state. Allowed actions: {string.Join(", ", _allowedActions.OrderBy(static action => action, StringComparer.Ordinal))}.";

    public string ParametersSchema { get; }

    public ToolPresentationDescriptor Presentation => _inner.Presentation;

    public ToolPresentationDescriptor ResolvePresentation(string argumentsJson) =>
        _inner.ResolvePresentation(argumentsJson);

    public ToolApprovalMode ApprovalMode => ToolApprovalMode.NeverRequire;

    public bool IsReadOnly => true;

    public bool IsDestructive => false;

    public AgentToolCallSafety GetCallSafety(string argumentsJson) => new(false, true, false);

    public AgentToolReplayPolicy ResolveReplayPolicy(string argumentsJson) =>
        AgentToolReplayPolicy.ReadOnlyRetryable;

    public AgentToolReceipt? CreateResultReceipt(
        string callId,
        string toolName,
        string argumentsJson,
        string resultJson) =>
        IsAllowed(argumentsJson)
            ? _inner.CreateResultReceipt(callId, toolName, argumentsJson, resultJson)
            : null;

    public async Task<AgentToolTerminalOutcome> ExecuteWithOutcomeAsync(
        string callId,
        string toolName,
        string argumentsJson,
        CancellationToken ct = default) =>
        IsAllowed(argumentsJson)
            ? await _inner.ExecuteWithOutcomeAsync(callId, toolName, argumentsJson, ct)
                .ConfigureAwait(false)
            : new AgentToolTerminalOutcome(RejectedJson);

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
        IsAllowed(argumentsJson)
            ? await _inner.ExecuteAsync(argumentsJson, ct).ConfigureAwait(false)
            : RejectedJson;

    private bool IsAllowed(string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!_allowedProperties.Contains(property.Name))
                    return false;
            }

            var action = document.RootElement.TryGetProperty("action", out var actionElement)
                ? actionElement.GetString()?.Trim()
                : _defaultAction;
            return action is not null && _allowedActions.Contains(action);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string BuildSchema(
        IReadOnlyCollection<string> allowedActions,
        string defaultAction,
        IReadOnlyCollection<string> allowedProperties)
    {
        var properties = new JsonObject
        {
            ["action"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(allowedActions
                    .OrderBy(static action => action, StringComparer.Ordinal)
                    .Select(static action => (JsonNode?)JsonValue.Create(action))
                    .ToArray()),
                ["default"] = defaultAction,
            },
        };
        foreach (var property in allowedProperties
                     .Where(static property => property != "action")
                     .OrderBy(static property => property, StringComparer.Ordinal))
        {
            properties[property] = new JsonObject { ["type"] = "string" };
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
        }.ToJsonString();
    }
}
