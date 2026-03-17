using Aevatar.CQRS.Core.Abstractions.Streaming;

namespace Aevatar.CQRS.Core.Streaming;

public sealed class IdentityEventFrameMapper<T> : IEventFrameMapper<T, T>
{
    public T Map(T evt) => evt;
}
