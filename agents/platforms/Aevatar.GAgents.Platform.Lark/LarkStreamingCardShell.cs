using System.Text.Json;

namespace Aevatar.GAgents.Platform.Lark;

/// <summary>
/// Builds the initial CardKit 2.0 card JSON used to seed a streaming card shell: a single
/// markdown element identified by <c>elementId</c> with empty content. Streaming text is
/// written via cardElement.content updates against the live card; this initial JSON only
/// declares the shell. Lives in the Lark platform project so the schema literal stays
/// inside the channel-card-literal guard's allowed boundary.
/// </summary>
public static class LarkStreamingCardShell
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    public static string BuildInitialCardJson(string streamingElementId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamingElementId);

        var card = new
        {
            schema = "2.0",
            config = new { streaming_mode = true },
            body = new
            {
                elements = new object[]
                {
                    new
                    {
                        tag = "markdown",
                        element_id = streamingElementId,
                        content = string.Empty,
                    },
                },
            },
        };
        return JsonSerializer.Serialize(card, JsonOptions);
    }
}
