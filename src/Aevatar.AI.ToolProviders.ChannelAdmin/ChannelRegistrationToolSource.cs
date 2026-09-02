using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.AI.ToolProviders.ChannelAdmin;

/// <summary>
/// Tool source that exposes channel_registrations tool to NyxIdChatGAgent.
/// </summary>
public sealed class ChannelRegistrationToolSource : IAgentToolSource
{
    // Refactor (iter83/cluster-083-agent-tool-source-root-provider-locator):
    //   Old pattern: tool source captures root IServiceProvider; tools resolve business ports via service locator in ExecuteAsync
    //   New principle: tool source + tools constructor-inject typed contracts; no root provider lookup
    // Refactor (iter36/cluster-041-nyx-relay-command-skeleton):
    //   Old pattern: Nyx relay registration endpoints + singleton provisioning services 在 Host 内做 platform selection / scope resolution / remote Nyx provisioning / actor creation / envelope construction / dispatch through raw runtime/dispatch helpers。
    //   New principle: Channel registration 暴露 typed application command facade(reuse existing CQRS command dispatch skeleton);Host 仅 adapt HTTP;provisioning adapters 只调 existing NyxID REST surfaces(**不修改 NyxID 仓库**);local mirror writes 进 standard command skeleton via narrow dispatch port。**不引入新 actor type / 新 envelope / 新 projection phase**(reflector force-pick minimal,排除 structural 的 ChannelRelayRegistrationRunGAgent)。
    private readonly IChannelBotRegistrationQueryPort _queryPort;
    private readonly ChannelRegistrationCommandFacade _commandFacade;
    private readonly ChannelRelayRegistrationFacade _registrationFacade;

    public ChannelRegistrationToolSource(
        IChannelBotRegistrationQueryPort queryPort,
        ChannelRegistrationCommandFacade commandFacade,
        ChannelRelayRegistrationFacade registrationFacade)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _commandFacade = commandFacade ?? throw new ArgumentNullException(nameof(commandFacade));
        _registrationFacade = registrationFacade ?? throw new ArgumentNullException(nameof(registrationFacade));
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<IAgentTool> tools = [new ChannelRegistrationTool(_queryPort, _commandFacade, _registrationFacade)];
        return Task.FromResult(tools);
    }
}
