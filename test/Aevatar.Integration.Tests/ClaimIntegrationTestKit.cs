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

namespace Aevatar.Integration.Tests;

internal static class ClaimIntegrationTestKit
{
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

        return await definitionPort.UpsertDefinitionAsync(
            scriptId: orchestrator.ScriptId,
            scriptRevision: orchestrator.Revision,
            sourceText: orchestrator.Source,
            sourceHash: orchestrator.SourceHash,
            definitionActorId: definitionActorId,
            ct: ct);
    }

    public static async Task<string> EnsureRuntimeAsync(
        IServiceProvider provider,
        string definitionActorId,
        string revision,
        string runtimeActorId,
        CancellationToken ct)
    {
        var provisioningPort = provider.GetRequiredService<IScriptRuntimeProvisioningPort>();
        return await provisioningPort.EnsureRuntimeAsync(definitionActorId, revision, runtimeActorId, ct);
    }

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
            var snapshot = await queryService.GetSnapshotAsync(runtimeActorId, ct);
            snapshot.Should().NotBeNull();
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
