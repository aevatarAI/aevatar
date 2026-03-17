using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.Integration.Tests.Fixtures.ScriptDocuments;
using Aevatar.Integration.Tests.Protocols;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Application.Queries;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Hosting.DependencyInjection;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;

namespace Aevatar.Integration.Tests;

internal static class ClaimIntegrationTestKit
{
    private static readonly ConcurrentDictionary<string, ScriptDefinitionSnapshot> DefinitionSnapshots = new(StringComparer.Ordinal);

    public static ServiceProvider BuildProvider(Action<IServiceCollection>? configure = null)
        => BuildProvider(configuration: null, configure);

    public static ServiceProvider BuildProvider(
        IConfiguration? configuration,
        Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        configure?.Invoke(services);
        services.AddScriptCapability(configuration);
        return services.BuildServiceProvider();
    }

    public static async Task<string> UpsertOrchestratorAsync(
        IServiceProvider provider,
        string definitionActorId,
        CancellationToken ct)
    {
        var definitionPort = provider.GetRequiredService<IScriptDefinitionCommandPort>();
        var orchestrator = ClaimScriptScenarioDocument.CreateEmbedded()
            .Scripts
            .Single(x => x.ScriptId == "claim_orchestrator");

        var result = await definitionPort.UpsertDefinitionWithSnapshotAsync(
            scriptId: orchestrator.ScriptId,
            scriptRevision: orchestrator.Revision,
            sourceText: orchestrator.Source,
            sourceHash: orchestrator.SourceHash,
            definitionActorId: definitionActorId,
            ct: ct);
        RememberDefinitionSnapshot(result.ActorId, result.Snapshot);
        return result.ActorId;
    }

    public static async Task<string> EnsureRuntimeAsync(
        IServiceProvider provider,
        string definitionActorId,
        string revision,
        string runtimeActorId,
        CancellationToken ct)
    {
        var provisioningPort = provider.GetRequiredService<IScriptRuntimeProvisioningPort>();
        var definitionSnapshot = ResolveDefinitionSnapshot(definitionActorId, revision)
            ?? await provider.GetRequiredService<IScriptDefinitionSnapshotPort>()
                .GetRequiredAsync(definitionActorId, revision, ct);
        return await provisioningPort.EnsureRuntimeAsync(definitionActorId, revision, runtimeActorId, definitionSnapshot, ct);
    }

    private static void RememberDefinitionSnapshot(
        string definitionActorId,
        ScriptDefinitionSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(definitionActorId))
            return;

        DefinitionSnapshots[BuildDefinitionSnapshotKey(definitionActorId, snapshot.Revision)] = snapshot;
    }

    private static ScriptDefinitionSnapshot? ResolveDefinitionSnapshot(
        string definitionActorId,
        string revision)
    {
        DefinitionSnapshots.TryGetValue(BuildDefinitionSnapshotKey(definitionActorId, revision), out var snapshot);
        return snapshot;
    }

    private static string BuildDefinitionSnapshotKey(
        string definitionActorId,
        string revision) =>
        string.Concat(
            definitionActorId ?? string.Empty,
            "::",
            string.IsNullOrWhiteSpace(revision) ? "latest" : revision);

    public static async Task<(ScriptDomainFactCommitted Committed, ScriptReadModelSnapshot Snapshot)> RunClaimAsync(
        IServiceProvider provider,
        string definitionActorId,
        string runtimeActorId,
        string revision,
        string runId,
        ClaimSubmitted command,
        CancellationToken ct)
    {
        await EnsureRuntimeAsync(provider, definitionActorId, revision, runtimeActorId, ct);

        var commandPort = provider.GetRequiredService<IScriptRuntimeCommandPort>();
        var queryService = provider.GetRequiredService<IScriptReadModelQueryApplicationService>();
        var projectionPort = provider.GetRequiredService<IScriptExecutionProjectionPort>();

        var lease = await projectionPort.EnsureActorProjectionAsync(runtimeActorId, ct);
        lease.Should().NotBeNull();
        await using var sink = new EventChannel<EventEnvelope>(capacity: 32);
        await projectionPort.AttachLiveSinkAsync(lease!, sink, ct);

        try
        {
            await commandPort.RunRuntimeAsync(
                runtimeActorId,
                runId,
                Any.Pack(command),
                revision,
                definitionActorId,
                ClaimSubmitted.Descriptor.FullName,
                ct);

            var committed = await ScriptRunCommittedObservationTestHelper.WaitForCommittedAsync(sink, runId, ct);
            var snapshot = await ScriptEvolutionIntegrationTestKit.WaitForSnapshotAsync(provider, runtimeActorId, ct);
            return (committed, snapshot!);
        }
        finally
        {
            await projectionPort.DetachLiveSinkAsync(lease!, sink, ct);
            await projectionPort.ReleaseActorProjectionAsync(lease!, ct);
        }
    }

    public static async Task<IActor> CreateFreshSinkActorAsync(IActorRuntime runtime, string actorId)
    {
        if (await runtime.ExistsAsync(actorId))
            await runtime.DestroyAsync(actorId, CancellationToken.None);

        return await runtime.CreateAsync<ClaimMessageSinkGAgent>(actorId, CancellationToken.None);
    }

    public static IReadOnlyList<string> ReadMessages(IActor actor) =>
        ((ClaimMessageSinkGAgent)actor.Agent).State.MessageTypes.ToArray();
}
