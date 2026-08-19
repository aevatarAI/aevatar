using Aevatar.Foundation.Abstractions.Runtime;
using Google.Protobuf.Reflection;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Typed rules for who may write <see cref="ProjectionScopeStatusDocument"/> for a source
/// projection scope and which committed source events are terminal outcomes.
/// </summary>
internal static class ProjectionScopeStatusRoutePolicy
{
    // Intermediate bookkeeping facts of the source scope. They stay durable source-scope facts
    // and count into the next terminal status write; on their own they cause no status write.
    private static readonly HashSet<string> IntermediateEventTypeNames = new(StringComparer.Ordinal)
    {
        ProjectionScopeEnvelopeReceivedEvent.Descriptor.FullName,
        ProjectionScopeEnvelopeAttemptedEvent.Descriptor.FullName,
        ProjectionScopeObservationStagedEvent.Descriptor.FullName,
    };

    public static bool IsTerminalRoute(ProjectionScopeStatusRoute? route) =>
        route != null &&
        route.RouteEpoch > 0 &&
        route.ContractVersion > 0 &&
        !string.IsNullOrWhiteSpace(route.ContractId);

    public static bool IsActiveTerminalRoute(
        ProjectionScopeStatusRoute? route,
        string contractId,
        long contractVersion) =>
        IsTerminalRoute(route) &&
        string.Equals(route!.ContractId, contractId, StringComparison.Ordinal) &&
        route.ContractVersion == contractVersion;

    /// <summary>
    /// A committed source event whose outcome must be visible on the status document:
    /// everything except the per-envelope received/attempted/staged bookkeeping.
    /// </summary>
    public static bool IsTerminalOutcome(MessageDescriptor? eventDescriptor) =>
        eventDescriptor != null &&
        !IntermediateEventTypeNames.Contains(eventDescriptor.FullName);

    public static bool IsTerminalOutcome(Google.Protobuf.WellKnownTypes.Any? eventData)
    {
        if (eventData == null || string.IsNullOrEmpty(eventData.TypeUrl))
            return false;

        var slash = eventData.TypeUrl.LastIndexOf('/');
        var typeName = slash < 0 ? eventData.TypeUrl : eventData.TypeUrl[(slash + 1)..];
        return !IntermediateEventTypeNames.Contains(typeName);
    }

    public static ProjectionScopeStatusRoute BuildTerminalRoute(long routeEpoch) =>
        new()
        {
            ContractId = RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalV1,
            ContractVersion = RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalReaderVersion,
            RouteEpoch = routeEpoch,
        };
}
