namespace Aevatar.Foundation.Runtime.Hosting;

public sealed class AevatarActorRuntimeOptions
{
    public const string SectionName = "ActorRuntime";
    public const string ProviderInMemory = "InMemory";
    public const string ProviderOrleans = "Orleans";

    public string Provider { get; set; } = ProviderInMemory;
}
