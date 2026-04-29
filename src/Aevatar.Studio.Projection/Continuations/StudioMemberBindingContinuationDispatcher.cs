using System.Security.Cryptography;
using System.Text;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.StudioMember;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Projection.Continuations;

internal sealed class StudioMemberBindingContinuationDispatcher : IStudioMemberBindingContinuationDispatcher
{
    private const string DirectRoute = "aevatar.studio.projection.studio-member-binding-continuation";

    private readonly IActorRuntime _actorRuntime;
    private readonly IStreamProvider _streamProvider;

    public StudioMemberBindingContinuationDispatcher(
        IActorRuntime actorRuntime,
        IStreamProvider streamProvider)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _streamProvider = streamProvider ?? throw new ArgumentNullException(nameof(streamProvider));
    }

    public async Task DispatchAsync(StudioMemberBindingRequestedEvent request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var actorId = BuildActorId(request);
        var actor = await _actorRuntime.GetAsync(actorId)
            ?? await _actorRuntime.CreateAsync<StudioMemberBindingContinuationGAgent>(actorId, ct);
        var command = new StudioMemberBindingContinuationRequestedCommand
        {
            Request = request.Clone(),
        };
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(command),
            Route = EnvelopeRouteSemantics.CreateDirect(DirectRoute, actor.Id),
        };

        await _streamProvider.GetStream(actor.Id).ProduceAsync(envelope, ct);
    }

    internal static string BuildActorId(StudioMemberBindingRequestedEvent request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var seed = string.Join(
            '\n',
            request.ScopeId?.Trim() ?? string.Empty,
            request.MemberId?.Trim() ?? string.Empty,
            request.BindingId?.Trim() ?? string.Empty);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed))).ToLowerInvariant();
        return $"studio-member-binding-continuation:{hash}";
    }
}
