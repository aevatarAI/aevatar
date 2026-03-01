using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.DependencyInjection;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Hosting.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Integration.Tests;

public class ScriptGAgentFactoryLifecycleBoundaryTests
{
    [Fact]
    public async Task Lifecycle_ShouldBeRuntimeAuthoritative_NotScopeAuthoritative()
    {
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        services.AddScriptCapability();
        using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IActorRuntime>();

        await using (var scope = provider.CreateAsyncScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<IGAgentFactoryPort>();
            var actorId = await factory.CreateAsync(
                typeof(ScriptDefinitionGAgent).AssemblyQualifiedName!,
                "factory-lifecycle-definition",
                CancellationToken.None);

            actorId.Should().Be("factory-lifecycle-definition");
        }

        (await runtime.ExistsAsync("factory-lifecycle-definition")).Should().BeTrue();

        await using (var scope = provider.CreateAsyncScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<IGAgentFactoryPort>();
            await factory.DestroyAsync("factory-lifecycle-definition", CancellationToken.None);
        }

        (await runtime.ExistsAsync("factory-lifecycle-definition")).Should().BeFalse();
    }

    [Fact]
    public async Task LinkAndUnlink_ShouldGoThroughRuntimeLifecyclePort()
    {
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        services.AddScriptCapability();
        using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IActorRuntime>();
        var factory = provider.GetRequiredService<IGAgentFactoryPort>();

        var parentId = await factory.CreateAsync(
            typeof(ScriptRuntimeGAgent).AssemblyQualifiedName!,
            "factory-parent",
            CancellationToken.None);
        var childId = await factory.CreateAsync(
            typeof(ScriptRuntimeGAgent).AssemblyQualifiedName!,
            "factory-child",
            CancellationToken.None);

        await factory.LinkAsync(parentId, childId, CancellationToken.None);
        var childActor = await runtime.GetAsync(childId);
        (await childActor!.GetParentIdAsync()).Should().Be(parentId);

        await factory.UnlinkAsync(childId, CancellationToken.None);
        (await childActor.GetParentIdAsync()).Should().BeNull();
    }
}
