using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Application.Internal;
using Aevatar.GAgentService.Application.Workflows;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Ports;
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
    private readonly IScopeScriptQueryPort _scopeScriptQueryPort;
    private readonly IScriptDefinitionSnapshotPort _scriptDefinitionSnapshotPort;
    private readonly IWorkflowRunActorPort _workflowRunActorPort;
    private readonly ScopeWorkflowCapabilityOptions _options;

    public ScopeBindingCommandApplicationService(
        IServiceCommandPort serviceCommandPort,
        IServiceLifecycleQueryPort serviceLifecycleQueryPort,
        IServiceGovernanceCommandPort serviceGovernanceCommandPort,
        IServiceGovernanceQueryPort serviceGovernanceQueryPort,
        IScopeScriptQueryPort scopeScriptQueryPort,
        IScriptDefinitionSnapshotPort scriptDefinitionSnapshotPort,
        IWorkflowRunActorPort workflowRunActorPort,
        IOptions<ScopeWorkflowCapabilityOptions> options)
    {
        _serviceCommandPort = serviceCommandPort ?? throw new ArgumentNullException(nameof(serviceCommandPort));
        _serviceLifecycleQueryPort = serviceLifecycleQueryPort ?? throw new ArgumentNullException(nameof(serviceLifecycleQueryPort));
        _serviceGovernanceCommandPort = serviceGovernanceCommandPort ?? throw new ArgumentNullException(nameof(serviceGovernanceCommandPort));
        _serviceGovernanceQueryPort = serviceGovernanceQueryPort ?? throw new ArgumentNullException(nameof(serviceGovernanceQueryPort));
        _scopeScriptQueryPort = scopeScriptQueryPort ?? throw new ArgumentNullException(nameof(scopeScriptQueryPort));
        _scriptDefinitionSnapshotPort = scriptDefinitionSnapshotPort ?? throw new ArgumentNullException(nameof(scriptDefinitionSnapshotPort));
        _workflowRunActorPort = workflowRunActorPort ?? throw new ArgumentNullException(nameof(workflowRunActorPort));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new InvalidOperationException("Scope workflow capability options are required.");
    }

    public async Task<ScopeBindingUpsertResult> UpsertAsync(
        ScopeBindingUpsertRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedScopeId = ScopeWorkflowCapabilityOptions.NormalizeRequired(request.ScopeId, nameof(request.ScopeId));
        var identity = ScopeWorkflowCapabilityConventions.BuildDefaultServiceIdentity(_options, normalizedScopeId, request.AppId);
        var desiredBinding = await ResolveDesiredBindingAsync(request, normalizedScopeId, identity, ct);
        var existingService = await _serviceLifecycleQueryPort.GetServiceAsync(identity, ct);

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

        var revisionId = ScopeWorkflowCapabilityConventions.ResolveRevisionId(request.RevisionId);
        var revisionSpec = desiredBinding.BuildRevision(identity, revisionId);

        if (await ShouldCreateRevisionAsync(request, revisionSpec, ct))
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
        return desiredBinding.BuildResult(normalizedScopeId, identity.ServiceId, revisionId, expectedDeploymentId);
    }

    private async Task<bool> ShouldCreateRevisionAsync(
        ScopeBindingUpsertRequest request,
        ServiceRevisionSpec revisionSpec,
        CancellationToken ct)
    {
        if (request.ImplementationKind != ScopeBindingImplementationKind.Scripting)
            return true;

        var requestedRevisionId = ScopeWorkflowCapabilityConventions.NormalizeOptional(request.RevisionId);
        if (string.IsNullOrWhiteSpace(requestedRevisionId))
            return true;

        var identity = revisionSpec.Identity
            ?? throw new InvalidOperationException("service identity is required.");
        var revisionId = ScopeWorkflowCapabilityOptions.NormalizeRequired(revisionSpec.RevisionId, nameof(revisionSpec.RevisionId));
        var revisions = await _serviceLifecycleQueryPort.GetServiceRevisionsAsync(identity, ct);
        var existingRevision = revisions?.Revisions.FirstOrDefault(x =>
            string.Equals(x.RevisionId, revisionId, StringComparison.Ordinal));
        if (existingRevision == null)
            return true;

        if (!string.Equals(existingRevision.ImplementationKind, ServiceImplementationKind.Scripting.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Revision '{revisionId}' already exists for service '{ServiceKeys.Build(identity)}' with implementation '{existingRevision.ImplementationKind}'.");
        }

        if (string.Equals(existingRevision.Status, ServiceRevisionStatus.Retired.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Revision '{revisionId}' already exists for service '{ServiceKeys.Build(identity)}' but has been retired.");
        }

        var expectedArtifactHash = await ComputeScriptingArtifactHashAsync(revisionSpec, ct);
        if (!string.Equals(existingRevision.ArtifactHash, expectedArtifactHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Revision '{revisionId}' already exists for service '{ServiceKeys.Build(identity)}' but points to a different scripting artifact.");
        }

        return false;
    }

    private async Task<string> ComputeScriptingArtifactHashAsync(
        ServiceRevisionSpec revisionSpec,
        CancellationToken ct)
    {
        var identity = revisionSpec.Identity
            ?? throw new InvalidOperationException("service identity is required.");
        var scriptingSpec = revisionSpec.ScriptingSpec
            ?? throw new InvalidOperationException("scripting implementation_spec is required.");
        if (string.IsNullOrWhiteSpace(scriptingSpec.DefinitionActorId))
            throw new InvalidOperationException("scripting definition_actor_id is required.");

        var snapshot = await _scriptDefinitionSnapshotPort.GetRequiredAsync(
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
        var normalizedArtifact = artifact.Clone();
        normalizedArtifact.ArtifactHash = string.Empty;
        return Convert.ToHexString(SHA256.HashData(normalizedArtifact.ToByteArray()));
    }

    private async Task<DesiredScopeBinding> ResolveDesiredBindingAsync(
        ScopeBindingUpsertRequest request,
        string normalizedScopeId,
        ServiceIdentity identity,
        CancellationToken ct)
    {
        return request.ImplementationKind switch
        {
            ScopeBindingImplementationKind.Workflow =>
                await BuildWorkflowBindingAsync(request, normalizedScopeId, identity, ct),
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
        CancellationToken ct)
    {
        var workflowBundle = await ParseWorkflowBundleAsync(request.Workflow?.WorkflowYamls, ct);
        var definitionActorIdPrefix = ScopeWorkflowCapabilityConventions.BuildDefaultDefinitionActorIdPrefix(_options, normalizedScopeId);
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
                        WorkflowName = workflowBundle.EntryWorkflowName,
                        WorkflowYaml = workflowBundle.EntryWorkflowYaml,
                        DefinitionActorId = definitionActorIdPrefix,
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
                        workflowBundle.EntryWorkflowName,
                        definitionActorIdPrefix)));
    }

    private async Task<DesiredScopeBinding> BuildScriptBindingAsync(
        ScopeBindingUpsertRequest request,
        string normalizedScopeId,
        ServiceIdentity identity,
        CancellationToken ct)
    {
        var script = request.Script
            ?? throw new InvalidOperationException("script is required for implementationKind 'scripting'.");
        var normalizedScriptId = ScopeWorkflowCapabilityOptions.NormalizeRequired(script.ScriptId, nameof(script.ScriptId));
        var scriptSummary = await _scopeScriptQueryPort.GetByScriptIdAsync(normalizedScopeId, normalizedScriptId, ct)
            ?? throw new InvalidOperationException(
                $"Scope '{normalizedScopeId}' does not have an active script '{normalizedScriptId}'.");
        var requestedScriptRevision = ScopeWorkflowCapabilityConventions.NormalizeOptional(script.ScriptRevision);
        if (!string.IsNullOrWhiteSpace(requestedScriptRevision) &&
            !string.Equals(requestedScriptRevision, scriptSummary.ActiveRevision, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Scope script '{normalizedScriptId}' is currently at revision '{scriptSummary.ActiveRevision}', but got '{requestedScriptRevision}'.");
        }

        var snapshot = await _scriptDefinitionSnapshotPort.GetRequiredAsync(
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
                        scriptSummary.DefinitionActorId)));
    }

    private DesiredScopeBinding BuildGAgentBinding(
        ScopeBindingUpsertRequest request,
        ServiceIdentity identity)
    {
        var gagent = request.GAgent
            ?? throw new InvalidOperationException("gagent is required for implementationKind 'gagent'.");
        var actorTypeName = ScopeWorkflowCapabilityOptions.NormalizeRequired(gagent.ActorTypeName, nameof(gagent.ActorTypeName));

        // Start with caller-supplied endpoints, then ensure a chat endpoint always exists.
        var endpointSpecs = (gagent.Endpoints ?? [])
            .Select(ToServiceEndpointSpec)
            .ToList();
        if (!endpointSpecs.Any(e => string.Equals(e.EndpointId, "chat", StringComparison.OrdinalIgnoreCase)))
            endpointSpecs.Insert(0, BuildChatEndpointSpec());
        var displayName = ScopeWorkflowCapabilityConventions.ResolveDisplayName(
            request.DisplayName,
            actorTypeName.Split(',')[0]);
        var preferredActorId = ScopeWorkflowCapabilityConventions.NormalizeOptional(gagent.PreferredActorId) ?? string.Empty;
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
                        ActorTypeName = actorTypeName,
                        PreferredActorId = preferredActorId,
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
                    string.IsNullOrWhiteSpace(preferredActorId)
                        ? $"gagent-service:static-runtime:{expectedDeploymentId}"
                        : $"{preferredActorId}:{expectedDeploymentId}",
                    GAgent: new ScopeBindingGAgentResult(
                        actorTypeName,
                        preferredActorId)));
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

            var parse = await _workflowRunActorPort.ParseWorkflowYamlAsync(workflowYaml, ct);
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
}
