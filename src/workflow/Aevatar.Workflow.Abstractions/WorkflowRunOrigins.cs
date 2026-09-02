namespace Aevatar.Workflow.Abstractions;

// Canonical run origin/type values stamped onto WorkflowRunState at bind by the creating ingress
// and surfaced as the observatory run-list filter dimension. Producers MUST use these constants so
// the closed set stays consistent across ingress, projection, query, and UI. The empty string means
// a legacy/unstamped run (not filtered out unless an origin is explicitly selected).
public static class WorkflowRunOrigins
{
    public const string Draft = "draft";
    public const string MemberInvoke = "member-invoke";
    public const string DefaultInvoke = "default-invoke";
    public const string TeamInvoke = "team-invoke";
    public const string ServiceInvoke = "service-invoke";
    public const string WorkOrder = "work-order";
    public const string AdHocChat = "ad-hoc-chat";
    public const string Provisioned = "provisioned";

    public static readonly IReadOnlyList<string> All =
    [
        Draft,
        MemberInvoke,
        DefaultInvoke,
        TeamInvoke,
        ServiceInvoke,
        WorkOrder,
        AdHocChat,
        Provisioned,
    ];

    public static bool IsKnown(string? value) =>
        !string.IsNullOrWhiteSpace(value) && All.Contains(value.Trim());

    // Normalizes a caller-supplied origin to a canonical value, or null when unknown/blank.
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        return All.Contains(trimmed) ? trimmed : null;
    }
}
