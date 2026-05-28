using System.Text.Json.Serialization;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Domain.Studio.Models;
using Aevatar.Studio.Hosting.Serialization;

namespace Aevatar.Studio.Hosting.Controllers;

public sealed record SerializeYamlHttpRequest(
    [property: JsonConverter(typeof(EditorWorkflowDocumentJsonInputConverter))]
    WorkflowDocument Document,
    IReadOnlyCollection<string>? AvailableWorkflowNames = null,
    IReadOnlyCollection<string>? AvailableStepTypes = null)
{
    internal SerializeYamlRequest ToApplicationRequest() =>
        new(Document, AvailableWorkflowNames, AvailableStepTypes);
}

public sealed record ValidateWorkflowHttpRequest(
    [property: JsonConverter(typeof(EditorWorkflowDocumentJsonInputConverter))]
    WorkflowDocument Document,
    IReadOnlyCollection<string>? AvailableWorkflowNames = null,
    IReadOnlyCollection<string>? AvailableStepTypes = null)
{
    internal ValidateWorkflowRequest ToApplicationRequest() =>
        new(Document, AvailableWorkflowNames, AvailableStepTypes);
}

public sealed record NormalizeWorkflowHttpRequest(
    [property: JsonConverter(typeof(EditorWorkflowDocumentJsonInputConverter))]
    WorkflowDocument Document,
    IReadOnlyCollection<string>? AvailableWorkflowNames = null,
    IReadOnlyCollection<string>? AvailableStepTypes = null)
{
    internal NormalizeWorkflowRequest ToApplicationRequest() =>
        new(Document, AvailableWorkflowNames, AvailableStepTypes);
}
