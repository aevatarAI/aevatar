namespace Aevatar.Foundation.Abstractions.Runtime.Async;

public sealed record RuntimeCallbackLease(
    string ActorId,
    string CallbackId,
    long Generation);
