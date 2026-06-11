using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.Integration.Tests.Protocols;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Abstractions.Evolution;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Application.Queries;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Hosting.DependencyInjection;
using Aevatar.Scripting.Infrastructure.Ports;
using Aevatar.Scripting.Projection.Orchestration;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Collections.Concurrent;

namespace Aevatar.Integration.Tests;

internal static class ScriptEvolutionIntegrationTestKit
{
    private static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ObservationPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly ConcurrentDictionary<string, ScriptDefinitionSnapshot> DefinitionSnapshots = new(StringComparer.Ordinal);

    public static ServiceProvider BuildProvider(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        configure?.Invoke(services);
        services.AddScriptCapability();
        services.AddAttachOnlyScriptEvolutionApplicationService();
        return services.BuildServiceProvider();
    }

    public static IServiceCollection AddAttachOnlyScriptEvolutionApplicationService(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Replace(ServiceDescriptor.Singleton<IScriptEvolutionApplicationService>(sp =>
            new AttachOnlyScriptEvolutionApplicationService(
                new ScriptEvolutionApplicationService(sp.GetRequiredService<IScriptEvolutionProposalPort>()),
                sp.GetRequiredService<IProjectionScopeActivationService<ScriptEvolutionRuntimeLease>>(),
                sp.GetRequiredService<IScriptingActorAddressResolver>())));
        services.Replace(ServiceDescriptor.Singleton<IScriptEvolutionProposalPort>(sp =>
            new AttachOnlyScriptEvolutionProposalPort(
                sp.GetRequiredService<RuntimeScriptEvolutionInteractionService>(),
                sp.GetRequiredService<IProjectionScopeActivationService<ScriptEvolutionRuntimeLease>>(),
                sp.GetRequiredService<IScriptingActorAddressResolver>())));
        return services;
    }

    public static IServiceCollection AddAuthorityActivatingScriptEvolutionApplicationService(
        this IServiceCollection services) =>
        services.AddAttachOnlyScriptEvolutionApplicationService();

    public static async Task<string> UpsertDefinitionAsync(
        IServiceProvider provider,
        string scriptId,
        string revision,
        string sourceText,
        string? definitionActorId,
        CancellationToken ct) =>
        (await UpsertDefinitionWithSnapshotAsync(
            provider,
            scriptId,
            revision,
            sourceText,
            definitionActorId,
            ct)).ActorId;

    public static async Task<ScriptDefinitionUpsertResult> UpsertDefinitionWithSnapshotAsync(
        IServiceProvider provider,
        string scriptId,
        string revision,
        string sourceText,
        string? definitionActorId,
        CancellationToken ct)
    {
        var addressResolver = provider.GetRequiredService<IScriptingActorAddressResolver>();
        var resolvedDefinitionActorId = string.IsNullOrWhiteSpace(definitionActorId)
            ? addressResolver.GetDefinitionActorId(scriptId)
            : definitionActorId;
        var result = await provider.GetRequiredService<IScriptDefinitionCommandPort>()
            .UpsertDefinitionWithSnapshotAsync(
                scriptId,
                revision,
                ScriptPackageSpecExtensions.CreateSingleSource(sourceText),
                resolvedDefinitionActorId,
                ct);
        RememberDefinitionSnapshot(result.ActorId, result.Snapshot);
        return result;
    }

    public static Task<string> EnsureRuntimeAsync(
        IServiceProvider provider,
        string definitionActorId,
        string revision,
        string runtimeActorId,
        CancellationToken ct) =>
        EnsureRuntimeAsync(
            provider,
            definitionActorId,
            revision,
            runtimeActorId,
            definitionSnapshot: null,
            ct);

    public static async Task<string> EnsureRuntimeAsync(
        IServiceProvider provider,
        string definitionActorId,
        string revision,
        string runtimeActorId,
        ScriptDefinitionSnapshot? definitionSnapshot,
        CancellationToken ct)
    {
        var resolvedSnapshot = definitionSnapshot
            ?? ResolveDefinitionSnapshot(definitionActorId, revision)
            ?? await provider.GetRequiredService<IScriptDefinitionSnapshotPort>()
                .GetRequiredAsync(definitionActorId, revision, ct);
        return await provider.GetRequiredService<IScriptRuntimeProvisioningPort>()
            .EnsureRuntimeAsync(definitionActorId, revision, runtimeActorId, resolvedSnapshot, ct);
    }

    public static async Task WaitForScriptBindingAsync(
        IServiceProvider provider,
        string runtimeActorId,
        string definitionActorId,
        string revision,
        CancellationToken ct)
    {
        var eventStore = provider.GetRequiredService<IEventStore>();
        await WaitForAsync(
            token => eventStore.GetEventsAsync(runtimeActorId, ct: token),
            events => events.Any(evt =>
                evt.EventData?.Is(ScriptBehaviorBoundEvent.Descriptor) == true &&
                MatchesBinding(evt.EventData.Unpack<ScriptBehaviorBoundEvent>(), definitionActorId, revision)),
            $"Script runtime binding not committed. actor_id={runtimeActorId}",
            ct);
    }

    public static async Task<TState> WaitForStateAsync<TState>(
        IServiceProvider provider,
        string runtimeActorId,
        Func<TState, bool> isReady,
        CancellationToken ct)
        where TState : class, IMessage<TState>, new()
    {
        ArgumentNullException.ThrowIfNull(isReady);

        return await WaitForAsync(
            token => TryGetStateAsync<TState>(provider, runtimeActorId, token),
            state => state != null && isReady(state),
            $"Script runtime state not ready. actor_id={runtimeActorId}",
            ct) ?? throw new InvalidOperationException($"Script runtime state not ready. actor_id={runtimeActorId}");
    }

    private static async Task<TState?> TryGetStateAsync<TState>(
        IServiceProvider provider,
        string runtimeActorId,
        CancellationToken ct)
        where TState : class, IMessage<TState>, new()
    {
        var runtime = provider.GetRequiredService<IActorRuntime>();
        var actor = await runtime.GetAsync(runtimeActorId);
        if (actor?.Agent is not ScriptBehaviorGAgent agent || agent.State.StateRoot == null)
            return null;

        return agent.State.StateRoot.Unpack<TState>();
    }

    private static bool MatchesBinding(
        ScriptBehaviorBoundEvent evt,
        string definitionActorId,
        string revision) =>
        string.Equals(evt.DefinitionActorId, definitionActorId, StringComparison.Ordinal) &&
        string.Equals(evt.Revision, revision, StringComparison.Ordinal);

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

        var lease = await provider.EnsureScriptExecutionProjectionAsync(runtimeActorId, ct)
            ?? throw new InvalidOperationException($"Failed to ensure script execution projection. actor_id={runtimeActorId}");
        await using var sink = new EventChannel<EventEnvelope>(capacity: 64);
        var liveSinkLease = await projectionPort.AttachLiveSinkAsync(lease, sink, ct);

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
            var snapshot = await WaitForSnapshotAsync(provider, runtimeActorId, ct);
            return (fact, snapshot);
        }
        finally
        {
            await projectionPort.DetachLiveSinkAsync(liveSinkLease, ct);
            await projectionPort.ReleaseActorProjectionAsync(lease, ct);
        }
    }

    public static async Task<TextNormalizationReadModel> QueryNormalizationAsync(
        IServiceProvider provider,
        string runtimeActorId,
        string requestId,
        CancellationToken ct)
    {
        var projectionPort = provider.GetRequiredService<IScriptExecutionProjectionPort>();
        var lease = await provider.EnsureScriptExecutionProjectionAsync(runtimeActorId, ct)
            ?? throw new InvalidOperationException($"Failed to ensure script execution projection. actor_id={runtimeActorId}");

        try
        {
            var snapshot = await WaitForSnapshotAsync(provider, runtimeActorId, ct);
            return snapshot.ReadModelPayload!.Unpack<TextNormalizationReadModel>();
        }
        finally
        {
            await projectionPort.ReleaseActorProjectionAsync(lease, ct);
        }
    }

    public static async Task<ScriptReadModelSnapshot> WaitForSnapshotAsync(
        IServiceProvider provider,
        string runtimeActorId,
        CancellationToken ct)
    {
        var queryService = provider.GetRequiredService<IScriptReadModelQueryApplicationService>();
        return await ScriptReadModelVisibilityTestHelper.WaitForSnapshotAsync(
            token => queryService.GetSnapshotAsync(runtimeActorId, token),
            snapshot => snapshot.ReadModelPayload != null,
            $"Script read model snapshot not found. actor_id={runtimeActorId}",
            ct,
            ObservationTimeout);
    }

    public static async Task<T> WaitForAsync<T>(
        Func<CancellationToken, Task<T>> probeAsync,
        Func<T, bool> isReady,
        string timeoutMessage,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(probeAsync);
        ArgumentNullException.ThrowIfNull(isReady);
        ArgumentException.ThrowIfNullOrWhiteSpace(timeoutMessage);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ObservationTimeout);

        try
        {
            while (true)
            {
                var current = await probeAsync(timeoutCts.Token);
                if (isReady(current))
                    return current;

                await Task.Delay(ObservationPollInterval, timeoutCts.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(timeoutMessage);
        }
    }

    public static async Task<ProjectionGraphSubgraph> WaitForGraphSubgraphAsync(
        IServiceProvider provider,
        string scope,
        string rootNodeId,
        Func<ProjectionGraphSubgraph, bool> isReady,
        CancellationToken ct,
        int depth = 1,
        int take = 20)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootNodeId);
        ArgumentNullException.ThrowIfNull(isReady);

        var graphStore = provider.GetRequiredService<IProjectionGraphStore>();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ObservationTimeout);

        ProjectionGraphSubgraph? last = null;
        try
        {
            while (true)
            {
                last = await graphStore.GetSubgraphAsync(
                    new ProjectionGraphQuery
                    {
                        Scope = scope,
                        RootNodeId = rootNodeId,
                        Depth = depth,
                        Take = take,
                    },
                    timeoutCts.Token);
                if (isReady(last))
                    return last;

                await Task.Delay(ObservationPollInterval, timeoutCts.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Script graph subgraph not ready. scope={scope}, root_node_id={rootNodeId}");
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

    public static async Task ActivateAuthorityReadModelsAsync(
        IServiceProvider provider,
        CancellationToken ct,
        params string[] actorIds)
    {
        foreach (var actorId in actorIds)
        {
            if (string.IsNullOrWhiteSpace(actorId))
                continue;

            _ = actorId;
        }
    }

    private sealed class AttachOnlyScriptEvolutionApplicationService(
        IScriptEvolutionApplicationService inner,
        IProjectionScopeActivationService<ScriptEvolutionRuntimeLease> evolutionProjectionActivation,
        IScriptingActorAddressResolver addressResolver)
        : IScriptEvolutionApplicationService
    {
        public async Task<ScriptPromotionDecision> ProposeAsync(
            ProposeScriptEvolutionRequest request,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request);

            var normalizedScopeId = request.ScopeId?.Trim() ?? string.Empty;
            var normalizedProposalId = string.IsNullOrWhiteSpace(request.ProposalId)
                ? Guid.NewGuid().ToString("N")
                : request.ProposalId.Trim();
            if (!string.IsNullOrWhiteSpace(normalizedScopeId) &&
                !normalizedProposalId.StartsWith($"{normalizedScopeId}:", StringComparison.Ordinal))
            {
                normalizedProposalId = $"{normalizedScopeId}:{normalizedProposalId}";
            }
            var normalizedRequest = request with
            {
                ScopeId = normalizedScopeId,
                ProposalId = normalizedProposalId,
            };

            var sessionActorId = addressResolver.GetEvolutionSessionActorId(
                normalizedProposalId,
                normalizedScopeId);
            var lease = await EnsureEvolutionProjectionAsync(
                evolutionProjectionActivation,
                sessionActorId,
                normalizedProposalId,
                ct);
            if (lease == null)
            {
                throw new InvalidOperationException(
                    $"Failed to ensure script evolution projection. actor_id={sessionActorId}, proposal_id={normalizedProposalId}");
            }

            return await inner.ProposeAsync(normalizedRequest, ct);
        }
    }

    private sealed class AttachOnlyScriptEvolutionProposalPort(
        IScriptEvolutionProposalPort inner,
        IProjectionScopeActivationService<ScriptEvolutionRuntimeLease> evolutionProjectionActivation,
        IScriptingActorAddressResolver addressResolver)
        : IScriptEvolutionProposalPort
    {
        public async Task<ScriptPromotionDecision> ProposeAsync(
            ScriptEvolutionProposal proposal,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(proposal);

            var normalizedProposalId = string.IsNullOrWhiteSpace(proposal.ProposalId)
                ? Guid.NewGuid().ToString("N")
                : proposal.ProposalId;
            var normalizedScopeId = proposal.ScopeId?.Trim() ?? string.Empty;
            var normalizedProposal = proposal with
            {
                ProposalId = normalizedProposalId,
                ScopeId = normalizedScopeId,
            };
            var sessionActorId = addressResolver.GetEvolutionSessionActorId(
                normalizedProposalId,
                normalizedScopeId);
            var lease = await EnsureEvolutionProjectionAsync(
                evolutionProjectionActivation,
                sessionActorId,
                normalizedProposalId,
                ct);
            if (lease == null)
            {
                throw new InvalidOperationException(
                    $"Failed to ensure script evolution projection. actor_id={sessionActorId}, proposal_id={normalizedProposalId}");
            }

            return await inner.ProposeAsync(normalizedProposal, ct);
        }
    }

    private static async Task<IScriptEvolutionProjectionLease?> EnsureEvolutionProjectionAsync(
        IProjectionScopeActivationService<ScriptEvolutionRuntimeLease> activationService,
        string sessionActorId,
        string proposalId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(activationService);

        return await activationService.EnsureAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = sessionActorId,
                ProjectionKind = ScriptProjectionKinds.EvolutionSession,
                Mode = ProjectionRuntimeMode.SessionObservation,
                SessionId = proposalId,
            },
            ct);
    }
}
