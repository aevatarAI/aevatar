namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;

public interface IRuntimeActorInlineCallbackSchedulerBindingAccessor
{
    IRuntimeActorInlineCallbackScheduler? Current { get; }

    IDisposable Bind(IRuntimeActorInlineCallbackScheduler scheduler);
}
