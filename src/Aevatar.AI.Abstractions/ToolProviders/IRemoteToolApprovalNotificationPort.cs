namespace Aevatar.AI.Abstractions.ToolProviders;

/// <summary>Delivers an out-of-band notification for a submitted remote tool approval.</summary>
public interface IRemoteToolApprovalNotificationPort
{
    Task NotifyAsync(RemoteToolApprovalNotification notification, CancellationToken ct);
}

public sealed record RemoteToolApprovalNotification(
    string RequestId,
    string RemoteApprovalId,
    string ToolName,
    string ToolCallId,
    string ArgumentsJson,
    ToolApprovalMode ApprovalMode,
    bool IsDestructive,
    string SessionId,
    DateTimeOffset? ExpiresAt,
    AgentToolExecutionContext ToolContext);
