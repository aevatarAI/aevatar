using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgents.NyxidChat.Relay;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.ChannelRuntime;

internal sealed class NyxRelayDayOneBridge : INyxRelayDayOneBridge
{
    private const string PrivateChatType = "p2p";
    private const string GroupChatType = "group";
    private const string DeviceConversationType = "device";

    private readonly IServiceProvider _services;

    public NyxRelayDayOneBridge(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public bool ShouldHandle(NyxRelayBridgeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Text))
            return false;
        if (!request.Text.TrimStart().StartsWith('/'))
            return false;
        if (IsDevice(request.ConversationType))
            return false;

        return true;
    }

    public async Task<string> HandleAsync(NyxRelayBridgeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var chatType = MapChatType(request.ConversationType);
        var inboundEvent = new ChannelInboundEvent
        {
            Text = request.Text.TrimStart(),
            ConversationId = request.ConversationId ?? string.Empty,
            MessageId = request.MessageId ?? string.Empty,
            ChatType = chatType,
            Platform = request.Platform ?? string.Empty,
            SenderId = request.SenderId ?? string.Empty,
            SenderName = request.SenderName ?? string.Empty,
            RegistrationScopeId = request.ScopeId,
        };

        if (!NyxRelayAgentBuilderFlow.TryResolve(inboundEvent, out var decision) || decision is null)
        {
            throw new InvalidOperationException(
                "NyxRelayDayOneBridge.HandleAsync was invoked for a message that ShouldHandle did not own.");
        }

        if (!decision.RequiresToolExecution)
            return decision.ReplyPayload;

        var previousMetadata = AgentToolRequestContext.CurrentMetadata;
        try
        {
            AgentToolRequestContext.CurrentMetadata = BuildToolMetadata(request, chatType);
            var tool = ActivatorUtilities.CreateInstance<AgentBuilderTool>(_services);
            var toolResult = await tool.ExecuteAsync(decision.ToolArgumentsJson!, ct);
            return NyxRelayAgentBuilderFlow.FormatToolResult(decision, toolResult);
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = previousMetadata;
        }
    }

    private static bool IsDevice(string? conversationType) =>
        !string.IsNullOrWhiteSpace(conversationType) &&
        string.Equals(conversationType.Trim(), DeviceConversationType, StringComparison.OrdinalIgnoreCase);

    private static string MapChatType(string? conversationType)
    {
        if (string.IsNullOrWhiteSpace(conversationType))
            return PrivateChatType;

        return conversationType.Trim().ToLowerInvariant() switch
        {
            "private" => PrivateChatType,
            "group" or "channel" => GroupChatType,
            _ => PrivateChatType,
        };
    }

    private static IReadOnlyDictionary<string, string> BuildToolMetadata(
        NyxRelayBridgeRequest request,
        string chatType)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = request.NyxIdAccessToken,
            [ChannelMetadataKeys.Platform] = request.Platform ?? string.Empty,
            [ChannelMetadataKeys.SenderId] = request.SenderId ?? string.Empty,
            [ChannelMetadataKeys.SenderName] = request.SenderName ?? string.Empty,
            [ChannelMetadataKeys.ConversationId] = request.ConversationId ?? string.Empty,
            [ChannelMetadataKeys.MessageId] = request.MessageId ?? string.Empty,
            [ChannelMetadataKeys.ChatType] = chatType,
            ["scope_id"] = request.ScopeId,
        };
    }
}
