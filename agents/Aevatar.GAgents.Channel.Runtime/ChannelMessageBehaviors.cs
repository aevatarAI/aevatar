using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Runtime;

/// <summary>Opt-in behavior for recognized non-private message scopes only.</summary>
public interface IChannelGroupAdmissionPolicy : IChannelPlatformBehavior
{
    Task<bool> IsAddressedAsync(ChatActivity activity, ChannelBotRegistrationEntry registration,
        ConversationTurnRuntimeContext runtimeContext, CancellationToken ct);
}

/// <summary>Platform typing indication lifecycle. No default implementation is registered.</summary>
public interface IChannelTypingIndicator : IChannelPlatformBehavior
{
    Task StartAsync(ChatActivity activity, ChannelBotRegistrationEntry registration, CancellationToken ct);
    Task ClearAsync(InboundMessage inbound, ChannelBotRegistrationEntry registration, CancellationToken ct);
}

/// <summary>Pure platform text formatting used by both ordinary and interactive replies.</summary>
public interface IChannelReplyTextFormatter : IChannelPlatformBehavior
{
    string Format(string text);
    bool ContainsStructuredContent(string? text);
}

/// <summary>Optional contact identity enrichment; absence leaves the existing subject intact.</summary>
public interface IChannelSubjectContactResolver : IChannelPlatformBehavior
{
    Task<ChannelSubjectContactIds?> ResolveAsync(ChannelInboundEvent inbound, ChatActivity? activity,
        ConversationTurnRuntimeContext runtimeContext, string? unionId, CancellationToken ct);
}

public sealed record ChannelSubjectContactIds(string? UserId, string? EmployeeId);
