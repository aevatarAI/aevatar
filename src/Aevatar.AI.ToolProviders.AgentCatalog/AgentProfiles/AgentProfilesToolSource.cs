using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgentService.Abstractions.AgentProfiles;

namespace Aevatar.AI.ToolProviders.AgentCatalog.AgentProfiles;

public sealed class AgentProfilesToolSource : IAgentToolSource
{
    private readonly Func<IAgentProfileCommandService> _commands;
    private readonly Func<IAgentProfileQueryService> _queries;

    public AgentProfilesToolSource(
        IAgentProfileCommandService commands,
        IAgentProfileQueryService queries)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(queries);
        _commands = () => commands;
        _queries = () => queries;
    }

    public AgentProfilesToolSource(
        Func<IAgentProfileCommandService> commands,
        Func<IAgentProfileQueryService> queries)
    {
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _queries = queries ?? throw new ArgumentNullException(nameof(queries));
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<IAgentTool> tools = [new AgentProfilesTool(_commands, _queries)];
        return Task.FromResult(tools);
    }
}
