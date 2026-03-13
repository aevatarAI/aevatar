using Aevatar.Workflow.Application.Abstractions.Authoring;
using Aevatar.Workflow.Application.Abstractions.Queries;

namespace Aevatar.Workflow.Application.Authoring;

public sealed class WorkflowAuthoringQueryApplicationService : IWorkflowAuthoringQueryApplicationService
{
    private readonly IWorkflowDefinitionValidationPort _validationPort;
    private readonly IWorkflowCatalogPort _workflowCatalogPort;
    private readonly IWorkflowCapabilitiesPort _workflowCapabilitiesPort;
    private readonly IWorkflowRuntimeStatusPort _runtimeStatusPort;

    public WorkflowAuthoringQueryApplicationService(
        IWorkflowDefinitionValidationPort validationPort,
        IWorkflowCatalogPort workflowCatalogPort,
        IWorkflowCapabilitiesPort workflowCapabilitiesPort,
        IWorkflowRuntimeStatusPort runtimeStatusPort)
    {
        _validationPort = validationPort ?? throw new ArgumentNullException(nameof(validationPort));
        _workflowCatalogPort = workflowCatalogPort ?? throw new ArgumentNullException(nameof(workflowCatalogPort));
        _workflowCapabilitiesPort = workflowCapabilitiesPort ?? throw new ArgumentNullException(nameof(workflowCapabilitiesPort));
        _runtimeStatusPort = runtimeStatusPort ?? throw new ArgumentNullException(nameof(runtimeStatusPort));
    }

    public Task<PlaygroundWorkflowParseResult> ParseWorkflowAsync(
        PlaygroundWorkflowParseRequest request,
        CancellationToken ct = default) =>
        _validationPort.ParseWorkflowAsync(request, ct);

    public Task<IReadOnlyList<WorkflowPrimitiveDescriptor>> ListPrimitivesAsync(
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var capabilities = _workflowCapabilitiesPort.GetCapabilities();
        var catalog = _workflowCatalogPort.ListWorkflowCatalog();
        var catalogByPrimitive = catalog
            .SelectMany(workflow => workflow.Primitives.Select(primitive => new { primitive, workflow }))
            .GroupBy(entry => entry.primitive, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(entry => entry.workflow)
                    .OrderBy(workflow => workflow.IsPrimitiveExample ? 0 : 1)
                    .ThenBy(workflow => workflow.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(workflow => workflow.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<WorkflowPrimitiveDescriptor> descriptors = capabilities.Primitives
            .OrderBy(capability => GetPrimitiveCategorySortOrder(capability.Category))
            .ThenBy(capability => capability.Name, StringComparer.OrdinalIgnoreCase)
            .Select(capability => new WorkflowPrimitiveDescriptor
            {
                Name = capability.Name,
                Aliases = capability.Aliases
                    .Where(alias => !string.IsNullOrWhiteSpace(alias))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Category = capability.Category,
                Description = capability.Description,
                Parameters = capability.Parameters
                    .Select(parameter => new WorkflowPrimitiveParameterDescriptor
                    {
                        Name = parameter.Name,
                        Type = parameter.Type,
                        Required = parameter.Required,
                        Description = parameter.Description,
                        Default = parameter.Default,
                        EnumValues = parameter.Enum
                            .Where(value => !string.IsNullOrWhiteSpace(value))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                            .ToList(),
                    })
                    .ToList(),
                ExampleWorkflows = catalogByPrimitive.TryGetValue(capability.Name, out var examples)
                    ? examples
                    : [],
            })
            .ToList();

        return Task.FromResult(descriptors);
    }

    public Task<WorkflowLlmStatus> GetLlmStatusAsync(CancellationToken ct = default) =>
        _runtimeStatusPort.GetStatusAsync(ct);

    private static int GetPrimitiveCategorySortOrder(string category) =>
        category switch
        {
            "data" => 0,
            "control" => 1,
            "composition" => 2,
            "ai" => 3,
            "human" => 4,
            "integration" => 5,
            _ => 6,
        };
}
