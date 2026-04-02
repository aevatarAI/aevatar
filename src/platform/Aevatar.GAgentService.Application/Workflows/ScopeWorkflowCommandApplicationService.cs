using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Application.Internal;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Google.Protobuf.Reflection;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Application.Workflows;

public sealed class ScopeWorkflowCommandApplicationService : IScopeWorkflowCommandPort
{
    private readonly IServiceCommandPort _serviceCommandPort;
    private readonly IServiceLifecycleQueryPort _serviceLifecycleQueryPort;
    private readonly IServiceGovernanceCommandPort _serviceGovernanceCommandPort;
    private readonly IServiceGovernanceQueryPort _serviceGovernanceQueryPort;
    private readonly IScopeWorkflowQueryPort _scopeWorkflowQueryPort;
    private readonly ScopeWorkflowCapabilityOptions _options;

    public ScopeWorkflowCommandApplicationService(
        IServiceCommandPort serviceCommandPort,
        IServiceLifecycleQueryPort serviceLifecycleQueryPort,
        IServiceGovernanceCommandPort serviceGovernanceCommandPort,
        IServiceGovernanceQueryPort serviceGovernanceQueryPort,
        IScopeWorkflowQueryPort scopeWorkflowQueryPort,
        IOptions<ScopeWorkflowCapabilityOptions> options)
    {
        _serviceCommandPort = serviceCommandPort ?? throw new ArgumentNullException(nameof(serviceCommandPort));
        _serviceLifecycleQueryPort = serviceLifecycleQueryPort ?? throw new ArgumentNullException(nameof(serviceLifecycleQueryPort));
        _serviceGovernanceCommandPort = serviceGovernanceCommandPort ?? throw new ArgumentNullException(nameof(serviceGovernanceCommandPort));
        _serviceGovernanceQueryPort = serviceGovernanceQueryPort ?? throw new ArgumentNullException(nameof(serviceGovernanceQueryPort));
        _scopeWorkflowQueryPort = scopeWorkflowQueryPort ?? throw new ArgumentNullException(nameof(scopeWorkflowQueryPort));
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
        var workflowYaml = ScopeWorkflowCapabilityOptions.NormalizeRequired(request.WorkflowYaml, nameof(request.WorkflowYaml));
        var identity = ScopeWorkflowCapabilityConventions.BuildIdentity(_options, normalizedScopeId, normalizedWorkflowId);
        var definitionActorIdPrefix = ScopeWorkflowCapabilityConventions.BuildDefinitionActorIdPrefix(
            _options,
            normalizedScopeId,
            normalizedWorkflowId);
        var desiredDisplayName = ScopeWorkflowCapabilityConventions.ResolveDisplayName(request.DisplayName, normalizedWorkflowId);
        var existingService = await _serviceLifecycleQueryPort.GetServiceAsync(identity, ct);

        if (existingService == null)
        {
            await _serviceCommandPort.CreateServiceAsync(new CreateServiceDefinitionCommand
            {
                Spec = new ServiceDefinitionSpec
                {
                    Identity = identity.Clone(),
                    DisplayName = desiredDisplayName,
                    Endpoints = { BuildChatEndpointSpec() },
                },
            }, ct);
        }
        else if (!string.Equals(existingService.DisplayName, desiredDisplayName, StringComparison.Ordinal))
        {
            await _serviceCommandPort.UpdateServiceAsync(new UpdateServiceDefinitionCommand
            {
                Spec = new ServiceDefinitionSpec
                {
                    Identity = identity.Clone(),
                    DisplayName = desiredDisplayName,
                    Endpoints = { BuildChatEndpointSpec() },
                    PolicyIds = { existingService.PolicyIds },
                },
            }, ct);
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

        var revisionId = ScopeWorkflowCapabilityConventions.ResolveRevisionId(request.RevisionId);
        var revisionSpec = new ServiceRevisionSpec
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
            ImplementationKind = ServiceImplementationKind.Workflow,
            WorkflowSpec = new WorkflowServiceRevisionSpec
            {
                WorkflowName = ScopeWorkflowCapabilityConventions.NormalizeOptional(request.WorkflowName),
                WorkflowYaml = workflowYaml,
                DefinitionActorId = definitionActorIdPrefix,
            },
        };
        ScopeWorkflowCapabilityConventions.AddInlineWorkflowYamls(revisionSpec.WorkflowSpec.InlineWorkflowYamls, request.InlineWorkflowYamls);

        await _serviceCommandPort.CreateRevisionAsync(new CreateServiceRevisionCommand { Spec = revisionSpec }, ct);
        await _serviceCommandPort.PrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
        }, ct);
        await _serviceCommandPort.PublishRevisionAsync(new PublishServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
        }, ct);
        await _serviceCommandPort.SetDefaultServingRevisionAsync(new SetDefaultServingRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
        }, ct);
        await _serviceCommandPort.ActivateServiceRevisionAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
        }, ct);

        var expectedDeploymentId = $"{ServiceActorIds.Deployment(identity)}:{revisionId}";
        var expectedActorId = $"{definitionActorIdPrefix}:{expectedDeploymentId}";
        var workflowSummary =
            await _scopeWorkflowQueryPort.GetByWorkflowIdAsync(normalizedScopeId, normalizedWorkflowId, ct) ??
            new ScopeWorkflowSummary(
                normalizedScopeId,
                normalizedWorkflowId,
                desiredDisplayName,
                ServiceKeys.Build(identity),
                ScopeWorkflowCapabilityConventions.NormalizeOptional(request.WorkflowName),
                expectedActorId,
                revisionId,
                expectedDeploymentId,
                "active",
                DateTimeOffset.UtcNow);

        return new ScopeWorkflowUpsertResult(
            workflowSummary,
            revisionId,
            definitionActorIdPrefix,
            expectedActorId);
    }

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
