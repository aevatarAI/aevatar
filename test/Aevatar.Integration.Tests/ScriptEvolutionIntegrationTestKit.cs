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
    private static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ObservationPollInterval = TimeSpan.FromMilliseconds(100);

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
        var queryPayload = Any.Pack(new TextNormalizationQueryRequested
        {
            RequestId = requestId,
            ReplyStreamId = $"reply-{requestId}",
        });
        var queryService = provider.GetRequiredService<IScriptReadModelQueryApplicationService>();
        try
        {
            var immediateResult = await queryService.ExecuteDeclaredQueryAsync(runtimeActorId, queryPayload, ct);
            if (immediateResult != null)
                return immediateResult.Unpack<TextNormalizationQueryResponded>().Current;
        }
        catch (InvalidOperationException)
        {
        }
        catch (ArgumentException)
        {
        }

        var projectionPort = provider.GetRequiredService<IScriptExecutionProjectionPort>();
        var lease = await projectionPort.EnsureActorProjectionAsync(runtimeActorId, ct)
            ?? throw new InvalidOperationException($"Failed to ensure script execution projection. actor_id={runtimeActorId}");

        try
        {
            static bool IsReady(ScriptReadModelSnapshot? snapshot) =>
                snapshot != null &&
                !string.IsNullOrWhiteSpace(snapshot.DefinitionActorId) &&
                !string.IsNullOrWhiteSpace(snapshot.Revision) &&
                snapshot.ReadModelPayload != null;

            var snapshot = await queryService.GetSnapshotAsync(runtimeActorId, ct);
            if (!IsReady(snapshot))
            {
                await using var sink = new EventChannel<EventEnvelope>(capacity: 32);
                await projectionPort.AttachLiveSinkAsync(lease, sink, ct);
                try
                {
                    snapshot = await queryService.GetSnapshotAsync(runtimeActorId, ct);
                    while (!IsReady(snapshot))
                    {
                        await ScriptRunCommittedObservationTestHelper.WaitForAnyCommittedAsync(sink, ct);
                        snapshot = await queryService.GetSnapshotAsync(runtimeActorId, ct);
                    }
                }
                finally
                {
                    await projectionPort.DetachLiveSinkAsync(lease, sink, ct);
                }
            }

            var result = await queryService.ExecuteDeclaredQueryAsync(runtimeActorId, queryPayload, ct);

            if (result == null)
                throw new InvalidOperationException($"Script query returned null. actor_id={runtimeActorId}");

            return result.Unpack<TextNormalizationQueryResponded>().Current;
        }
        finally
        {
            await projectionPort.ReleaseActorProjectionAsync(lease, ct);
        }
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

    public static async Task<ScriptCatalogEntrySnapshot?> GetCatalogEntryAsync(
        IServiceProvider provider,
        string scriptId,
        CancellationToken ct,
        string? expectedRevision = null)
    {
        var queryPort = provider.GetRequiredService<IScriptCatalogQueryPort>();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ObservationTimeout);

        ScriptCatalogEntrySnapshot? last = null;
        try
        {
            while (true)
            {
                last = await queryPort.GetCatalogEntryAsync(null, scriptId, timeoutCts.Token);
                if (last != null &&
                    (string.IsNullOrWhiteSpace(expectedRevision) ||
                     string.Equals(last.ActiveRevision, expectedRevision, StringComparison.Ordinal)))
                {
                    return last;
                }

                await Task.Delay(ObservationPollInterval, timeoutCts.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return last;
        }
    }

    public static async Task<ScriptDefinitionSnapshot> GetDefinitionSnapshotAsync(
        IServiceProvider provider,
        string definitionActorId,
        string revision,
        CancellationToken ct)
    {
        var snapshotPort = provider.GetRequiredService<IScriptDefinitionSnapshotPort>();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ObservationTimeout);

        try
        {
            while (true)
            {
                var snapshot = await snapshotPort.TryGetAsync(definitionActorId, revision, timeoutCts.Token);
                if (snapshot != null)
                    return snapshot;

                await Task.Delay(ObservationPollInterval, timeoutCts.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Script definition snapshot not found for actor `{definitionActorId}` revision `{revision}`.");
        }
    }
}
