using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Workflows;
using Aevatar.Workflow.Core;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Projection.ReadModels;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.Workflow.Infrastructure.Workflows;

internal sealed class FileBackedWorkflowCatalogPort
{
    private const string PublisherActorId = "workflow.definition.startup.materializer";
    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IWorkflowDefinitionBindObservationScopeLeasePreparationPort _observationPreparation;
    private readonly IWorkflowDefinitionBindObservationProjectionPort _observationProjection;
    private readonly IWorkflowExternalCapabilityAdmissionService _capabilityAdmissionService;
    private readonly IWorkflowActorBindingReader? _bindingReader;
    private readonly IProjectionDocumentReader<WorkflowCatalogCurrentStateDocument, string>? _catalogReader;
    private readonly WorkflowDefinitionFileSourceOptions _options;
    private readonly ILogger<FileBackedWorkflowCatalogPort> _logger;

    public FileBackedWorkflowCatalogPort(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        IWorkflowDefinitionBindObservationScopeLeasePreparationPort observationPreparation,
        IWorkflowDefinitionBindObservationProjectionPort observationProjection,
        IWorkflowExternalCapabilityAdmissionService capabilityAdmissionService,
        IOptions<WorkflowDefinitionFileSourceOptions> options,
        ILogger<FileBackedWorkflowCatalogPort>? logger = null,
        IWorkflowActorBindingReader? bindingReader = null,
        IProjectionDocumentReader<WorkflowCatalogCurrentStateDocument, string>? catalogReader = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _observationPreparation = observationPreparation ??
                                  throw new ArgumentNullException(nameof(observationPreparation));
        _observationProjection = observationProjection ??
                                 throw new ArgumentNullException(nameof(observationProjection));
        _capabilityAdmissionService = capabilityAdmissionService ??
                                      throw new ArgumentNullException(nameof(capabilityAdmissionService));
        _bindingReader = bindingReader;
        _catalogReader = catalogReader;
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        if (_options.BindCommitTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(options),
                _options.BindCommitTimeout,
                "Workflow definition bind commit timeout must be positive.");
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

            try
            {
                await MaterializeDefinitionAsync(definition, ct);
            }
            catch (WorkflowExternalCapabilityAdmissionException ex) when (
                _options.SkipSourceCredentialRequiredDefinitionsOnStartup &&
                IsSourceCredentialRequiredBlocker(ex.SafeBlockerCode))
            {
                _logger.LogWarning(
                    ex,
                    "Skipping startup workflow definition '{WorkflowName}' because source credentials are required and startup skipping is enabled.",
                    definition.WorkflowName);
            }
        }
    }

    private static bool IsSourceCredentialRequiredBlocker(string blockerCode) =>
        string.Equals(
            blockerCode,
            "CODE_EXECUTION_SOURCE_CREDENTIAL_REQUIRED",
            StringComparison.Ordinal) ||
        string.Equals(
            blockerCode,
            "NYXID_ADMISSION_SOURCE_CREDENTIAL_REQUIRED",
            StringComparison.Ordinal);

    private async Task MaterializeDefinitionAsync(
        WorkflowDefinitionRegistration definition,
        CancellationToken ct)
    {
        var actorId = string.IsNullOrWhiteSpace(definition.DefinitionActorId)
            ? WorkflowDefinitionActorId.Format(definition.WorkflowName)
            : definition.DefinitionActorId.Trim();
        var executionMode = definition.ExpectedExecutionMode;
        EnsureExpectedExecutionMode(definition.WorkflowName, actorId, executionMode);
        var capabilityAdmissionPlan = await _capabilityAdmissionService.AdmitAsync(
            new WorkflowExternalCapabilityAdmissionRequest(
                new ExternalWorkflowCapabilityAccessContext(
                    "system",
                    PublisherActorId),
                definition.WorkflowYaml,
                inlineWorkflowYamls: null,
                definition.SourceKind,
                executionMode),
            ct);
        EnsureAdmissionModeMatches(
            definition.WorkflowName,
            actorId,
            executionMode,
            capabilityAdmissionPlan);

        if (await HasExactCommittedBindingAsync(
                definition,
                actorId,
                capabilityAdmissionPlan,
                ct).ConfigureAwait(false))
        {
            return;
        }

        var actor = await _runtime.CreateAsync<WorkflowGAgent>(actorId, ct);
        var bindEnvelope = CreateBindEnvelope(definition, capabilityAdmissionPlan);
        var stateVersion = await DispatchAndObserveBindAsync(
            definition,
            actor.Id,
            bindEnvelope,
            executionMode,
            ct);
        _logger.LogInformation(
            "Materialized startup workflow definition '{WorkflowName}' into WorkflowGAgent '{ActorId}' at committed state version {StateVersion}.",
            definition.WorkflowName,
            actor.Id,
            stateVersion);
    }

    private async Task<bool> HasExactCommittedBindingAsync(
        WorkflowDefinitionRegistration definition,
        string actorId,
        WorkflowCapabilityAdmissionPlan capabilityAdmissionPlan,
        CancellationToken ct)
    {
        if (_bindingReader == null || string.IsNullOrWhiteSpace(capabilityAdmissionPlan.AdmissionDigest))
            return false;

        var binding = await _bindingReader.GetAsync(actorId, ct).ConfigureAwait(false);
        var persistedPlan = binding?.CapabilityAdmissionPlan;
        if (binding is not
            {
                ActorKind: WorkflowActorKind.Definition,
                SourceVersion: > 0,
            } ||
            string.IsNullOrWhiteSpace(binding.SourceEventId) ||
            !string.Equals(binding.ActorId, actorId, StringComparison.Ordinal) ||
            !string.Equals(binding.DefinitionActorId, actorId, StringComparison.Ordinal) ||
            !string.Equals(binding.WorkflowName, definition.WorkflowName, StringComparison.Ordinal) ||
            !string.Equals(binding.WorkflowYaml, definition.WorkflowYaml, StringComparison.Ordinal) ||
            binding.InlineWorkflowYamls.Count != 0 ||
            binding.ExpectedExecutionMode != definition.ExpectedExecutionMode ||
            !string.IsNullOrWhiteSpace(binding.ScopeId) ||
            !string.Equals(
                NormalizeSourceKind(binding.SourceKind),
                NormalizeSourceKind(definition.SourceKind),
                StringComparison.Ordinal) ||
            !string.IsNullOrWhiteSpace(binding.WorkflowId) ||
            !string.IsNullOrWhiteSpace(binding.RevisionId) ||
            persistedPlan == null ||
            !WorkflowToolCatalogPolicies.IsCurrent(binding.ToolCatalogPolicyVersion) ||
            !WorkflowCatalogPublicationContracts.IsCurrent(binding.CatalogPublicationContractVersion) ||
            !string.Equals(
                persistedPlan.AdmissionDigest,
                capabilityAdmissionPlan.AdmissionDigest,
                StringComparison.Ordinal) ||
            !string.Equals(
                persistedPlan.AdmissionDigest,
                WorkflowCapabilityAdmissionPlanIntegrity.ComputeAdmissionDigest(persistedPlan),
                StringComparison.Ordinal))
        {
            return false;
        }

        if (!await HasFreshPublicCatalogReadModelAsync(definition, actorId, binding.SourceVersion, ct)
                .ConfigureAwait(false))
        {
            return false;
        }

        _logger.LogInformation(
            "Reused committed startup workflow definition '{WorkflowName}' from WorkflowGAgent '{ActorId}' at state version {StateVersion}.",
            definition.WorkflowName,
            actorId,
            binding.SourceVersion);
        return true;
    }

    private async Task<bool> HasFreshPublicCatalogReadModelAsync(
        WorkflowDefinitionRegistration definition,
        string actorId,
        long sourceVersion,
        CancellationToken ct)
    {
        if (_catalogReader == null)
        {
            _logger.LogInformation(
                "Startup workflow definition '{WorkflowName}' cannot reuse committed binding for WorkflowGAgent '{ActorId}' because the public catalog readmodel reader is unavailable.",
                definition.WorkflowName,
                actorId);
            return false;
        }

        var workflowName = definition.WorkflowName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(workflowName))
            return false;

        var catalog = await _catalogReader.GetAsync(workflowName, ct).ConfigureAwait(false);
        if (catalog == null)
        {
            _logger.LogInformation(
                "Startup workflow definition '{WorkflowName}' will refresh WorkflowGAgent '{ActorId}' because its public catalog readmodel is missing.",
                definition.WorkflowName,
                actorId);
            return false;
        }

        if (catalog.StateVersion != sourceVersion ||
            !WorkflowCatalogPublicationContracts.IsCurrent(catalog.CatalogPublicationContractVersion) ||
            !string.Equals(catalog.ActorId, actorId, StringComparison.Ordinal) ||
            !string.Equals(catalog.WorkflowName, workflowName, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "Startup workflow definition '{WorkflowName}' will refresh WorkflowGAgent '{ActorId}' because its public catalog readmodel is stale or from an old contract. Binding version: {BindingVersion}; catalog version: {CatalogVersion}; catalog contract: {CatalogContractVersion}.",
                definition.WorkflowName,
                actorId,
                sourceVersion,
                catalog.StateVersion,
                catalog.CatalogPublicationContractVersion);
            return false;
        }

        return true;
    }

    private static string NormalizeSourceKind(string? sourceKind) =>
        string.IsNullOrWhiteSpace(sourceKind) ? "builtin" : sourceKind.Trim();

    private async Task<long> DispatchAndObserveBindAsync(
        WorkflowDefinitionRegistration definition,
        string actorId,
        EventEnvelope bindEnvelope,
        ExternalCapabilityExecutionMode executionMode,
        CancellationToken ct)
    {
        var commandId = bindEnvelope.Propagation.CorrelationId;
        WorkflowDefinitionBindObservationScopeLeasePreparation? preparation;
        try
        {
            preparation = await _observationPreparation.PrepareAsync(actorId, commandId, ct);
        }
        catch (TimeoutException ex)
        {
            throw new WorkflowDefinitionMaterializationException(
                WorkflowDefinitionMaterializationException.ObservationUnavailableCode,
                definition.WorkflowName,
                actorId,
                executionMode,
                $"Workflow definition bind observation did not become ready for actor '{actorId}'.",
                ex);
        }

        if (preparation == null)
        {
            throw new WorkflowDefinitionMaterializationException(
                WorkflowDefinitionMaterializationException.ObservationUnavailableCode,
                definition.WorkflowName,
                actorId,
                executionMode,
                $"Workflow definition bind observation is unavailable for actor '{actorId}'.");
        }

        var sink = new EventChannel<EventEnvelope>(capacity: 8);
        EventSinkProjectionAttachment<IWorkflowDefinitionBindObservationProjectionLease>? attachment = null;
        try
        {
            attachment = await _observationProjection.AttachExistingDefinitionProjectionAsync(
                actorId,
                commandId,
                sink,
                ct);
            if (attachment == null)
            {
                throw new WorkflowDefinitionMaterializationException(
                    WorkflowDefinitionMaterializationException.ObservationUnavailableCode,
                    definition.WorkflowName,
                    actorId,
                    executionMode,
                    $"Workflow definition bind observation attachment is unavailable for actor '{actorId}'.");
            }

            var dispatchAdmission = await _dispatchPort.DispatchAsync(actorId, bindEnvelope, ct);
            if (!dispatchAdmission.Accepted)
            {
                throw new WorkflowDefinitionMaterializationException(
                    WorkflowDefinitionMaterializationException.DispatchRejectedCode,
                    definition.WorkflowName,
                    actorId,
                    executionMode,
                    $"Workflow definition bind dispatch was rejected for actor '{actorId}'.");
            }

            return await WaitForCommittedBindAsync(sink, bindEnvelope, definition, actorId, ct);
        }
        finally
        {
            try
            {
                await _observationProjection.DetachReleaseAndDisposeAsync(
                    attachment?.ProjectionLease,
                    attachment?.LiveSinkLease,
                    sink,
                    ct: CancellationToken.None);
            }
            finally
            {
                await _observationPreparation.ReleaseAsync(preparation, CancellationToken.None);
            }
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
                ExpectedExecutionMode = definition.ExpectedExecutionMode,
                ToolCatalogPolicyVersion = WorkflowToolCatalogPolicies.CurrentVersion,
                CatalogPublicationContractVersion = WorkflowCatalogPublicationContracts.CurrentVersion,
            }),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(PublisherActorId, TopologyAudience.Self),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = Guid.NewGuid().ToString("N"),
            },
        };

    private async Task<long> WaitForCommittedBindAsync(
        IEventSink<EventEnvelope> sink,
        EventEnvelope bindEnvelope,
        WorkflowDefinitionRegistration definition,
        string actorId,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.BindCommitTimeout);
        try
        {
            await foreach (var observed in sink.ReadAllAsync(timeoutCts.Token))
            {
                if (TryObserveCommittedBind(
                        observed,
                        bindEnvelope,
                        definition,
                        actorId,
                        out var stateVersion))
                {
                    return stateVersion;
                }
            }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            throw BindNotCommitted(definition, actorId, ex);
        }

        throw BindNotCommitted(definition, actorId);
    }

    private static bool TryObserveCommittedBind(
        EventEnvelope observed,
        EventEnvelope bindEnvelope,
        WorkflowDefinitionRegistration definition,
        string actorId,
        out long stateVersion)
    {
        stateVersion = 0;
        if (!string.Equals(
                observed.Propagation?.CorrelationId,
                bindEnvelope.Propagation?.CorrelationId,
                StringComparison.Ordinal) ||
            !string.Equals(
                observed.Propagation?.CausationEventId,
                bindEnvelope.Id,
                StringComparison.Ordinal) ||
            observed.Payload?.Is(CommittedStateEventPublished.Descriptor) != true)
        {
            return false;
        }

        var publication = observed.Payload.Unpack<CommittedStateEventPublished>();
        if (publication.StateEvent?.EventData?.Is(BindWorkflowDefinitionEvent.Descriptor) != true)
            return false;

        var committedBind = publication.StateEvent.EventData.Unpack<BindWorkflowDefinitionEvent>();
        if (committedBind.ExpectedExecutionMode != definition.ExpectedExecutionMode ||
            committedBind.CapabilityAdmissionPlan?.ExecutionMode != definition.ExpectedExecutionMode)
        {
            var committedPlanMode = committedBind.CapabilityAdmissionPlan?.ExecutionMode.ToString() ?? "missing";
            throw new WorkflowDefinitionMaterializationException(
                WorkflowDefinitionMaterializationException.AdmissionModeMismatchCode,
                definition.WorkflowName,
                actorId,
                definition.ExpectedExecutionMode,
                $"Committed workflow definition bind execution mode '{committedBind.ExpectedExecutionMode}' " +
                $"and admission mode '{committedPlanMode}' do not match startup registration mode " +
                $"'{definition.ExpectedExecutionMode}'.");
        }

        stateVersion = publication.StateEvent.Version;
        return true;
    }

    private WorkflowDefinitionMaterializationException BindNotCommitted(
        WorkflowDefinitionRegistration definition,
        string actorId,
        Exception? innerException = null) =>
        new(
            WorkflowDefinitionMaterializationException.BindNotCommittedCode,
            definition.WorkflowName,
            actorId,
            definition.ExpectedExecutionMode,
            $"Workflow definition bind was not observed as committed for actor '{actorId}' " +
            $"within {_options.BindCommitTimeout}.",
            innerException);

    private static void EnsureExpectedExecutionMode(
        string workflowName,
        string actorId,
        ExternalCapabilityExecutionMode executionMode)
    {
        if (executionMode != ExternalCapabilityExecutionMode.Unspecified && System.Enum.IsDefined(executionMode))
            return;

        throw new WorkflowDefinitionMaterializationException(
            WorkflowDefinitionMaterializationException.InvalidExecutionModeCode,
            workflowName,
            actorId,
            executionMode,
            "Startup workflow definition requires an explicit expected execution mode.");
    }

    private static void EnsureAdmissionModeMatches(
        string workflowName,
        string actorId,
        ExternalCapabilityExecutionMode executionMode,
        WorkflowCapabilityAdmissionPlan capabilityAdmissionPlan)
    {
        if (capabilityAdmissionPlan.ExecutionMode == executionMode)
            return;

        throw new WorkflowDefinitionMaterializationException(
            WorkflowDefinitionMaterializationException.AdmissionModeMismatchCode,
            workflowName,
            actorId,
            executionMode,
            "Startup workflow capability admission mode does not match its registration.");
    }
}
