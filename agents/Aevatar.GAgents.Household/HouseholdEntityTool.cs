using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Household;

/// <summary>
/// Agent-as-Tool: exposes HouseholdEntity actor as an IAgentTool.
/// NyxIdChatGAgent's LLM decides when to call this tool for home automation.
/// </summary>
public sealed class HouseholdEntityTool : IAgentTool
{
    private const string PublisherActorId = "household.tool";
    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly HouseholdEntityToolOptions _options;
    private readonly ILogger _logger;

    public HouseholdEntityTool(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        HouseholdEntityToolOptions options,
        ILogger logger)
    {
        _runtime = runtime;
        _dispatchPort = dispatchPort;
        _options = options;
        _logger = logger;
    }

    public string Name => "household";

    public string Description =>
        "Interact with the household AI agent for home automation. " +
        "Use for: controlling lights, playing music, moving robots, speaking via TTS, " +
        "or asking about the home environment (temperature, humidity, light, motion, camera scene). " +
        "The household agent perceives the environment and autonomously decides whether to act.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "message": {
              "type": "string",
              "description": "Message or instruction for the household agent (e.g., 'turn on warm lights in the living room', 'what's the current temperature?')"
            },
            "household_id": {
              "type": "string",
              "description": "Household actor ID. Omit to use the default household for the current scope."
            }
          },
          "required": ["message"]
        }
        """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        // 1. Extract typed request context
        var token = AgentToolRequestContext.NyxIdAccessToken;
        var scopeId = AgentToolRequestContext.ScopeId;

        // 2. Parse arguments
        string? message;
        string? householdId;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
            householdId = root.TryGetProperty("household_id", out var h) ? h.GetString() : null;
        }
        catch (JsonException)
        {
            return """{"error":"Failed to parse tool arguments"}""";
        }

        if (string.IsNullOrWhiteSpace(message))
            return """{"error":"'message' is required"}""";

        // 3. Resolve actor ID
        var actorId = !string.IsNullOrWhiteSpace(householdId)
            ? householdId
            : $"{_options.ActorIdPrefix}-{scopeId ?? "default"}";

        _logger.LogInformation("[household-tool] Dispatching to actor={ActorId}, message={Message}",
            actorId, message.Length > 100 ? message[..100] + "..." : message);

        // Refactor (iter2/cluster-007):
        //   Old pattern: the tool directly invoked the actor and read its live state.
        //   New principle: the tool only ensures lifecycle and dispatches an accepted command envelope.
        try
        {
            var actor = await _runtime.GetAsync(actorId)
                        ?? await _runtime.CreateAsync<HouseholdEntity>(actorId, ct);

            var chatEvent = new HouseholdChatEvent { Prompt = message };
            var externalMetadata = AgentToolRequestContext.Current?.ExternalMetadata;
            if (externalMetadata != null)
            {
                foreach (var kv in externalMetadata)
                    chatEvent.Metadata[kv.Key] = kv.Value;
            }

            var messageId = Guid.NewGuid().ToString("N");
            var envelope = new EventEnvelope
            {
                Id = messageId,
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Payload = Any.Pack(chatEvent),
                Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, actor.Id),
            };

            await _dispatchPort.DispatchAsync(actor.Id, envelope, ct);

            var result = new
            {
                status = "accepted",
                actor_id = actorId,
                message_id = messageId,
                propagation = "accepted_for_dispatch; observe household read model or event stream for committed state",
            };

            return JsonSerializer.Serialize(result,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[household-tool] Failed to dispatch to actor={ActorId}", actorId);
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }
}
