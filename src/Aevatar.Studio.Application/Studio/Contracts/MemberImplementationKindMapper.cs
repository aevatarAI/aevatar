using Aevatar.GAgents.StudioMember;

namespace Aevatar.Studio.Application.Studio.Contracts;

/// <summary>
/// Boundary mapping between the lowercase wire string used by Studio's HTTP
/// surface and the strongly-typed proto enum used by the StudioMember actor.
/// Keeps the rest of the codebase from string-matching kind values inline.
/// </summary>
public static class MemberImplementationKindMapper
{
    public static StudioMemberImplementationKind Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("implementationKind is required.");

        return value.Trim().ToLowerInvariant() switch
        {
            MemberImplementationKindNames.Workflow => StudioMemberImplementationKind.Workflow,
            MemberImplementationKindNames.Script => StudioMemberImplementationKind.Script,
            MemberImplementationKindNames.GAgent => StudioMemberImplementationKind.Gagent,
            _ => throw new InvalidOperationException(
                $"Unknown implementationKind '{value}'. " +
                $"Expected one of: {MemberImplementationKindNames.Workflow}, " +
                $"{MemberImplementationKindNames.Script}, " +
                $"{MemberImplementationKindNames.GAgent}."),
        };
    }

    public static string ToWireName(StudioMemberImplementationKind kind) => kind switch
    {
        StudioMemberImplementationKind.Workflow => MemberImplementationKindNames.Workflow,
        StudioMemberImplementationKind.Script => MemberImplementationKindNames.Script,
        StudioMemberImplementationKind.Gagent => MemberImplementationKindNames.GAgent,
        _ => string.Empty,
    };

    public static string ToWireName(StudioMemberLifecycleStage stage) => stage switch
    {
        StudioMemberLifecycleStage.Created => MemberLifecycleStageNames.Created,
        StudioMemberLifecycleStage.BuildReady => MemberLifecycleStageNames.BuildReady,
        StudioMemberLifecycleStage.BindReady => MemberLifecycleStageNames.BindReady,
        _ => MemberLifecycleStageNames.Created,
    };
}
