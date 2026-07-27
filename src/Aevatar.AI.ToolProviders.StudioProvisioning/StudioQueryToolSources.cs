using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.AI.ToolProviders.StudioProvisioning;

public sealed class StudioTeamQueryToolSource : IAgentToolSource
{
    private readonly IStudioTeamQueryPort? _teamQueryPort;

    public StudioTeamQueryToolSource(IStudioTeamQueryPort? teamQueryPort = null)
    {
        _teamQueryPort = teamQueryPort;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<IAgentTool>>(
            _teamQueryPort is null
                ? []
                : [new ListStudioTeamsTool(_teamQueryPort), new GetStudioTeamTool(_teamQueryPort)]);
    }
}

public sealed class StudioMemberQueryToolSource : IAgentToolSource
{
    private readonly IStudioMemberQueryPort? _memberQueryPort;

    public StudioMemberQueryToolSource(IStudioMemberQueryPort? memberQueryPort = null)
    {
        _memberQueryPort = memberQueryPort;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<IAgentTool>>(
            _memberQueryPort is null
                ? []
                : [new ListStudioMembersTool(_memberQueryPort), new GetStudioMemberTool(_memberQueryPort)]);
    }
}

public sealed class StudioScheduleQueryToolSource : IAgentToolSource
{
    private readonly IStudioMemberAutomationQueryPort? _schedules;

    public StudioScheduleQueryToolSource(IStudioMemberAutomationQueryPort? schedules = null)
    {
        _schedules = schedules;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<IAgentTool>>(
            _schedules is null
                ? []
                : [new ListStudioSchedulesTool(_schedules), new GetStudioScheduleTool(_schedules)]);
    }
}
