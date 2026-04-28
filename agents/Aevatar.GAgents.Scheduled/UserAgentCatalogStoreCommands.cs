using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.Scheduled;

internal static class UserAgentCatalogStoreCommands
{
    public static Task DispatchUpsertAsync(
        IServiceProvider services,
        string publisherActorId,
        UserAgentCatalogUpsertCommand command,
        CancellationToken ct = default) =>
        DispatchAsync(services, publisherActorId, command, ct);

    public static Task DispatchExecutionUpdateAsync(
        IServiceProvider services,
        string publisherActorId,
        UserAgentCatalogExecutionUpdateCommand command,
        CancellationToken ct = default) =>
        DispatchAsync(services, publisherActorId, command, ct);

    private static async Task DispatchAsync(
        IServiceProvider services,
        string publisherActorId,
        IMessage command,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(command);

        var actorRuntime = services.GetService<IActorRuntime>();
        var dispatchPort = services.GetService<IActorDispatchPort>();
        if (actorRuntime is null || dispatchPort is null)
            return;

        _ = await actorRuntime.GetAsync(UserAgentCatalogGAgent.WellKnownId)
            ?? await actorRuntime.CreateAsync<UserAgentCatalogGAgent>(UserAgentCatalogGAgent.WellKnownId, ct);

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(command),
            Route = EnvelopeRouteSemantics.CreateDirect(publisherActorId, UserAgentCatalogGAgent.WellKnownId),
        };

        await dispatchPort.DispatchAsync(UserAgentCatalogGAgent.WellKnownId, envelope, ct);
    }
}
