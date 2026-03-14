using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.Integration.Tests.Protocols;
using Aevatar.Scripting.Application.Queries;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Hosting.DependencyInjection;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Integration.Tests;

internal static class ScriptEvolutionIntegrationTestKit
{
    public static ServiceProvider BuildProvider(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        configure?.Invoke(services);
        services.AddScriptCapability();
        return services.BuildServiceProvider();
    }

    public static Task<string> UpsertDefinitionAsync(
        IServiceProvider provider,
        string scriptId,
        string revision,
        string sourceText,
        string? definitionActorId,
        CancellationToken ct) =>
        provider.GetRequiredService<IScriptDefinitionCommandPort>()
            .UpsertDefinitionAsync(
                scriptId,
                revision,
                sourceText,
                ScriptingCommandEnvelopeTestKit.ComputeSourceHash(sourceText),
                definitionActorId,
                ct);

    public static Task<string> EnsureRuntimeAsync(
        IServiceProvider provider,
        string definitionActorId,
        string revision,
        string runtimeActorId,
        CancellationToken ct) =>
        provider.GetRequiredService<IScriptRuntimeProvisioningPort>()
            .EnsureRuntimeAsync(definitionActorId, revision, runtimeActorId, ct);

    public static async Task<(ScriptDomainFactCommitted Fact, ScriptReadModelSnapshot Snapshot)> RunAndReadAsync(
        IServiceProvider provider,
        string runtimeActorId,
        string runId,
        Any? inputPayload,
        string revision,
        string definitionActorId,
        string requestedEventType,
        CancellationToken ct)
    {
        var commandPort = provider.GetRequiredService<IScriptRuntimeCommandPort>();
        var queryService = provider.GetRequiredService<IScriptReadModelQueryApplicationService>();
        var projectionPort = provider.GetRequiredService<IScriptExecutionProjectionPort>();

        var lease = await projectionPort.EnsureActorProjectionAsync(runtimeActorId, ct)
            ?? throw new InvalidOperationException($"Failed to ensure script execution projection. actor_id={runtimeActorId}");
        await using var sink = new EventChannel<EventEnvelope>(capacity: 64);
        await projectionPort.AttachLiveSinkAsync(lease, sink, ct);

        try
        {
            await commandPort.RunRuntimeAsync(
                runtimeActorId,
                runId,
                inputPayload,
                revision,
                definitionActorId,
                requestedEventType,
                ct);

            var fact = await ScriptRunCommittedObservationTestHelper.WaitForCommittedAsync(sink, runId, ct);
            var snapshot = await queryService.GetSnapshotAsync(runtimeActorId, ct)
                ?? throw new InvalidOperationException($"Script read model snapshot not found. actor_id={runtimeActorId}");
            return (fact, snapshot);
        }
        finally
        {
            await projectionPort.DetachLiveSinkAsync(lease, sink, ct);
            await projectionPort.ReleaseActorProjectionAsync(lease, ct);
        }
    }

    public static async Task<TextNormalizationReadModel> QueryNormalizationAsync(
        IServiceProvider provider,
        string runtimeActorId,
        string requestId,
        CancellationToken ct)
    {
        var queryService = provider.GetRequiredService<IScriptReadModelQueryApplicationService>();
        var result = await queryService.ExecuteDeclaredQueryAsync(
            runtimeActorId,
            Any.Pack(new TextNormalizationQueryRequested
            {
                RequestId = requestId,
                ReplyStreamId = $"reply-{requestId}",
            }),
            ct);

        if (result == null)
            throw new InvalidOperationException($"Script query returned null. actor_id={runtimeActorId}");

        return result.Unpack<TextNormalizationQueryResponded>().Current;
    }

    public static async Task<TState> GetStateAsync<TState>(
        IServiceProvider provider,
        string runtimeActorId,
        CancellationToken ct)
        where TState : class, IMessage<TState>, new()
    {
        var runtime = provider.GetRequiredService<IActorRuntime>();
        var actor = await runtime.GetAsync(runtimeActorId)
            ?? throw new InvalidOperationException($"Script runtime actor not found. actor_id={runtimeActorId}");
        var agent = actor.Agent as ScriptBehaviorGAgent
            ?? throw new InvalidOperationException($"Actor `{runtimeActorId}` is not a ScriptBehaviorGAgent.");
        if (agent.State.StateRoot == null)
            throw new InvalidOperationException($"Script runtime `{runtimeActorId}` does not have state.");

        return agent.State.StateRoot.Unpack<TState>();
    }

    public static Task<ScriptCatalogEntrySnapshot?> GetCatalogEntryAsync(
        IServiceProvider provider,
        string scriptId,
        CancellationToken ct) =>
        provider.GetRequiredService<IScriptCatalogQueryPort>()
            .GetCatalogEntryAsync(null, scriptId, ct);

    public static Task<ScriptDefinitionSnapshot> GetDefinitionSnapshotAsync(
        IServiceProvider provider,
        string definitionActorId,
        string revision,
        CancellationToken ct) =>
        provider.GetRequiredService<IScriptDefinitionSnapshotPort>()
            .GetRequiredAsync(definitionActorId, revision, ct);
}
