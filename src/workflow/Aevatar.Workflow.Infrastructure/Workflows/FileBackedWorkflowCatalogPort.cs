using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Workflows;
using Aevatar.Workflow.Core;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Infrastructure.Workflows;

internal sealed class FileBackedWorkflowCatalogPort
{
    private const string PublisherActorId = "workflow.definition.startup.materializer";
    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IWorkflowExternalCapabilityAdmissionService _capabilityAdmissionService;
    private readonly ILogger<FileBackedWorkflowCatalogPort> _logger;

    public FileBackedWorkflowCatalogPort(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        IWorkflowExternalCapabilityAdmissionService capabilityAdmissionService,
        ILogger<FileBackedWorkflowCatalogPort>? logger = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _capabilityAdmissionService = capabilityAdmissionService ??
                                      throw new ArgumentNullException(nameof(capabilityAdmissionService));
        _logger = logger ?? NullLogger<FileBackedWorkflowCatalogPort>.Instance;
    }

    // Refactor (iter46/issue-871-workflow-file-catalog-query-port):
    //   Old pattern: Workflow catalog/capabilities query port discovered files, parsed YAML, loaded connector config, and cached results in singleton process memory during query execution.
    //   New principle: WorkflowGAgent per-definition authority; query ports only read freshness-bearing readmodels; file discovery/parsing happens at startup/import time, not in query path.
    public async Task MaterializeAsync(
        IEnumerable<WorkflowDefinitionRegistration> definitions,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        foreach (var definition in definitions.OrderBy(x => x.WorkflowName, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(definition.WorkflowName) ||
                string.IsNullOrWhiteSpace(definition.WorkflowYaml))
            {
                continue;
            }

            var capabilityAdmissionPlan = await _capabilityAdmissionService.AdmitAsync(
                new WorkflowExternalCapabilityAdmissionRequest(
                    new ExternalWorkflowCapabilityAccessContext(
                        "system",
                        PublisherActorId),
                    definition.WorkflowYaml,
                    inlineWorkflowYamls: null,
                    definition.SourceKind,
                    ExternalCapabilityExecutionMode.Durable),
                ct);

            var actorId = string.IsNullOrWhiteSpace(definition.DefinitionActorId)
                ? WorkflowDefinitionActorId.Format(definition.WorkflowName)
                : definition.DefinitionActorId.Trim();
            var actor = await _runtime.CreateAsync<WorkflowGAgent>(actorId, ct);
            await _dispatchPort.DispatchAsync(
                actor.Id,
                CreateBindEnvelope(definition, capabilityAdmissionPlan),
                ct);
            _logger.LogInformation(
                "Materialized startup workflow definition '{WorkflowName}' into WorkflowGAgent '{ActorId}'.",
                definition.WorkflowName,
                actor.Id);
        }
    }

    private static EventEnvelope CreateBindEnvelope(
        WorkflowDefinitionRegistration definition,
        WorkflowCapabilityAdmissionPlan capabilityAdmissionPlan) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new BindWorkflowDefinitionEvent
            {
                WorkflowName = definition.WorkflowName ?? string.Empty,
                WorkflowYaml = definition.WorkflowYaml ?? string.Empty,
                ScopeId = string.Empty,
                SourceKind = string.IsNullOrWhiteSpace(definition.SourceKind)
                    ? "builtin"
                    : definition.SourceKind.Trim(),
                CapabilityAdmissionPlan = capabilityAdmissionPlan.Clone(),
            }),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(PublisherActorId, TopologyAudience.Self),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = Guid.NewGuid().ToString("N"),
            },
        };
}
