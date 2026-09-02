using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.AI.ToolProviders.StudioProvisioning;

public sealed class StudioWorkflowQueryToolSource : IAgentToolSource
{
    private readonly IStudioMemberQueryPort? _memberQueryPort;

    public StudioWorkflowQueryToolSource(IStudioMemberQueryPort? memberQueryPort = null)
    {
        _memberQueryPort = memberQueryPort;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<IAgentTool>>(
            _memberQueryPort is null
                ? []
                : [new ListStudioWorkflowsTool(_memberQueryPort)]);
    }
}
