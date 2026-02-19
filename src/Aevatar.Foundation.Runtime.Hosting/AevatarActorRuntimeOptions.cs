namespace Aevatar.Foundation.Runtime.Hosting;

public sealed class AevatarActorRuntimeOptions
{
    public const string SectionName = "ActorRuntime";

    public string Provider { get; set; } = "InMemory";
}
