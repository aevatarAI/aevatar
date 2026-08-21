using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Application.Internal;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;

namespace Aevatar.GAgentService.Application.Workflows;

public sealed class ScopeWorkflowCommandApplicationService : IScopeWorkflowCommandPort
{
    private readonly IServiceCommandPort _serviceCommandPort;
    private readonly IServiceLifecycleQueryPort _serviceLifecycleQueryPort;
    private readonly IServiceGovernanceCommandPort _serviceGovernanceCommandPort;
    private readonly IServiceGovernanceQueryPort _serviceGovernanceQueryPort;
    private readonly ScopeWorkflowCapabilityOptions _options;
    private readonly IWorkflowExternalCapabilityAdmissionService _capabilityAdmissionService;
    private readonly IWorkflowDefinitionParser _workflowDefinitionParser;

    public ScopeWorkflowCommandApplicationService(
        IServiceCommandPort serviceCommandPort,
        IServiceLifecycleQueryPort serviceLifecycleQueryPort,
        IServiceGovernanceCommandPort serviceGovernanceCommandPort,
        IServiceGovernanceQueryPort serviceGovernanceQueryPort,
        IOptions<ScopeWorkflowCapabilityOptions> options,
        IWorkflowExternalCapabilityAdmissionService capabilityAdmissionService,
        IWorkflowDefinitionParser workflowDefinitionParser)
    {
        _serviceCommandPort = serviceCommandPort ?? throw new ArgumentNullException(nameof(serviceCommandPort));
        _serviceLifecycleQueryPort = serviceLifecycleQueryPort ?? throw new ArgumentNullException(nameof(serviceLifecycleQueryPort));
        _serviceGovernanceCommandPort = serviceGovernanceCommandPort ?? throw new ArgumentNullException(nameof(serviceGovernanceCommandPort));
        _serviceGovernanceQueryPort = serviceGovernanceQueryPort ?? throw new ArgumentNullException(nameof(serviceGovernanceQueryPort));
        _capabilityAdmissionService = capabilityAdmissionService ?? throw new ArgumentNullException(nameof(capabilityAdmissionService));
        _workflowDefinitionParser = workflowDefinitionParser ?? throw new ArgumentNullException(nameof(workflowDefinitionParser));
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
        var publicationParse = await _workflowDefinitionParser
            .ParseWorkflowYamlForPublicationAsync(workflowYaml, ct);
        if (!publicationParse.Succeeded)
            throw new InvalidOperationException(publicationParse.Error);
        foreach (var (inlineName, inlineYaml) in inlineWorkflowYamls)
        {
            var inlineParse = await _workflowDefinitionParser
                .ParseWorkflowYamlForPublicationAsync(inlineYaml, ct);
            if (!inlineParse.Succeeded)
                throw new InvalidOperationException($"Inline workflow '{inlineName}' is invalid: {inlineParse.Error}");
        }
        var admissionContext = request.CapabilityAdmission;
        var executionMode = admissionContext?.ExecutionMode ?? ExternalCapabilityExecutionMode.Interactive;
        var explicitRequestConfirmations = admissionContext?.ExplicitRequestConfirmations;
        var capabilityAccess = new ExternalWorkflowCapabilityAccessContext(
            normalizedScopeId,
            admissionContext?.CallerId ?? string.Empty,
            admissionContext?.NyxIdCallerCredential,
            admissionContext?.NyxIdOrganizationBearerToken);
        var capabilityAdmissionPlan = admissionContext?.ExistingPlan is { } existingPlan
            ? admissionContext.NyxIdCallerCredential is not null
                ? await _capabilityAdmissionService.RefreshPersistedAsync(
                    new RefreshPersistedWorkflowCapabilityAdmissionRequest(
                        new PersistedWorkflowCapabilityAdmissionRequest(
                            existingPlan,
                            workflowYaml,
                            inlineWorkflowYamls,
                            "scope_workflow_upsert",
                            executionMode,
                            normalizedWorkflowId,
                            revisionId),
                        capabilityAccess,
                        explicitRequestConfirmations),
                    ct)
                : await _capabilityAdmissionService.RevalidatePersistedAsync(
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
                    capabilityAccess,
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
                ToolCatalogPolicyVersion = WorkflowToolCatalogPolicies.CurrentVersion,
            },
        };
        ScopeWorkflowCapabilityConventions.AddInlineWorkflowYamls(revisionSpec.WorkflowSpec.InlineWorkflowYamls, inlineWorkflowYamls);
        var expectedArtifact = await BuildWorkflowArtifactAsync(revisionSpec, ct);
        var expectedArtifactHash = ComputeArtifactHash(expectedArtifact);
        expectedArtifact.ArtifactHash = expectedArtifactHash;
        var existingRevisions = await _serviceLifecycleQueryPort.GetServiceRevisionsAsync(identity, ct);
        var revisionDispatchPlan = ResolveRevisionDispatchPlan(
            existingRevisions,
            expectedArtifact,
            expectedArtifactHash);

        if (revisionDispatchPlan.ShouldCreate)
        {
            commandHandles.Add(ScopeWorkflowCommandAcceptedHandle.FromReceipt(
                "create_revision",
                await _serviceCommandPort.CreateRevisionAsync(new CreateServiceRevisionCommand { Spec = revisionSpec }, ct)));
        }
        commandHandles.Add(ScopeWorkflowCommandAcceptedHandle.FromReceipt(
            "prepare_revision",
            await _serviceCommandPort.PrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
            PreparationSpec = revisionSpec.Clone(),
        }, ct)));
        commandHandles.Add(ScopeWorkflowCommandAcceptedHandle.FromReceipt(
            "publish_revision",
            await _serviceCommandPort.PublishRevisionAsync(new PublishServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
            PublicationSpec = revisionSpec.Clone(),
        }, ct)));
        commandHandles.Add(ScopeWorkflowCommandAcceptedHandle.FromReceipt(
            "activate_service_revision",
            await _serviceCommandPort.ActivateServiceRevisionAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
            ExpectedArtifactHash = revisionDispatchPlan.ExpectedArtifactHash,
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

    private async Task<PreparedServiceRevisionArtifact> BuildWorkflowArtifactAsync(
        ServiceRevisionSpec revisionSpec,
        CancellationToken ct)
    {
        var workflowSpec = revisionSpec.WorkflowSpec
            ?? throw new InvalidOperationException("workflow implementation_spec is required.");
        var parse = await _workflowDefinitionParser.ParseWorkflowYamlForPublicationAsync(workflowSpec.WorkflowYaml, ct);
        if (!parse.Succeeded)
            throw new InvalidOperationException(parse.Error);

        var resolvedWorkflowName = string.IsNullOrWhiteSpace(workflowSpec.WorkflowName)
            ? parse.WorkflowName
            : workflowSpec.WorkflowName;
        if (!string.Equals(resolvedWorkflowName, parse.WorkflowName, StringComparison.Ordinal))
            throw new InvalidOperationException("workflow_name must match workflow_yaml name.");

        var authorizationDependencies = parse.AuthorizationDependencies
            ?? throw new InvalidOperationException("workflow authorization dependencies are required.");
        var capabilityAdmissionPlan = workflowSpec.CapabilityAdmissionPlan
            ?? throw new InvalidOperationException("workflow capability admission plan is required.");
        return WorkflowServiceRevisionArtifactBuilder.Build(
            revisionSpec,
            resolvedWorkflowName,
            authorizationDependencies,
            capabilityAdmissionPlan);
    }

    private static RevisionDispatchPlan ResolveRevisionDispatchPlan(
        ServiceRevisionCatalogSnapshot? catalog,
        PreparedServiceRevisionArtifact expectedArtifact,
        string expectedArtifactHash)
    {
        var existing = catalog?.Revisions.FirstOrDefault(revision =>
            string.Equals(revision.RevisionId, expectedArtifact.RevisionId, StringComparison.Ordinal));
        if (existing == null ||
            !string.Equals(
                existing.Status,
                ServiceRevisionStatus.Published.ToString(),
                StringComparison.OrdinalIgnoreCase) ||
            existing.PreparedArtifact == null)
        {
            return new RevisionDispatchPlan(
                ShouldCreate: existing == null,
                ExpectedArtifactHash: expectedArtifactHash);
        }

        if (string.IsNullOrWhiteSpace(existing.ArtifactHash) ||
            !string.Equals(
                existing.ArtifactHash,
                existing.PreparedArtifact.ArtifactHash,
                StringComparison.Ordinal) ||
            !WorkflowServiceRevisionEquivalence.HasValidArtifactHash(existing.PreparedArtifact))
        {
            throw new InvalidOperationException(
                $"Published revision '{expectedArtifact.RevisionId}' has an inconsistent prepared artifact hash.");
        }

        if (string.Equals(existing.ArtifactHash, expectedArtifactHash, StringComparison.Ordinal))
        {
            return new RevisionDispatchPlan(
                ShouldCreate: false,
                ExpectedArtifactHash: existing.ArtifactHash);
        }

        if (!WorkflowServiceRevisionEquivalence.AreEquivalent(
                existing.PreparedArtifact,
                expectedArtifact))
        {
            throw new InvalidOperationException(
                $"Published revision '{expectedArtifact.RevisionId}' conflicts with the requested Workflow artifact.");
        }

        var currentPlan = existing.PreparedArtifact.DeploymentPlan?.WorkflowPlan?
            .CapabilityAdmissionPlan
            ?? throw new InvalidOperationException(
                $"Published revision '{expectedArtifact.RevisionId}' has no capability admission plan.");
        var refreshedPlan = expectedArtifact.DeploymentPlan?.WorkflowPlan?.CapabilityAdmissionPlan
            ?? throw new InvalidOperationException(
                $"Requested revision '{expectedArtifact.RevisionId}' has no capability admission plan.");
        WorkflowServiceRevisionEquivalence.EnsureRenewableAdmissionEvidenceMovesForward(
            currentPlan,
            refreshedPlan);
        return new RevisionDispatchPlan(
            ShouldCreate: false,
            ExpectedArtifactHash: existing.ArtifactHash);
    }

    private static string ComputeArtifactHash(PreparedServiceRevisionArtifact artifact)
    {
        var normalizedArtifact = artifact.Clone();
        normalizedArtifact.ArtifactHash = string.Empty;
        return Convert.ToHexString(SHA256.HashData(normalizedArtifact.ToByteArray()));
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

    private sealed record RevisionDispatchPlan(bool ShouldCreate, string ExpectedArtifactHash);
}
