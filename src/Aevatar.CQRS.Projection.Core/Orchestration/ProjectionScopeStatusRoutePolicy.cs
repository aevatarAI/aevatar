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

    /// <summary>
    /// Route contract that selects the legacy event-sourced status shadow as the writer again
    /// (durable rollback). Absent route == legacy writer as well (pre-route sources).
    /// </summary>
    public const string LegacyContractId = "aevatar.projection.scope-status-legacy.v1";
    public const long LegacyContractVersion = 1;

    private static bool IsRoute(ProjectionScopeStatusRoute? route) =>
        route != null &&
        route.RouteEpoch > 0 &&
        route.ContractVersion > 0 &&
        !string.IsNullOrWhiteSpace(route.ContractId);

    /// <summary>A route selecting the terminal materializer contract, in any cutover phase.</summary>
    public static bool IsTerminalRoute(ProjectionScopeStatusRoute? route) =>
        IsRoute(route) &&
        string.Equals(
            route!.ContractId,
            RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalV1,
            StringComparison.Ordinal);

    /// <summary>A route selecting the legacy shadow again (rollback), in any cutover phase.</summary>
    public static bool IsLegacyRoute(ProjectionScopeStatusRoute? route) =>
        IsRoute(route) &&
        string.Equals(route!.ContractId, LegacyContractId, StringComparison.Ordinal);

    /// <summary>
    /// Routes committed by binaries without cutover phases are ACTIVE by definition.
    /// </summary>
    public static bool IsWritingPhase(ProjectionScopeStatusRoute route) =>
        route.Phase is ProjectionScopeStatusRoutePhase.Unspecified
            or ProjectionScopeStatusRoutePhase.Blocked
            or ProjectionScopeStatusRoutePhase.Active;

    /// <summary>
    /// The terminal materializer of <paramref name="contractId"/> with reader version
    /// <paramref name="contractVersion"/> is the writer: a terminal route at or below its
    /// reader version (a v2 reader keeps writing v1 routes) in a writing phase (ACTIVE, or
    /// BLOCKED for the epoch-fenced same-version takeover, or a phase-less legacy-binary route).
    /// </summary>
    public static bool IsActiveTerminalRoute(
        ProjectionScopeStatusRoute? route,
        string contractId,
        long contractVersion) =>
        IsTerminalRoute(route) &&
        string.Equals(route!.ContractId, contractId, StringComparison.Ordinal) &&
        route.ContractVersion <= contractVersion &&
        IsWritingPhase(route);

    /// <summary>The terminal route is warming: the materializer observes and reports, never writes.</summary>
    public static bool IsWarmingTerminalRoute(
        ProjectionScopeStatusRoute? route,
        string contractId,
        long contractVersion) =>
        IsTerminalRoute(route) &&
        string.Equals(route!.ContractId, contractId, StringComparison.Ordinal) &&
        route.ContractVersion <= contractVersion &&
        route.Phase == ProjectionScopeStatusRoutePhase.Warming;

    /// <summary>
    /// The legacy event-sourced status shadow is the writer: no route, a legacy (rollback)
    /// route in a writing phase, or a terminal route that is still warming.
    /// </summary>
    public static bool LegacyShadowMayWrite(ProjectionScopeStatusRoute? route) =>
        route == null ||
        !IsRoute(route) ||
        (IsLegacyRoute(route) && IsWritingPhase(route)) ||
        (IsTerminalRoute(route) && route.Phase == ProjectionScopeStatusRoutePhase.Warming);

    /// <summary>
    /// The legacy shadow has been superseded and must release itself: a terminal route in a
    /// writing phase (blocked or active).
    /// </summary>
    public static bool LegacyShadowIsSuperseded(ProjectionScopeStatusRoute? route) =>
        IsTerminalRoute(route) && IsWritingPhase(route!);

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

    public static ProjectionScopeStatusRoute BuildTerminalRoute(
        long routeEpoch,
        ProjectionScopeStatusRoutePhase phase = ProjectionScopeStatusRoutePhase.Active) =>
        new()
        {
            ContractId = RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalV1,
            ContractVersion = RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalReaderVersion,
            RouteEpoch = routeEpoch,
            Phase = phase,
        };

    public static ProjectionScopeStatusRoute BuildLegacyRoute(
        long routeEpoch,
        ProjectionScopeStatusRoutePhase phase = ProjectionScopeStatusRoutePhase.Active) =>
        new()
        {
            ContractId = LegacyContractId,
            ContractVersion = LegacyContractVersion,
            RouteEpoch = routeEpoch,
            Phase = phase,
        };
}
