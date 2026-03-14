using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Hosting.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Integration.Tests;

public class ScriptGAgentLifecycleBoundaryTests
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
            var scopedRuntime = scope.ServiceProvider.GetRequiredService<IActorRuntime>();
            var actorId = (await scopedRuntime.CreateAsync<ScriptDefinitionGAgent>("factory-lifecycle-definition")).Id;

            actorId.Should().Be("factory-lifecycle-definition");
        }

        (await runtime.ExistsAsync("factory-lifecycle-definition")).Should().BeTrue();

        await using (var scope = provider.CreateAsyncScope())
        {
            var scopedRuntime = scope.ServiceProvider.GetRequiredService<IActorRuntime>();
            await scopedRuntime.DestroyAsync("factory-lifecycle-definition", CancellationToken.None);
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

        var parentId = (await runtime.CreateAsync<ScriptRuntimeGAgent>("factory-parent")).Id;
        var childId = (await runtime.CreateAsync<ScriptRuntimeGAgent>("factory-child")).Id;

        await runtime.LinkAsync(parentId, childId, CancellationToken.None);
        var childActor = await runtime.GetAsync(childId);
        (await childActor!.GetParentIdAsync()).Should().Be(parentId);

        await runtime.UnlinkAsync(childId, CancellationToken.None);
        (await childActor.GetParentIdAsync()).Should().BeNull();
    }
}
