namespace Aevatar.Foundation.Abstractions.Runtime.Callbacks;

public sealed record RuntimeCallbackLease(
    string ActorId,
    string CallbackId,
    long Generation,
    RuntimeCallbackBackend Backend);
