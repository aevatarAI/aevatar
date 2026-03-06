using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Abstractions;
using Google.Protobuf.Collections;

namespace Aevatar.Workflow.Core.Primitives;

public sealed class WorkflowStepTargetAgentResolver
{
    private const string AgentTypeParameterName = "agent_type";
    private const string AgentIdParameterName = "agent_id";

    private readonly IActorRuntime? _runtime;
    private readonly IReadOnlyList<IWorkflowAgentTypeAliasProvider> _aliasProviders;
    private readonly ConcurrentDictionary<string, Type> _typeCache = new(StringComparer.OrdinalIgnoreCase);

    public WorkflowStepTargetAgentResolver(
        IActorRuntime runtime,
        IEnumerable<IWorkflowAgentTypeAliasProvider>? aliasProviders = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _aliasProviders = aliasProviders?.ToList() ?? [];
    }

    public WorkflowStepTargetAgentResolver(IEnumerable<IWorkflowAgentTypeAliasProvider>? aliasProviders = null)
    {
        _runtime = null;
        _aliasProviders = aliasProviders?.ToList() ?? [];
    }

    public async Task<WorkflowStepTargetAgentResolution> ResolveAsync(
        StepRequestEvent request,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(ctx);

        var agentTypeValue = TryReadParameterValue(request.Parameters, AgentTypeParameterName);
        if (!string.IsNullOrWhiteSpace(agentTypeValue))
        {
            var agentType = ResolveAgentType(agentTypeValue.Trim());
            if (!typeof(IAgent).IsAssignableFrom(agentType))
            {
                throw new InvalidOperationException(
                    $"Step '{request.StepId}' parameter '{AgentTypeParameterName}' resolved to '{agentType.FullName}', which is not an IAgent.");
            }

            var actorId = ResolveActorId(
                workflowActorId: ctx.AgentId,
                stepId: request.StepId,
                requestedActorId: TryReadParameterValue(request.Parameters, AgentIdParameterName),
                agentType: agentType);

            if (_runtime == null)
            {
                throw new InvalidOperationException(
                    $"Step '{request.StepId}' uses '{AgentTypeParameterName}', but no {nameof(IActorRuntime)} is available.");
            }

            var actor = await _runtime.GetAsync(actorId);
            if (actor == null)
                actor = await _runtime.CreateAsync(agentType, actorId, ct);
            else if (!agentType.IsAssignableFrom(actor.Agent.GetType()))
            {
                throw new InvalidOperationException(
                    $"Actor '{actorId}' already exists with agent type '{actor.Agent.GetType().FullName}', expected '{agentType.FullName}'.");
            }

            await EnsureLinkedToWorkflowActorAsync(_runtime, ctx.AgentId, actor.Id, ct);
            return WorkflowStepTargetAgentResolution.Actor(
                actor.Id,
                $"agent_type:{agentType.FullName ?? agentType.Name}");
        }

        var targetRole = request.TargetRole;
        if (!string.IsNullOrWhiteSpace(targetRole))
        {
            var roleActorId = WorkflowRoleActorIdResolver.ResolveTargetActorId(ctx.AgentId, targetRole);
            return WorkflowStepTargetAgentResolution.Actor(roleActorId, $"target_role:{targetRole}");
        }

        return WorkflowStepTargetAgentResolution.Self(ctx.AgentId);
    }

    private Type ResolveAgentType(string configuredType)
    {
        return _typeCache.GetOrAdd(configuredType, ResolveAgentTypeCore);
    }

    private Type ResolveAgentTypeCore(string configuredType)
    {
        foreach (var provider in _aliasProviders)
        {
            if (provider.TryResolve(configuredType, out var resolvedByProvider))
                return resolvedByProvider;
        }

        var directType = Type.GetType(configuredType, throwOnError: false, ignoreCase: true);
        if (directType != null)
            return directType;

        var matches = AppDomain.CurrentDomain
            .GetAssemblies()
            .SelectMany(static assembly =>
            {
                try
                {
                    return assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    return ex.Types.OfType<Type>();
                }
            })
            .Where(type =>
                string.Equals(type.FullName, configuredType, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type.Name, configuredType, StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToArray();

        if (matches.Length == 1)
            return matches[0];
        if (matches.Length > 1)
        {
            throw new InvalidOperationException(
                $"Step parameter '{AgentTypeParameterName}' value '{configuredType}' is ambiguous. Use assembly-qualified name.");
        }

        throw new InvalidOperationException(
            $"Step parameter '{AgentTypeParameterName}' value '{configuredType}' did not resolve to a loadable type.");
    }

    private static string ResolveActorId(
        string workflowActorId,
        string stepId,
        string requestedActorId,
        Type agentType)
    {
        if (!string.IsNullOrWhiteSpace(requestedActorId))
            return requestedActorId.Trim();

        var workflowToken = NormalizeActorToken(workflowActorId);
        var stepToken = NormalizeActorToken(stepId);
        var typeToken = NormalizeActorToken(agentType.FullName ?? agentType.Name);
        return $"{workflowToken}:step:{stepToken}:agent:{typeToken}";
    }

    private static string NormalizeActorToken(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "unknown";

        var value = raw.Trim();
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_' or ':')
            {
                sb.Append(ch);
            }
            else
            {
                sb.Append('_');
            }
        }

        return sb.Length == 0 ? "unknown" : sb.ToString();
    }

    private static string TryReadParameterValue(MapField<string, string> parameters, string key)
    {
        if (parameters.TryGetValue(key, out var direct))
            return direct ?? string.Empty;

        foreach (var (existingKey, value) in parameters)
        {
            if (string.Equals(existingKey, key, StringComparison.OrdinalIgnoreCase))
                return value ?? string.Empty;
        }

        return string.Empty;
    }

    private static async Task EnsureLinkedToWorkflowActorAsync(
        IActorRuntime runtime,
        string workflowActorId,
        string targetActorId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workflowActorId) ||
            string.IsNullOrWhiteSpace(targetActorId) ||
            string.Equals(workflowActorId, targetActorId, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            await runtime.LinkAsync(workflowActorId, targetActorId, ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Step parameter '{AgentTypeParameterName}' resolved actor '{targetActorId}', but failed to link it under workflow actor '{workflowActorId}'.",
                ex);
        }
    }
}

public readonly record struct WorkflowStepTargetAgentResolution(
    bool UseSelf,
    string ActorId,
    string Mode,
    string WorkerId)
{
    public static WorkflowStepTargetAgentResolution Self(string workerId) =>
        new(true, string.Empty, "self", workerId);

    public static WorkflowStepTargetAgentResolution Actor(string actorId, string mode) =>
        new(false, actorId, mode, actorId);
}
