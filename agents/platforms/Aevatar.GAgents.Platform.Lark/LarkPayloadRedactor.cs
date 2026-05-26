using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Platform.Lark;

public sealed class LarkPayloadRedactor : IPayloadRedactor
{
    private static readonly ChannelId LarkChannel = ChannelId.From("lark");

    public Task<RedactionResult> RedactAsync(ChannelId channel, byte[] rawPayload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(rawPayload);

        if (!string.Equals(channel.Value, LarkChannel.Value, StringComparison.Ordinal))
            return Task.FromResult(RedactionResult.Unchanged(rawPayload));

        if (rawPayload.Length == 0)
            return Task.FromResult(RedactionResult.Unchanged(rawPayload));

        var text = Encoding.UTF8.GetString(rawPayload);
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return Task.FromResult(RedactionResult.Unchanged(rawPayload));
        }

        if (root is null)
            return Task.FromResult(RedactionResult.Unchanged(rawPayload));

        var modified = false;
        StripSecrets(root, ref modified);
        if (!modified)
            return Task.FromResult(RedactionResult.Unchanged(rawPayload));

        var sanitized = Encoding.UTF8.GetBytes(root.ToJsonString());
        return Task.FromResult(RedactionResult.Modified(sanitized));
    }

    public Task<HealthStatus> HealthCheckAsync(CancellationToken ct) =>
        Task.FromResult(HealthStatus.Healthy);

    private static void StripSecrets(JsonNode node, ref bool modified)
    {
        if (node is JsonObject obj)
        {
            foreach (var name in obj.Select(kvp => kvp.Key).ToArray())
            {
                if (ShouldRemove(name))
                {
                    obj.Remove(name);
                    modified = true;
                    continue;
                }

                if (name is "form_value")
                {
                    obj[name] = "[redacted]";
                    modified = true;
                    continue;
                }

                if (obj[name] is { } child)
                    StripSecrets(child, ref modified);
            }

            return;
        }

        if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                if (child is not null)
                    StripSecrets(child, ref modified);
            }
        }
    }

    private static bool ShouldRemove(string propertyName) =>
        propertyName is "encrypt" or "token" or "email" or "phone" or "avatar_url";
}
