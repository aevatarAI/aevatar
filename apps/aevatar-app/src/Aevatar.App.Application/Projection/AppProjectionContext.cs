using System.Collections.Concurrent;
using Aevatar.CQRS.Projection.Core.Abstractions;

namespace Aevatar.App.Application.Projection;

public sealed class AppProjectionContext : IProjectionContext, IProjectionStreamSubscriptionContext
{
    public required string ActorId { get; init; }
    string IProjectionContext.ProjectionId => ActorId;
    public required string RootActorId { get; init; }

    public IActorStreamSubscriptionLease? StreamSubscriptionLease { get; set; }

    private readonly ConcurrentDictionary<string, byte> _processedEventIds = new(StringComparer.Ordinal);

    public bool TryMarkProcessed(string eventId) => _processedEventIds.TryAdd(eventId, 0);
}
