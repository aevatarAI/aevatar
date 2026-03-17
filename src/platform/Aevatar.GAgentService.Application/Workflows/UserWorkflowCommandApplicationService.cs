using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Services;
using Google.Protobuf.Reflection;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Application.Workflows;

public sealed class UserWorkflowCommandApplicationService : IUserWorkflowCommandPort
{
    private readonly IServiceCommandPort _serviceCommandPort;
    private readonly IServiceLifecycleQueryPort _serviceLifecycleQueryPort;
    private readonly IUserWorkflowQueryPort _userWorkflowQueryPort;
    private readonly UserWorkflowCapabilityOptions _options;

    public UserWorkflowCommandApplicationService(
        IServiceCommandPort serviceCommandPort,
        IServiceLifecycleQueryPort serviceLifecycleQueryPort,
        IUserWorkflowQueryPort userWorkflowQueryPort,
        IOptions<UserWorkflowCapabilityOptions> options)
    {
        _serviceCommandPort = serviceCommandPort ?? throw new ArgumentNullException(nameof(serviceCommandPort));
        _serviceLifecycleQueryPort = serviceLifecycleQueryPort ?? throw new ArgumentNullException(nameof(serviceLifecycleQueryPort));
        _userWorkflowQueryPort = userWorkflowQueryPort ?? throw new ArgumentNullException(nameof(userWorkflowQueryPort));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new InvalidOperationException("User workflow capability options are required.");
    }

    public async Task<UserWorkflowUpsertResult> UpsertAsync(
        UserWorkflowUpsertRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedUserId = UserWorkflowCapabilityOptions.NormalizeRequired(request.UserId, nameof(request.UserId));
        var normalizedWorkflowId = UserWorkflowCapabilityConventions.NormalizeWorkflowId(request.WorkflowId);
        var workflowYaml = UserWorkflowCapabilityOptions.NormalizeRequired(request.WorkflowYaml, nameof(request.WorkflowYaml));
        var identity = UserWorkflowCapabilityConventions.BuildIdentity(_options, normalizedUserId, normalizedWorkflowId);
        var definitionActorIdPrefix = _options.BuildDefinitionActorIdPrefix(normalizedUserId, normalizedWorkflowId);
        var desiredDisplayName = UserWorkflowCapabilityConventions.ResolveDisplayName(request.DisplayName, normalizedWorkflowId);
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

        var revisionId = UserWorkflowCapabilityConventions.ResolveRevisionId(request.RevisionId);
        var revisionSpec = new ServiceRevisionSpec
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
            ImplementationKind = ServiceImplementationKind.Workflow,
            WorkflowSpec = new WorkflowServiceRevisionSpec
            {
                WorkflowName = UserWorkflowCapabilityConventions.NormalizeOptional(request.WorkflowName),
                WorkflowYaml = workflowYaml,
                DefinitionActorId = definitionActorIdPrefix,
            },
        };
        UserWorkflowCapabilityConventions.AddInlineWorkflowYamls(revisionSpec.WorkflowSpec.InlineWorkflowYamls, request.InlineWorkflowYamls);

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
            await _userWorkflowQueryPort.GetByWorkflowIdAsync(normalizedUserId, normalizedWorkflowId, ct) ??
            new UserWorkflowSummary(
                normalizedUserId,
                normalizedWorkflowId,
                desiredDisplayName,
                ServiceKeys.Build(identity),
                UserWorkflowCapabilityConventions.NormalizeOptional(request.WorkflowName),
                expectedActorId,
                revisionId,
                expectedDeploymentId,
                "active",
                DateTimeOffset.UtcNow);

        return new UserWorkflowUpsertResult(
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
