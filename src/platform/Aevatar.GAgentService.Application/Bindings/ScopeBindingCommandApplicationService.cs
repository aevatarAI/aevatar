using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Application.Internal;
using Aevatar.GAgentService.Application.Workflows;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;

namespace Aevatar.GAgentService.Application.Bindings;

public sealed class ScopeBindingCommandApplicationService : IScopeBindingCommandPort
{
    private readonly IServiceCommandPort _serviceCommandPort;
    private readonly IServiceLifecycleQueryPort _serviceLifecycleQueryPort;
    private readonly IServiceGovernanceCommandPort _serviceGovernanceCommandPort;
    private readonly IServiceGovernanceQueryPort _serviceGovernanceQueryPort;
    private readonly IScopeScriptQueryPort? _scopeScriptQueryPort;
    private readonly IScriptDefinitionSnapshotPort? _scriptDefinitionSnapshotPort;
    private readonly IWorkflowDefinitionParser _workflowDefinitionParser;
    private readonly IWorkflowExternalCapabilityAdmissionService _capabilityAdmissionService;
    private readonly IServiceExternalExposureIntentPort _externalExposureIntentPort;
    private readonly IAgentKindRegistry? _agentKindRegistry;
    private readonly ScopeWorkflowCapabilityOptions _options;

    public ScopeBindingCommandApplicationService(
        IServiceCommandPort serviceCommandPort,
        IServiceLifecycleQueryPort serviceLifecycleQueryPort,
        IServiceGovernanceCommandPort serviceGovernanceCommandPort,
        IServiceGovernanceQueryPort serviceGovernanceQueryPort,
        IScopeScriptQueryPort? scopeScriptQueryPort,
        IScriptDefinitionSnapshotPort? scriptDefinitionSnapshotPort,
        IWorkflowDefinitionParser workflowDefinitionParser,
        IOptions<ScopeWorkflowCapabilityOptions> options,
        IWorkflowExternalCapabilityAdmissionService capabilityAdmissionService,
        IAgentKindRegistry? agentKindRegistry = null,
        IServiceExternalExposureIntentPort? externalExposureIntentPort = null)
    {
        _serviceCommandPort = serviceCommandPort ?? throw new ArgumentNullException(nameof(serviceCommandPort));
        _serviceLifecycleQueryPort = serviceLifecycleQueryPort ?? throw new ArgumentNullException(nameof(serviceLifecycleQueryPort));
        _serviceGovernanceCommandPort = serviceGovernanceCommandPort ?? throw new ArgumentNullException(nameof(serviceGovernanceCommandPort));
        _serviceGovernanceQueryPort = serviceGovernanceQueryPort ?? throw new ArgumentNullException(nameof(serviceGovernanceQueryPort));
        // Nullable by design: the scripting capability is optional. Hosts composed without it
        // resolve these ports to null, and script bindings are rejected in BuildScriptBindingAsync.
        _scopeScriptQueryPort = scopeScriptQueryPort;
        _scriptDefinitionSnapshotPort = scriptDefinitionSnapshotPort;
        _workflowDefinitionParser = workflowDefinitionParser ?? throw new ArgumentNullException(nameof(workflowDefinitionParser));
        _capabilityAdmissionService = capabilityAdmissionService ?? throw new ArgumentNullException(nameof(capabilityAdmissionService));
        _externalExposureIntentPort = externalExposureIntentPort ?? new ServiceCommandExternalExposureIntentPort(serviceCommandPort);
        _agentKindRegistry = agentKindRegistry;
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new InvalidOperationException("Scope workflow capability options are required.");
    }

    // Refactor (iter2/cluster-006):
    //   Old pattern: Upsert dispatched lifecycle commands then polled service catalog and serving readmodels before ACK.
    //   New principle: Upsert returns accepted lifecycle ids; readmodel freshness is observed through explicit read paths.
    public async Task<ScopeBindingUpsertResult> UpsertAsync(
        ScopeBindingUpsertRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedScopeId = ScopeWorkflowCapabilityOptions.NormalizeRequired(request.ScopeId, nameof(request.ScopeId));
        var identity = string.IsNullOrWhiteSpace(request.ServiceId)
            ? ScopeWorkflowCapabilityConventions.BuildDefaultServiceIdentity(_options, normalizedScopeId, request.AppId)
            : ScopeWorkflowCapabilityConventions.BuildServiceIdentity(_options, normalizedScopeId, request.ServiceId.Trim(), request.AppId);
        var revisionId = ScopeWorkflowCapabilityConventions.ResolveRevisionId(request.RevisionId);
        var activationAttemptId = ScopeWorkflowCapabilityConventions.NormalizeOptional(request.ActivationAttemptId);
        var explicitRequestConfirmations = request.CapabilityAdmission?.ExplicitRequestConfirmations;
        var desiredBinding = await ResolveDesiredBindingAsync(
            request,
            normalizedScopeId,
            identity,
            revisionId,
            explicitRequestConfirmations,
            ct);
        var existingService = await _serviceLifecycleQueryPort.GetServiceAsync(identity, ct);
        ApplyExternalExposureIntent(request, desiredBinding.ServiceDefinition);

        if (existingService == null)
        {
            await _serviceCommandPort.CreateServiceAsync(new CreateServiceDefinitionCommand
            {
                Spec = CloneServiceDefinition(desiredBinding.ServiceDefinition),
            }, ct);
        }
        else if (ServiceDefinitionNeedsUpdate(existingService, desiredBinding.ServiceDefinition))
        {
            var updateSpec = CloneServiceDefinition(desiredBinding.ServiceDefinition);
            updateSpec.PolicyIds.Add(existingService.PolicyIds);
            await _serviceCommandPort.UpdateServiceAsync(new UpdateServiceDefinitionCommand
            {
                Spec = updateSpec,
            }, ct);
        }

        await ServiceEndpointCatalogUpsert.EnsureAsync(
            desiredBinding.ServiceDefinition,
            _serviceGovernanceCommandPort,
            _serviceGovernanceQueryPort,
            ct);

        var revisionSpec = desiredBinding.BuildRevision(identity, revisionId);

        var revisionDispatchPlan = await ResolveRevisionDispatchPlanAsync(request, revisionSpec, ct);
        if (revisionDispatchPlan.ShouldCreate)
        {
            await _serviceCommandPort.CreateRevisionAsync(new CreateServiceRevisionCommand
            {
                Spec = revisionSpec,
            }, ct);
        }
        await _serviceCommandPort.PrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
            PreparationSpec = revisionSpec.Clone(),
        }, ct);
        await _serviceCommandPort.PublishRevisionAsync(new PublishServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
            PublicationSpec = revisionSpec.Clone(),
        }, ct);
        await _serviceCommandPort.ActivateServiceRevisionAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
            ActivationAttemptId = activationAttemptId,
            ExpectedArtifactHash = revisionDispatchPlan.ExpectedArtifactHash,
        }, ct);
        await DispatchExternalExposureIntentAsync(request, identity, desiredBinding.ServiceDefinition, existingService, ct);

        var expectedDeploymentId = $"{ServiceActorIds.Deployment(identity)}:{revisionId}";
        // TODO(iter2/cluster-006): If callers need "invoke safe now", add an explicit read/projection
        // observation path in a separate PR rather than blocking this command path on readmodels.
        return desiredBinding.BuildResult(normalizedScopeId, identity.ServiceId, revisionId, expectedDeploymentId) with
        {
            ActivationAttemptId = activationAttemptId,
        };
    }

    private static void ApplyExternalExposureIntent(
        ScopeBindingUpsertRequest request,
        ServiceDefinitionSpec serviceDefinition)
    {
        if (request.ExposureDesired == true)
        {
            serviceDefinition.ExternalExposure = new ExternalExposure
            {
                ExposureDesired = true,
            };
        }
    }

    private async Task DispatchExternalExposureIntentAsync(
        ScopeBindingUpsertRequest request,
        ServiceIdentity identity,
        ServiceDefinitionSpec serviceDefinition,
        ServiceCatalogSnapshot? existingService,
        CancellationToken ct)
    {
        if (request.ExposureDesired == null)
            return;

        await _externalExposureIntentPort.ApplyAsync(
            new ServiceExternalExposureIntentRequest(
                identity.Clone(),
                request.ExposureDesired.Value,
                CloneServiceDefinition(serviceDefinition),
                existingService),
            ct);
    }

    private async Task<RevisionDispatchPlan> ResolveRevisionDispatchPlanAsync(
        ScopeBindingUpsertRequest request,
        ServiceRevisionSpec revisionSpec,
        CancellationToken ct)
    {
        var requestedRevisionId = ScopeWorkflowCapabilityConventions.NormalizeOptional(request.RevisionId);
        if (string.IsNullOrWhiteSpace(requestedRevisionId))
        {
            return new RevisionDispatchPlan(
                ShouldCreate: true,
                await ComputeExpectedArtifactHashAsync(revisionSpec, ct));
        }

        var identity = revisionSpec.Identity
            ?? throw new InvalidOperationException("service identity is required.");
        var revisionId = ScopeWorkflowCapabilityOptions.NormalizeRequired(revisionSpec.RevisionId, nameof(revisionSpec.RevisionId));
        var revisions = await _serviceLifecycleQueryPort.GetServiceRevisionsAsync(identity, ct);
        var existingRevision = revisions?.Revisions.FirstOrDefault(x =>
            string.Equals(x.RevisionId, revisionId, StringComparison.Ordinal));
        if (existingRevision == null)
        {
            return new RevisionDispatchPlan(
                ShouldCreate: !MatchesAcceptedRevisionCreation(request, identity, revisionId),
                await ComputeExpectedArtifactHashAsync(revisionSpec, ct));
        }

        if (!string.Equals(existingRevision.ImplementationKind, revisionSpec.ImplementationKind.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Revision '{revisionId}' already exists for service '{ServiceKeys.Build(identity)}' with implementation '{existingRevision.ImplementationKind}'.");
        }

        if (string.Equals(existingRevision.Status, ServiceRevisionStatus.Retired.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Revision '{revisionId}' already exists for service '{ServiceKeys.Build(identity)}' but has been retired.");
        }

        if (request.ImplementationKind == ScopeBindingImplementationKind.Scripting)
        {
            var expectedScriptingArtifactHash = await ComputeScriptingArtifactHashAsync(revisionSpec, ct);
            if (IsAwaitingPreparation(existingRevision.Status) &&
                string.IsNullOrWhiteSpace(existingRevision.ArtifactHash))
            {
                return new RevisionDispatchPlan(
                    ShouldCreate: false,
                    expectedScriptingArtifactHash);
            }

            if (!string.Equals(existingRevision.ArtifactHash, expectedScriptingArtifactHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Revision '{revisionId}' already exists for service '{ServiceKeys.Build(identity)}' but points to a different scripting artifact.");
            }

            return new RevisionDispatchPlan(ShouldCreate: false, expectedScriptingArtifactHash);
        }

        if (!request.AllowExistingRevisionReplay ||
            !string.Equals(request.ReplayRevisionId, revisionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Revision '{revisionId}' already exists for service '{ServiceKeys.Build(identity)}'.");
        }

        if (request.ImplementationKind == ScopeBindingImplementationKind.Workflow &&
            IsAwaitingPreparation(existingRevision.Status))
        {
            return new RevisionDispatchPlan(
                ShouldCreate: false,
                await ComputeNonScriptingArtifactHashAsync(revisionSpec, ct));
        }

        if (request.ImplementationKind == ScopeBindingImplementationKind.Workflow &&
            existingRevision.PreparedArtifact != null)
        {
            if (string.IsNullOrWhiteSpace(existingRevision.ArtifactHash) ||
                !string.Equals(
                    existingRevision.ArtifactHash,
                    existingRevision.PreparedArtifact.ArtifactHash,
                    StringComparison.Ordinal) ||
                !WorkflowServiceRevisionEquivalence.HasValidArtifactHash(
                    existingRevision.PreparedArtifact))
            {
                throw new InvalidOperationException(
                    $"Revision '{revisionId}' already exists for service '{ServiceKeys.Build(identity)}' but its prepared artifact hash is inconsistent.");
            }

            if (WorkflowServiceArtifactReadiness.RequiresCapabilityAdmissionRebind(
                    existingRevision.PreparedArtifact))
            {
                throw new InvalidOperationException(
                    $"Revision '{revisionId}' already exists for service '{ServiceKeys.Build(identity)}' but its workflow capability admission plan requires rebind; publish a new revision id.");
            }

            var expectedWorkflowArtifact = await BuildWorkflowArtifactAsync(revisionSpec, ct);
            var expectedWorkflowArtifactHash = ComputeArtifactHash(expectedWorkflowArtifact);
            expectedWorkflowArtifact.ArtifactHash = expectedWorkflowArtifactHash;
            if (string.Equals(
                    existingRevision.ArtifactHash,
                    expectedWorkflowArtifactHash,
                    StringComparison.Ordinal))
            {
                return new RevisionDispatchPlan(
                    ShouldCreate: false,
                    expectedWorkflowArtifactHash);
            }

            if (!WorkflowServiceRevisionEquivalence.AreEquivalent(
                    existingRevision.PreparedArtifact,
                    expectedWorkflowArtifact))
            {
                throw new InvalidOperationException(
                    $"Revision '{revisionId}' already exists for service '{ServiceKeys.Build(identity)}' but points to a different Workflow artifact.");
            }

            var currentPlan = existingRevision.PreparedArtifact.DeploymentPlan?.WorkflowPlan?
                .CapabilityAdmissionPlan
                ?? throw new InvalidOperationException(
                    $"Revision '{revisionId}' already exists for service '{ServiceKeys.Build(identity)}' but has no prepared workflow capability admission plan.");
            var refreshedPlan = expectedWorkflowArtifact.DeploymentPlan?.WorkflowPlan?
                .CapabilityAdmissionPlan
                ?? throw new InvalidOperationException(
                    $"Revision '{revisionId}' replay produced no workflow capability admission plan.");
            WorkflowServiceRevisionEquivalence.EnsureRenewableAdmissionEvidenceMovesForward(
                currentPlan,
                refreshedPlan);

            if (!string.Equals(
                    existingRevision.Status,
                    ServiceRevisionStatus.Prepared.ToString(),
                    StringComparison.OrdinalIgnoreCase))
            {
                // Published revisions are immutable. Renewable admission evidence may be
                // revalidated by the caller, but activation must remain fenced to the
                // artifact that was actually published.
                return new RevisionDispatchPlan(
                    ShouldCreate: false,
                    existingRevision.ArtifactHash);
            }

            return new RevisionDispatchPlan(
                ShouldCreate: false,
                expectedWorkflowArtifactHash);
        }

        if (string.IsNullOrWhiteSpace(existingRevision.ArtifactHash))
        {
            return new RevisionDispatchPlan(
                ShouldCreate: false,
                await ComputeNonScriptingArtifactHashAsync(revisionSpec, ct));
        }

        var expectedArtifactHash = await ComputeNonScriptingArtifactHashAsync(revisionSpec, ct);
        if (!string.Equals(existingRevision.ArtifactHash, expectedArtifactHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Revision '{revisionId}' already exists for service '{ServiceKeys.Build(identity)}' but points to a different {request.ImplementationKind} artifact.");
        }

        return new RevisionDispatchPlan(ShouldCreate: false, expectedArtifactHash);
    }

    private Task<string> ComputeExpectedArtifactHashAsync(
        ServiceRevisionSpec revisionSpec,
        CancellationToken ct) =>
        revisionSpec.ImplementationSpecCase ==
        ServiceRevisionSpec.ImplementationSpecOneofCase.ScriptingSpec
            ? ComputeScriptingArtifactHashAsync(revisionSpec, ct)
            : ComputeNonScriptingArtifactHashAsync(revisionSpec, ct);

    private static bool IsAwaitingPreparation(string? status) =>
        string.Equals(
            status,
            ServiceRevisionStatus.Created.ToString(),
            StringComparison.OrdinalIgnoreCase) ||
        string.Equals(
            status,
            ServiceRevisionStatus.PreparationFailed.ToString(),
            StringComparison.OrdinalIgnoreCase);

    private static bool MatchesAcceptedRevisionCreation(
        ScopeBindingUpsertRequest request,
        ServiceIdentity identity,
        string revisionId)
    {
        var accepted = request.AcceptedRevisionCreation;
        return accepted != null &&
               request.AllowExistingRevisionReplay &&
               string.Equals(request.ReplayRevisionId, revisionId, StringComparison.Ordinal) &&
               string.Equals(accepted.RevisionId, revisionId, StringComparison.Ordinal) &&
               string.Equals(accepted.ServiceKey, ServiceKeys.Build(identity), StringComparison.Ordinal);
    }

    private async Task<string> ComputeNonScriptingArtifactHashAsync(
        ServiceRevisionSpec revisionSpec,
        CancellationToken ct)
    {
        var artifact = revisionSpec.ImplementationSpecCase switch
        {
            ServiceRevisionSpec.ImplementationSpecOneofCase.WorkflowSpec =>
                await BuildWorkflowArtifactAsync(revisionSpec, ct),
            ServiceRevisionSpec.ImplementationSpecOneofCase.StaticSpec => BuildStaticArtifact(revisionSpec),
            _ => throw new InvalidOperationException(
                $"Unsupported replay implementation spec '{revisionSpec.ImplementationSpecCase}'."),
        };
        return ComputeArtifactHash(artifact);
    }

    private async Task<string> ComputeScriptingArtifactHashAsync(
        ServiceRevisionSpec revisionSpec,
        CancellationToken ct)
    {
        if (_scriptDefinitionSnapshotPort is not { } scriptDefinitionSnapshotPort)
        {
            throw new InvalidOperationException(
                "Scripting capability is not enabled on this host; implementationKind 'scripting' is not supported.");
        }

        var identity = revisionSpec.Identity
            ?? throw new InvalidOperationException("service identity is required.");
        var scriptingSpec = revisionSpec.ScriptingSpec
            ?? throw new InvalidOperationException("scripting implementation_spec is required.");
        if (string.IsNullOrWhiteSpace(scriptingSpec.DefinitionActorId))
            throw new InvalidOperationException("scripting definition_actor_id is required.");

        var snapshot = await scriptDefinitionSnapshotPort.GetRequiredAsync(
            scriptingSpec.DefinitionActorId,
            scriptingSpec.Revision,
            ct);
        var artifact = new PreparedServiceRevisionArtifact
        {
            Identity = identity.Clone(),
            RevisionId = revisionSpec.RevisionId,
            ImplementationKind = ServiceImplementationKind.Scripting,
            ProtocolDescriptorSet = snapshot.ProtocolDescriptorSet,
            DeploymentPlan = new ServiceDeploymentPlan
            {
                ScriptingPlan = new ScriptingServiceDeploymentPlan
                {
                    ScriptId = snapshot.ScriptId,
                    Revision = snapshot.Revision,
                    DefinitionActorId = scriptingSpec.DefinitionActorId,
                    SourceHash = snapshot.SourceHash,
                    PackageSpec = ToServicePackage(snapshot.ScriptPackage),
                },
            },
        };
        artifact.Endpoints.Add(
            BuildScriptEndpointSpecs(snapshot)
                .Select(ToEndpointDescriptor));
        return ComputeArtifactHash(artifact);
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
        var artifactRevisionSpec = revisionSpec.Clone();
        artifactRevisionSpec.WorkflowSpec!.WorkflowId = string.IsNullOrWhiteSpace(workflowSpec.WorkflowId)
            ? revisionSpec.RevisionId
            : workflowSpec.WorkflowId;
        return WorkflowServiceRevisionArtifactBuilder.Build(
            artifactRevisionSpec,
            resolvedWorkflowName,
            authorizationDependencies,
            capabilityAdmissionPlan);
    }

    private static PreparedServiceRevisionArtifact BuildStaticArtifact(ServiceRevisionSpec revisionSpec)
    {
        var staticSpec = revisionSpec.StaticSpec
            ?? throw new InvalidOperationException("static implementation_spec is required.");
        return new PreparedServiceRevisionArtifact
        {
            Identity = revisionSpec.Identity.Clone(),
            RevisionId = revisionSpec.RevisionId,
            ImplementationKind = ServiceImplementationKind.Static,
            Endpoints = { staticSpec.Endpoints.Select(x => x.Clone()) },
            DeploymentPlan = new ServiceDeploymentPlan
            {
                StaticPlan = new StaticServiceDeploymentPlan
                {
                    ActorTypeName = staticSpec.ActorTypeName,
                    AgentKind = staticSpec.AgentKind,
                    PreferredActorId = staticSpec.PreferredActorId ?? string.Empty,
                },
            },
        };
    }

    private static string ComputeArtifactHash(PreparedServiceRevisionArtifact artifact)
    {
        var normalizedArtifact = artifact.Clone();
        normalizedArtifact.ArtifactHash = string.Empty;
        return Convert.ToHexString(SHA256.HashData(normalizedArtifact.ToByteArray()));
    }

    private sealed record RevisionDispatchPlan(bool ShouldCreate, string ExpectedArtifactHash);

    private async Task<DesiredScopeBinding> ResolveDesiredBindingAsync(
        ScopeBindingUpsertRequest request,
        string normalizedScopeId,
        ServiceIdentity identity,
        string revisionId,
        IReadOnlyList<NyxIdExplicitRequestConfirmation>? explicitRequestConfirmations,
        CancellationToken ct)
    {
        return request.ImplementationKind switch
        {
            ScopeBindingImplementationKind.Workflow =>
                await BuildWorkflowBindingAsync(
                    request,
                    normalizedScopeId,
                    identity,
                    revisionId,
                    explicitRequestConfirmations,
                    ct),
            ScopeBindingImplementationKind.Scripting =>
                await BuildScriptBindingAsync(request, normalizedScopeId, identity, ct),
            ScopeBindingImplementationKind.GAgent =>
                BuildGAgentBinding(request, identity),
            _ => throw new InvalidOperationException($"Unsupported implementationKind '{request.ImplementationKind}'."),
        };
    }

    private async Task<DesiredScopeBinding> BuildWorkflowBindingAsync(
        ScopeBindingUpsertRequest request,
        string normalizedScopeId,
        ServiceIdentity identity,
        string revisionId,
        IReadOnlyList<NyxIdExplicitRequestConfirmation>? explicitRequestConfirmations,
        CancellationToken ct)
    {
        var workflowBundle = await ParseWorkflowBundleAsync(request.Workflow?.WorkflowYamls, ct);
        var suppliedWorkflowId = ScopeWorkflowCapabilityConventions.NormalizeOptional(request.Workflow?.WorkflowId);
        var workflowId = string.IsNullOrWhiteSpace(suppliedWorkflowId)
            ? string.Empty
            : ScopeWorkflowCapabilityConventions.NormalizeWorkflowId(suppliedWorkflowId);
        var admissionContext = request.CapabilityAdmission;
        var executionMode = admissionContext?.ExecutionMode ?? ExternalCapabilityExecutionMode.Interactive;
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
                            workflowBundle.EntryWorkflowYaml,
                            workflowBundle.SubWorkflowYamls,
                            "scope_binding_upsert",
                            executionMode,
                            workflowId,
                            revisionId),
                        capabilityAccess,
                        explicitRequestConfirmations),
                    ct)
                : await _capabilityAdmissionService.RevalidatePersistedAsync(
                    new PersistedWorkflowCapabilityAdmissionRequest(
                        existingPlan,
                        workflowBundle.EntryWorkflowYaml,
                        workflowBundle.SubWorkflowYamls,
                        "scope_binding_upsert",
                        executionMode,
                        workflowId,
                        revisionId),
                    ct)
            : await _capabilityAdmissionService.AdmitAsync(
                new WorkflowExternalCapabilityAdmissionRequest(
                    capabilityAccess,
                    workflowBundle.EntryWorkflowYaml,
                workflowBundle.SubWorkflowYamls,
                "scope_binding_upsert",
                executionMode,
                explicitRequestConfirmations,
                workflowId,
                revisionId),
                ct);
        var definitionActorIdPrefix = string.IsNullOrWhiteSpace(suppliedWorkflowId)
            ? ScopeWorkflowCapabilityConventions.BuildDefaultDefinitionActorIdPrefix(_options, normalizedScopeId)
            : ScopeWorkflowCapabilityConventions.BuildDefinitionActorIdPrefix(
                _options,
                normalizedScopeId,
                workflowId);
        var displayName = ScopeWorkflowCapabilityConventions.ResolveDisplayName(
            request.DisplayName,
            workflowBundle.EntryWorkflowName);
        var serviceDefinition = new ServiceDefinitionSpec
        {
            Identity = identity.Clone(),
            DisplayName = displayName,
        };
        serviceDefinition.Endpoints.Add(BuildChatEndpointSpec());

        return new DesiredScopeBinding(
            serviceDefinition,
            (serviceIdentity, revisionId) =>
            {
                var revisionSpec = new ServiceRevisionSpec
                {
                    Identity = serviceIdentity.Clone(),
                    RevisionId = revisionId,
                    ImplementationKind = ServiceImplementationKind.Workflow,
                    WorkflowSpec = new WorkflowServiceRevisionSpec
                    {
                        WorkflowId = workflowId,
                        WorkflowName = workflowBundle.EntryWorkflowName,
                        WorkflowYaml = workflowBundle.EntryWorkflowYaml,
                        DefinitionActorId = definitionActorIdPrefix,
                        CapabilityAdmissionPlan = capabilityAdmissionPlan,
                        ExpectedExecutionMode = executionMode,
                        ToolCatalogPolicyVersion = WorkflowToolCatalogPolicies.CurrentVersion,
                    },
                };
                ScopeWorkflowCapabilityConventions.AddInlineWorkflowYamls(
                    revisionSpec.WorkflowSpec.InlineWorkflowYamls,
                    workflowBundle.SubWorkflowYamls);
                return revisionSpec;
            },
            (scopeId, serviceId, revisionId, expectedDeploymentId) =>
                new ScopeBindingUpsertResult(
                    scopeId,
                    serviceId,
                    displayName,
                    revisionId,
                    ScopeBindingImplementationKind.Workflow,
                    $"{definitionActorIdPrefix}:{expectedDeploymentId}",
                    WorkflowName: workflowBundle.EntryWorkflowName,
                    DefinitionActorIdPrefix: definitionActorIdPrefix,
                    Workflow: new ScopeBindingWorkflowResult(
                        workflowId,
                        workflowBundle.EntryWorkflowName,
                        definitionActorIdPrefix),
                    ExpectedDeploymentId: expectedDeploymentId));
    }

    private async Task<DesiredScopeBinding> BuildScriptBindingAsync(
        ScopeBindingUpsertRequest request,
        string normalizedScopeId,
        ServiceIdentity identity,
        CancellationToken ct)
    {
        if (_scopeScriptQueryPort is not { } scopeScriptQueryPort ||
            _scriptDefinitionSnapshotPort is not { } scriptDefinitionSnapshotPort)
        {
            throw new InvalidOperationException(
                "Scripting capability is not enabled on this host; implementationKind 'scripting' is not supported.");
        }

        var script = request.Script
            ?? throw new InvalidOperationException("script is required for implementationKind 'scripting'.");
        var normalizedScriptId = ScopeWorkflowCapabilityOptions.NormalizeRequired(script.ScriptId, nameof(script.ScriptId));
        var scriptSummary = await scopeScriptQueryPort.GetByScriptIdAsync(normalizedScopeId, normalizedScriptId, ct)
            ?? throw new InvalidOperationException(
                $"Scope '{normalizedScopeId}' does not have an active script '{normalizedScriptId}'.");
        var requestedScriptRevision = ScopeWorkflowCapabilityConventions.NormalizeOptional(script.ScriptRevision);
        if (!string.IsNullOrWhiteSpace(requestedScriptRevision) &&
            !string.Equals(requestedScriptRevision, scriptSummary.ActiveRevision, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Scope script '{normalizedScriptId}' is currently at revision '{scriptSummary.ActiveRevision}', but got '{requestedScriptRevision}'.");
        }

        var snapshot = await scriptDefinitionSnapshotPort.GetRequiredAsync(
            scriptSummary.DefinitionActorId,
            scriptSummary.ActiveRevision,
            ct);
        var endpointSpecs = BuildScriptEndpointSpecs(snapshot);
        var displayName = ScopeWorkflowCapabilityConventions.ResolveDisplayName(
            request.DisplayName,
            normalizedScriptId);
        var serviceDefinition = new ServiceDefinitionSpec
        {
            Identity = identity.Clone(),
            DisplayName = displayName,
        };
        serviceDefinition.Endpoints.Add(endpointSpecs.Select(CloneEndpointSpec));

        return new DesiredScopeBinding(
            serviceDefinition,
            (serviceIdentity, revisionId) =>
                new ServiceRevisionSpec
                {
                    Identity = serviceIdentity.Clone(),
                    RevisionId = revisionId,
                    ImplementationKind = ServiceImplementationKind.Scripting,
                    ScriptingSpec = new ScriptingServiceRevisionSpec
                    {
                        ScriptId = scriptSummary.ScriptId,
                        Revision = scriptSummary.ActiveRevision,
                        DefinitionActorId = scriptSummary.DefinitionActorId,
                        SourceHash = scriptSummary.ActiveSourceHash,
                    },
                },
            (scopeId, serviceId, revisionId, expectedDeploymentId) =>
                new ScopeBindingUpsertResult(
                    scopeId,
                    serviceId,
                    displayName,
                    revisionId,
                    ScopeBindingImplementationKind.Scripting,
                    $"gagent-service:script-runtime:{expectedDeploymentId}",
                    Script: new ScopeBindingScriptResult(
                        scriptSummary.ScriptId,
                        scriptSummary.ActiveRevision,
                        scriptSummary.DefinitionActorId)
                    {
                        EndpointIds = endpointSpecs.Select(endpoint => endpoint.EndpointId).ToArray(),
                    },
                    ExpectedDeploymentId: expectedDeploymentId));
    }

    private DesiredScopeBinding BuildGAgentBinding(
        ScopeBindingUpsertRequest request,
        ServiceIdentity identity)
    {
        var gagent = request.GAgent
            ?? throw new InvalidOperationException("gagent is required for implementationKind 'gagent'.");
        var agentKind = NormalizeGAgentKind(gagent);
        var diagnosticClrTypeName = ResolveDiagnosticClrTypeName(agentKind);

        // Start with caller-supplied endpoints, then ensure a chat endpoint always exists.
        var endpointSpecs = (gagent.Endpoints ?? [])
            .Select(ToServiceEndpointSpec)
            .ToList();
        if (!endpointSpecs.Any(e => string.Equals(e.EndpointId, "chat", StringComparison.OrdinalIgnoreCase)))
            endpointSpecs.Insert(0, BuildChatEndpointSpec());
        var displayName = ScopeWorkflowCapabilityConventions.ResolveDisplayName(
            request.DisplayName,
            agentKind);
        var serviceDefinition = new ServiceDefinitionSpec
        {
            Identity = identity.Clone(),
            DisplayName = displayName,
        };
        serviceDefinition.Endpoints.Add(endpointSpecs.Select(CloneEndpointSpec));

        return new DesiredScopeBinding(
            serviceDefinition,
            (serviceIdentity, revisionId) =>
            {
                var revisionSpec = new ServiceRevisionSpec
                {
                    Identity = serviceIdentity.Clone(),
                    RevisionId = revisionId,
                    ImplementationKind = ServiceImplementationKind.Static,
                    StaticSpec = new StaticServiceRevisionSpec
                    {
                        ActorTypeName = diagnosticClrTypeName,
                        AgentKind = agentKind,
                    },
                };
                revisionSpec.StaticSpec.Endpoints.Add(endpointSpecs.Select(ToEndpointDescriptor));
                return revisionSpec;
            },
            (scopeId, serviceId, revisionId, expectedDeploymentId) =>
                new ScopeBindingUpsertResult(
                    scopeId,
                    serviceId,
                    displayName,
                    revisionId,
                    ScopeBindingImplementationKind.GAgent,
                    $"gagent-service:static-runtime:{expectedDeploymentId}",
                    GAgent: new ScopeBindingGAgentResult(
                        diagnosticClrTypeName),
                    ExpectedDeploymentId: expectedDeploymentId));
    }

    private string NormalizeGAgentKind(ScopeBindingGAgentSpec gagent)
    {
        var agentKind = ScopeWorkflowCapabilityConventions.NormalizeOptional(gagent.AgentKind);
        if (!string.IsNullOrWhiteSpace(agentKind))
            return agentKind;

        ScopeWorkflowCapabilityOptions.NormalizeRequired(string.Empty, nameof(gagent.AgentKind));
        throw new InvalidOperationException("gagent agentKind is required.");
    }

    private string ResolveDiagnosticClrTypeName(string agentKind)
    {
        if (_agentKindRegistry != null)
        {
            var implementation = _agentKindRegistry.Resolve(agentKind);
            return implementation.Metadata.ImplementationClrTypeName;
        }

        return string.Empty;
    }

    private async Task<WorkflowYamlBundle> ParseWorkflowBundleAsync(
        IReadOnlyList<string>? workflowYamls,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workflowYamls);
        if (workflowYamls.Count == 0)
            throw new InvalidOperationException("workflowYamls is required.");

        string? entryWorkflowName = null;
        string? entryWorkflowYaml = null;
        var subWorkflowYamls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seenWorkflowNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < workflowYamls.Count; index++)
        {
            var workflowYaml = ScopeWorkflowCapabilityConventions.NormalizeOptional(workflowYamls[index]);
            if (string.IsNullOrWhiteSpace(workflowYaml))
                throw new InvalidOperationException("workflowYamls must not contain empty YAML entries.");

            var parse = await _workflowDefinitionParser.ParseWorkflowYamlForPublicationAsync(workflowYaml, ct);
            if (!parse.Succeeded)
                throw new InvalidOperationException(parse.Error);

            var workflowName = ScopeWorkflowCapabilityConventions.NormalizeOptional(parse.WorkflowName);
            if (string.IsNullOrWhiteSpace(workflowName))
                throw new InvalidOperationException("workflowYamls must define a workflow name.");
            if (!seenWorkflowNames.Add(workflowName))
                throw new InvalidOperationException($"Duplicate workflow name '{workflowName}' in workflowYamls.");

            if (index == 0)
            {
                entryWorkflowName = workflowName;
                entryWorkflowYaml = workflowYaml;
                continue;
            }

            subWorkflowYamls[workflowName] = workflowYaml;
        }

        return new WorkflowYamlBundle(
            entryWorkflowName ?? throw new InvalidOperationException("workflowYamls must include a root workflow."),
            entryWorkflowYaml ?? throw new InvalidOperationException("workflowYamls must include a root workflow YAML."),
            subWorkflowYamls);
    }

    private static bool ServiceDefinitionNeedsUpdate(
        ServiceCatalogSnapshot existingService,
        ServiceDefinitionSpec desiredDefinition)
    {
        if (!string.Equals(existingService.DisplayName, desiredDefinition.DisplayName, StringComparison.Ordinal))
            return true;

        if (desiredDefinition.ExternalExposure?.ExposureDesired == true &&
            existingService.ExternalExposure?.ExposureDesired != true)
        {
            return true;
        }

        var existingEndpoints = existingService.Endpoints
            .OrderBy(x => x.EndpointId, StringComparer.Ordinal)
            .ToArray();
        var desiredEndpoints = desiredDefinition.Endpoints
            .OrderBy(x => x.EndpointId, StringComparer.Ordinal)
            .ToArray();
        if (existingEndpoints.Length != desiredEndpoints.Length)
            return true;

        for (var index = 0; index < existingEndpoints.Length; index++)
        {
            if (!string.Equals(existingEndpoints[index].EndpointId, desiredEndpoints[index].EndpointId, StringComparison.Ordinal) ||
                !string.Equals(existingEndpoints[index].DisplayName, desiredEndpoints[index].DisplayName, StringComparison.Ordinal) ||
                !string.Equals(existingEndpoints[index].Kind, desiredEndpoints[index].Kind.ToString(), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(existingEndpoints[index].RequestTypeUrl, desiredEndpoints[index].RequestTypeUrl, StringComparison.Ordinal) ||
                !string.Equals(existingEndpoints[index].ResponseTypeUrl, desiredEndpoints[index].ResponseTypeUrl, StringComparison.Ordinal) ||
                !string.Equals(existingEndpoints[index].Description, desiredEndpoints[index].Description, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static ServiceDefinitionSpec CloneServiceDefinition(ServiceDefinitionSpec source)
    {
        var clone = new ServiceDefinitionSpec
        {
            Identity = source.Identity.Clone(),
            DisplayName = source.DisplayName,
        };
        clone.Endpoints.Add(source.Endpoints.Select(CloneEndpointSpec));
        clone.PolicyIds.Add(source.PolicyIds);
        if (source.ExternalExposure != null)
            clone.ExternalExposure = source.ExternalExposure.Clone();
        return clone;
    }

    private static ServiceEndpointSpec CloneEndpointSpec(ServiceEndpointSpec spec) =>
        new()
        {
            EndpointId = spec.EndpointId,
            DisplayName = spec.DisplayName,
            Kind = spec.Kind,
            RequestTypeUrl = spec.RequestTypeUrl,
            ResponseTypeUrl = spec.ResponseTypeUrl,
            Description = spec.Description,
        };

    private static ServiceEndpointSpec ToServiceEndpointSpec(ScopeBindingGAgentEndpoint endpoint) =>
        new()
        {
            EndpointId = ScopeWorkflowCapabilityOptions.NormalizeRequired(endpoint.EndpointId, nameof(endpoint.EndpointId)),
            DisplayName = ScopeWorkflowCapabilityConventions.NormalizeOptional(endpoint.DisplayName) ?? endpoint.EndpointId.Trim(),
            Kind = endpoint.Kind,
            RequestTypeUrl = ScopeWorkflowCapabilityConventions.NormalizeOptional(endpoint.RequestTypeUrl) ?? string.Empty,
            ResponseTypeUrl = ScopeWorkflowCapabilityConventions.NormalizeOptional(endpoint.ResponseTypeUrl) ?? string.Empty,
            Description = ScopeWorkflowCapabilityConventions.NormalizeOptional(endpoint.Description) ?? string.Empty,
        };

    private static ServiceEndpointDescriptor ToEndpointDescriptor(ServiceEndpointSpec spec) =>
        new()
        {
            EndpointId = spec.EndpointId,
            DisplayName = spec.DisplayName,
            Kind = spec.Kind,
            RequestTypeUrl = spec.RequestTypeUrl,
            ResponseTypeUrl = spec.ResponseTypeUrl,
            Description = spec.Description,
        };

    private static ServiceSourcePackageSpec ToServicePackage(ScriptPackageSpec packageSpec)
    {
        var result = new ServiceSourcePackageSpec
        {
            EntryBehaviorTypeName = packageSpec.EntryBehaviorTypeName ?? string.Empty,
            EntrySourcePath = packageSpec.EntrySourcePath ?? string.Empty,
        };
        result.CsharpSources.Add(packageSpec.CsharpSources.Select(x => new ServicePackageFile
        {
            Path = x.Path ?? string.Empty,
            Content = x.Content ?? string.Empty,
        }));
        result.ProtoFiles.Add(packageSpec.ProtoFiles.Select(x => new ServicePackageFile
        {
            Path = x.Path ?? string.Empty,
            Content = x.Content ?? string.Empty,
        }));
        return result;
    }

    private static ServiceEndpointSpec[] BuildScriptEndpointSpecs(ScriptDefinitionSnapshot snapshot)
    {
        var endpoints = snapshot.RuntimeSemantics?.Messages
            .Where(x => x.Kind == ScriptMessageKind.Command)
            .Select(x =>
            {
                var endpointId = string.IsNullOrWhiteSpace(x.DescriptorFullName)
                    ? x.TypeUrl ?? string.Empty
                    : x.DescriptorFullName;
                return new ServiceEndpointSpec
                {
                    EndpointId = endpointId,
                    DisplayName = endpointId,
                    Kind = ServiceEndpointKind.Command,
                    RequestTypeUrl = x.TypeUrl ?? string.Empty,
                    ResponseTypeUrl = string.Empty,
                    Description = $"Scripting command endpoint for {endpointId}.",
                };
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.EndpointId))
            .ToArray()
            ?? [];
        if (endpoints.Length == 0)
        {
            throw new InvalidOperationException(
                $"Script '{snapshot.ScriptId}' revision '{snapshot.Revision}' does not declare command endpoints.");
        }

        return endpoints;
    }

    private static ServiceEndpointSpec BuildChatEndpointSpec() =>
        new()
        {
            EndpointId = "chat",
            DisplayName = "chat",
            Kind = ServiceEndpointKind.Chat,
            RequestTypeUrl = GetTypeUrl(ChatRequestEvent.Descriptor),
            ResponseTypeUrl = GetTypeUrl(ChatResponseEvent.Descriptor),
            Description = "Default chat endpoint.",
        };

    private static string GetTypeUrl(MessageDescriptor descriptor) =>
        $"type.googleapis.com/{descriptor.FullName}";

    private sealed record WorkflowYamlBundle(
        string EntryWorkflowName,
        string EntryWorkflowYaml,
        IReadOnlyDictionary<string, string> SubWorkflowYamls);

    private sealed record DesiredScopeBinding(
        ServiceDefinitionSpec ServiceDefinition,
        Func<ServiceIdentity, string, ServiceRevisionSpec> BuildRevision,
        Func<string, string, string, string, ScopeBindingUpsertResult> BuildResult);

    private sealed class ServiceCommandExternalExposureIntentPort(IServiceCommandPort commandPort) : IServiceExternalExposureIntentPort
    {
        public Task ApplyAsync(
            ServiceExternalExposureIntentRequest request,
            CancellationToken ct = default)
        {
            if (request.ExposureDesired || request.ExistingService == null)
                return Task.CompletedTask;

            return commandPort.RetireExternalExposureAsync(new RetireExternalExposureCommand
            {
                Identity = request.Identity.Clone(),
                DesiredSpecHash = request.ExistingService.ExternalExposure?.DesiredSpecHash ?? string.Empty,
            }, ct);
        }
    }
}
