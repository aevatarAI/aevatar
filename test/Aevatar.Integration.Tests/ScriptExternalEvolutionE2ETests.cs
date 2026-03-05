using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Hosting.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Integration.Tests;

public class ScriptExternalEvolutionE2ETests
{
    [Fact]
    public async Task ExternalEvolutionFlow_ShouldPromoteRevisionThroughUnifiedManagerChain()
    {
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        services.AddScriptCapability();

        using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IActorRuntime>();
        var evolutionService = provider.GetRequiredService<IScriptEvolutionApplicationService>();

        var decision = await evolutionService.ProposeAsync(
            new ProposeScriptEvolutionRequest(
                ScriptId: "external-script",
                BaseRevision: "rev-0",
                CandidateRevision: "rev-1",
                CandidateSource: ExternalScriptSource,
                CandidateSourceHash: string.Empty,
                Reason: "external pipeline rollout",
                ProposalId: "external-proposal-1"),
            CancellationToken.None);

        decision.Accepted.Should().BeTrue();
        decision.Status.Should().Be("promoted");
        decision.CandidateRevision.Should().Be("rev-1");
        decision.DefinitionActorId.Should().Be("script-definition:external-script");
        decision.CatalogActorId.Should().Be("script-catalog");

        var manager = (ScriptEvolutionManagerGAgent)(await runtime.GetAsync("script-evolution-manager"))!.Agent;
        manager.State.Proposals.Should().ContainKey("external-proposal-1");
        manager.State.Proposals["external-proposal-1"].Status.Should().Be("promoted");

        var catalog = (ScriptCatalogGAgent)(await runtime.GetAsync("script-catalog"))!.Agent;
        catalog.State.Entries.Should().ContainKey("external-script");
        catalog.State.Entries["external-script"].ActiveRevision.Should().Be("rev-1");

        var definition = (ScriptDefinitionGAgent)(await runtime.GetAsync("script-definition:external-script"))!.Agent;
        definition.State.ScriptId.Should().Be("external-script");
        definition.State.Revision.Should().Be("rev-1");
    }

    private const string ExternalScriptSource = """
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public sealed class ExternalScriptV1 : IScriptPackageRuntime
{
    public Task<ScriptHandlerResult> HandleRequestedEventAsync(
        ScriptRequestedEventEnvelope requestedEvent,
        ScriptExecutionContext context,
        CancellationToken ct)
    {
        _ = requestedEvent;
        _ = context;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new ScriptHandlerResult(
            new IMessage[] { new StringValue { Value = "ExternalScriptCompleted" } }));
    }

    public ValueTask<IReadOnlyDictionary<string, Any>?> ApplyDomainEventAsync(
        IReadOnlyDictionary<string, Any> currentState,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(currentState);

    public ValueTask<IReadOnlyDictionary<string, Any>?> ReduceReadModelAsync(
        IReadOnlyDictionary<string, Any> currentReadModel,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(currentReadModel);
}
""";
}
