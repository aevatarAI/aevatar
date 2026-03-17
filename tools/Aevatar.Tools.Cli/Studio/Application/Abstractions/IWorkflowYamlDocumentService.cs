using Aevatar.Tools.Cli.Studio.Domain.Models;

namespace Aevatar.Tools.Cli.Studio.Application.Abstractions;

public interface IWorkflowYamlDocumentService
{
    WorkflowParseResult Parse(string yaml);

    string Serialize(WorkflowDocument document);
}

public sealed record WorkflowParseResult(
    WorkflowDocument? Document,
    IReadOnlyList<ValidationFinding> Findings);
