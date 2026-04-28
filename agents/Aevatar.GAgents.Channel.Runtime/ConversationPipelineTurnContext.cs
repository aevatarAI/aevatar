using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Runtime;

public sealed class ConversationPipelineTurnContext : ITurnContext
{
    public ConversationPipelineTurnContext(
        ChatActivity activity,
        ChannelBotDescriptor bot,
        IServiceProvider services)
    {
        Activity = activity ?? throw new ArgumentNullException(nameof(activity));
        Bot = bot ?? throw new ArgumentNullException(nameof(bot));
        Services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public ChatActivity Activity { get; }

    public ChannelBotDescriptor Bot { get; }

    public IServiceProvider Services { get; }

    public Task<EmitResult> SendAsync(MessageContent content, CancellationToken ct) =>
        throw CreateUnsupportedOperation();

    public Task<EmitResult> ReplyAsync(MessageContent content, CancellationToken ct) =>
        throw CreateUnsupportedOperation();

    public Task<StreamingHandle> BeginStreamingReplyAsync(MessageContent initial, CancellationToken ct) =>
        throw CreateUnsupportedOperation();

    public Task<EmitResult> UpdateAsync(string activityId, MessageContent content, CancellationToken ct) =>
        throw CreateUnsupportedOperation();

    public Task DeleteAsync(string activityId, CancellationToken ct) =>
        throw CreateUnsupportedOperation();

    private static NotSupportedException CreateUnsupportedOperation() =>
        new("The durable-inbox middleware pipeline only routes inbound activities into ConversationGAgent.");
}
