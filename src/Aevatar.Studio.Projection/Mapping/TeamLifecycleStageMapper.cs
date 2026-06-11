using Aevatar.GAgents.StudioTeam;
using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Projection.Mapping;

/// <summary>
/// Maps between the typed <see cref="StudioTeamLifecycleStage"/> proto enum
/// and the wire-stable string used by the read model document and the
/// application contract (<see cref="TeamLifecycleStageNames"/>).
/// </summary>
internal static class TeamLifecycleStageMapper
{
    public static string ToWireName(StudioTeamLifecycleStage stage) => stage switch
    {
        StudioTeamLifecycleStage.Active => TeamLifecycleStageNames.Active,
        StudioTeamLifecycleStage.Archived => TeamLifecycleStageNames.Archived,
        _ => string.Empty,
    };
}
