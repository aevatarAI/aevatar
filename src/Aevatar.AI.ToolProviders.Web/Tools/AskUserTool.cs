using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.Web.Tools;

/// <summary>
/// Presents structured questions to the user with predefined options.
/// The result is delivered via the AGUI protocol to the frontend for rendering.
/// </summary>
public sealed class AskUserTool : IAgentTool
{
    public string Name => "ask_user";

    public string Description =>
        "Ask the user a structured question with predefined options. " +
        "Use this when you need to clarify requirements, get user preferences, " +
        "or let the user choose between approaches. " +
        "Returns the user's selected option(s). " +
        "Prefer this over asking in free text when the choices are clear.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "question": {
              "type": "string",
              "description": "The question to ask the user. Should be clear and specific."
            },
            "options": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "label": {
                    "type": "string",
                    "description": "Short display text for this option (1-5 words)"
                  },
                  "description": {
                    "type": "string",
                    "description": "Explanation of what this option means"
                  }
                },
                "required": ["label"]
              },
              "description": "2-6 options for the user to choose from"
            },
            "multi_select": {
              "type": "boolean",
              "description": "Whether the user can select multiple options (default: false)"
            },
            "context": {
              "type": "string",
              "description": "Optional context to help the user understand why you're asking"
            }
          },
          "required": ["question", "options"]
        }
        """;

    public bool IsReadOnly => true;

    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var args = ToolArgs.Parse(argumentsJson);
        var question = args.Str("question");
        if (string.IsNullOrWhiteSpace(question))
            return Task.FromResult("""{"error":"'question' is required"}""");

        // Return structured question for the AGUI protocol layer to intercept,
        // render the question UI, collect the answer, and inject it back.
        try
        {
            using var doc = JsonDocument.Parse(args.Raw);
            var options = doc.RootElement.TryGetProperty("options", out var optionsEl)
                && optionsEl.ValueKind == JsonValueKind.Array
                ? optionsEl.Clone()
                : (JsonElement?)null;

            var multiSelect = doc.RootElement.TryGetProperty("multi_select", out var ms)
                && ms.ValueKind == JsonValueKind.True;

            var result = new
            {
                type = "ask_user",
                question,
                options = options?.EnumerateArray().Select(e => new
                {
                    label = e.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "",
                    description = e.TryGetProperty("description", out var d) ? d.GetString() : null,
                }).ToArray() ?? Array.Empty<object>(),
                multi_select = multiSelect,
                context = args.Str("context"),
                status = "awaiting_user_response",
            };

            return Task.FromResult(JsonSerializer.Serialize(result,
                new JsonSerializerOptions { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
        }
        catch
        {
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                type = "ask_user",
                question,
                options = Array.Empty<object>(),
                status = "awaiting_user_response",
            }));
        }
    }
}
