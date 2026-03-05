using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.App.Application.Services;

public sealed class ActorAccessAppService : IActorAccessAppService
{
    private readonly IActorRuntime _runtime;

    public ActorAccessAppService(IActorRuntime runtime)
    {
        _runtime = runtime;
    }

    public async Task SendCommandAsync<TAgent>(string id, IMessage command, CancellationToken ct = default)
        where TAgent : class, IAgent
    {
        var actorId = BuildActorId<TAgent>(id);
        var existing = await _runtime.GetAsync(actorId);
        var actor = existing ?? await _runtime.CreateAsync<TAgent>(actorId, ct);
        var envelope = BuildEnvelope(command);
        await actor.HandleEventAsync(envelope, ct);
    }

    public string ResolveActorId<TAgent>(string id) where TAgent : class, IAgent =>
        BuildActorId<TAgent>(id);

    private static string BuildActorId<TAgent>(string id) where TAgent : class, IAgent
    {
        var typeName = typeof(TAgent).Name;
        var prefix = typeName.EndsWith("GAgent", StringComparison.Ordinal)
            ? typeName[..^6].ToLowerInvariant()
            : typeName.ToLowerInvariant();
        return $"{prefix}:{id}";
    }

    private static EventEnvelope BuildEnvelope(IMessage command) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        Payload = Any.Pack(command),
        PublisherId = "app",
        Direction = EventDirection.Self,
    };
}
