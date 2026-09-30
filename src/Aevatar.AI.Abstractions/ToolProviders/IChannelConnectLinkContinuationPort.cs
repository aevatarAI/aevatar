namespace Aevatar.AI.Abstractions.ToolProviders;

/// <summary>
/// Creates a durable external-operation continuation for a trusted Channel tool invocation.
/// Routing and sender authority come exclusively from the trusted execution context, never tool arguments.
/// </summary>
public interface IChannelConnectLinkContinuationPort
{
    Task<ChannelConnectLinkCreateResult> CreateAsync(
        AgentToolExecutionContext context,
        ChannelConnectLinkCreateRequest request,
        CancellationToken ct = default);
}

public sealed record ChannelConnectLinkCreateRequest(
    string CatalogServiceSlug,
    string? Label = null,
    string? RequestedBy = null,
    int? ExpiresInSeconds = null);

public sealed record ChannelConnectLinkCreateResult(
    string? CallbackId = null,
    bool Accepted = false,
    string? ErrorCode = null,
    string? OperationActorId = null);
