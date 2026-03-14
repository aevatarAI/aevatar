using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Hosting.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Integration.Tests;

public sealed class ScriptGAgentFactoryLifecycleBoundaryTests
{
    [Fact]
    public async Task EnsureRuntimeAsync_ShouldReuseExistingActorAndAvoidDuplicateBindingEvent()
    {
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        services.AddScriptCapability();

        await using var provider = services.BuildServiceProvider();
        var eventStore = provider.GetRequiredService<IEventStore>();
        var definitionPort = provider.GetRequiredService<IScriptDefinitionCommandPort>();
        var provisioningPort = provider.GetRequiredService<IScriptRuntimeProvisioningPort>();

        const string definitionActorId = "factory-definition";
        const string runtimeActorId = "factory-runtime";
        const string revision = "rev-1";

        await definitionPort.UpsertDefinitionAsync(
            scriptId: "factory-script",
            scriptRevision: revision,
            sourceText: ScriptingCommandEnvelopeTestKit.UppercaseBehaviorSource,
            sourceHash: ScriptingCommandEnvelopeTestKit.UppercaseBehaviorHash,
            definitionActorId: definitionActorId,
            ct: CancellationToken.None);

        var first = await provisioningPort.EnsureRuntimeAsync(definitionActorId, revision, runtimeActorId, CancellationToken.None);
        var second = await provisioningPort.EnsureRuntimeAsync(definitionActorId, revision, runtimeActorId, CancellationToken.None);

        first.Should().Be(runtimeActorId);
        second.Should().Be(runtimeActorId);

        var persisted = await eventStore.GetEventsAsync(runtimeActorId, ct: CancellationToken.None);
        persisted.Count(x => x.EventData.Is(ScriptBehaviorBoundEvent.Descriptor)).Should().Be(1);
    }
}
