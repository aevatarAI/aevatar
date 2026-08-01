using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.Tools;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Implementations.Local.Actors;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet;
using Aevatar.Foundation.Runtime.Streaming;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace Aevatar.RoleStreamingWriteAmplification;

public static class Program
{
    private const string GarnetVersion = "2.1.0";
    private const string GarnetImageReference =
        "ghcr.io/microsoft/garnet:2.1.0@sha256:4e298b9b274088cded4156853a32b85fed7b42242eb9ca90216d332e25f2bceb";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static async Task<int> Main(string[] args)
    {
        var options = CommandLineOptions.Parse(args);
        var config = await LoadConfigAsync(options.ConfigPath);
        ValidateConfig(config);
        if (options.VerifyOnly)
        {
            Console.WriteLine($"Configuration valid: {config.Workloads.Count} workloads.");
            return 0;
        }

        var selectedAdapters = ResolveAdapters(options.Adapter);
        var adapterResults = new List<AdapterResult>();
        foreach (var adapterName in selectedAdapters)
        {
            await using var adapter = await AdapterContext.CreateAsync(
                adapterName,
                GarnetVersion,
                GarnetImageReference);

            Console.WriteLine($"Measuring adapter={adapterName}");
            var workloadResults = new List<WorkloadResult>();
            foreach (var workload in config.Workloads)
            {
                var crashFences = string.Equals(workload.Kind, "crash_recovery", StringComparison.Ordinal)
                    ? config.CrashAfterSuccessfulAppendFences.Select(static fence => (int?)fence)
                    : [null];
                foreach (var crashFence in crashFences)
                {
                    var resultName = crashFence.HasValue
                        ? $"{workload.Name}_fence_{crashFence.Value}"
                        : workload.Name;
                    Console.WriteLine($"  workload={resultName}");
                    for (var warmup = 0; warmup < config.WarmupIterations; warmup++)
                    {
                        _ = await RunSampleAsync(
                            adapter.EventStore,
                            config,
                            workload,
                            warmup,
                            measured: false,
                            crashFence);
                        _ = await RunResourceControlAsync(
                            adapter.EventStore,
                            config,
                            workload,
                            warmup,
                            measured: false,
                            crashFence);
                    }

                    var samples = new List<TurnSample>(config.MeasuredIterations);
                    for (var iteration = 0; iteration < config.MeasuredIterations; iteration++)
                    {
                        TurnSample sample;
                        ResourceControlSample control;
                        if (iteration % 2 == 0)
                        {
                            control = await RunResourceControlAsync(
                                adapter.EventStore,
                                config,
                                workload,
                                iteration,
                                measured: true,
                                crashFence);
                            sample = await RunSampleAsync(
                                adapter.EventStore,
                                config,
                                workload,
                                iteration,
                                measured: true,
                                crashFence);
                        }
                        else
                        {
                            sample = await RunSampleAsync(
                                adapter.EventStore,
                                config,
                                workload,
                                iteration,
                                measured: true,
                                crashFence);
                            control = await RunResourceControlAsync(
                                adapter.EventStore,
                                config,
                                workload,
                                iteration,
                                measured: true,
                                crashFence);
                        }

                        samples.Add(sample.WithResourceControl(control));
                    }

                    workloadResults.Add(new WorkloadResult(
                        resultName,
                        workload.Kind,
                        crashFence,
                        samples,
                        WorkloadSummary.From(samples)));
                }
            }

            adapterResults.Add(new AdapterResult(
                adapterName,
                "measured",
                adapter.Identity,
                workloadResults));
        }

        ValidateRecoveryEvidence(config, selectedAdapters, adapterResults);
        var output = new MeasurementOutput(
            3,
            DateTimeOffset.UtcNow,
            await ResolveGitCommitAsync(),
            new EnvironmentFacts(
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString(),
                RuntimeInformation.FrameworkDescription,
                Environment.ProcessorCount,
                GCSettings.IsServerGC),
            config,
            new MetricSemantics(
                "One non-empty ConfirmEventsAsync call maps to one IEventStore.AppendAsync call on this path.",
                "Serialized bytes are low-allocation StateEvent.CalculateSize scalar totals from adapter-returned committed records; the append decorator does not clone them.",
                "Mailbox occupancy is in-flight plus queued chat turns. The harness awaits one dispatch at a time, so max occupancy is one; chunks remain inside that actor turn.",
                "Nearest-rank percentiles: ceil(p * sample_count) - 1 after ascending sort.",
                "Net CPU/allocation values come from an alternating-order matched control turn with append/snapshot measurement decorators removed. Gross values include those decorators; their signed difference estimates instrumentation cost. Both remain process-level and noisy.",
                "Crash recovery reconciles the append-acknowledged ledger, final adapter durable readback, and real CommittedStateEventPublished observations in both directions by StateEvent event ID. Progress redo remains a separate session-sequence and payload-fingerprint diagnostic. Each fence reports only its observed window."),
            adapterResults);

        var outputPath = Path.GetFullPath(options.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(output, JsonOptions) + Environment.NewLine);
        Console.WriteLine($"Wrote {outputPath}");
        return 0;
    }

    private static async Task<TurnSample> RunSampleAsync(
        IEventStore baseStore,
        MeasurementConfig config,
        WorkloadDefinition workload,
        int iteration,
        bool measured,
        int? crashFence)
    {
        var sampleId = $"{workload.Name}-{crashFence}-{(measured ? "m" : "w")}-{iteration}-{Guid.NewGuid():N}";
        var actorId = $"measure-role-{sampleId}";
        var isRecovery = string.Equals(workload.Kind, "crash_recovery", StringComparison.Ordinal);
        var eventStore = new MeasuringEventStore(baseStore, captureCommittedEvents: isRecovery);
        var snapshotStore = new MeasuringSnapshotStore<RoleGAgentState>(
            new InMemoryEventSourcingSnapshotStore<RoleGAgentState>());
        var provider = new WorkloadProviderFactory(workload);
        var streams = CreateStreamProvider();
        CommittedStateEventRecorder? projectionRecorder = null;
        IAsyncDisposable? projectionSubscription = null;
        if (isRecovery)
        {
            projectionRecorder = new CommittedStateEventRecorder();
            var subscriptionProvider = new StreamProviderActorEventSubscriptionProvider(streams);
            projectionSubscription = await subscriptionProvider.SubscribeAsync(
                actorId,
                (Func<EventEnvelope, Task>)projectionRecorder.ObserveAsync);
        }

        var actor = await CreateActorAsync(
            actorId,
            eventStore,
            snapshotStore,
            config,
            provider,
            streams,
            initialize: true);

        var preMeasurementAppendLedger = isRecovery
            ? eventStore.CommittedEventsSnapshot()
            : [];
        eventStore.Reset();
        snapshotStore.Reset();

        var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var workingSetBefore = process.WorkingSet64;
        var managedHeapBefore = GC.GetTotalMemory(forceFullCollection: false);
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var startedAt = Stopwatch.GetTimestamp();
        Exception? failure = null;
        var mailboxTurns = 1;
        TimeSpan? firstToken = null;
        ActorFixture? recoveredActor = null;
        CrashRecoveryObservation? crashRecovery = null;
        var excludedRecoveryValidationDuration = TimeSpan.Zero;
        IReadOnlyList<StateEvent> committedBeforeRecovery = [];
        IReadOnlyList<StateEvent> durableBeforeRecovery = [];
        IReadOnlyList<StateEvent> observedBeforeRecovery = [];

        if (isRecovery)
        {
            if (!crashFence.HasValue)
                throw new InvalidOperationException("Crash-recovery workload requires an append fence.");
            eventStore.FailAfterSuccessfulAppends = crashFence.Value;
            try
            {
                await DispatchAsync(actor, actorId, CreateRequest(workload, sampleId));
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            firstToken = actor.Agent.FirstTokenLatency;

            await actor.LocalActor.DeactivateAsync();
            await actor.Services.DisposeAsync();
            var validationStartedAt = Stopwatch.GetTimestamp();
            await projectionRecorder!.DrainAsync(streams.GetStream(actorId));
            committedBeforeRecovery = eventStore.CommittedEventsSnapshot();
            durableBeforeRecovery = await baseStore.GetEventsAsync(actorId);
            observedBeforeRecovery = projectionRecorder.ObservedEventsSnapshot();
            excludedRecoveryValidationDuration += Stopwatch.GetElapsedTime(validationStartedAt);
            eventStore.FailAfterSuccessfulAppends = null;
            var recoveryProvider = new WorkloadProviderFactory(workload);
            recoveredActor = await CreateActorAsync(
                actorId,
                eventStore,
                snapshotStore,
                config,
                recoveryProvider,
                streams,
                initialize: false);
            mailboxTurns++;
            await DispatchAsync(recoveredActor, actorId, CreateRequest(workload, sampleId));
            firstToken ??= recoveredActor.Agent.FirstTokenLatency;
        }
        else
        {
            await DispatchAsync(actor, actorId, CreateRequest(workload, sampleId));
            firstToken = actor.Agent.FirstTokenLatency;
        }

        var completion = Stopwatch.GetElapsedTime(startedAt) - excludedRecoveryValidationDuration;
        process.Refresh();
        var cpuAfter = process.TotalProcessorTime;
        var workingSetAfter = process.WorkingSet64;
        var managedHeapAfter = GC.GetTotalMemory(forceFullCollection: false);
        var allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
        var storeMetrics = eventStore.Snapshot();
        var snapshotMetrics = snapshotStore.Snapshot();

        if (recoveredActor != null)
        {
            await recoveredActor.LocalActor.DeactivateAsync();
            await recoveredActor.Services.DisposeAsync();
        }
        else
        {
            await actor.LocalActor.DeactivateAsync();
            await actor.Services.DisposeAsync();
        }

        if (isRecovery)
        {
            await projectionRecorder!.DrainAsync(streams.GetStream(actorId));
            var finalDurableEvents = await baseStore.GetEventsAsync(actorId);
            crashRecovery = AnalyzeCrashRecovery(
                crashFence!.Value,
                preMeasurementAppendLedger,
                committedBeforeRecovery,
                durableBeforeRecovery,
                eventStore.CommittedEventsSnapshot(),
                finalDurableEvents,
                observedBeforeRecovery,
                projectionRecorder.ObservedEventsSnapshot());
        }
        else
        {
            await DrainStreamAsync(streams, actorId);
        }

        if (projectionSubscription != null)
            await projectionSubscription.DisposeAsync();

        if (isRecovery && failure == null)
            throw new InvalidOperationException("Crash-recovery workload did not hit its configured append failure fence.");

        return new TurnSample(
            iteration,
            storeMetrics.AppendAttempts,
            storeMetrics.SuccessfulAppendCalls,
            storeMetrics.FailedAppendCalls,
            storeMetrics.CommittedEventCount,
            storeMetrics.CommittedSerializedBytes,
            snapshotMetrics.SaveCalls,
            snapshotMetrics.SnapshotSerializedBytes,
            snapshotMetrics.SaveDuration.TotalMilliseconds,
            storeMetrics.DeleteCalls,
            storeMetrics.DeletedEventCount,
            storeMetrics.DeleteDuration.TotalMilliseconds,
            storeMetrics.AppendDuration.TotalMilliseconds,
            storeMetrics.ReadCalls,
            storeMetrics.ReadDuration.TotalMilliseconds,
            storeMetrics.VersionCalls,
            storeMetrics.VersionDuration.TotalMilliseconds,
            storeMetrics.TotalIoDuration.TotalMilliseconds,
            Math.Max(0, (cpuAfter - cpuBefore).TotalMilliseconds),
            allocatedAfter - allocatedBefore,
            0,
            0,
            0,
            0,
            managedHeapAfter - managedHeapBefore,
            workingSetAfter - workingSetBefore,
            firstToken?.TotalMilliseconds,
            completion.TotalMilliseconds,
            mailboxTurns,
            1,
            0,
            crashRecovery,
            failure?.GetType().Name);
    }

    private static ChatRequestEvent CreateRequest(WorkloadDefinition workload, string sampleId) =>
        new()
        {
            Prompt = $"fixed measurement prompt for {workload.Name}",
            SessionId = $"session-{sampleId}",
            TimeoutMs = workload.TimeoutMilliseconds,
            CommandAttemptId = $"attempt-{sampleId}",
        };

    private static async Task<ResourceControlSample> RunResourceControlAsync(
        IEventStore baseStore,
        MeasurementConfig config,
        WorkloadDefinition workload,
        int iteration,
        bool measured,
        int? crashFence)
    {
        var sampleId = $"control-{workload.Name}-{crashFence}-{(measured ? "m" : "w")}-{iteration}-{Guid.NewGuid():N}";
        var actorId = $"measure-role-{sampleId}";
        var isRecovery = string.Equals(workload.Kind, "crash_recovery", StringComparison.Ordinal);
        var controlStore = isRecovery
            ? new FailureInjectingEventStore(baseStore)
            : null;
        var eventStore = (IEventStore?)controlStore ?? baseStore;
        var snapshotStore = new InMemoryEventSourcingSnapshotStore<RoleGAgentState>();
        var streams = CreateStreamProvider();
        var actor = await CreateActorAsync(
            actorId,
            eventStore,
            snapshotStore,
            config,
            new WorkloadProviderFactory(workload),
            streams,
            initialize: true);
        controlStore?.Reset();

        var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        ActorFixture? recoveredActor = null;
        try
        {
            if (isRecovery)
            {
                if (!crashFence.HasValue)
                    throw new InvalidOperationException("Crash-recovery control requires an append fence.");
                controlStore!.FailAfterSuccessfulAppends = crashFence.Value;
                try
                {
                    await DispatchAsync(actor, actorId, CreateRequest(workload, sampleId));
                    throw new InvalidOperationException("Crash-recovery control did not hit its append fence.");
                }
                catch (SimulatedProcessCrashException)
                {
                }

                await actor.LocalActor.DeactivateAsync();
                await actor.Services.DisposeAsync();
                controlStore.FailAfterSuccessfulAppends = null;
                recoveredActor = await CreateActorAsync(
                    actorId,
                    eventStore,
                    snapshotStore,
                    config,
                    new WorkloadProviderFactory(workload),
                    streams,
                    initialize: false);
                await DispatchAsync(recoveredActor, actorId, CreateRequest(workload, sampleId));
            }
            else
            {
                await DispatchAsync(actor, actorId, CreateRequest(workload, sampleId));
            }

            process.Refresh();
            return new ResourceControlSample(
                Math.Max(0, (process.TotalProcessorTime - cpuBefore).TotalMilliseconds),
                GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore);
        }
        finally
        {
            if (recoveredActor != null)
            {
                await recoveredActor.LocalActor.DeactivateAsync();
                await recoveredActor.Services.DisposeAsync();
            }
            else if (!isRecovery)
            {
                await actor.LocalActor.DeactivateAsync();
                await actor.Services.DisposeAsync();
            }

            await DrainStreamAsync(streams, actorId);
        }
    }

    private static InMemoryStreamProvider CreateStreamProvider() =>
        new(
            new InMemoryStreamOptions(),
            NullLoggerFactory.Instance,
            new InMemoryStreamForwardingRegistry());

    private static async Task DrainStreamAsync(InMemoryStreamProvider streams, string actorId)
    {
        var stream = streams.GetStream(actorId);
        var subscriptionProvider = new StreamProviderActorEventSubscriptionProvider(streams);
        var markerId = $"stream-drain-{Guid.NewGuid():N}";
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = await subscriptionProvider.SubscribeAsync(
            actorId,
            (EventEnvelope envelope) =>
            {
                if (string.Equals(envelope.Id, markerId, StringComparison.Ordinal))
                    completion.TrySetResult();
                return Task.CompletedTask;
            });
        await stream.ProduceAsync(new EventEnvelope
        {
            Id = markerId,
            Payload = Any.Pack(new StringValue { Value = "measurement-stream-drain" }),
            Route = EnvelopeRouteSemantics.CreateDirect("measurement-harness", stream.StreamId),
        });
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static CrashRecoveryObservation AnalyzeCrashRecovery(
        int fence,
        IReadOnlyList<StateEvent> preMeasurementAppendLedger,
        IReadOnlyList<StateEvent> committedBeforeRecovery,
        IReadOnlyList<StateEvent> durableBeforeRecovery,
        IReadOnlyList<StateEvent> allCommitted,
        IReadOnlyList<StateEvent> finalDurableEvents,
        IReadOnlyList<StateEvent> observedBeforeRecovery,
        IReadOnlyList<StateEvent> allObserved)
    {
        var preCommitted = BuildProgressFacts(committedBeforeRecovery);
        var preDurable = BuildProgressFacts(durableBeforeRecovery);
        var finalCommitted = BuildProgressFacts(allCommitted);
        var finalDurable = BuildProgressFacts(finalDurableEvents);
        var preObserved = BuildProgressFacts(observedBeforeRecovery);
        var finalObserved = BuildProgressFacts(allObserved);
        var recoveryCommitted = BuildProgressFacts(allCommitted.Skip(committedBeforeRecovery.Count));
        var recoveryObserved = BuildProgressFacts(allObserved.Skip(observedBeforeRecovery.Count));

        var preCommittedIds = preCommitted.Select(static fact => fact.EventId).ToHashSet(StringComparer.Ordinal);
        var preDurableIds = preDurable.Select(static fact => fact.EventId).ToHashSet(StringComparer.Ordinal);
        var preObservedIds = preObserved.Select(static fact => fact.EventId).ToHashSet(StringComparer.Ordinal);
        var preSequences = preCommitted.Select(static fact => fact.SequenceKey).ToHashSet(StringComparer.Ordinal);
        var prePayloads = preCommitted.Select(static fact => fact.PayloadFingerprint).ToHashSet(StringComparer.Ordinal);

        var durableReadbackMissing = preCommittedIds.Except(preDurableIds).LongCount();
        var phaseOneProjectionMissing = preCommittedIds.Except(preObservedIds).LongCount();
        var finalAppendLedger = preMeasurementAppendLedger.Concat(allCommitted).ToArray();
        var finalLedgerEventIds = BuildEventIds(finalAppendLedger, "append ledger");
        var finalDurableEventIds = BuildEventIds(finalDurableEvents, "final durable readback");
        var finalProjectionEventIds = BuildEventIds(allObserved, "committed-state projection");
        var ledgerToDurableMissing = finalLedgerEventIds.Except(finalDurableEventIds).LongCount();
        var durableToLedgerUnexpected = finalDurableEventIds.Except(finalLedgerEventIds).LongCount();
        var durableToProjectionMissing = finalDurableEventIds.Except(finalProjectionEventIds).LongCount();
        var projectionToDurableUnexpected = finalProjectionEventIds.Except(finalDurableEventIds).LongCount();
        var committedIdentityOverlap = recoveryCommitted.LongCount(fact => preCommittedIds.Contains(fact.EventId));
        var projectionIdentityOverlap = recoveryObserved.LongCount(fact => preObservedIds.Contains(fact.EventId));
        var sequenceOverlap = recoveryCommitted.LongCount(fact => preSequences.Contains(fact.SequenceKey));
        var payloadOverlapFacts = recoveryCommitted
            .Where(fact => prePayloads.Contains(fact.PayloadFingerprint))
            .ToArray();

        if (durableReadbackMissing != 0 || phaseOneProjectionMissing != 0 ||
            ledgerToDurableMissing != 0 || durableToLedgerUnexpected != 0 ||
            durableToProjectionMissing != 0 || projectionToDurableUnexpected != 0)
        {
            throw new InvalidOperationException(
                $"Recovery fence {fence} failed committed/projection reconciliation: " +
                $"durableMissing={durableReadbackMissing}, phaseOneProjectionMissing={phaseOneProjectionMissing}, " +
                $"ledgerToDurableMissing={ledgerToDurableMissing}, " +
                $"durableToLedgerUnexpected={durableToLedgerUnexpected}, " +
                $"durableToProjectionMissing={durableToProjectionMissing}, " +
                $"projectionToDurableUnexpected={projectionToDurableUnexpected}.");
        }

        return new CrashRecoveryObservation(
            fence,
            preCommitted.LongCount(),
            preDurable.LongCount(),
            preObserved.LongCount(),
            recoveryCommitted.LongCount(),
            recoveryObserved.LongCount(),
            finalCommitted.LongCount(),
            finalDurable.LongCount(),
            finalObserved.LongCount(),
            finalLedgerEventIds.Count,
            finalDurableEventIds.Count,
            finalProjectionEventIds.Count,
            committedIdentityOverlap,
            projectionIdentityOverlap,
            sequenceOverlap,
            payloadOverlapFacts.LongLength,
            payloadOverlapFacts.Sum(static fact => fact.SerializedBytes),
            durableReadbackMissing,
            phaseOneProjectionMissing,
            ledgerToDurableMissing,
            durableToLedgerUnexpected,
            durableToProjectionMissing,
            projectionToDurableUnexpected);
    }

    private static HashSet<string> BuildEventIds(
        IReadOnlyCollection<StateEvent> events,
        string source)
    {
        if (events.Any(static stateEvent => string.IsNullOrWhiteSpace(stateEvent.EventId)))
            throw new InvalidOperationException($"Recovery {source} contains an empty StateEvent event ID.");
        var eventIds = events.Select(static stateEvent => stateEvent.EventId)
            .ToHashSet(StringComparer.Ordinal);
        if (eventIds.Count != events.Count)
            throw new InvalidOperationException($"Recovery {source} contains duplicate StateEvent event IDs.");
        return eventIds;
    }

    private static void ValidateRecoveryEvidence(
        MeasurementConfig config,
        IReadOnlyCollection<string> selectedAdapters,
        IReadOnlyCollection<AdapterResult> adapterResults)
    {
        if (adapterResults.Count != selectedAdapters.Count)
            throw new InvalidOperationException("Recovery evidence is missing a selected event-store adapter.");

        var expectedFences = config.CrashAfterSuccessfulAppendFences.ToHashSet();
        foreach (var adapter in adapterResults)
        {
            var recoveryResults = adapter.Workloads
                .Where(static workload => string.Equals(
                    workload.StreamShape,
                    "crash_recovery",
                    StringComparison.Ordinal))
                .ToArray();
            var measuredFences = recoveryResults
                .Select(static workload => workload.CrashAfterSuccessfulAppends)
                .Where(static fence => fence.HasValue)
                .Select(static fence => fence!.Value)
                .ToHashSet();
            if (!measuredFences.SetEquals(expectedFences))
            {
                throw new InvalidOperationException(
                    $"Adapter {adapter.Adapter} recovery evidence does not cover every configured append fence.");
            }

            foreach (var recoveryResult in recoveryResults)
            {
                if (recoveryResult.Samples.Count != config.MeasuredIterations)
                {
                    throw new InvalidOperationException(
                        $"Adapter {adapter.Adapter} fence {recoveryResult.CrashAfterSuccessfulAppends} " +
                        "does not contain every configured measured sample.");
                }

                foreach (var sample in recoveryResult.Samples)
                {
                    var observation = sample.CrashRecovery ?? throw new InvalidOperationException(
                        $"Adapter {adapter.Adapter} fence {recoveryResult.CrashAfterSuccessfulAppends} " +
                        $"sample {sample.Iteration} has no recovery reconciliation evidence.");
                    if (observation.LedgerToDurableMissingEvents != 0 ||
                        observation.DurableToLedgerUnexpectedEvents != 0 ||
                        observation.DurableToProjectionMissingEvents != 0 ||
                        observation.ProjectionToDurableUnexpectedEvents != 0 ||
                        observation.FinalAppendLedgerEvents != observation.FinalDurableReadbackEvents ||
                        observation.FinalDurableReadbackEvents != observation.FinalProjectionVisibleEvents)
                    {
                        throw new InvalidOperationException(
                            $"Adapter {adapter.Adapter} fence {observation.Fence} sample {sample.Iteration} " +
                            "contains unreconciled final recovery evidence.");
                    }
                }
            }
        }
    }

    private static IReadOnlyList<ProgressFact> BuildProgressFacts(IEnumerable<StateEvent> events)
    {
        var facts = new List<ProgressFact>();
        foreach (var stateEvent in events)
        {
            if (stateEvent.EventData?.Is(RoleChatSessionProgressedEvent.Descriptor) != true)
                continue;
            var progress = stateEvent.EventData.Unpack<RoleChatSessionProgressedEvent>();
            var payloadOnly = progress.Clone();
            payloadOnly.SessionId = string.Empty;
            payloadOnly.Sequence = 0;
            var payloadFingerprint = Convert.ToHexString(SHA256.HashData(payloadOnly.ToByteArray()));
            facts.Add(new ProgressFact(
                stateEvent.EventId,
                $"{progress.SessionId}\u001f{progress.Sequence}",
                payloadFingerprint,
                stateEvent.CalculateSize()));
        }

        return facts;
    }

    private static async Task<ActorFixture> CreateActorAsync(
        string actorId,
        IEventStore eventStore,
        IEventSourcingSnapshotStore<RoleGAgentState> snapshotStore,
        MeasurementConfig config,
        WorkloadProviderFactory provider,
        InMemoryStreamProvider streams,
        bool initialize)
    {
        var services = new ServiceCollection()
            .AddSingleton<IEventStore>(eventStore)
            .AddSingleton<IEventSourcingSnapshotStore<RoleGAgentState>>(snapshotStore)
            .AddSingleton(new EventSourcingRuntimeOptions
            {
                EnableSnapshots = true,
                SnapshotInterval = config.SnapshotInterval,
                EnableEventCompaction = config.EnableEventCompaction,
                RetainedEventsAfterSnapshot = config.RetainedEventsAfterSnapshot,
            })
            .AddSingleton<IActorRuntimeCallbackScheduler, NoopRuntimeCallbackScheduler>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();

        var toolNames = Enumerable.Range(1, Math.Max(1, provider.Workload.ToolCalls))
            .Select(index => $"measurement_tool_{index}")
            .ToArray();
        var tools = toolNames.Select(static name => (IAgentTool)new MeasurementTool(name)).ToArray();
        var agent = new MeasuredRoleGAgent(
            new MeasurementToolExecutionPort(),
            provider,
            [new StaticToolSource(tools)])
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<IEventSourcingBehaviorFactory<RoleGAgentState>>(),
        };
        typeof(GAgentBase).GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(agent, [actorId]);

        var localActor = new LocalActor(agent, actorId, streams, NullLogger.Instance);
        var publisher = new LocalActorPublisher(
            actorId,
            static () => null,
            static () => 0,
            streams);
        agent.EventPublisher = publisher;
        typeof(GAgentBase)
            .GetProperty("CommittedStateEventPublisher", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(agent, publisher);
        await localActor.ActivateAsync();
        var fixture = new ActorFixture(
            agent,
            localActor,
            new LocalActorDispatchPort(new SingleActorRuntime(localActor)),
            services);

        if (initialize)
        {
            await DispatchAsync(fixture, actorId, new InitializeRoleAgentEvent
            {
                RoleName = "measurement-role",
                ProviderName = provider.Name,
                Model = "deterministic-stream-shape",
                SystemPrompt = "Fixed measurement system prompt.",
                MaxToolRounds = 8,
                MaxHistoryMessages = 32,
            });
        }

        return fixture;
    }

    private static async Task DispatchAsync<T>(ActorFixture actor, string actorId, T payload)
        where T : IMessage
    {
        var handled = actor.Agent.ExpectNextHandlerCompletion();
        await actor.DispatchPort.DispatchAsync(actorId, new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect("measurement-harness", actorId),
        });
        var error = await handled.WaitAsync(TimeSpan.FromSeconds(30));
        if (error != null)
            throw error;
    }

    private static async Task<MeasurementConfig> LoadConfigAsync(string path)
    {
        await using var stream = File.OpenRead(Path.GetFullPath(path));
        return await JsonSerializer.DeserializeAsync<MeasurementConfig>(stream, JsonOptions)
               ?? throw new InvalidOperationException("Measurement configuration is empty.");
    }

    private static void ValidateConfig(MeasurementConfig config)
    {
        if (config.SchemaVersion != 1)
            throw new InvalidOperationException($"Unsupported config schema version {config.SchemaVersion}.");
        if (config.WarmupIterations < 0 || config.MeasuredIterations < 1)
            throw new InvalidOperationException("Iteration counts are invalid.");
        if (config.SnapshotInterval < 1 ||
            config.CrashAfterSuccessfulAppendFences.Count < 2 ||
            config.CrashAfterSuccessfulAppendFences.Any(static fence => fence < 3) ||
            config.CrashAfterSuccessfulAppendFences.Distinct().Count()
            != config.CrashAfterSuccessfulAppendFences.Count)
        {
            throw new InvalidOperationException(
                "Snapshot/crash fences are invalid; recovery requires at least two distinct fences >= 3.");
        }

        var required = new HashSet<string>(StringComparer.Ordinal)
        {
            "short_text",
            "long_text_high_chunk",
            "reasoning_and_text",
            "single_tool_call",
            "multiple_tool_calls",
            "media_parts",
            "terminal_only_completion",
            "cancellation",
            "provider_failure",
            "crash_recovery",
        };
        required.ExceptWith(config.Workloads.Select(static workload => workload.Name));
        if (required.Count > 0)
            throw new InvalidOperationException($"Missing required workloads: {string.Join(", ", required)}.");
        if (config.Workloads.Select(static workload => workload.Name).Distinct(StringComparer.Ordinal).Count()
            != config.Workloads.Count)
        {
            throw new InvalidOperationException("Workload names must be unique.");
        }
    }

    private static IReadOnlyList<string> ResolveAdapters(string adapter) => adapter switch
    {
        "all" => ["inmemory", "garnet"],
        "inmemory" => ["inmemory"],
        "garnet" => ["garnet"],
        _ => throw new InvalidOperationException($"Unknown adapter '{adapter}'."),
    };

    private static async Task<string> ResolveGitCommitAsync()
    {
        using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });
        if (process == null)
            return "unknown";
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return process.ExitCode == 0 ? output.Trim() : "unknown";
    }
}

public sealed record CommandLineOptions(string ConfigPath, string OutputPath, string Adapter, bool VerifyOnly)
{
    public static CommandLineOptions Parse(IReadOnlyList<string> args)
    {
        string? config = null;
        string? output = null;
        var adapter = "all";
        var verify = false;
        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--config":
                    config = RequireValue(args, ref index, "--config");
                    break;
                case "--output":
                    output = RequireValue(args, ref index, "--output");
                    break;
                case "--adapter":
                    adapter = RequireValue(args, ref index, "--adapter").Trim().ToLowerInvariant();
                    break;
                case "--verify":
                    verify = true;
                    break;
                default:
                    throw new InvalidOperationException($"Unknown argument '{args[index]}'.");
            }
        }

        config ??= Path.Combine(AppContext.BaseDirectory, "streaming-write-amplification.config.json");
        output ??= Path.Combine(Environment.CurrentDirectory, "streaming-write-amplification.json");
        return new CommandLineOptions(config, output, adapter, verify);
    }

    private static string RequireValue(IReadOnlyList<string> args, ref int index, string option)
    {
        if (++index >= args.Count)
            throw new InvalidOperationException($"{option} requires a value.");
        return args[index];
    }
}

public sealed record MeasurementConfig
{
    public int SchemaVersion { get; init; }
    public int WarmupIterations { get; init; }
    public int MeasuredIterations { get; init; }
    public int SnapshotInterval { get; init; }
    public bool EnableEventCompaction { get; init; }
    public int RetainedEventsAfterSnapshot { get; init; }
    public List<int> CrashAfterSuccessfulAppendFences { get; init; } = [];
    public List<WorkloadDefinition> Workloads { get; init; } = [];
}

public sealed record WorkloadDefinition
{
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "";
    public int TextChunks { get; init; }
    public int ReasoningChunks { get; init; }
    public int ChunkCharacters { get; init; }
    public int ToolCalls { get; init; }
    public int MediaParts { get; init; }
    public int TimeoutMilliseconds { get; init; }
}

public sealed record MeasurementOutput(
    int SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    string SourceCommit,
    EnvironmentFacts Environment,
    MeasurementConfig Config,
    MetricSemantics MetricSemantics,
    IReadOnlyList<AdapterResult> Adapters);

public sealed record EnvironmentFacts(
    string OsDescription,
    string ProcessArchitecture,
    string Framework,
    int ProcessorCount,
    bool ServerGc);

public sealed record MetricSemantics(
    string ConfirmEventsAndAppend,
    string SerializedBytes,
    string MailboxOccupancy,
    string Percentiles,
    string ResourceDeltas,
    string CrashRecovery);

public sealed record AdapterResult(
    string Adapter,
    string Status,
    AdapterIdentity Identity,
    IReadOnlyList<WorkloadResult> Workloads);

public sealed record AdapterIdentity(
    string ServerName,
    string ServerVersion,
    string ProtocolVersion,
    string Mode,
    string Role,
    string ImageReference,
    string ImageReferenceEvidence,
    IReadOnlyList<string> VerifiedCapabilities);

public sealed record WorkloadResult(
    string Workload,
    string StreamShape,
    int? CrashAfterSuccessfulAppends,
    IReadOnlyList<TurnSample> Samples,
    WorkloadSummary Summary);

public sealed record TurnSample(
    int Iteration,
    long AppendAttempts,
    long SuccessfulAppendCalls,
    long FailedAppendCalls,
    long CommittedEventCount,
    long CommittedSerializedBytes,
    long SnapshotSaveCalls,
    long SnapshotSerializedBytes,
    double SnapshotDurationMs,
    long CompactionCalls,
    long CompactionDeletedEvents,
    double CompactionDurationMs,
    double AppendDurationMs,
    long EventReadCalls,
    double EventReadDurationMs,
    long VersionReadCalls,
    double VersionReadDurationMs,
    double EventStoreIoDurationMs,
    double GrossInstrumentedCpuMs,
    long GrossInstrumentedAllocatedBytes,
    double NetCpuMs,
    long NetAllocatedBytes,
    double EstimatedInstrumentationCpuDeltaMs,
    long EstimatedInstrumentationAllocatedDeltaBytes,
    long GrossManagedHeapDeltaBytes,
    long GrossWorkingSetDeltaBytes,
    double? FirstTokenLatencyMs,
    double CompletionLatencyMs,
    int MailboxTurnCount,
    int MailboxMaxOccupancy,
    int MailboxMaxQueued,
    CrashRecoveryObservation? CrashRecovery,
    string? InjectedFailureType)
{
    internal TurnSample WithResourceControl(ResourceControlSample control) => this with
    {
        NetCpuMs = control.CpuMs,
        NetAllocatedBytes = control.AllocatedBytes,
        EstimatedInstrumentationCpuDeltaMs = GrossInstrumentedCpuMs - control.CpuMs,
        EstimatedInstrumentationAllocatedDeltaBytes = GrossInstrumentedAllocatedBytes - control.AllocatedBytes,
    };
}

public sealed record CrashRecoveryObservation(
    int Fence,
    long PhaseOneAppendLedgerProgressEvents,
    long PhaseOneDurableReadbackProgressEvents,
    long PhaseOneProjectionVisibleProgressEvents,
    long RecoveryAppendLedgerProgressEvents,
    long RecoveryProjectionVisibleProgressEvents,
    long FinalAppendLedgerProgressEvents,
    long FinalDurableReadbackProgressEvents,
    long FinalProjectionVisibleProgressEvents,
    long FinalAppendLedgerEvents,
    long FinalDurableReadbackEvents,
    long FinalProjectionVisibleEvents,
    long RecoveryAppendLedgerEventIdentityOverlap,
    long RecoveryProjectionEventIdentityOverlap,
    long RecoverySequenceOverlap,
    long RecoveryPayloadOverlapEvents,
    long RecoveryPayloadOverlapSerializedBytes,
    long PhaseOneDurableReadbackMissingEvents,
    long PhaseOneProjectionMissingEvents,
    long LedgerToDurableMissingEvents,
    long DurableToLedgerUnexpectedEvents,
    long DurableToProjectionMissingEvents,
    long ProjectionToDurableUnexpectedEvents);

internal sealed record ResourceControlSample(double CpuMs, long AllocatedBytes);
internal sealed record ProgressFact(
    string EventId,
    string SequenceKey,
    string PayloadFingerprint,
    long SerializedBytes);

public sealed record Distribution(int Count, double? P50, double? P95, double? P99, double? Max)
{
    public static Distribution From(IEnumerable<double?> source)
    {
        var values = source.Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .OrderBy(static value => value)
            .ToArray();
        if (values.Length == 0)
            return new Distribution(0, null, null, null, null);
        return new Distribution(
            values.Length,
            Percentile(values, 0.50),
            Percentile(values, 0.95),
            Percentile(values, 0.99),
            values[^1]);
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        var index = Math.Clamp((int)Math.Ceiling(percentile * sorted.Count) - 1, 0, sorted.Count - 1);
        return sorted[index];
    }
}

public sealed record WorkloadSummary(
    Distribution AppendAttempts,
    Distribution CommittedEvents,
    Distribution CommittedSerializedBytes,
    Distribution SnapshotSaves,
    Distribution SnapshotSerializedBytes,
    Distribution SnapshotDurationMs,
    Distribution CompactionCalls,
    Distribution CompactionDeletedEvents,
    Distribution CompactionDurationMs,
    Distribution EventStoreIoDurationMs,
    Distribution GrossInstrumentedCpuMs,
    Distribution GrossInstrumentedAllocatedBytes,
    Distribution NetCpuMs,
    Distribution NetAllocatedBytes,
    Distribution EstimatedInstrumentationCpuDeltaMs,
    Distribution EstimatedInstrumentationAllocatedDeltaBytes,
    Distribution GrossManagedHeapDeltaBytes,
    Distribution GrossWorkingSetDeltaBytes,
    Distribution FirstTokenLatencyMs,
    Distribution CompletionLatencyMs,
    Distribution MailboxMaxOccupancy,
    CrashRecoverySummary? CrashRecovery)
{
    public static WorkloadSummary From(IReadOnlyList<TurnSample> samples) => new(
        Distribution.From(samples.Select(static sample => (double?)sample.AppendAttempts)),
        Distribution.From(samples.Select(static sample => (double?)sample.CommittedEventCount)),
        Distribution.From(samples.Select(static sample => (double?)sample.CommittedSerializedBytes)),
        Distribution.From(samples.Select(static sample => (double?)sample.SnapshotSaveCalls)),
        Distribution.From(samples.Select(static sample => (double?)sample.SnapshotSerializedBytes)),
        Distribution.From(samples.Select(static sample => (double?)sample.SnapshotDurationMs)),
        Distribution.From(samples.Select(static sample => (double?)sample.CompactionCalls)),
        Distribution.From(samples.Select(static sample => (double?)sample.CompactionDeletedEvents)),
        Distribution.From(samples.Select(static sample => (double?)sample.CompactionDurationMs)),
        Distribution.From(samples.Select(static sample => (double?)sample.EventStoreIoDurationMs)),
        Distribution.From(samples.Select(static sample => (double?)sample.GrossInstrumentedCpuMs)),
        Distribution.From(samples.Select(static sample => (double?)sample.GrossInstrumentedAllocatedBytes)),
        Distribution.From(samples.Select(static sample => (double?)sample.NetCpuMs)),
        Distribution.From(samples.Select(static sample => (double?)sample.NetAllocatedBytes)),
        Distribution.From(samples.Select(static sample => (double?)sample.EstimatedInstrumentationCpuDeltaMs)),
        Distribution.From(samples.Select(static sample => (double?)sample.EstimatedInstrumentationAllocatedDeltaBytes)),
        Distribution.From(samples.Select(static sample => (double?)sample.GrossManagedHeapDeltaBytes)),
        Distribution.From(samples.Select(static sample => (double?)sample.GrossWorkingSetDeltaBytes)),
        Distribution.From(samples.Select(static sample => sample.FirstTokenLatencyMs)),
        Distribution.From(samples.Select(static sample => (double?)sample.CompletionLatencyMs)),
        Distribution.From(samples.Select(static sample => (double?)sample.MailboxMaxOccupancy)),
        CrashRecoverySummary.From(samples));
}

public sealed record CrashRecoverySummary(
    Distribution RecoveryAppendLedgerEventIdentityOverlap,
    Distribution RecoveryProjectionEventIdentityOverlap,
    Distribution RecoverySequenceOverlap,
    Distribution RecoveryPayloadOverlapEvents,
    Distribution RecoveryPayloadOverlapSerializedBytes,
    Distribution PhaseOneDurableReadbackMissingEvents,
    Distribution PhaseOneProjectionMissingEvents,
    Distribution LedgerToDurableMissingEvents,
    Distribution DurableToLedgerUnexpectedEvents,
    Distribution DurableToProjectionMissingEvents,
    Distribution ProjectionToDurableUnexpectedEvents)
{
    public static CrashRecoverySummary? From(IReadOnlyList<TurnSample> samples)
    {
        var observations = samples.Select(static sample => sample.CrashRecovery)
            .Where(static observation => observation != null)
            .Cast<CrashRecoveryObservation>()
            .ToArray();
        if (observations.Length == 0)
            return null;
        return new CrashRecoverySummary(
            Distribution.From(observations.Select(static item => (double?)item.RecoveryAppendLedgerEventIdentityOverlap)),
            Distribution.From(observations.Select(static item => (double?)item.RecoveryProjectionEventIdentityOverlap)),
            Distribution.From(observations.Select(static item => (double?)item.RecoverySequenceOverlap)),
            Distribution.From(observations.Select(static item => (double?)item.RecoveryPayloadOverlapEvents)),
            Distribution.From(observations.Select(static item => (double?)item.RecoveryPayloadOverlapSerializedBytes)),
            Distribution.From(observations.Select(static item => (double?)item.PhaseOneDurableReadbackMissingEvents)),
            Distribution.From(observations.Select(static item => (double?)item.PhaseOneProjectionMissingEvents)),
            Distribution.From(observations.Select(static item => (double?)item.LedgerToDurableMissingEvents)),
            Distribution.From(observations.Select(static item => (double?)item.DurableToLedgerUnexpectedEvents)),
            Distribution.From(observations.Select(static item => (double?)item.DurableToProjectionMissingEvents)),
            Distribution.From(observations.Select(static item => (double?)item.ProjectionToDurableUnexpectedEvents)));
    }
}

internal sealed record ActorFixture(
    MeasuredRoleGAgent Agent,
    LocalActor LocalActor,
    IActorDispatchPort DispatchPort,
    ServiceProvider Services);

internal sealed class MeasuredRoleGAgent(
    IAgentToolExecutionPort toolExecutionPort,
    ILLMProviderFactory llmProviderFactory,
    IEnumerable<IAgentToolSource> toolSources)
    : RoleGAgent(
        toolExecutionPort: toolExecutionPort,
        llmProviderFactory: llmProviderFactory,
        toolSources: toolSources)
{
    private readonly object _completionLock = new();
    private TaskCompletionSource<Exception?>? _nextHandlerCompletion;

    public TimeSpan? FirstTokenLatency { get; private set; }

    public Task<Exception?> ExpectNextHandlerCompletion()
    {
        lock (_completionLock)
        {
            if (_nextHandlerCompletion is { Task.IsCompleted: false })
                throw new InvalidOperationException("Only one measured actor turn may be in flight.");
            _nextHandlerCompletion = new TaskCompletionSource<Exception?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return _nextHandlerCompletion.Task;
        }
    }

    protected override void OnFirstStreamedOutputObserved(TimeSpan elapsed) =>
        FirstTokenLatency ??= elapsed;

    protected override Task OnEventHandlerEndAsync(
        EventEnvelope envelope,
        string handlerName,
        object? payload,
        TimeSpan duration,
        Exception? exception,
        CancellationToken ct)
    {
        _ = envelope;
        _ = handlerName;
        _ = payload;
        _ = duration;
        _ = ct;
        lock (_completionLock)
            _nextHandlerCompletion?.TrySetResult(exception);
        return Task.CompletedTask;
    }
}

internal sealed class SingleActorRuntime(LocalActor actor) : IActorRuntime
{
    public Task<IActor?> GetAsync(string id) =>
        Task.FromResult<IActor?>(string.Equals(id, actor.Id, StringComparison.Ordinal) ? actor : null);

    public Task<bool> ExistsAsync(string id) =>
        Task.FromResult(string.Equals(id, actor.Id, StringComparison.Ordinal));

    public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
        where TAgent : IAgent =>
        throw new NotSupportedException("The measurement runtime owns one pre-created actor.");

    public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default) =>
        throw new NotSupportedException("The measurement runtime owns one pre-created actor.");

    public Task DestroyAsync(string id, CancellationToken ct = default) =>
        throw new NotSupportedException("Actor lifecycle is owned by the measurement fixture.");

    public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
        throw new NotSupportedException("The measurement fixture has no topology links.");

    public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
        throw new NotSupportedException("The measurement fixture has no topology links.");
}

internal sealed class WorkloadProviderFactory(WorkloadDefinition workload)
    : ILLMProviderFactory, ILLMProvider
{
    private int _round;

    public WorkloadDefinition Workload { get; } = workload;
    public string Name => "measurement-deterministic";
    public ILLMProvider GetProvider(string name) => this;
    public ILLMProvider GetDefault() => this;
    public IReadOnlyList<string> GetAvailableProviders() => [Name];

    public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        LLMRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _ = request;
        var round = _round++;
        if (string.Equals(Workload.Kind, "tool", StringComparison.Ordinal) && round == 0)
        {
            for (var index = 1; index <= Workload.ToolCalls; index++)
            {
                ct.ThrowIfCancellationRequested();
                yield return new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = $"call-{index}",
                        Name = $"measurement_tool_{index}",
                        ArgumentsJson = $"{{\"index\":{index}}}",
                    },
                };
                await Task.Yield();
            }
            yield return new LLMStreamChunk { IsLast = true, FinishReason = "tool_calls" };
            yield break;
        }

        switch (Workload.Kind)
        {
            case "text":
            case "crash_recovery":
            case "tool":
                await foreach (var chunk in TextChunks(Workload.TextChunks, Workload.ChunkCharacters, ct))
                    yield return chunk;
                break;
            case "reasoning_text":
                for (var index = 0; index < Workload.ReasoningChunks; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    yield return new LLMStreamChunk
                    {
                        DeltaReasoningContent = FixedChunk("reasoning", index, Workload.ChunkCharacters),
                    };
                    await Task.Yield();
                }
                await foreach (var chunk in TextChunks(Workload.TextChunks, Workload.ChunkCharacters, ct))
                    yield return chunk;
                break;
            case "media":
                for (var index = 0; index < Workload.MediaParts; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    yield return new LLMStreamChunk
                    {
                        DeltaContentPart = ContentPart.ImageUriPart(
                            $"https://example.invalid/measurement/{index}.png",
                            name: $"measurement-{index}.png"),
                    };
                    await Task.Yield();
                }
                break;
            case "terminal":
                break;
            case "cancellation":
                await foreach (var chunk in TextChunks(Workload.TextChunks, Workload.ChunkCharacters, ct))
                    yield return chunk;
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                break;
            case "provider_failure":
                await foreach (var chunk in TextChunks(Workload.TextChunks, Workload.ChunkCharacters, ct))
                    yield return chunk;
                throw new InvalidOperationException("deterministic provider failure");
            default:
                throw new InvalidOperationException($"Unknown workload kind '{Workload.Kind}'.");
        }

        yield return new LLMStreamChunk
        {
            IsLast = true,
            FinishReason = "stop",
            Usage = new TokenUsage(32, Math.Max(1, Workload.TextChunks), 32 + Math.Max(1, Workload.TextChunks)),
        };
    }

    private static async IAsyncEnumerable<LLMStreamChunk> TextChunks(
        int count,
        int characters,
        [EnumeratorCancellation] CancellationToken ct)
    {
        for (var index = 0; index < count; index++)
        {
            ct.ThrowIfCancellationRequested();
            yield return new LLMStreamChunk { DeltaContent = FixedChunk("text", index, characters) };
            await Task.Yield();
        }
    }

    private static string FixedChunk(string prefix, int index, int characters)
    {
        var header = $"{prefix}-{index:D3}:";
        return header + new string('x', Math.Max(1, characters - header.Length));
    }
}

internal sealed class StaticToolSource(IReadOnlyList<IAgentTool> tools) : IAgentToolSource
{
    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(tools);
    }
}

internal sealed class MeasurementTool(string name) : IAgentTool
{
    public string Name => name;
    public string Description => "Deterministic read-only measurement tool.";
    public string ParametersSchema => "{\"type\":\"object\"}";
    public bool IsReadOnly => true;

    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult($"{{\"ok\":true,\"arguments\":{JsonSerializer.Serialize(argumentsJson)}}}");
    }
}

internal sealed class MeasurementToolExecutionPort : IAgentToolExecutionPort
{
    public async Task<AgentToolExecutionOutcome> ExecuteAsync(
        AgentToolExecutionRequest request,
        CancellationToken ct = default)
    {
        var result = await request.Tool.ExecuteAsync(request.ArgumentsJson, ct);
        return new AgentToolExecutionOutcome(
            AgentToolExecutionOutcomeKind.Executed,
            result,
            new AgentToolReceipt
            {
                CallId = request.ExecutionContext.Request.CallId ?? string.Empty,
                ToolName = request.Tool.Name,
                Status = AgentToolReceiptStatus.Success,
                ResultJson = result,
            },
            IsMutation: false,
            FailureCode: string.Empty,
            SafeMessage: string.Empty,
            AgentToolExecutionFailureStage.None,
            TerminalInvoked: true,
            Retryable: false,
            AuditCompleted: true);
    }
}

internal sealed class NoopRuntimeCallbackScheduler : IActorRuntimeCallbackScheduler
{
    public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
        RuntimeCallbackTimeoutRequest request,
        CancellationToken ct = default) =>
        Task.FromResult(new RuntimeCallbackLease(
            request.ActorId,
            request.CallbackId,
            1,
            RuntimeCallbackBackend.InMemory));

    public Task<RuntimeCallbackLease> ScheduleTimerAsync(
        RuntimeCallbackTimerRequest request,
        CancellationToken ct = default) =>
        Task.FromResult(new RuntimeCallbackLease(
            request.ActorId,
            request.CallbackId,
            1,
            RuntimeCallbackBackend.InMemory));

    public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) => Task.CompletedTask;
    public Task PurgeActorAsync(string actorId, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class MeasuringEventStore(IEventStore inner, bool captureCommittedEvents) : IEventStore
{
    private readonly object _lock = new();
    private readonly List<StateEvent> _committedEvents = new(256);
    private StoreMetrics _metrics = new();

    public int? FailAfterSuccessfulAppends { get; set; }

    public async Task<EventStoreCommitResult> AppendAsync(
        string agentId,
        IEnumerable<StateEvent> events,
        long expectedVersion,
        CancellationToken ct = default)
    {
        var batch = events as IReadOnlyList<StateEvent> ?? events.ToArray();
        var startedAt = Stopwatch.GetTimestamp();
        lock (_lock)
            _metrics.AppendAttempts++;
        try
        {
            if (FailAfterSuccessfulAppends is { } fence)
            {
                lock (_lock)
                {
                    if (_metrics.SuccessfulAppendCalls >= fence)
                        throw new SimulatedProcessCrashException(fence);
                }
            }

            var result = await inner.AppendAsync(agentId, batch, expectedVersion, ct);
            var committedCount = result.CommittedEvents.Count;
            var committedBytes = result.CommittedEvents.Sum(static stateEvent => (long)stateEvent.CalculateSize());
            lock (_lock)
            {
                _metrics.SuccessfulAppendCalls++;
                _metrics.CommittedEventCount += committedCount;
                _metrics.CommittedSerializedBytes += committedBytes;
                if (captureCommittedEvents)
                    _committedEvents.AddRange(result.CommittedEvents);
            }
            return result;
        }
        catch
        {
            lock (_lock)
                _metrics.FailedAppendCalls++;
            throw;
        }
        finally
        {
            lock (_lock)
                _metrics.AppendDuration += Stopwatch.GetElapsedTime(startedAt);
        }
    }

    public async Task<IReadOnlyList<StateEvent>> GetEventsAsync(
        string agentId,
        long? fromVersion = null,
        CancellationToken ct = default)
    {
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            return await inner.GetEventsAsync(agentId, fromVersion, ct);
        }
        finally
        {
            lock (_lock)
            {
                _metrics.ReadCalls++;
                _metrics.ReadDuration += Stopwatch.GetElapsedTime(startedAt);
            }
        }
    }

    public async Task<long> GetVersionAsync(string agentId, CancellationToken ct = default)
    {
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            return await inner.GetVersionAsync(agentId, ct);
        }
        finally
        {
            lock (_lock)
            {
                _metrics.VersionCalls++;
                _metrics.VersionDuration += Stopwatch.GetElapsedTime(startedAt);
            }
        }
    }

    public async Task<long> DeleteEventsUpToAsync(
        string agentId,
        long toVersion,
        CancellationToken ct = default)
    {
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            var deleted = await inner.DeleteEventsUpToAsync(agentId, toVersion, ct);
            lock (_lock)
            {
                _metrics.DeleteCalls++;
                _metrics.DeletedEventCount += deleted;
            }
            return deleted;
        }
        finally
        {
            lock (_lock)
                _metrics.DeleteDuration += Stopwatch.GetElapsedTime(startedAt);
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _metrics = new StoreMetrics();
            _committedEvents.Clear();
        }
    }

    public StoreMetrics Snapshot()
    {
        lock (_lock)
            return _metrics with { };
    }

    public IReadOnlyList<StateEvent> CommittedEventsSnapshot()
    {
        lock (_lock)
            return _committedEvents.ToArray();
    }
}

internal sealed class FailureInjectingEventStore(IEventStore inner) : IEventStore
{
    private int _successfulAppends;

    public int? FailAfterSuccessfulAppends { get; set; }

    public async Task<EventStoreCommitResult> AppendAsync(
        string agentId,
        IEnumerable<StateEvent> events,
        long expectedVersion,
        CancellationToken ct = default)
    {
        if (FailAfterSuccessfulAppends is { } fence && _successfulAppends >= fence)
            throw new SimulatedProcessCrashException(fence);
        var result = await inner.AppendAsync(agentId, events, expectedVersion, ct);
        _successfulAppends++;
        return result;
    }

    public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
        string agentId,
        long? fromVersion = null,
        CancellationToken ct = default) =>
        inner.GetEventsAsync(agentId, fromVersion, ct);

    public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default) =>
        inner.GetVersionAsync(agentId, ct);

    public Task<long> DeleteEventsUpToAsync(
        string agentId,
        long toVersion,
        CancellationToken ct = default) =>
        inner.DeleteEventsUpToAsync(agentId, toVersion, ct);

    public void Reset() => _successfulAppends = 0;
}

internal sealed class CommittedStateEventRecorder
{
    private readonly object _lock = new();
    private readonly List<StateEvent> _observedEvents = new(256);
    private readonly Dictionary<string, TaskCompletionSource> _drainMarkers = new(StringComparer.Ordinal);

    public Task ObserveAsync(EventEnvelope envelope)
    {
        TaskCompletionSource? drainCompletion = null;
        lock (_lock)
        {
            if (_drainMarkers.Remove(envelope.Id, out var pendingDrain))
                drainCompletion = pendingDrain;
        }
        if (drainCompletion != null)
        {
            drainCompletion.TrySetResult();
            return Task.CompletedTask;
        }

        if (envelope.Payload?.Is(CommittedStateEventPublished.Descriptor) != true)
            return Task.CompletedTask;
        var published = envelope.Payload.Unpack<CommittedStateEventPublished>();
        if (published.StateEvent != null)
        {
            lock (_lock)
                _observedEvents.Add(published.StateEvent);
        }

        return Task.CompletedTask;
    }

    public async Task DrainAsync(IStream stream)
    {
        var markerId = $"projection-drain-{Guid.NewGuid():N}";
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
            _drainMarkers.Add(markerId, completion);
        await stream.ProduceAsync(new EventEnvelope
        {
            Id = markerId,
            Payload = Any.Pack(new StringValue { Value = "measurement-projection-drain" }),
            Route = EnvelopeRouteSemantics.CreateDirect("measurement-harness", stream.StreamId),
        });
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    public IReadOnlyList<StateEvent> ObservedEventsSnapshot()
    {
        lock (_lock)
            return _observedEvents.ToArray();
    }
}

internal sealed record StoreMetrics
{
    public long AppendAttempts { get; set; }
    public long SuccessfulAppendCalls { get; set; }
    public long FailedAppendCalls { get; set; }
    public long CommittedEventCount { get; set; }
    public long CommittedSerializedBytes { get; set; }
    public TimeSpan AppendDuration { get; set; }
    public long ReadCalls { get; set; }
    public TimeSpan ReadDuration { get; set; }
    public long VersionCalls { get; set; }
    public TimeSpan VersionDuration { get; set; }
    public long DeleteCalls { get; set; }
    public long DeletedEventCount { get; set; }
    public TimeSpan DeleteDuration { get; set; }
    public TimeSpan TotalIoDuration => AppendDuration + ReadDuration + VersionDuration + DeleteDuration;
}

internal sealed class MeasuringSnapshotStore<TState>(IEventSourcingSnapshotStore<TState> inner)
    : IEventSourcingSnapshotStore<TState>
    where TState : class, IMessage<TState>, new()
{
    private readonly object _lock = new();
    private SnapshotMetrics _metrics = new();

    public async Task<EventSourcingSnapshot<TState>?> LoadAsync(
        string agentId,
        CancellationToken ct = default)
    {
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            return await inner.LoadAsync(agentId, ct);
        }
        finally
        {
            lock (_lock)
            {
                _metrics.LoadCalls++;
                _metrics.LoadDuration += Stopwatch.GetElapsedTime(startedAt);
            }
        }
    }

    public async Task SaveAsync(
        string agentId,
        EventSourcingSnapshot<TState> snapshot,
        CancellationToken ct = default)
    {
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            await inner.SaveAsync(agentId, snapshot, ct);
            lock (_lock)
            {
                _metrics.SaveCalls++;
                _metrics.SnapshotSerializedBytes += snapshot.State.CalculateSize();
            }
        }
        finally
        {
            lock (_lock)
                _metrics.SaveDuration += Stopwatch.GetElapsedTime(startedAt);
        }
    }

    public void Reset()
    {
        lock (_lock)
            _metrics = new SnapshotMetrics();
    }

    public SnapshotMetrics Snapshot()
    {
        lock (_lock)
            return _metrics with { };
    }
}

internal sealed record SnapshotMetrics
{
    public long LoadCalls { get; set; }
    public TimeSpan LoadDuration { get; set; }
    public long SaveCalls { get; set; }
    public long SnapshotSerializedBytes { get; set; }
    public TimeSpan SaveDuration { get; set; }
}

internal sealed class SimulatedProcessCrashException(int successfulAppendFence)
    : Exception($"Simulated process crash after {successfulAppendFence} successful turn append calls.");

internal sealed class AdapterContext : IAsyncDisposable
{
    private readonly IConnectionMultiplexer? _connection;

    private AdapterContext(
        string name,
        IEventStore eventStore,
        AdapterIdentity identity,
        IConnectionMultiplexer? connection)
    {
        Name = name;
        EventStore = eventStore;
        Identity = identity;
        _connection = connection;
    }

    public string Name { get; }
    public IEventStore EventStore { get; }
    public AdapterIdentity Identity { get; }

    public static async Task<AdapterContext> CreateAsync(
        string name,
        string expectedGarnetVersion,
        string expectedGarnetImageReference)
    {
        if (string.Equals(name, "inmemory", StringComparison.Ordinal))
        {
            return new AdapterContext(
                name,
                new InMemoryEventStore(),
                new AdapterIdentity(
                    "aevatar-inmemory-event-store",
                    "repository-source",
                    "in-process",
                    "development-test",
                    "single-process",
                    "not-applicable",
                    "Repository implementation selected directly by the harness.",
                    ["append", "version-read", "event-read", "compaction"]),
                null);
        }

        var connectionString = Environment.GetEnvironmentVariable("AEVATAR_TEST_GARNET_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "AEVATAR_TEST_GARNET_CONNECTION_STRING is required when Garnet is selected.");

        var declaredImageReference = Environment.GetEnvironmentVariable("AEVATAR_TEST_GARNET_IMAGE_REFERENCE");
        if (!string.Equals(declaredImageReference, expectedGarnetImageReference, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "AEVATAR_TEST_GARNET_IMAGE_REFERENCE must exactly match the checked-in pinned image reference " +
                $"'{expectedGarnetImageReference}'.");
        }

        IConnectionMultiplexer? connection = null;
        try
        {
            connection = await ConnectionMultiplexer.ConnectAsync(connectionString);
            var database = connection.GetDatabase();
            var info = ParseInfo((await database.ExecuteAsync("INFO", "SERVER")).ToString());
            RequireInfoValue(info, "server_name", "garnet");
            RequireInfoValue(info, "garnet_version", expectedGarnetVersion);

            var hello = ParseAlternatingArray(await database.ExecuteAsync("HELLO", 2));
            RequireInfoValue(hello, "garnet_version", expectedGarnetVersion);
            RequireInfoValue(hello, "proto", "2");
            RequireInfoValue(hello, "mode", "standalone");
            RequireInfoValue(hello, "role", "master");

            var capabilityKey = (RedisKey)$"aevatar:measure:capability:{Guid.NewGuid():N}";
            const string capabilityValue = "lua-read-write-ok";
            var luaResult = await database.ScriptEvaluateAsync(
                "redis.call('SET', KEYS[1], ARGV[1]); return redis.call('GET', KEYS[1])",
                [capabilityKey],
                [capabilityValue]);
            if (!string.Equals(luaResult.ToString(), capabilityValue, StringComparison.Ordinal))
                throw new InvalidOperationException("Garnet Lua redis.call read/write probe returned an unexpected value.");
            _ = await database.KeyDeleteAsync(capabilityKey);

            var prefix = $"aevatar:measure:role-stream:{Guid.NewGuid():N}";
            var store = new GarnetEventStore(connection, new GarnetEventStoreOptions
            {
                ConnectionString = connectionString,
                KeyPrefix = prefix,
            });
            var probeActorId = $"adapter-capability-{Guid.NewGuid():N}";
            var probeEvent = new StateEvent
            {
                AgentId = probeActorId,
                EventId = Guid.NewGuid().ToString("N"),
                EventType = StringValue.Descriptor.FullName,
                EventData = Any.Pack(new StringValue { Value = "capability" }),
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                Version = 1,
            };
            var appendProbe = await store.AppendAsync(probeActorId, [probeEvent], expectedVersion: 0);
            if (appendProbe.LatestVersion != 1 || appendProbe.CommittedEvents.Count != 1)
                throw new InvalidOperationException("Garnet production append-script capability probe failed.");
            var deletedProbeEvents = await store.DeleteEventsUpToAsync(probeActorId, 1);
            if (deletedProbeEvents != 1)
                throw new InvalidOperationException("Garnet production compaction-script capability probe failed.");

            return new AdapterContext(
                name,
                store,
                new AdapterIdentity(
                    info["server_name"],
                    info["garnet_version"],
                    hello["proto"],
                    hello["mode"],
                    hello["role"],
                    declaredImageReference!,
                    "Operator-declared pinned digest; server name/version independently verified through INFO and HELLO.",
                    [
                        "info-server-identity",
                        "hello-resp2",
                        "lua-redis-call-read-write",
                        "production-append-script",
                        "production-compaction-script",
                    ]),
                connection);
        }
        catch (Exception ex)
        {
            if (connection != null)
                await connection.DisposeAsync();
            throw new InvalidOperationException(
                $"Garnet fail-closed capability validation failed: {ex.GetType().Name}: {ex.Message}",
                ex);
        }
    }

    private static Dictionary<string, string> ParseInfo(string? raw)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in (raw ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var normalized = line.Trim();
            if (normalized.Length == 0 || normalized[0] == '#')
                continue;
            var separator = normalized.IndexOf(':');
            if (separator > 0)
                values[normalized[..separator]] = normalized[(separator + 1)..];
        }

        return values;
    }

    private static Dictionary<string, string> ParseAlternatingArray(RedisResult result)
    {
        var entries = (RedisResult[]?)result;
        if (entries == null || entries.Length % 2 != 0)
            throw new InvalidOperationException("Garnet HELLO response is not an alternating key/value array.");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < entries.Length; index += 2)
            values[entries[index].ToString()] = entries[index + 1].ToString();
        return values;
    }

    private static void RequireInfoValue(
        IReadOnlyDictionary<string, string> values,
        string key,
        string expected)
    {
        if (!values.TryGetValue(key, out var actual) ||
            !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Garnet capability field '{key}' expected '{expected}' but observed '{actual ?? "<missing>"}'.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection != null)
            await _connection.DisposeAsync();
    }
}
