using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.StatusDashboard;

public static class HealthProbeStoreCommands
{
    private const string PublisherActorId = "status-dashboard.scheduler";
    private const string ActorIdPrefix = "health-probe::";

    /// <summary>
    /// Stable actor id for a probe target. Treated as opaque by callers — they
    /// must look up read-model documents by slug, not by parsing this string.
    /// </summary>
    public static string BuildActorId(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            throw new ArgumentException("slug must be non-empty", nameof(slug));
        return ActorIdPrefix + slug.Trim();
    }

    public static async Task DispatchConfigureAsync(
        IActorRuntime actorRuntime,
        IActorDispatchPort dispatchPort,
        HealthProbeTargetDescriptor descriptor,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actorRuntime);
        ArgumentNullException.ThrowIfNull(dispatchPort);
        ArgumentNullException.ThrowIfNull(descriptor);
        if (string.IsNullOrWhiteSpace(descriptor.Slug))
            throw new ArgumentException("descriptor.slug must be non-empty", nameof(descriptor));

        var actorId = BuildActorId(descriptor.Slug);
        await EnsureActorAsync(actorRuntime, actorId, ct);
        await DispatchAsync(dispatchPort, actorId, new HealthProbeConfigureCommand
        {
            Spec = descriptor,
        }, ct);
    }

    private static async Task EnsureActorAsync(IActorRuntime actorRuntime, string actorId, CancellationToken ct)
    {
        _ = await actorRuntime.GetAsync(actorId)
            ?? await actorRuntime.CreateAsync<HealthProbeTargetGAgent>(actorId, ct);
    }

    private static Task DispatchAsync(
        IActorDispatchPort dispatchPort,
        string actorId,
        IMessage payload,
        CancellationToken ct)
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, actorId),
        };
        return dispatchPort.DispatchAsync(actorId, envelope, ct);
    }
}
