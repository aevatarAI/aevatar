using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.Integration.Tests.Protocols;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Hosting.DependencyInjection;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Integration.Tests;

public sealed class ScriptDefinitionRuntimeContractTests
{
    [Fact]
    public async Task Runtime_ShouldBindFromDefinitionSnapshot_AndReplayStateAfterRecreate()
    {
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        services.AddScriptCapability();

        await using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IActorRuntime>();
        var eventStore = provider.GetRequiredService<IEventStore>();
        var definitionPort = provider.GetRequiredService<IScriptDefinitionCommandPort>();
        var provisioningPort = provider.GetRequiredService<IScriptRuntimeProvisioningPort>();
        var commandPort = provider.GetRequiredService<IScriptRuntimeCommandPort>();

        const string definitionActorId = "contract-definition";
        const string runtimeActorId = "contract-runtime";
        const string revision = "rev-1";

        await definitionPort.UpsertDefinitionAsync(
            scriptId: "contract-script",
            scriptRevision: revision,
            sourceText: ScriptingCommandEnvelopeTestKit.UppercaseBehaviorSource,
            sourceHash: ScriptingCommandEnvelopeTestKit.UppercaseBehaviorHash,
            definitionActorId: definitionActorId,
            ct: CancellationToken.None);

        await provisioningPort.EnsureRuntimeAsync(definitionActorId, revision, runtimeActorId, CancellationToken.None);
        await commandPort.RunRuntimeAsync(
            runtimeActorId,
            "run-1",
            Any.Pack(new TextNormalizationRequested
            {
                CommandId = "command-1",
                InputText = "  hello ",
            }),
            revision,
            definitionActorId,
            "integration.requested",
            CancellationToken.None);

        var firstActor = await runtime.GetAsync(runtimeActorId);
        firstActor.Should().NotBeNull();
        var firstAgent = firstActor!.Agent.Should().BeOfType<ScriptBehaviorGAgent>().Subject;
        firstAgent.State.DefinitionActorId.Should().Be(definitionActorId);
        firstAgent.State.Revision.Should().Be(revision);
        firstAgent.State.StateRoot.Should().NotBeNull();
        firstAgent.State.StateRoot.Unpack<TextNormalizationReadModel>().NormalizedText.Should().Be("HELLO");

        var persisted = await eventStore.GetEventsAsync(runtimeActorId, ct: CancellationToken.None);
        persisted.Should().Contain(x => x.EventData.Is(ScriptBehaviorBoundEvent.Descriptor));
        persisted.Should().Contain(x => x.EventData.Is(ScriptDomainFactCommitted.Descriptor));

        await runtime.DestroyAsync(runtimeActorId, CancellationToken.None);
        await provisioningPort.EnsureRuntimeAsync(definitionActorId, revision, runtimeActorId, CancellationToken.None);

        var replayedActor = await runtime.GetAsync(runtimeActorId);
        replayedActor.Should().NotBeNull();
        var replayedAgent = replayedActor!.Agent.Should().BeOfType<ScriptBehaviorGAgent>().Subject;
        replayedAgent.State.DefinitionActorId.Should().Be(definitionActorId);
        replayedAgent.State.Revision.Should().Be(revision);
        replayedAgent.State.StateRoot.Should().NotBeNull();
        replayedAgent.State.StateRoot.Unpack<TextNormalizationReadModel>().NormalizedText.Should().Be("HELLO");
    }
}
