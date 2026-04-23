using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.AI.ToolProviders.Channel;

/// <summary>
/// LLM-callable tool that emits a platform-neutral interactive reply (text + actions + cards)
/// and stages it into the active turn's <see cref="IInteractiveReplyCollector"/>.
/// </summary>
/// <remarks>
/// The tool produces no network I/O; it only expresses intent. The relay finalize path later
/// asks the collector for the captured intent and dispatches it through the configured
/// <see cref="IInteractiveReplyCollector"/> + dispatcher pair. When the tool fires outside an
/// active collector scope (for example, non-relay paths) the intent is silently discarded.
/// </remarks>
public sealed class ReplyWithInteractionTool : IAgentTool
{
    private static readonly JsonSerializerOptions ParseOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IInteractiveReplyCollector _collector;

    public ReplyWithInteractionTool(IInteractiveReplyCollector collector)
    {
        _collector = collector ?? throw new ArgumentNullException(nameof(collector));
    }

    public string Name => "reply_with_interaction";

    public string Description =>
        "Reply to the current user turn with an interactive message (title, body, buttons, cards). " +
        "Use this when the user would benefit from a rich card with selectable actions. " +
        "Returns a short confirmation; the reply is delivered when the turn completes. " +
        "If the channel does not support cards the runtime automatically degrades to plain text.";

    public string ParametersSchema => ParametersSchemaJson;

    public bool IsReadOnly => false;

    public bool IsDestructive => false;

    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        ReplyWithInteractionArguments? arguments;
        try
        {
            arguments = string.IsNullOrWhiteSpace(argumentsJson)
                ? new ReplyWithInteractionArguments()
                : JsonSerializer.Deserialize<ReplyWithInteractionArguments>(argumentsJson, ParseOptions);
        }
        catch (JsonException ex)
        {
            return Task.FromResult($"{{\"error\":\"invalid_arguments\",\"detail\":{JsonSerializer.Serialize(ex.Message)}}}");
        }

        arguments ??= new ReplyWithInteractionArguments();
        var intent = ReplyWithInteractionIntentMapper.ToMessageContent(arguments);
        if (string.IsNullOrWhiteSpace(intent.Text) && intent.Actions.Count == 0 && intent.Cards.Count == 0)
            return Task.FromResult("""{"error":"empty_interaction"}""");

        if (!_collector.Capture(intent))
        {
            // Tool was invoked outside an active collector scope (for example, a non-relay
            // chat turn). Surface this so the LLM can retry with a plain-text reply instead
            // of silently losing the response.
            return Task.FromResult(
                """{"error":"no_active_interactive_scope","detail":"reply_with_interaction is only available on channel relay turns. Reply with plain text instead."}""");
        }

        return Task.FromResult("""{"status":"queued"}""");
    }

    private const string ParametersSchemaJson = """
        {
          "type": "object",
          "properties": {
            "title": {
              "type": "string",
              "description": "Optional card header title."
            },
            "body": {
              "type": "string",
              "description": "Optional card body text or plain-text fallback."
            },
            "actions": {
              "type": "array",
              "description": "Optional top-level interactive actions (buttons).",
              "items": {
                "type": "object",
                "properties": {
                  "action_id": { "type": "string" },
                  "label": { "type": "string" },
                  "value": { "type": "string" },
                  "style": {
                    "type": "string",
                    "enum": ["default", "primary", "danger"]
                  }
                },
                "required": ["action_id", "label"]
              }
            },
            "fields": {
              "type": "array",
              "description": "Optional top-level title/text pairs rendered as card fields.",
              "items": {
                "type": "object",
                "properties": {
                  "title": { "type": "string" },
                  "text": { "type": "string" }
                }
              }
            },
            "cards": {
              "type": "array",
              "description": "Optional structured card blocks.",
              "items": {
                "type": "object",
                "properties": {
                  "title": { "type": "string" },
                  "text": { "type": "string" },
                  "fields": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "properties": {
                        "title": { "type": "string" },
                        "text": { "type": "string" }
                      }
                    }
                  },
                  "actions": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "properties": {
                        "action_id": { "type": "string" },
                        "label": { "type": "string" },
                        "value": { "type": "string" },
                        "style": {
                          "type": "string",
                          "enum": ["default", "primary", "danger"]
                        }
                      },
                      "required": ["action_id", "label"]
                    }
                  }
                }
              }
            }
          }
        }
        """;
}

/// <summary>Parameter shape consumed by <see cref="ReplyWithInteractionTool"/>.</summary>
public sealed class ReplyWithInteractionArguments
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("actions")]
    public List<ReplyActionArgument>? Actions { get; set; }

    [JsonPropertyName("fields")]
    public List<ReplyFieldArgument>? Fields { get; set; }

    [JsonPropertyName("cards")]
    public List<ReplyCardArgument>? Cards { get; set; }
}

/// <summary>Card-level action parameter.</summary>
public sealed class ReplyActionArgument
{
    [JsonPropertyName("action_id")]
    public string? ActionId { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }

    [JsonPropertyName("style")]
    public string? Style { get; set; }
}

/// <summary>Card field parameter (title/text pair).</summary>
public sealed class ReplyFieldArgument
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

/// <summary>Card block parameter.</summary>
public sealed class ReplyCardArgument
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("fields")]
    public List<ReplyFieldArgument>? Fields { get; set; }

    [JsonPropertyName("actions")]
    public List<ReplyActionArgument>? Actions { get; set; }
}
