using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.Tools;

namespace Aevatar.AI.ToolProviders.Web.Tools;

/// <summary>
/// Presents one structured choice or free-text question to the user.
/// The result is delivered via the AGUI protocol to the frontend for rendering.
/// </summary>
public sealed class AskUserTool : IAgentTool
{
    public string Name => "ask_user";

    public string Description =>
        "Ask the user one structured choice or free-text question. " +
        "Use this when you need to clarify requirements, get user preferences, " +
        "or let the user choose between approaches. " +
        "Returns either free text or the user's selected option(s). " +
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
              "oneOf": [
                { "maxItems": 0 },
                { "minItems": 2, "maxItems": 6 }
              ],
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
              "description": "Omit or use zero options only when allow_free_text is true; otherwise provide 2-6 options for a choice question"
            },
            "allow_free_text": {
              "type": "boolean",
              "description": "Allow a free-text answer. Must be true when options are omitted or empty (default: false)"
            },
            "multi_select": {
              "type": "boolean",
              "description": "Whether the user can select multiple options. Requires 2-6 options (default: false)"
            },
            "context": {
              "type": "string",
              "description": "Optional context to help the user understand why you're asking"
            }
          },
          "required": ["question"]
        }
        """;

    public ToolPresentationDescriptor Presentation =>
        ToolPresentationDescriptors.BuiltIn(Name, "Ask user", Description);

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
            var allowFreeText = doc.RootElement.TryGetProperty("allow_free_text", out var aft)
                && aft.ValueKind == JsonValueKind.True;

            var result = new
            {
                type = "ask_user",
                question,
                options = options?.EnumerateArray().Select(e => new
                {
                    label = e.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "",
                    description = e.TryGetProperty("description", out var d) ? d.GetString() : null,
                }).ToArray() ?? Array.Empty<object>(),
                allow_free_text = allowFreeText,
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
                allow_free_text = false,
                multi_select = false,
                status = "awaiting_user_response",
            }));
        }
    }
}
