using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.ChannelAdmin;

/// <summary>
/// Tool source that exposes channel_registrations tool to NyxIdChatGAgent.
/// Only depends on IServiceProvider — the tool itself lazy-resolves its
/// dependencies (ChannelRegistrationCommandFacade, IChannelBotRegistrationQueryPort)
/// at call time in ExecuteAsync, not at construction time. This avoids DI failures
/// during Orleans grain activation when services may not yet be available.
/// </summary>
public sealed class ChannelRegistrationToolSource : IAgentToolSource
{
    // Refactor (iter36/cluster-041-nyx-relay-command-skeleton):
    //   Old pattern: Nyx relay registration endpoints + singleton provisioning services 在 Host 内做 platform selection / scope resolution / remote Nyx provisioning / actor creation / envelope construction / dispatch through raw runtime/dispatch helpers。
    //   New principle: Channel registration 暴露 typed application command facade(reuse existing CQRS command dispatch skeleton);Host 仅 adapt HTTP;provisioning adapters 只调 existing NyxID REST surfaces(**不修改 NyxID 仓库**);local mirror writes 进 standard command skeleton via narrow dispatch port。**不引入新 actor type / 新 envelope / 新 projection phase**(reflector force-pick minimal,排除 structural 的 ChannelRelayRegistrationRunGAgent)。
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
