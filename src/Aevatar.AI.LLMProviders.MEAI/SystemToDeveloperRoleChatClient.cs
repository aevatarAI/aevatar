using Microsoft.Extensions.AI;

namespace Aevatar.AI.LLMProviders.MEAI;

/// <summary>
/// Delegating <see cref="IChatClient"/> that converts <see cref="ChatRole.System"/> messages
/// to <c>developer</c> role messages before forwarding to the inner client.
/// Required for the OpenAI Responses API, which does not accept <c>system</c> role
/// in the input array — it expects <c>developer</c> instead.
/// </summary>
internal sealed class SystemToDeveloperRoleChatClient(IChatClient innerClient)
    : DelegatingChatClient(innerClient)
{
    private static readonly ChatRole DeveloperRole = new("developer");

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetResponseAsync(ConvertSystemMessages(messages), options, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetStreamingResponseAsync(ConvertSystemMessages(messages), options, cancellationToken);

    private static IEnumerable<ChatMessage> ConvertSystemMessages(IEnumerable<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            if (message.Role == ChatRole.System)
            {
                var converted = new ChatMessage(DeveloperRole, message.Contents)
                {
                    AuthorName = message.AuthorName,
                    RawRepresentation = message.RawRepresentation,
                };
                foreach (var kvp in message.AdditionalProperties ?? [])
                    (converted.AdditionalProperties ??= [])[kvp.Key] = kvp.Value;
                yield return converted;
            }
            else
            {
                yield return message;
            }
        }
    }
}
