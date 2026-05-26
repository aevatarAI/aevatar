using System.Text.Json;
using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Authoring.Lark;

/// <summary>
/// Shared <see cref="JsonElement"/> reading helpers for the agent-builder formatters.
/// </summary>
/// <remarks>
/// These were previously copy-pasted across <see cref="AgentBuilderCardContent"/>,
/// <see cref="AgentBuilderCardFlow"/>, and <see cref="NyxRelayAgentBuilderFlow"/>; a fix in one
/// copy needed manual replication everywhere or behavior would silently diverge across the typed
/// and card-action surfaces. The helpers are intentionally narrow — only the json-shape concerns
/// every formatter shares — so this file does not become a junk drawer.
/// </remarks>
internal static class AgentBuilderJson
{
    /// <summary>Builds a plain-text <see cref="MessageContent"/> reply.</summary>
    public static MessageContent TextContent(string text) => new() { Text = text };

    /// <summary>
    /// Reads the canonical <c>error</c> field. Returns <c>true</c> when the element carries a
    /// non-empty error string and emits its value via <paramref name="error"/>.
    /// </summary>
    public static bool TryReadError(JsonElement root, out string error)
    {
        error = TryReadString(root, "error") ?? string.Empty;
        return error.Length > 0;
    }

    /// <summary>Reads a property as a string, coercing scalar JSON kinds to text.</summary>
    public static string? TryReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => null,
        };
    }

    /// <summary>
    /// Reads a property as a boolean, also accepting the canonical lowercased string form
    /// <c>true</c>/<c>false</c> emitted by some of the upstream tools.
    /// </summary>
    public static bool TryReadBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return false;

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(property.GetString(), out var parsed) && parsed,
            _ => false,
        };
    }

    /// <summary>
    /// Reads an optional string property, returning <c>null</c> for missing or whitespace-only
    /// values. Trims surrounding whitespace from the captured value.
    /// </summary>
    public static string? TryReadOptional(JsonElement element, string propertyName)
    {
        var raw = TryReadString(element, propertyName);
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }
}
