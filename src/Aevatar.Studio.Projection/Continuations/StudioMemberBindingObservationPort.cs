using System.Security.Cryptography;
using System.Text;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Projection.Continuations;

internal sealed class StudioMemberBindingObservationPort : IStudioMemberBindingObservationPort
{
    private const string DirectRoute = "aevatar.studio.projection.studio-member-binding-observation";

    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _dispatchPort;

    public StudioMemberBindingObservationPort(
        IActorRuntime actorRuntime,
        IActorDispatchPort dispatchPort)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
    }

    public async Task EnsureObservationAsync(string rootActorId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootActorId);

        var actorId = BuildActorId(rootActorId);
        var actor = await _actorRuntime.GetAsync(actorId)
            ?? await _actorRuntime.CreateAsync<StudioMemberBindingObservationGAgent>(actorId, ct);
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(new EnsureStudioMemberBindingObservationCommand
            {
                RootActorId = rootActorId,
            }),
            Route = EnvelopeRouteSemantics.CreateDirect(DirectRoute, actor.Id),
        };

        await _dispatchPort.DispatchAsync(actor.Id, envelope, ct);
    }

    internal static string BuildActorId(string rootActorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootActorId);

        var hash = Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rootActorId.Trim())))
            .ToLowerInvariant();
        return $"studio-member-binding-observation:{hash}";
    }
}
