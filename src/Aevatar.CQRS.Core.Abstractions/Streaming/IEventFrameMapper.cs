namespace Aevatar.CQRS.Core.Abstractions.Streaming;

public interface IEventFrameMapper<in TEvent, out TFrame>
{
    TFrame Map(TEvent evt);
}
