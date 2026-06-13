using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Capabilities;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

public static class ProjectionIndexDiagnostics
{
    public const string Remediation =
        "Rebuild or repoint the projection index lifecycle explicitly before accepting projection writes.";

    public static IResult ToServiceUnavailableResult(
        string code,
        string message,
        ProjectionIndexSchemaDriftException exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentNullException.ThrowIfNull(exception);

        return Results.Json(
            new WorkflowProjectionIndexDiagnosticResponse(
                code,
                message,
                exception.Provider,
                exception.IndexAlias,
                exception.CurrentPhysicalIndex,
                exception.ExpectedPhysicalIndex,
                Remediation),
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    public static AevatarHealthContributorResult ToUnhealthyContributorResult(
        ProjectionIndexSchemaDriftException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var details = new Dictionary<string, string>(exception.ToDiagnosticDetails(), StringComparer.Ordinal)
        {
            ["remediation"] = Remediation,
        };

        return AevatarHealthContributorResult.Unhealthy(
            "Workflow execution current-state projection index schema drift detected.",
            details);
    }

    public static AevatarHealthContributorResult ToContributorResult(
        ProjectionIndexConsistencyResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var details = new Dictionary<string, string>(result.ToDiagnosticDetails(), StringComparer.Ordinal)
        {
            ["remediation"] = Remediation,
        };

        if (result.Status == ProjectionIndexConsistencyStatus.Consistent)
        {
            details.Remove("remediation");
            return AevatarHealthContributorResult.Healthy(
                "Workflow execution current-state projection index is consistent.",
                details);
        }

        return AevatarHealthContributorResult.Unhealthy(
            result.Message,
            details);
    }
}

public sealed record WorkflowProjectionIndexDiagnosticResponse(
    string Code,
    string Message,
    string Provider,
    string IndexAlias,
    string CurrentPhysicalIndex,
    string ExpectedPhysicalIndex,
    string Remediation);
