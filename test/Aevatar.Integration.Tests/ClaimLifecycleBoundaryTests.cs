using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.DependencyInjection;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Hosting.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Integration.Tests;

public class ClaimLifecycleBoundaryTests
{
    [Fact]
    public async Task Should_use_runtime_for_create_destroy_link()
    {
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        services.AddScriptCapability();
        using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IActorRuntime>();
        var factory = provider.GetRequiredService<IGAgentFactoryPort>();

        var orchestratorId = await factory.CreateAsync(
            typeof(ScriptRuntimeGAgent).AssemblyQualifiedName!,
            "claim-orchestrator-runtime",
            CancellationToken.None);
        var analystId = await factory.CreateAsync(
            typeof(ScriptRuntimeGAgent).AssemblyQualifiedName!,
            "claim-analyst-runtime",
            CancellationToken.None);

        await factory.LinkAsync(orchestratorId, analystId, CancellationToken.None);
        var analystActor = await runtime.GetAsync(analystId);
        (await analystActor!.GetParentIdAsync()).Should().Be(orchestratorId);

        await factory.UnlinkAsync(analystId, CancellationToken.None);
        (await analystActor.GetParentIdAsync()).Should().BeNull();

        await factory.DestroyAsync(analystId, CancellationToken.None);
        await factory.DestroyAsync(orchestratorId, CancellationToken.None);

        (await runtime.ExistsAsync(analystId)).Should().BeFalse();
        (await runtime.ExistsAsync(orchestratorId)).Should().BeFalse();
    }

    [Fact]
    public async Task Should_not_treat_scope_as_actor_lifecycle_authority()
    {
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        services.AddScriptCapability();
        using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IActorRuntime>();

        string actorId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<IGAgentFactoryPort>();
            actorId = await factory.CreateAsync(
                typeof(ScriptRuntimeGAgent).AssemblyQualifiedName!,
                "claim-scope-runtime",
                CancellationToken.None);
        }

        (await runtime.ExistsAsync(actorId)).Should().BeTrue();
    }
}
