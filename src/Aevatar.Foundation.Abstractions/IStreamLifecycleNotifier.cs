namespace Aevatar.Foundation.Abstractions;

public interface IStreamLifecycleNotifier
{
    IDisposable SubscribeCreated(Action<string> onCreated);

    IDisposable SubscribeRemoved(Action<string> onRemoved);
}
