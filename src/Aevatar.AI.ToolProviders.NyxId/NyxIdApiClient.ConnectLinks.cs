using System.Text.Json;

namespace Aevatar.AI.ToolProviders.NyxId;

public sealed partial class NyxIdApiClient
{
    private const string ConnectLinksPath = "/api/v1/connect-links";

    public Task<string> CreateConnectLinkAsync(
        string token,
        string body,
        CancellationToken ct) =>
        PostAsync(token, ConnectLinksPath, body, ct);

    public Task<string> GetConnectLinkAsync(
        string token,
        string id,
        CancellationToken ct) =>
        GetAsync(token, $"{ConnectLinksPath}/{Uri.EscapeDataString(id.Trim())}", ct);

    public Task<string> CancelConnectLinkAsync(
        string token,
        string id,
        CancellationToken ct) =>
        PostAsync(token, $"{ConnectLinksPath}/{Uri.EscapeDataString(id.Trim())}/cancel", "{}", ct);

    internal static string? TryRedactConnectUrl(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, "connect_url", StringComparison.Ordinal))
                {
                    writer.WriteString(property.Name, "[redacted]");
                    continue;
                }

                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
