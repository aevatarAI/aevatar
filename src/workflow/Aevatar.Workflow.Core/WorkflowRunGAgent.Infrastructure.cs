using System.Globalization;
using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core;

public sealed partial class WorkflowRunGAgent
{
    private Task LogWarningAsync(Exception? ex, string message, object?[] args)
    {
        if (ex == null)
            Logger.LogWarning(message, args);
        else
            Logger.LogWarning(ex, message, args);

        return Task.CompletedTask;
    }

    private async Task<string> ResolveWorkflowYamlCoreAsync(string workflowName, CancellationToken ct)
    {
        foreach (var (registeredName, yaml) in State.InlineWorkflowYamls)
        {
            if (string.Equals(registeredName, workflowName, StringComparison.OrdinalIgnoreCase))
                return yaml;
        }

        var resolver = _workflowDefinitionResolver ?? Services?.GetService<IWorkflowDefinitionResolver>();
        if (resolver == null)
            throw new InvalidOperationException("workflow_call requires IWorkflowDefinitionResolver service registration.");

        var yamlFromResolver = await resolver.GetWorkflowYamlAsync(workflowName, ct);
        if (string.IsNullOrWhiteSpace(yamlFromResolver))
            throw new InvalidOperationException($"workflow_call references unregistered workflow '{workflowName}'");
        return yamlFromResolver;
    }

    private EventEnvelope CreateWorkflowDefinitionBindEnvelopeCore(string workflowYaml, string workflowName)
    {
        var bind = new BindWorkflowDefinitionEvent
        {
            WorkflowYaml = workflowYaml ?? string.Empty,
            WorkflowName = workflowName ?? string.Empty,
        };
        foreach (var (name, yaml) in State.InlineWorkflowYamls)
            bind.InlineWorkflowYamls[name] = yaml;

        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(bind),
            PublisherId = Id,
            Direction = EventDirection.Self,
            CorrelationId = Guid.NewGuid().ToString("N"),
        };
    }

    private EventEnvelope CreateRoleAgentInitializeEnvelopeCore(RoleDefinition role)
    {
        var initialize = new InitializeRoleAgentEvent
        {
            RoleName = role.Name ?? string.Empty,
            ProviderName = string.IsNullOrWhiteSpace(role.Provider) ? string.Empty : role.Provider,
            Model = string.IsNullOrWhiteSpace(role.Model) ? string.Empty : role.Model,
            SystemPrompt = role.SystemPrompt ?? string.Empty,
            MaxTokens = role.MaxTokens ?? 0,
            MaxToolRounds = role.MaxToolRounds ?? 0,
            MaxHistoryMessages = role.MaxHistoryMessages ?? 0,
            StreamBufferCapacity = role.StreamBufferCapacity ?? 0,
        };
        if (role.Temperature.HasValue)
            initialize.Temperature = role.Temperature.Value;

        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(initialize),
            PublisherId = Id,
            Direction = EventDirection.Self,
            CorrelationId = Guid.NewGuid().ToString("N"),
        };
    }
}
