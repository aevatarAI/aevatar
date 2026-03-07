using Aevatar.Foundation.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Evolution;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;
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
        var evolutionQueryService = provider.GetRequiredService<IScriptEvolutionQueryApplicationService>();
        var lifecyclePort = provider.GetRequiredService<IScriptLifecyclePort>();
        var projectionLifecycle = provider.GetRequiredService<IScriptEvolutionProjectionLifecyclePort>();
        var addressResolver = provider.GetRequiredService<IScriptingActorAddressResolver>();
        var definitionActorId = "script-definition:external-script";
        const string proposalId = "external-proposal-1";

        await BootstrapDefinitionAsync(
            lifecyclePort,
            definitionActorId,
            scriptId: "external-script",
            revision: "rev-0",
            source: ExternalScriptBaselineSource,
            sourceHash: "hash-external-0");

        var sessionActorId = addressResolver.GetEvolutionSessionActorId(proposalId);
        var sink = new EventChannel<ScriptEvolutionSessionCompletedEvent>();
        var lease = await projectionLifecycle.EnsureAndAttachAsync(
            token => projectionLifecycle.EnsureActorProjectionAsync(sessionActorId, proposalId, token),
            sink,
            CancellationToken.None);
        lease.Should().NotBeNull();

        var accepted = await evolutionService.ProposeAsync(
            new ProposeScriptEvolutionRequest(
                ScriptId: "external-script",
                BaseRevision: "rev-0",
                CandidateRevision: "rev-1",
                CandidateSource: ExternalScriptSource,
                CandidateSourceHash: string.Empty,
                Reason: "external pipeline rollout",
                ProposalId: proposalId),
            CancellationToken.None);

        accepted.ProposalId.Should().Be(proposalId);
        accepted.ScriptId.Should().Be("external-script");
        accepted.SessionActorId.Should().Be(sessionActorId);

        var completed = await ReadSingleAsync(sink, CancellationToken.None);
        completed.ProposalId.Should().Be(proposalId);
        completed.Accepted.Should().BeTrue();
        completed.Status.Should().Be("promoted");
        completed.DefinitionActorId.Should().Be("script-definition:external-script");
        completed.CatalogActorId.Should().Be("script-catalog");

        var snapshot = await evolutionQueryService.GetProposalSnapshotAsync(proposalId, CancellationToken.None);
        snapshot.Should().NotBeNull();
        snapshot!.Completed.Should().BeTrue();
        snapshot.Accepted.Should().BeTrue();
        snapshot.PromotionStatus.Should().Be("promoted");
        snapshot.DefinitionActorId.Should().Be("script-definition:external-script");
        snapshot.CatalogActorId.Should().Be("script-catalog");

        var session = (ScriptEvolutionSessionGAgent)(await runtime.GetAsync(sessionActorId))!.Agent;
        session.State.Status.Should().Be("promoted");

        var catalog = (ScriptCatalogGAgent)(await runtime.GetAsync("script-catalog"))!.Agent;
        catalog.State.Entries.Should().ContainKey("external-script");
        catalog.State.Entries["external-script"].ActiveRevision.Should().Be("rev-1");

        var definition = (ScriptDefinitionGAgent)(await runtime.GetAsync(definitionActorId))!.Agent;
        definition.State.ScriptId.Should().Be("external-script");
        definition.State.Revision.Should().Be("rev-1");

        await projectionLifecycle.DetachReleaseAndDisposeAsync(lease, sink, null, CancellationToken.None);
    }

    private static async Task<ScriptEvolutionSessionCompletedEvent> ReadSingleAsync(
        IEventSink<ScriptEvolutionSessionCompletedEvent> sink,
        CancellationToken ct)
    {
        await foreach (var evt in sink.ReadAllAsync(ct))
            return evt;

        throw new InvalidOperationException("Script evolution live delivery completed without a terminal event.");
    }

    private static async Task BootstrapDefinitionAsync(
        IScriptLifecyclePort lifecyclePort,
        string definitionActorId,
        string scriptId,
        string revision,
        string source,
        string sourceHash)
    {
        await lifecyclePort.UpsertDefinitionAsync(
            scriptId,
            revision,
            source,
            sourceHash,
            definitionActorId,
            CancellationToken.None);
        await lifecyclePort.PromoteCatalogRevisionAsync(
            "script-catalog",
            scriptId,
            string.Empty,
            revision,
            definitionActorId,
            sourceHash,
            $"bootstrap-{scriptId}-{revision}",
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
