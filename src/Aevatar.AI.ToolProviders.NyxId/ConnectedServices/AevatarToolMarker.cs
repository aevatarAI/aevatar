using System.Text.Json;

namespace Aevatar.AI.ToolProviders.NyxId.ConnectedServices;

/// <summary>
/// Parsed <c>x-aevatar-tool</c> vendor extension that explicitly opts an OpenAPI
/// service spec or a single operation into Aevatar LLM tool registration.
/// </summary>
/// <remarks>
/// The extension is an allow-list: an operation is only registered as an
/// <see cref="Aevatar.AI.Abstractions.ToolProviders.IAgentTool"/> when it (or its
/// owning service spec) carries this marker. The extension accepts two shapes:
/// <list type="bullet">
///   <item><description>A bare boolean — <c>x-aevatar-tool: true</c></description></item>
///   <item><description>An object — <c>x-aevatar-tool: { enabled: true, name: "...", readOnly: true, approval: "auto" }</c></description></item>
/// </list>
/// An <see cref="Enabled"/> value of <c>false</c> is an explicit opt-out that overrides
/// a service-level allow.
/// </remarks>
public sealed record AevatarToolMarker(
    bool Enabled,
    string? Name = null,
    bool? ReadOnly = null,
    bool? Destructive = null,
    string? Approval = null,
    string? Description = null)
{
    /// <summary>The vendor extension key read from the OpenAPI document.</summary>
    public const string ExtensionKey = "x-aevatar-tool";

    /// <summary>
    /// Reads the <c>x-aevatar-tool</c> extension from an OpenAPI object node (document
    /// root, <c>info</c>, or a single operation). Returns <c>null</c> when the marker is
    /// absent — absence means "not eligible", never "implicitly enabled".
    /// </summary>
    public static AevatarToolMarker? FromOwner(JsonElement owner)
    {
        if (owner.ValueKind != JsonValueKind.Object ||
            !owner.TryGetProperty(ExtensionKey, out var marker))
        {
            return null;
        }

        return Parse(marker);
    }

    /// <summary>Parses the raw value of an <c>x-aevatar-tool</c> property.</summary>
    public static AevatarToolMarker? Parse(JsonElement marker)
    {
        switch (marker.ValueKind)
        {
            case JsonValueKind.True:
                return new AevatarToolMarker(Enabled: true);
            case JsonValueKind.False:
                return new AevatarToolMarker(Enabled: false);
            case JsonValueKind.String:
                return bool.TryParse(marker.GetString(), out var parsed)
                    ? new AevatarToolMarker(Enabled: parsed)
                    : null;
            case JsonValueKind.Object:
                return new AevatarToolMarker(
                    Enabled: ReadBool(marker, "enabled") ?? true,
                    Name: ReadString(marker, "name"),
                    ReadOnly: ReadBool(marker, "readOnly") ?? ReadBool(marker, "read_only"),
                    Destructive: ReadBool(marker, "destructive"),
                    Approval: ReadString(marker, "approval"),
                    Description: ReadString(marker, "description"));
            default:
                return null;
        }
    }

    private static bool? ReadBool(JsonElement owner, string name)
    {
        if (!owner.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var b) ? b : null,
            _ => null,
        };
    }

    private static string? ReadString(JsonElement owner, string name)
    {
        if (!owner.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}
