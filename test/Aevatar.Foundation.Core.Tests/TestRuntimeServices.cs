using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Streaming;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Foundation.Core.Tests;

internal static class TestRuntimeServices
{
    public static IServiceCollection AddRuntimeScheduler(this IServiceCollection services)
    {
        services.AddSingleton<IStreamProvider, InMemoryStreamProvider>();
        services.AddSingleton<InMemoryActorRuntimeCallbackScheduler>();
        services.AddSingleton<IActorRuntimeCallbackScheduler>(sp =>
            sp.GetRequiredService<InMemoryActorRuntimeCallbackScheduler>());
        return services;
    }

    public static ServiceProvider BuildProvider(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection().AddRuntimeScheduler();
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }
}

public abstract class TestGAgentBase<TState> : GAgentBase<TState>
    where TState : class, IMessage<TState>, new()
{
    protected TestGAgentBase()
    {
        Services = TestRuntimeServices.BuildProvider();
    }
}
