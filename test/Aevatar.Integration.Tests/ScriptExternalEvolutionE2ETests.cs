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
    public async Task ExternalEvolutionFlow_ShouldPromoteRevisionThroughSessionOwnedEvolutionChain()
    {
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        services.AddScriptCapability();

        using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IActorRuntime>();
        var evolutionService = provider.GetRequiredService<IScriptEvolutionApplicationService>();
        var definitionActorId = "script-definition:external-script";

        await BootstrapDefinitionAsync(
            runtime,
            definitionActorId,
            scriptId: "external-script",
            revision: "rev-0",
            source: ExternalScriptBaselineSource,
            sourceHash: "hash-external-0");

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

        var session = (ScriptEvolutionSessionGAgent)(await runtime.GetAsync("script-evolution-session:external-proposal-1"))!.Agent;
        session.State.Status.Should().Be("promoted");

        var catalog = (ScriptCatalogGAgent)(await runtime.GetAsync("script-catalog"))!.Agent;
        catalog.State.Entries.Should().ContainKey("external-script");
        catalog.State.Entries["external-script"].ActiveRevision.Should().Be("rev-1");

        var definition = (ScriptDefinitionGAgent)(await runtime.GetAsync(definitionActorId))!.Agent;
        definition.State.ScriptId.Should().Be("external-script");
        definition.State.Revision.Should().Be("rev-1");
    }

    private static async Task BootstrapDefinitionAsync(
        IActorRuntime runtime,
        string definitionActorId,
        string scriptId,
        string revision,
        string source,
        string sourceHash)
    {
        var definition = await runtime.CreateAsync<ScriptDefinitionGAgent>(definitionActorId);
        var upsert = new UpsertScriptDefinitionActorRequestAdapter();
        await definition.HandleEventAsync(
            upsert.Map(
                new UpsertScriptDefinitionActorRequest(
                    ScriptId: scriptId,
                    ScriptRevision: revision,
                    SourceText: source,
                    SourceHash: sourceHash),
                definitionActorId),
            CancellationToken.None);

        var catalogActor = await runtime.GetAsync("script-catalog")
            ?? await runtime.CreateAsync<ScriptCatalogGAgent>("script-catalog");
        var promote = new PromoteScriptRevisionActorRequestAdapter();
        await catalogActor.HandleEventAsync(
            promote.Map(
                new PromoteScriptRevisionActorRequest(
                    ScriptId: scriptId,
                    Revision: revision,
                    DefinitionActorId: definitionActorId,
                    SourceHash: sourceHash,
                    ProposalId: $"bootstrap-{scriptId}-{revision}",
                    ExpectedBaseRevision: string.Empty),
                "script-catalog"),
            CancellationToken.None);
    }

    private const string ExternalScriptBaselineSource = """
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public sealed class ExternalScriptV0 : IScriptPackageRuntime
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
            new IMessage[] { new StringValue { Value = "ExternalScriptBaseline" } }));
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
