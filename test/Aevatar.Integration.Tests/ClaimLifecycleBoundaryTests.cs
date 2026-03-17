using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Core;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Integration.Tests;

public class ClaimLifecycleBoundaryTests
{
    [Fact]
    public async Task Should_use_runtime_for_create_destroy_link()
    {
        await using var provider = ClaimIntegrationTestKit.BuildProvider();
        var runtime = provider.GetRequiredService<IActorRuntime>();

        var orchestratorId = (await runtime.CreateAsync<ScriptBehaviorGAgent>("claim-orchestrator-runtime")).Id;
        var analystId = (await runtime.CreateAsync<ScriptBehaviorGAgent>("claim-analyst-runtime")).Id;

        await runtime.LinkAsync(orchestratorId, analystId, CancellationToken.None);
        var analystActor = await runtime.GetAsync(analystId);
        (await analystActor!.GetParentIdAsync()).Should().Be(orchestratorId);

        await runtime.UnlinkAsync(analystId, CancellationToken.None);
        (await analystActor.GetParentIdAsync()).Should().BeNull();

        await runtime.DestroyAsync(analystId, CancellationToken.None);
        await runtime.DestroyAsync(orchestratorId, CancellationToken.None);

        (await runtime.ExistsAsync(analystId)).Should().BeFalse();
        (await runtime.ExistsAsync(orchestratorId)).Should().BeFalse();
    }

    [Fact]
    public async Task Should_not_treat_scope_as_actor_lifecycle_authority()
    {
        await using var provider = ClaimIntegrationTestKit.BuildProvider();
        var runtime = provider.GetRequiredService<IActorRuntime>();

        string actorId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var scopedRuntime = scope.ServiceProvider.GetRequiredService<IActorRuntime>();
            actorId = (await scopedRuntime.CreateAsync<ScriptBehaviorGAgent>("claim-scope-runtime")).Id;
        }

        (await runtime.ExistsAsync(actorId)).Should().BeTrue();
    }
}
