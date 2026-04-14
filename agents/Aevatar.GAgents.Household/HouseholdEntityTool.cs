using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Household;

/// <summary>
/// Agent-as-Tool: exposes HouseholdEntity actor as an IAgentTool.
/// NyxIdChatGAgent's LLM decides when to call this tool for home automation.
/// </summary>
public sealed class HouseholdEntityTool : IAgentTool
{
    private readonly IActorRuntime _runtime;
    private readonly HouseholdEntityToolOptions _options;
    private readonly ILogger _logger;

    public HouseholdEntityTool(
        IActorRuntime runtime,
        HouseholdEntityToolOptions options,
        ILogger logger)
    {
        _runtime = runtime;
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
        // 1. Extract metadata from request context
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        var scopeId = AgentToolRequestContext.TryGet("scope_id");
        var metadata = AgentToolRequestContext.CurrentMetadata;

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

        // 4. Get or create HouseholdEntity actor
        try
        {
            var actor = await _runtime.GetAsync(actorId)
                        ?? await _runtime.CreateAsync<HouseholdEntity>(actorId, ct);

            // 5. Build and dispatch HouseholdChatEvent
            var chatEvent = new HouseholdChatEvent { Prompt = message };
            if (metadata != null)
            {
                foreach (var kv in metadata)
                    chatEvent.Metadata[kv.Key] = kv.Value;
            }

            var envelope = new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Payload = Any.Pack(chatEvent),
                Route = new EnvelopeRoute
                {
                    Direct = new DirectRoute { TargetActorId = actor.Id },
                },
            };

            await actor.HandleEventAsync(envelope, ct);

            // 6. Read result from actor state
            var state = ((IAgent<HouseholdEntityState>)actor.Agent).State;
            var lastAction = state.RecentActions.Count > 0
                ? state.RecentActions[^1]
                : null;

            var result = new
            {
                status = "ok",
                actor_id = actorId,
                mode = state.CurrentMode ?? "active",
                reasoning_count_today = state.ReasoningCountToday,
                last_reasoning_ts = state.LastReasoningTs,
                environment = state.Environment != null
                    ? new
                    {
                        temperature = state.Environment.Temperature,
                        humidity = state.Environment.Humidity,
                        light_level = state.Environment.LightLevel,
                        motion_detected = state.Environment.MotionDetected,
                        scene_description = state.Environment.SceneDescription,
                        time_of_day = state.Environment.TimeOfDay,
                    }
                    : null,
                last_action = lastAction != null
                    ? new
                    {
                        agent = lastAction.Agent,
                        action = lastAction.Action,
                        detail = lastAction.Detail,
                        reasoning = lastAction.Reasoning,
                    }
                    : null,
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
