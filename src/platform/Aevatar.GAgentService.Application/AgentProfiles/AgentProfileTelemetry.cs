using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Aevatar.GAgentService.Application.AgentProfiles;

public static class AgentProfileTelemetry
{
    public const string InstrumentationName = "Aevatar.GAgentService.AgentProfiles";

    public static readonly ActivitySource ActivitySource = new(InstrumentationName, "1.0.0");

    public static readonly Meter Meter = new(InstrumentationName, "1.0.0");

    private static readonly Counter<long> Operations = Meter.CreateCounter<long>(
        "aevatar.agent_profile.operations",
        description: "Agent Profile operations by ingress, operation, outcome, failure class, and activation mode.");

    private static readonly Counter<long> RequiredSystemReadiness = Meter.CreateCounter<long>(
        "aevatar.agent_profile.required_system_readiness",
        description: "Required system Agent Profile readiness observations.");

    public static Activity? StartOperation(string operation, string ingress)
    {
        var activity = ActivitySource.StartActivity(operation, ActivityKind.Internal);
        activity?.SetTag("agent_profile.operation", operation);
        activity?.SetTag("agent_profile.ingress", ingress);
        return activity;
    }

    public static void RecordOperation(
        string ingress,
        string operation,
        string outcome,
        string failureClass = "none",
        string activationMode = "unspecified") =>
        Operations.Add(
            1,
            new KeyValuePair<string, object?>("ingress", ingress),
            new KeyValuePair<string, object?>("operation", operation),
            new KeyValuePair<string, object?>("outcome", outcome),
            new KeyValuePair<string, object?>("failure.class", failureClass),
            new KeyValuePair<string, object?>("activation.mode", activationMode));

    public static void RecordRequiredSystemReadiness(string readiness) =>
        RequiredSystemReadiness.Add(
            1,
            new KeyValuePair<string, object?>("required_system.readiness", readiness));
}
