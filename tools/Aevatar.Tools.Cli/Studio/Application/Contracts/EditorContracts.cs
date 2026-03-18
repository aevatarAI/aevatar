using Aevatar.Tools.Cli.Studio.Domain.Graph;
using Aevatar.Tools.Cli.Studio.Domain.Models;

namespace Aevatar.Tools.Cli.Studio.Application.Contracts;

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
