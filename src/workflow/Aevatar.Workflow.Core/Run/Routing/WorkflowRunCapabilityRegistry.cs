using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Core.Primitives;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowRunCapabilityRegistry
{
    private readonly IReadOnlyDictionary<string, IWorkflowRunCapability> _stepHandlersByType;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<IWorkflowRunCapability>> _internalSignalHandlersByTypeUrl;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<IWorkflowRunCapability>> _responseHandlersByTypeUrl;

    public WorkflowRunCapabilityRegistry(IEnumerable<IWorkflowRunCapability> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        var capabilityList = capabilities.ToArray();
        if (capabilityList.Length == 0)
            throw new InvalidOperationException("Workflow run capability registry requires at least one capability.");

        Capabilities = capabilityList;
        _stepHandlersByType = BuildStepHandlerMap(capabilityList);
        _internalSignalHandlersByTypeUrl = BuildGroupedRouteMap(capabilityList, x => x.Descriptor.SupportedInternalSignalTypeUrls);
        _responseHandlersByTypeUrl = BuildGroupedRouteMap(capabilityList, x => x.Descriptor.SupportedResponseTypeUrls);
    }

    public IReadOnlyList<IWorkflowRunCapability> Capabilities { get; }

    public bool TryGetStepCapability(string stepType, out IWorkflowRunCapability capability)
    {
        var canonicalType = WorkflowPrimitiveCatalog.ToCanonicalType(stepType);
        return _stepHandlersByType.TryGetValue(canonicalType, out capability!);
    }

    public IReadOnlyList<IWorkflowRunCapability> GetInternalSignalCandidates(Any? payload)
    {
        if (payload == null || string.IsNullOrWhiteSpace(payload.TypeUrl))
            return Array.Empty<IWorkflowRunCapability>();

        return _internalSignalHandlersByTypeUrl.TryGetValue(payload.TypeUrl, out var handlers)
            ? handlers
            : Array.Empty<IWorkflowRunCapability>();
    }

    public IReadOnlyList<IWorkflowRunCapability> GetResponseCandidates(Any? payload)
    {
        if (payload == null || string.IsNullOrWhiteSpace(payload.TypeUrl))
            return Array.Empty<IWorkflowRunCapability>();

        return _responseHandlersByTypeUrl.TryGetValue(payload.TypeUrl, out var handlers)
            ? handlers
            : Array.Empty<IWorkflowRunCapability>();
    }

    private static IReadOnlyDictionary<string, IWorkflowRunCapability> BuildStepHandlerMap(
        IReadOnlyList<IWorkflowRunCapability> capabilities)
    {
        var mapping = new Dictionary<string, IWorkflowRunCapability>(StringComparer.OrdinalIgnoreCase);
        foreach (var capability in capabilities)
        {
            foreach (var rawStepType in capability.Descriptor.SupportedStepTypes)
            {
                var stepType = WorkflowPrimitiveCatalog.ToCanonicalType(rawStepType);
                if (string.IsNullOrWhiteSpace(stepType))
                    throw new InvalidOperationException($"{capability.Descriptor.Name} declares an empty workflow step type.");
                if (!mapping.TryAdd(stepType, capability))
                {
                    throw new InvalidOperationException(
                        $"Duplicate workflow capability registration for step type '{stepType}'.");
                }
            }
        }

        return mapping;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<IWorkflowRunCapability>> BuildGroupedRouteMap(
        IReadOnlyList<IWorkflowRunCapability> capabilities,
        Func<IWorkflowRunCapability, IReadOnlyCollection<string>> routeSelector)
    {
        var mapping = new Dictionary<string, List<IWorkflowRunCapability>>(StringComparer.Ordinal);
        foreach (var capability in capabilities)
        {
            foreach (var route in routeSelector(capability))
            {
                if (string.IsNullOrWhiteSpace(route))
                    throw new InvalidOperationException($"{capability.Descriptor.Name} declares an empty route.");

                if (!mapping.TryGetValue(route, out var handlers))
                {
                    handlers = [];
                    mapping[route] = handlers;
                }

                handlers.Add(capability);
            }
        }

        return mapping.ToDictionary(
            x => x.Key,
            x => (IReadOnlyList<IWorkflowRunCapability>)x.Value,
            StringComparer.Ordinal);
    }
}
