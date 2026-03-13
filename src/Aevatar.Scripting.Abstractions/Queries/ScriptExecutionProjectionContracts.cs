using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;

namespace Aevatar.Scripting.Abstractions.Queries;

public interface IScriptExecutionProjectionLease
{
    string ActorId { get; }
}

public interface IScriptExecutionProjectionPort
    : IEventSinkProjectionLifecyclePort<IScriptExecutionProjectionLease, EventEnvelope>
{
    Task<IScriptExecutionProjectionLease?> EnsureActorProjectionAsync(
        string actorId,
        CancellationToken ct = default);
}
