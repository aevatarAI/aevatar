using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Studio.Application.Provisioning;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.ToolProviders.StudioProvisioning;

/// <summary>
/// Tool source for the channel-free workflow scheduling tool
/// <c>aevatar_provision_workflow_schedule</c> (the Observatory-delivered analogue
/// of the Lark <c>scheduled_agent_creator</c>). It depends only on the narrow
/// <see cref="IWorkflowScheduleProvisioningPort"/> from the Studio Abstractions
/// project, so this tool provider keeps the thin-abstraction layering the other
/// tool providers follow (no reference to the Studio application impl assembly).
/// </summary>
public sealed class ProvisionWorkflowScheduleToolSource : IAgentToolSource
{
    private readonly IWorkflowScheduleProvisioningPort _provisioningPort;

    public ProvisionWorkflowScheduleToolSource(IWorkflowScheduleProvisioningPort provisioningPort)
    {
        _provisioningPort = provisioningPort
            ?? throw new ArgumentNullException(nameof(provisioningPort));
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<IAgentTool>>(
            [new ProvisionWorkflowScheduleTool(_provisioningPort)]);
    }
}

/// <summary>
/// Tool source for local Studio team creation. The port is optional so hosts can
/// register the source generically while only exposing the tool when Studio team
/// application services are composed.
/// </summary>
public sealed class CreateStudioTeamToolSource : IAgentToolSource
{
    private readonly IStudioTeamProvisioningPort? _teamProvisioningPort;

    public CreateStudioTeamToolSource(IStudioTeamProvisioningPort? teamProvisioningPort = null)
    {
        _teamProvisioningPort = teamProvisioningPort;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<IAgentTool>>(
            _teamProvisioningPort is null
                ? []
                : [new CreateStudioTeamTool(_teamProvisioningPort)]);
    }
}

public sealed class CreateStudioMemberToolSource : IAgentToolSource
{
    private readonly IStudioMemberProvisioningPort? _memberProvisioningPort;

    public CreateStudioMemberToolSource(IStudioMemberProvisioningPort? memberProvisioningPort = null)
    {
        _memberProvisioningPort = memberProvisioningPort;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<IAgentTool>>(
            _memberProvisioningPort is null
                ? []
                : [new CreateStudioMemberTool(_memberProvisioningPort)]);
    }
}

public sealed class CreateStudioMemberWorkflowDraftToolSource : IAgentToolSource
{
    private readonly IStudioMemberWorkflowDraftProvisioningPort? _provisioningPort;

    public CreateStudioMemberWorkflowDraftToolSource(
        IStudioMemberWorkflowDraftProvisioningPort? provisioningPort = null)
    {
        _provisioningPort = provisioningPort;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<IAgentTool>>(
            _provisioningPort is null
                ? []
                : [new CreateStudioMemberWorkflowDraftTool(_provisioningPort)]);
    }
}

public sealed class BindStudioMemberWorkflowToolSource : IAgentToolSource
{
    private readonly IStudioMemberWorkflowBindingPort? _bindingPort;

    public BindStudioMemberWorkflowToolSource(IStudioMemberWorkflowBindingPort? bindingPort = null)
    {
        _bindingPort = bindingPort;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<IAgentTool>>(
            _bindingPort is null
                ? []
                : [new BindStudioMemberWorkflowTool(_bindingPort)]);
    }
}

public sealed class ScheduleStudioMemberWorkflowToolSource : IAgentToolSource
{
    private readonly IStudioMemberWorkflowSchedulePort? _schedulePort;
    private readonly ILoggerFactory? _loggerFactory;

    public ScheduleStudioMemberWorkflowToolSource(
        IStudioMemberWorkflowSchedulePort? schedulePort = null,
        ILoggerFactory? loggerFactory = null)
    {
        _schedulePort = schedulePort;
        _loggerFactory = loggerFactory;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<IAgentTool>>(
            _schedulePort is null
                ? []
                : [new ScheduleStudioMemberWorkflowTool(
                    _schedulePort,
                    _loggerFactory?.CreateLogger<ScheduleStudioMemberWorkflowTool>())]);
    }
}
