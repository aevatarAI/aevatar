using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Application.Internal;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Google.Protobuf.Reflection;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Application.Workflows;

public sealed class ScopeWorkflowCommandApplicationService : IScopeWorkflowCommandPort
{
    private readonly IServiceCommandPort _serviceCommandPort;
    private readonly IServiceLifecycleQueryPort _serviceLifecycleQueryPort;
    private readonly IServiceGovernanceCommandPort _serviceGovernanceCommandPort;
    private readonly IServiceGovernanceQueryPort _serviceGovernanceQueryPort;
    private readonly ScopeWorkflowCapabilityOptions _options;
    private readonly IWorkflowExternalCapabilityAdmissionService _capabilityAdmissionService;

    public ScopeWorkflowCommandApplicationService(
        IServiceCommandPort serviceCommandPort,
        IServiceLifecycleQueryPort serviceLifecycleQueryPort,
        IServiceGovernanceCommandPort serviceGovernanceCommandPort,
        IServiceGovernanceQueryPort serviceGovernanceQueryPort,
        IOptions<ScopeWorkflowCapabilityOptions> options,
        IWorkflowExternalCapabilityAdmissionService capabilityAdmissionService)
    {
        _serviceCommandPort = serviceCommandPort ?? throw new ArgumentNullException(nameof(serviceCommandPort));
        _serviceLifecycleQueryPort = serviceLifecycleQueryPort ?? throw new ArgumentNullException(nameof(serviceLifecycleQueryPort));
        _serviceGovernanceCommandPort = serviceGovernanceCommandPort ?? throw new ArgumentNullException(nameof(serviceGovernanceCommandPort));
        _serviceGovernanceQueryPort = serviceGovernanceQueryPort ?? throw new ArgumentNullException(nameof(serviceGovernanceQueryPort));
        _capabilityAdmissionService = capabilityAdmissionService ?? throw new ArgumentNullException(nameof(capabilityAdmissionService));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new InvalidOperationException("User workflow capability options are required.");
    }

    public async Task<ScopeWorkflowUpsertResult> UpsertAsync(
        ScopeWorkflowUpsertRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedScopeId = ScopeWorkflowCapabilityOptions.NormalizeRequired(request.ScopeId, nameof(request.ScopeId));
        var normalizedWorkflowId = ScopeWorkflowCapabilityConventions.NormalizeWorkflowId(request.WorkflowId);
        var revisionId = ScopeWorkflowCapabilityConventions.ResolveRevisionId(request.RevisionId);
        var workflowYaml = ScopeWorkflowCapabilityOptions.NormalizeRequired(request.WorkflowYaml, nameof(request.WorkflowYaml));
        var inlineWorkflowYamls = ScopeWorkflowCapabilityConventions.NormalizeInlineWorkflowYamls(request.InlineWorkflowYamls);
        var admissionContext = request.CapabilityAdmission;
        var executionMode = admissionContext?.ExecutionMode ?? ExternalCapabilityExecutionMode.Interactive;
        var explicitRequestConfirmations = admissionContext?.ExplicitRequestConfirmations;
        var capabilityAdmissionPlan = admissionContext?.ExistingPlan is { } existingPlan
            ? await _capabilityAdmissionService.RevalidatePersistedAsync(
                new PersistedWorkflowCapabilityAdmissionRequest(
                    existingPlan,
                    workflowYaml,
                    inlineWorkflowYamls,
                    "scope_workflow_upsert",
                    executionMode,
                    normalizedWorkflowId,
                    revisionId),
                ct)
            : await _capabilityAdmissionService.AdmitAsync(
                new WorkflowExternalCapabilityAdmissionRequest(
                new ExternalWorkflowCapabilityAccessContext(
                    normalizedScopeId,
                    admissionContext?.CallerId ?? string.Empty,
                    admissionContext?.NyxIdCallerCredential,
                    admissionContext?.NyxIdOrganizationBearerToken),
                workflowYaml,
                inlineWorkflowYamls,
                "scope_workflow_upsert",
                executionMode,
                explicitRequestConfirmations,
                normalizedWorkflowId,
                revisionId),
                ct);
        var identity = ScopeWorkflowCapabilityConventions.BuildIdentity(_options, normalizedScopeId, normalizedWorkflowId);
        var definitionActorIdPrefix = ScopeWorkflowCapabilityConventions.BuildDefinitionActorIdPrefix(
            _options,
            normalizedScopeId,
            normalizedWorkflowId);
        var desiredDisplayName = ScopeWorkflowCapabilityConventions.ResolveDisplayName(request.DisplayName, normalizedWorkflowId);
        var existingService = await _serviceLifecycleQueryPort.GetServiceAsync(identity, ct);
        var commandHandles = new List<ScopeWorkflowCommandAcceptedHandle>();

        if (existingService == null)
        {
            var receipt = await _serviceCommandPort.CreateServiceAsync(new CreateServiceDefinitionCommand
            {
                Spec = new ServiceDefinitionSpec
                {
                    Identity = identity.Clone(),
                    DisplayName = desiredDisplayName,
                    Endpoints = { BuildChatEndpointSpec() },
                },
            }, ct);
            commandHandles.Add(ScopeWorkflowCommandAcceptedHandle.FromReceipt("create_service", receipt));
        }
        else if (!string.Equals(existingService.DisplayName, desiredDisplayName, StringComparison.Ordinal))
        {
            var receipt = await _serviceCommandPort.UpdateServiceAsync(new UpdateServiceDefinitionCommand
            {
                Spec = new ServiceDefinitionSpec
                {
                    Identity = identity.Clone(),
                    DisplayName = desiredDisplayName,
                    Endpoints = { BuildChatEndpointSpec() },
                    PolicyIds = { existingService.PolicyIds },
                },
            }, ct);
            commandHandles.Add(ScopeWorkflowCommandAcceptedHandle.FromReceipt("update_service", receipt));
        }

        var endpointCatalogDefinition = new ServiceDefinitionSpec
        {
            Identity = identity.Clone(),
            DisplayName = desiredDisplayName,
        };
        endpointCatalogDefinition.Endpoints.Add(BuildChatEndpointSpec());
        await ServiceEndpointCatalogUpsert.EnsureAsync(
            endpointCatalogDefinition,
            _serviceGovernanceCommandPort,
            _serviceGovernanceQueryPort,
            ct);

        var revisionSpec = new ServiceRevisionSpec
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
            ImplementationKind = ServiceImplementationKind.Workflow,
            WorkflowSpec = new WorkflowServiceRevisionSpec
            {
                WorkflowId = normalizedWorkflowId,
                WorkflowName = ScopeWorkflowCapabilityConventions.NormalizeOptional(request.WorkflowName),
                WorkflowYaml = workflowYaml,
                DefinitionActorId = definitionActorIdPrefix,
                CapabilityAdmissionPlan = capabilityAdmissionPlan,
                ExpectedExecutionMode = executionMode,
            },
        };
        ScopeWorkflowCapabilityConventions.AddInlineWorkflowYamls(revisionSpec.WorkflowSpec.InlineWorkflowYamls, inlineWorkflowYamls);

        commandHandles.Add(ScopeWorkflowCommandAcceptedHandle.FromReceipt(
            "create_revision",
            await _serviceCommandPort.CreateRevisionAsync(new CreateServiceRevisionCommand { Spec = revisionSpec }, ct)));
        commandHandles.Add(ScopeWorkflowCommandAcceptedHandle.FromReceipt(
            "prepare_revision",
            await _serviceCommandPort.PrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
        }, ct)));
        commandHandles.Add(ScopeWorkflowCommandAcceptedHandle.FromReceipt(
            "publish_revision",
            await _serviceCommandPort.PublishRevisionAsync(new PublishServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
        }, ct)));
        commandHandles.Add(ScopeWorkflowCommandAcceptedHandle.FromReceipt(
            "set_default_serving_revision",
            await _serviceCommandPort.SetDefaultServingRevisionAsync(new SetDefaultServingRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
        }, ct)));
        commandHandles.Add(ScopeWorkflowCommandAcceptedHandle.FromReceipt(
            "activate_service_revision",
            await _serviceCommandPort.ActivateServiceRevisionAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
        }, ct)));

        var expectedDeploymentId = $"{ServiceActorIds.Deployment(identity)}:{revisionId}";
        var expectedActorId = $"{definitionActorIdPrefix}:{expectedDeploymentId}";
        // Refactor (issue1531): Old pattern: upsert queried or fabricated a workflow read model in the write path.  New principle: return accepted-only command handles; observed state stays behind the readmodel GET route.
        return new ScopeWorkflowUpsertResult(
            normalizedScopeId,
            normalizedWorkflowId,
            ServiceKeys.Build(identity),
            revisionId,
            definitionActorIdPrefix,
            expectedActorId,
            expectedDeploymentId,
            DateTimeOffset.UtcNow,
            commandHandles,
            BuildReadModelUrl(normalizedScopeId, normalizedWorkflowId),
            DisplayName: desiredDisplayName,
            WorkflowName: ScopeWorkflowCapabilityConventions.NormalizeOptional(request.WorkflowName));
    }

    private static string BuildReadModelUrl(string scopeId, string workflowId) =>
        $"/api/scopes/{Uri.EscapeDataString(scopeId)}/workflows/{Uri.EscapeDataString(workflowId)}";

    private static ServiceEndpointSpec BuildChatEndpointSpec() =>
        new()
        {
            EndpointId = "chat",
            DisplayName = "chat",
            Kind = ServiceEndpointKind.Chat,
            RequestTypeUrl = GetTypeUrl(ChatRequestEvent.Descriptor),
            ResponseTypeUrl = GetTypeUrl(ChatResponseEvent.Descriptor),
            Description = "Workflow chat endpoint.",
        };

    private static string GetTypeUrl(MessageDescriptor descriptor) =>
        $"type.googleapis.com/{descriptor.FullName}";
}
