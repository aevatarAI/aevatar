using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.ChannelAdmin;

/// <summary>
/// Tool source that exposes channel_registrations tool to NyxIdChatGAgent.
/// Only depends on IServiceProvider — the tool itself lazy-resolves its
/// dependencies (IActorRuntime, IChannelBotRegistrationQueryPort) at call
/// time in ExecuteAsync, not at construction time. This avoids DI failures
/// during Orleans grain activation when services may not yet be available.
/// </summary>
public sealed class ChannelRegistrationToolSource : IAgentToolSource
{
    private readonly IServiceProvider _serviceProvider;

    public ChannelRegistrationToolSource(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<IAgentTool> tools = [new ChannelRegistrationTool(_serviceProvider)];
        return Task.FromResult(tools);
    }
}
