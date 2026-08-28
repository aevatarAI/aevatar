namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;

public static class OrleansRuntimeConstants
{
    public const string GrainStateStorageName = "aevatar-foundation-runtime-orleans";
    public const string RuntimeActorGrainStateStorageName =
        "aevatar-foundation-runtime-actor-state";
    public const string RuntimeCallbackSchedulerStorageName = "aevatar-runtime-callback-scheduler";
    public const string DefaultOrleansStreamProviderName = "AevatarOrleansStreamProvider";
    public const string ActorEventStreamNamespace = "aevatar.actor.events";
}
