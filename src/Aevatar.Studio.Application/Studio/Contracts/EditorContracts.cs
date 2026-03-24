using Aevatar.Studio.Domain.Studio.Graph;
using Aevatar.Studio.Domain.Studio.Models;

namespace Aevatar.Studio.Application.Studio.Contracts;

public sealed record ParseYamlRequest(
    string Yaml,
    IReadOnlyCollection<string>? AvailableWorkflowNames = null);

public sealed record ParseYamlResponse(
    WorkflowDocument? Document,
    WorkflowGraphDocument? Graph,
    IReadOnlyList<ValidationFinding> Findings);

public sealed record SerializeYamlRequest(
    WorkflowDocument Document,
    IReadOnlyCollection<string>? AvailableWorkflowNames = null);

public sealed record SerializeYamlResponse(
    string Yaml,
    WorkflowDocument Document,
    IReadOnlyList<ValidationFinding> Findings);

public sealed record ValidateWorkflowRequest(
    WorkflowDocument Document,
    IReadOnlyCollection<string>? AvailableWorkflowNames = null);

public sealed record ValidateWorkflowResponse(IReadOnlyList<ValidationFinding> Findings);

public sealed record NormalizeWorkflowRequest(
    WorkflowDocument Document,
    IReadOnlyCollection<string>? AvailableWorkflowNames = null);

public sealed record NormalizeWorkflowResponse(
    WorkflowDocument Document,
    string Yaml,
    IReadOnlyList<ValidationFinding> Findings);

public sealed record DiffWorkflowRequest(string? BeforeYaml, string? AfterYaml);

public sealed record DiffWorkflowResponse(IReadOnlyList<DiffLine> Lines);

public sealed record DiffLine(
    int? LeftLineNumber,
    int? RightLineNumber,
    string Operation,
    string Text);
