using Aevatar.Studio.Domain.Studio.Models;

namespace Aevatar.Studio.Application.Studio.Abstractions;

public interface IWorkflowYamlDocumentService
{
    WorkflowParseResult Parse(string yaml);

    string Serialize(WorkflowDocument document);
}

public sealed record WorkflowParseResult(
    WorkflowDocument? Document,
    IReadOnlyList<ValidationFinding> Findings);
