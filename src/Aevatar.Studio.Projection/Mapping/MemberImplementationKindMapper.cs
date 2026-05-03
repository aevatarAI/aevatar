using Aevatar.GAgents.StudioMember;
using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Projection.Mapping;

/// <summary>
/// Boundary mapping between the lowercase wire string used by Studio's HTTP
/// surface and the strongly-typed proto enum used by the StudioMember actor.
/// Lives in the Projection layer (alongside other StudioMember adapters)
/// because the Application layer does not depend on the agent proto package
/// — see CLAUDE.md `严格分层 / 上层依赖抽象`.
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
        _ => string.Empty,
    };

    public static string ToWireName(StudioMemberBindingRunStatus status) => status switch
    {
        StudioMemberBindingRunStatus.Accepted => StudioMemberBindingRunStatusNames.Accepted,
        StudioMemberBindingRunStatus.AdmissionPending => StudioMemberBindingRunStatusNames.AdmissionPending,
        StudioMemberBindingRunStatus.Admitted => StudioMemberBindingRunStatusNames.Admitted,
        StudioMemberBindingRunStatus.PlatformBindingPending => StudioMemberBindingRunStatusNames.PlatformBindingPending,
        StudioMemberBindingRunStatus.Succeeded => StudioMemberBindingRunStatusNames.Succeeded,
        StudioMemberBindingRunStatus.Failed => StudioMemberBindingRunStatusNames.Failed,
        StudioMemberBindingRunStatus.Rejected => StudioMemberBindingRunStatusNames.Rejected,
        _ => string.Empty,
    };
}
