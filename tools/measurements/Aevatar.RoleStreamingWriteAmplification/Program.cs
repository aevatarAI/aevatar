using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.Foundation.Abstractions.EventSourcing;
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
        if (string.Equals(options.Measurement, CommandLineOptions.RoleContentionMeasurement, StringComparison.Ordinal))
            return await RoleContentionMeasurement.RunAsync(options);
        if (string.Equals(options.Measurement, CommandLineOptions.ProviderNormalizationMeasurement, StringComparison.Ordinal))
            return await ProviderNormalizationMeasurement.RunAsync(options);

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
            5,
            DateTimeOffset.UtcNow,
            await ResolveGitCommitAsync(),
            new MeasurementProvenance(
                await HashFileAsync(GetProgramSourcePath()),
                await HashFileAsync(Path.GetFullPath(options.ConfigPath))),
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
                "Crash recovery reconciles provider-generated semantic evidence, the complete append ledger and committed publications, the compaction-aware durable tail, runtime-owned publication checkpoint, typed snapshot, fresh activation, and a measurement-only current-state read model. Generated boundaries use session + attempt + semantic ordinal + kind + payload hash identities: the injected phase-one generated-but-uncommitted tail is reported separately, while the successful recovery attempt must match committed semantics bidirectionally and its final text/usage hashes must match the materialized user-visible state. Each fence reports only its observed window."),
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
        var sessionId = $"session-{sampleId}";
        var isRecovery = string.Equals(workload.Kind, "crash_recovery", StringComparison.Ordinal);
        var publicationStateStore = new MeasuringCommittedStatePublicationStateStore(
            new InMemoryCommittedStatePublicationStateStore());
        var eventStore = new MeasuringEventStore(
            baseStore,
            captureCommittedEvents: isRecovery,
            () => publicationStateStore.LatestPublishedVersion);
        var snapshotStore = new MeasuringSnapshotStore<RoleGAgentState>(
            new InMemoryEventSourcingSnapshotStore<RoleGAgentState>());
        var toolExecutionPort = new MeasurementToolExecutionPort();
        var secretVault = new InMemorySecretVault();
        var phaseOneGeneratedRecorder = isRecovery
            ? new GeneratedSemanticEvidenceRecorder(sessionId, "phase-one")
            : null;
        var provider = new WorkloadProviderFactory(workload, phaseOneGeneratedRecorder);
        var streams = CreateStreamProvider();
        ServiceProvider? projectionServices = null;
        CommittedPublicationProjectionObserver? publicationObserver = null;
        IAsyncDisposable? publicationSubscription = null;
        IProjectionDocumentReader<MeasurementRoleCurrentStateReadModel, string>? currentStateReader = null;
        ICurrentStateProjectionMaterializer<MeasurementRoleCurrentStateProjectionContext>?
            currentStateMaterializer = null;
        MeasurementRoleCurrentStateProjectionContext? projectionContext = null;
        if (isRecovery)
        {
            projectionServices = CreateMeasurementProjectionServices();
            currentStateReader = projectionServices.GetRequiredService<
                IProjectionDocumentReader<MeasurementRoleCurrentStateReadModel, string>>();
            currentStateMaterializer = projectionServices.GetRequiredService<
                ICurrentStateProjectionMaterializer<MeasurementRoleCurrentStateProjectionContext>>();
            projectionContext = new MeasurementRoleCurrentStateProjectionContext(actorId);
            publicationObserver = new CommittedPublicationProjectionObserver(
                currentStateMaterializer,
                projectionContext);
            var subscriptionProvider = new StreamProviderActorEventSubscriptionProvider(streams);
            publicationSubscription = await subscriptionProvider.SubscribeAsync(
                actorId,
                (Func<EventEnvelope, Task>)publicationObserver.ObserveAsync);
        }

        var actor = await CreateActorAsync(
            actorId,
            eventStore,
            snapshotStore,
            publicationStateStore,
            config,
            provider,
            toolExecutionPort,
            secretVault,
            streams,
            initialize: true);

        var preMeasurementAppendLedger = isRecovery
            ? eventStore.CommittedEventsSnapshot()
            : [];
        eventStore.Reset();
        snapshotStore.Reset();
        publicationStateStore.ResetMetrics();

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
        IReadOnlyList<StateEvent> publicationsBeforeRecovery = [];
        IReadOnlyList<EventEnvelope> publicationEnvelopesBeforeRecovery = [];
        GeneratedSemanticEvidenceSnapshot? generatedBeforeRecovery = null;
        MeasurementRoleCurrentStateReadModel? materializedBeforeRecovery = null;
        GeneratedSemanticEvidenceRecorder? recoveryGeneratedRecorder = null;
        CompactionEvidence? compactionBeforeRecovery = null;

        if (isRecovery)
        {
            if (!crashFence.HasValue)
                throw new InvalidOperationException("Crash-recovery workload requires an append fence.");
            eventStore.FailAfterSuccessfulAppends = crashFence.Value;
            try
            {
                await DispatchAsync(actor, actorId, CreateRequest(workload, sampleId));
            }
            catch (SimulatedProcessCrashException ex)
            {
                failure = ex;
            }

            firstToken = actor.Agent.FirstTokenLatency;

            snapshotStore.RejectCrashDeactivationSave();
            await actor.LocalActor.DeactivateAsync();
            snapshotStore.ResumeSaves();
            await actor.Services.DisposeAsync();
            var validationStartedAt = Stopwatch.GetTimestamp();
            await publicationObserver!.DrainAsync(streams.GetStream(actorId));
            committedBeforeRecovery = eventStore.CommittedEventsSnapshot();
            durableBeforeRecovery = await baseStore.GetEventsAsync(actorId);
            publicationsBeforeRecovery = publicationObserver.PublishedEventsSnapshot();
            publicationEnvelopesBeforeRecovery = publicationObserver.PublishedEnvelopesSnapshot();
            generatedBeforeRecovery = phaseOneGeneratedRecorder!.Snapshot();
            materializedBeforeRecovery = await currentStateReader!.GetAsync(actorId);
            compactionBeforeRecovery = eventStore.CompactionSnapshot();
            excludedRecoveryValidationDuration += Stopwatch.GetElapsedTime(validationStartedAt);
            eventStore.FailAfterSuccessfulAppends = null;
            recoveryGeneratedRecorder = new GeneratedSemanticEvidenceRecorder(sessionId, "recovery");
            var recoveryProvider = new WorkloadProviderFactory(workload, recoveryGeneratedRecorder);
            recoveredActor = await CreateActorAsync(
                actorId,
                eventStore,
                snapshotStore,
                publicationStateStore,
                config,
                recoveryProvider,
                toolExecutionPort,
                secretVault,
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
        var publicationStateMetrics = publicationStateStore.SnapshotMetrics();

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
            await publicationObserver!.DrainAsync(streams.GetStream(actorId));
            var freshActor = await CreateActorAsync(
                actorId,
                eventStore,
                snapshotStore,
                publicationStateStore,
                config,
                new WorkloadProviderFactory(workload),
                toolExecutionPort,
                secretVault,
                streams,
                initialize: false);
            var freshActivationStateSha256 = Convert.ToHexString(
                SHA256.HashData(freshActor.Agent.State.ToByteArray()));
            await freshActor.LocalActor.DeactivateAsync();
            await freshActor.Services.DisposeAsync();
            await publicationObserver.DrainAsync(streams.GetStream(actorId));
            var finalDurableEvents = await baseStore.GetEventsAsync(actorId);
            var finalPublicationEnvelopes = publicationObserver.PublishedEnvelopesSnapshot();
            var allCommittedEvents = eventStore.CommittedEventsSnapshot();
            var allPublicationEvents = publicationObserver.PublishedEventsSnapshot();
            var finalStoreVersion = await baseStore.GetVersionAsync(actorId);
            var finalCheckpoint = publicationStateStore.LatestStateSnapshot()
                ?? throw new InvalidOperationException("Recovery verification requires a publication checkpoint.");
            var finalSnapshot = snapshotStore.LatestSnapshot();
            var finalCompaction = eventStore.CompactionSnapshot();
            var finalMaterializedState = await VerifyMaterializedCurrentStateAsync(
                actorId,
                currentStateReader!,
                currentStateMaterializer!,
                projectionContext!,
                durableBeforeRecovery,
                finalDurableEvents,
                publicationEnvelopesBeforeRecovery,
                finalPublicationEnvelopes,
                materializedBeforeRecovery);
            crashRecovery = AnalyzeCrashRecovery(
                crashFence!.Value,
                preMeasurementAppendLedger,
                committedBeforeRecovery,
                durableBeforeRecovery,
                allCommittedEvents,
                finalDurableEvents,
                publicationsBeforeRecovery,
                allPublicationEvents,
                finalPublicationEnvelopes,
                generatedBeforeRecovery!,
                recoveryGeneratedRecorder!.Snapshot(),
                compactionBeforeRecovery!,
                finalCompaction,
                finalSnapshot,
                finalCheckpoint,
                finalStoreVersion,
                config.RetainedEventsAfterSnapshot,
                freshActivationStateSha256,
                finalMaterializedState);
        }
        else
        {
            await DrainStreamAsync(streams, actorId);
        }

        if (publicationSubscription != null)
            await publicationSubscription.DisposeAsync();
        if (projectionServices != null)
            await projectionServices.DisposeAsync();

        if (isRecovery && failure == null)
            throw new InvalidOperationException("Crash-recovery workload did not hit its configured append failure fence.");
        if (string.Equals(workload.Kind, "tool", StringComparison.Ordinal) &&
            toolExecutionPort.ExecutionCount != workload.ToolCalls)
        {
            throw new InvalidOperationException(
                $"Tool workload '{workload.Name}' executed {toolExecutionPort.ExecutionCount} calls; " +
                $"expected {workload.ToolCalls}.");
        }

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
            publicationStateMetrics.LoadCalls,
            publicationStateMetrics.InitializeCalls,
            publicationStateMetrics.InitializeMutations,
            publicationStateMetrics.AdvanceCalls,
            publicationStateMetrics.AdvanceMutations,
            publicationStateMetrics.FailureRecordCalls,
            publicationStateMetrics.FailureRecordMutations,
            publicationStateMetrics.SerializedWriteBytes,
            publicationStateMetrics.TotalDuration.TotalMilliseconds,
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
        var publicationStateStore = new InMemoryCommittedStatePublicationStateStore();
        var toolExecutionPort = new MeasurementToolExecutionPort();
        var secretVault = new InMemorySecretVault();
        var streams = CreateStreamProvider();
        var actor = await CreateActorAsync(
            actorId,
            eventStore,
            snapshotStore,
            publicationStateStore,
            config,
            new WorkloadProviderFactory(workload),
            toolExecutionPort,
            secretVault,
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
                    publicationStateStore,
                    config,
                    new WorkloadProviderFactory(workload),
                    toolExecutionPort,
                    secretVault,
                    streams,
                    initialize: false);
                await DispatchAsync(recoveredActor, actorId, CreateRequest(workload, sampleId));
            }
            else
            {
                await DispatchAsync(actor, actorId, CreateRequest(workload, sampleId));
            }

            if (string.Equals(workload.Kind, "tool", StringComparison.Ordinal) &&
                toolExecutionPort.ExecutionCount != workload.ToolCalls)
            {
                throw new InvalidOperationException(
                    $"Tool control workload '{workload.Name}' executed {toolExecutionPort.ExecutionCount} calls; " +
                    $"expected {workload.ToolCalls}.");
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

    private static ServiceProvider CreateMeasurementProjectionServices()
    {
        var services = new ServiceCollection();
        services.AddProjectionReadModelRuntime();
        services.AddInMemoryDocumentProjectionStore<MeasurementRoleCurrentStateReadModel, string>(
            static readModel => readModel.Id);
        services.AddSingleton<IProjectionClock, SystemProjectionClock>();
        services.AddCurrentStateProjectionMaterializer<
            MeasurementRoleCurrentStateProjectionContext,
            MeasurementRoleCurrentStateProjector>();
        return services.BuildServiceProvider();
    }

    private static async Task<MaterializedCurrentStateEvidence> VerifyMaterializedCurrentStateAsync(
        string actorId,
        IProjectionDocumentReader<MeasurementRoleCurrentStateReadModel, string> reader,
        ICurrentStateProjectionMaterializer<MeasurementRoleCurrentStateProjectionContext> materializer,
        MeasurementRoleCurrentStateProjectionContext context,
        IReadOnlyList<StateEvent> durableBeforeRecovery,
        IReadOnlyList<StateEvent> finalDurableEvents,
        IReadOnlyList<EventEnvelope> publicationEnvelopesBeforeRecovery,
        IReadOnlyList<EventEnvelope> finalPublicationEnvelopes,
        MeasurementRoleCurrentStateReadModel? materializedBeforeRecovery)
    {
        var phaseOne = BuildMaterializedCurrentStateCheckpoint(
            actorId,
            durableBeforeRecovery,
            publicationEnvelopesBeforeRecovery,
            materializedBeforeRecovery);
        var materializedFinal = await reader.GetAsync(actorId);
        var final = BuildMaterializedCurrentStateCheckpoint(
            actorId,
            finalDurableEvents,
            finalPublicationEnvelopes,
            materializedFinal);

        if (materializedFinal == null)
            return new MaterializedCurrentStateEvidence(phaseOne, final, false, false);

        var latestPublication = FindLatestCommittedPublication(finalPublicationEnvelopes);
        var baseline = materializedFinal.ToByteString();
        await materializer.ProjectAsync(context, latestPublication.Envelope);
        var afterDuplicate = await reader.GetAsync(actorId);
        var duplicateWriteIdempotent = afterDuplicate?.ToByteString().Equals(baseline) == true;

        var stalePublication = finalPublicationEnvelopes
            .Select(TryCreateCommittedPublication)
            .Where(static publication => publication != null)
            .Cast<CommittedPublicationSnapshot>()
            .Where(publication => publication.StateVersion < latestPublication.StateVersion)
            .OrderByDescending(static publication => publication.StateVersion)
            .FirstOrDefault();
        var staleWriteDidNotOverwrite = false;
        if (stalePublication != null && afterDuplicate != null)
        {
            var duplicateBaseline = afterDuplicate.ToByteString();
            await materializer.ProjectAsync(context, stalePublication.Envelope);
            var afterStale = await reader.GetAsync(actorId);
            staleWriteDidNotOverwrite = afterStale?.ToByteString().Equals(duplicateBaseline) == true;
        }

        return new MaterializedCurrentStateEvidence(
            phaseOne,
            final,
            duplicateWriteIdempotent,
            staleWriteDidNotOverwrite);
    }

    private static MaterializedCurrentStateCheckpoint BuildMaterializedCurrentStateCheckpoint(
        string actorId,
        IReadOnlyList<StateEvent> durableEvents,
        IReadOnlyList<EventEnvelope> publicationEnvelopes,
        MeasurementRoleCurrentStateReadModel? materialized)
    {
        var latestDurable = durableEvents.MaxBy(static stateEvent => stateEvent.Version)
            ?? throw new InvalidOperationException("Current-state verification requires a durable event.");
        var latestPublication = FindLatestCommittedPublication(publicationEnvelopes);
        var publishedFacts = BuildRoleCurrentStateFacts(actorId, latestPublication);
        var materializedFacts = materialized == null
            ? null
            : new RoleCurrentStateFacts(
                materialized.ActorId,
                materialized.StateVersion,
                materialized.LastEventId,
                materialized.StateRootSha256,
                materialized.TrackedSessionCount,
                materialized.TerminalSessionCount,
                materialized.CompletedSessionCount,
                materialized.FailedSessionCount,
                materialized.BlockedSessionCount,
                materialized.MaxProgressSequence,
                materialized.SessionId,
                materialized.FinalContentSha256,
                materialized.UsageSha256);
        var durableIdentity = new StateEventIdentity(latestDurable.Version, latestDurable.EventId);
        return new MaterializedCurrentStateCheckpoint(
            materialized != null,
            durableIdentity,
            publishedFacts,
            materializedFacts,
            durableIdentity.StateVersion == publishedFacts.StateVersion &&
            string.Equals(durableIdentity.EventId, publishedFacts.LastEventId, StringComparison.Ordinal),
            materializedFacts == publishedFacts);
    }

    private static CommittedPublicationSnapshot FindLatestCommittedPublication(
        IReadOnlyList<EventEnvelope> envelopes) =>
        envelopes
            .Select(TryCreateCommittedPublication)
            .Where(static publication => publication != null)
            .Cast<CommittedPublicationSnapshot>()
            .MaxBy(static publication => publication.StateVersion)
        ?? throw new InvalidOperationException("Current-state verification requires a committed publication.");

    private static CommittedPublicationSnapshot? TryCreateCommittedPublication(EventEnvelope envelope)
    {
        if (!CommittedStateEventEnvelope.TryUnpackState<RoleGAgentState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent == null ||
            state == null)
        {
            return null;
        }

        return new CommittedPublicationSnapshot(envelope, stateEvent.Version, stateEvent.EventId, state);
    }

    private static RoleCurrentStateFacts BuildRoleCurrentStateFacts(
        string actorId,
        CommittedPublicationSnapshot publication)
    {
        var sessions = publication.State.Sessions.Values;
        var onlySession = publication.State.Sessions.Count == 1
            ? publication.State.Sessions.Single()
            : default(KeyValuePair<string, RoleChatSessionState>);
        return new RoleCurrentStateFacts(
            actorId,
            publication.StateVersion,
            publication.EventId,
            Convert.ToHexString(SHA256.HashData(publication.State.ToByteArray())),
            sessions.Count,
            sessions.Count(static session =>
                session.Outcome != RoleChatSessionOutcome.Unspecified),
            sessions.Count(static session =>
                session.Outcome == RoleChatSessionOutcome.Completed),
            sessions.Count(static session =>
                session.Outcome == RoleChatSessionOutcome.Failed),
            sessions.Count(static session =>
                session.Outcome == RoleChatSessionOutcome.Blocked),
            sessions.Count == 0 ? 0 : sessions.Max(static session => session.LastProgressSequence),
            onlySession.Key ?? string.Empty,
            StreamingSemanticEvidence.HashTextContent(onlySession.Value?.FinalContent ?? string.Empty),
            onlySession.Value?.Usage == null
                ? string.Empty
                : StreamingSemanticEvidence.HashUsage(onlySession.Value.Usage));
    }

    private static CrashRecoveryObservation AnalyzeCrashRecovery(
        int fence,
        IReadOnlyList<StateEvent> preMeasurementAppendLedger,
        IReadOnlyList<StateEvent> committedBeforeRecovery,
        IReadOnlyList<StateEvent> durableBeforeRecovery,
        IReadOnlyList<StateEvent> allCommitted,
        IReadOnlyList<StateEvent> finalDurableEvents,
        IReadOnlyList<StateEvent> publicationsBeforeRecovery,
        IReadOnlyList<StateEvent> allPublications,
        IReadOnlyList<EventEnvelope> finalPublicationEnvelopes,
        GeneratedSemanticEvidenceSnapshot generatedBeforeRecovery,
        GeneratedSemanticEvidenceSnapshot generatedDuringRecovery,
        CompactionEvidence compactionBeforeRecovery,
        CompactionEvidence finalCompaction,
        EventSourcingSnapshot<RoleGAgentState>? finalSnapshot,
        CommittedStatePublicationState finalCheckpoint,
        long finalStoreVersion,
        int retainedEventsAfterSnapshot,
        string freshActivationStateSha256,
        MaterializedCurrentStateEvidence materializedCurrentState)
    {
        var preCommitted = BuildProgressFacts(committedBeforeRecovery);
        var preDurable = BuildProgressFacts(durableBeforeRecovery);
        var finalCommitted = BuildProgressFacts(allCommitted);
        var finalDurable = BuildProgressFacts(finalDurableEvents);
        var prePublished = BuildProgressFacts(publicationsBeforeRecovery);
        var finalPublished = BuildProgressFacts(allPublications);
        var recoveryCommittedEvents = allCommitted.Skip(committedBeforeRecovery.Count).ToArray();
        var recoveryCommitted = BuildProgressFacts(recoveryCommittedEvents);
        var recoveryPublished = BuildProgressFacts(allPublications.Skip(publicationsBeforeRecovery.Count));

        var preCommittedIds = preCommitted.Select(static fact => fact.EventId).ToHashSet(StringComparer.Ordinal);
        var preDurableIds = preDurable.Select(static fact => fact.EventId).ToHashSet(StringComparer.Ordinal);
        var prePublishedIds = prePublished.Select(static fact => fact.EventId).ToHashSet(StringComparer.Ordinal);
        var preSequences = preCommitted.Select(static fact => fact.SequenceKey).ToHashSet(StringComparer.Ordinal);
        var prePayloads = preCommitted.Select(static fact => fact.PayloadFingerprint).ToHashSet(StringComparer.Ordinal);

        var phaseOneDurableTailIds = committedBeforeRecovery
            .Where(stateEvent => stateEvent.Version > compactionBeforeRecovery.CompactedThroughVersion)
            .Select(static stateEvent => stateEvent.EventId)
            .ToHashSet(StringComparer.Ordinal);
        var durableReadbackMissing = phaseOneDurableTailIds.Except(
            durableBeforeRecovery.Select(static stateEvent => stateEvent.EventId)).LongCount();
        var phaseOnePublicationMissing = preCommittedIds.Except(prePublishedIds).LongCount();
        var finalAppendLedger = preMeasurementAppendLedger.Concat(allCommitted).ToArray();
        var finalLedgerIdentities = BuildEventIdentities(finalAppendLedger, "append ledger");
        var finalDurableIdentities = BuildEventIdentities(finalDurableEvents, "final durable readback");
        var finalPublicationIdentities = BuildEventIdentities(allPublications, "committed publication");
        var expectedDurableTailIdentities = finalLedgerIdentities
            .Where(identity => identity.StateVersion > finalCompaction.CompactedThroughVersion)
            .ToHashSet();
        var publishedTailIdentities = finalPublicationIdentities
            .Where(identity => identity.StateVersion > finalCompaction.CompactedThroughVersion)
            .ToHashSet();
        var ledgerToDurableMissing = expectedDurableTailIdentities.Except(finalDurableIdentities).LongCount();
        var durableToLedgerUnexpected = finalDurableIdentities.Except(expectedDurableTailIdentities).LongCount();
        var durableToPublicationMissing = finalDurableIdentities.Except(publishedTailIdentities).LongCount();
        var publicationToDurableUnexpected = publishedTailIdentities.Except(finalDurableIdentities).LongCount();
        var committedIdentityOverlap = recoveryCommitted.LongCount(fact => preCommittedIds.Contains(fact.EventId));
        var publicationIdentityOverlap = recoveryPublished.LongCount(fact => prePublishedIds.Contains(fact.EventId));
        var sequenceOverlap = recoveryCommitted.LongCount(fact => preSequences.Contains(fact.SequenceKey));
        var payloadOverlapFacts = recoveryCommitted
            .Where(fact => prePayloads.Contains(fact.PayloadFingerprint))
            .ToArray();
        var phaseOneCommittedSemantics = BuildProviderComparableSemanticOperations(
            committedBeforeRecovery,
            "phase-one");
        var recoveryCommittedSemantics = BuildProviderComparableSemanticOperations(
            recoveryCommittedEvents,
            "recovery");
        var phaseOneAttemptLocalTail = CountOperationDifference(
            generatedBeforeRecovery.Operations,
            phaseOneCommittedSemantics);
        var phaseOneCommittedWithoutGenerated = CountOperationDifference(
            phaseOneCommittedSemantics,
            generatedBeforeRecovery.Operations);
        var recoveryGeneratedToCommittedMissing = CountOperationDifference(
            generatedDuringRecovery.Operations,
            recoveryCommittedSemantics);
        var recoveryCommittedWithoutGenerated = CountOperationDifference(
            recoveryCommittedSemantics,
            generatedDuringRecovery.Operations);
        var materializedFinal = materializedCurrentState.Final.MaterializedReadModel;
        var recoveredUserVisibleSemantics = new RecoveredUserVisibleSemanticEvidence(
            generatedDuringRecovery.SessionId,
            generatedDuringRecovery.TextDeltaCount,
            generatedDuringRecovery.GeneratedTextSha256,
            materializedFinal?.FinalContentSha256 ?? string.Empty,
            generatedDuringRecovery.GeneratedUsageSha256,
            materializedFinal?.UsageSha256 ?? string.Empty,
            materializedFinal != null &&
            string.Equals(
                generatedDuringRecovery.GeneratedTextSha256,
                materializedFinal.FinalContentSha256,
                StringComparison.Ordinal),
            materializedFinal != null &&
            string.Equals(
                generatedDuringRecovery.GeneratedUsageSha256,
                materializedFinal.UsageSha256,
                StringComparison.Ordinal));

        var durableAuthority = BuildDurableAuthorityEvidence(
            finalAppendLedger,
            finalDurableEvents,
            allPublications,
            finalPublicationEnvelopes,
            finalCompaction,
            finalSnapshot,
            finalCheckpoint,
            finalStoreVersion,
            retainedEventsAfterSnapshot,
            freshActivationStateSha256);

        if (durableReadbackMissing != 0 || phaseOnePublicationMissing != 0 ||
            ledgerToDurableMissing != 0 || durableToLedgerUnexpected != 0 ||
            durableToPublicationMissing != 0 || publicationToDurableUnexpected != 0)
        {
            throw new InvalidOperationException(
                $"Recovery fence {fence} failed committed/publication reconciliation: " +
                $"durableMissing={durableReadbackMissing}, phaseOnePublicationMissing={phaseOnePublicationMissing}, " +
                $"ledgerToDurableMissing={ledgerToDurableMissing}, " +
                $"durableToLedgerUnexpected={durableToLedgerUnexpected}, " +
                $"durableToPublicationMissing={durableToPublicationMissing}, " +
                $"publicationToDurableUnexpected={publicationToDurableUnexpected}, " +
                $"compactedThrough={finalCompaction.CompactedThroughVersion}.");
        }

        return new CrashRecoveryObservation(
            fence,
            preCommitted.LongCount(),
            preDurable.LongCount(),
            prePublished.LongCount(),
            recoveryCommitted.LongCount(),
            recoveryPublished.LongCount(),
            finalCommitted.LongCount(),
            finalDurable.LongCount(),
            finalPublished.LongCount(),
            finalLedgerIdentities.Count,
            finalDurableIdentities.Count,
            finalPublicationIdentities.Count,
            committedIdentityOverlap,
            publicationIdentityOverlap,
            sequenceOverlap,
            payloadOverlapFacts.LongLength,
            payloadOverlapFacts.Sum(static fact => fact.SerializedBytes),
            durableReadbackMissing,
            phaseOnePublicationMissing,
            ledgerToDurableMissing,
            durableToLedgerUnexpected,
            durableToPublicationMissing,
            publicationToDurableUnexpected,
            generatedBeforeRecovery.Operations.Count,
            phaseOneCommittedSemantics.Count,
            generatedDuringRecovery.Operations.Count,
            recoveryCommittedSemantics.Count,
            phaseOneAttemptLocalTail,
            phaseOneCommittedWithoutGenerated,
            recoveryGeneratedToCommittedMissing,
            recoveryCommittedWithoutGenerated,
            durableAuthority,
            materializedCurrentState,
            recoveredUserVisibleSemantics);
    }

    private static DurableAuthorityEvidence BuildDurableAuthorityEvidence(
        IReadOnlyList<StateEvent> appendLedger,
        IReadOnlyList<StateEvent> durableEvents,
        IReadOnlyList<StateEvent> publications,
        IReadOnlyList<EventEnvelope> publicationEnvelopes,
        CompactionEvidence compaction,
        EventSourcingSnapshot<RoleGAgentState>? snapshot,
        CommittedStatePublicationState checkpoint,
        long storeVersion,
        int retainedEventsAfterSnapshot,
        string freshActivationStateSha256)
    {
        var ledger = BuildEventIdentities(appendLedger, "append ledger");
        var durable = BuildEventIdentities(durableEvents, "durable tail");
        var published = BuildEventIdentities(publications, "committed publication");
        var compactedPrefix = ledger
            .Where(identity => identity.StateVersion <= compaction.CompactedThroughVersion)
            .ToHashSet();
        var expectedTail = ledger
            .Where(identity => identity.StateVersion > compaction.CompactedThroughVersion)
            .ToHashSet();
        var latestPublication = FindLatestCommittedPublication(publicationEnvelopes);
        var latestPublicationStateSha256 = Convert.ToHexString(
            SHA256.HashData(latestPublication.State.ToByteArray()));
        var snapshotVersion = snapshot?.Version ?? 0;
        var snapshotStateSha256 = snapshot == null
            ? string.Empty
            : Convert.ToHexString(SHA256.HashData(snapshot.State.ToByteArray()));
        var snapshotPublication = snapshot == null
            ? null
            : publicationEnvelopes
                .Select(TryCreateCommittedPublication)
                .Where(static publication => publication != null)
                .Cast<CommittedPublicationSnapshot>()
                .LastOrDefault(publication => publication.StateVersion == snapshot.Version);
        var snapshotPublicationSha256 = snapshotPublication == null
            ? string.Empty
            : Convert.ToHexString(SHA256.HashData(snapshotPublication.State.ToByteArray()));
        var expectedVersions = Enumerable.Range(
                checked((int)compaction.CompactedThroughVersion + 1),
                checked((int)(storeVersion - compaction.CompactedThroughVersion)))
            .Select(static version => (long)version)
            .ToArray();
        var durableVersions = durableEvents.Select(static stateEvent => stateEvent.Version).ToArray();
        var snapshotCoversCompaction = compaction.CompactedThroughVersion == 0 ||
            snapshot != null &&
            snapshot.Version >= compaction.CompactedThroughVersion &&
            compaction.CompactedThroughVersion == snapshot.Version - retainedEventsAfterSnapshot &&
            compaction.PublishedVersionAtCompaction >= compaction.CompactedThroughVersion;
        var checkpointMatchesAuthority = checkpoint.Initialized &&
            checkpoint.PublishedVersion == storeVersion &&
            latestPublication.StateVersion == storeVersion &&
            string.Equals(checkpoint.PublishedEventId, latestPublication.EventId, StringComparison.Ordinal);

        var evidence = new DurableAuthorityEvidence(
            storeVersion,
            snapshotVersion,
            snapshotStateSha256,
            checkpoint.PublishedVersion,
            checkpoint.PublishedEventId,
            checkpoint.Revision,
            compaction.CompactedThroughVersion,
            compaction.PublishedVersionAtCompaction,
            retainedEventsAfterSnapshot,
            ledger.Count,
            published.Count,
            compactedPrefix.Count,
            expectedTail.Count,
            durable.Count,
            compaction.DeletedEvents,
            ledger.Except(published).LongCount(),
            published.Except(ledger).LongCount(),
            expectedTail.Except(durable).LongCount(),
            durable.Except(expectedTail).LongCount(),
            snapshotCoversCompaction,
            snapshot == null || string.Equals(
                snapshotStateSha256,
                snapshotPublicationSha256,
                StringComparison.Ordinal),
            checkpointMatchesAuthority,
            durableVersions.SequenceEqual(expectedVersions),
            string.Equals(
                freshActivationStateSha256,
                latestPublicationStateSha256,
                StringComparison.Ordinal),
            freshActivationStateSha256,
            latestPublicationStateSha256);

        if (evidence.LedgerToPublicationMissingEvents != 0 ||
            evidence.PublicationToLedgerUnexpectedEvents != 0 ||
            evidence.TailLedgerToDurableMissingEvents != 0 ||
            evidence.DurableToTailUnexpectedEvents != 0 ||
            evidence.CompactedBySnapshotEvents != evidence.CompactionDeletedEvents ||
            !evidence.SnapshotCoversCompaction ||
            !evidence.SnapshotStateMatchesCommittedPublication ||
            !evidence.CheckpointMatchesAuthority ||
            !evidence.RetainedTailVersionsContinuous ||
            !evidence.FreshActivationStateMatchesLatestPublication)
        {
            throw new InvalidOperationException(
                "Recovery durable authority evidence is inconsistent with snapshot/compaction semantics.");
        }

        return evidence;
    }

    private static IReadOnlyList<string> BuildProviderComparableSemanticOperations(
        IEnumerable<StateEvent> events,
        string attemptId)
    {
        var operations = new List<string>();
        long ordinal = 0;

        void Add(RoleChatSessionProgressedEvent progress)
        {
            var fingerprint = StreamingSemanticEvidence.FromCommittedProgress(progress);
            if (fingerprint == null)
                return;
            operations.Add(StreamingSemanticEvidence.BuildOperationEvidence(
                progress.SessionId,
                attemptId,
                ++ordinal,
                fingerprint));
        }

        foreach (var stateEvent in events)
        {
            if (stateEvent.EventData?.Is(RoleChatSessionProgressedEvent.Descriptor) == true)
            {
                Add(stateEvent.EventData.Unpack<RoleChatSessionProgressedEvent>());
                continue;
            }

            if (stateEvent.EventData?.Is(RoleChatSessionCompletedEvent.Descriptor) != true)
                continue;
            var completed = stateEvent.EventData.Unpack<RoleChatSessionCompletedEvent>();
            foreach (var terminalProgress in completed.TerminalProgress)
                Add(terminalProgress);
        }

        return operations;
    }

    private static long CountOperationDifference(
        IReadOnlyCollection<string> source,
        IReadOnlyCollection<string> comparison)
    {
        var comparisonKeys = comparison.ToHashSet(StringComparer.Ordinal);
        if (comparisonKeys.Count != comparison.Count)
            throw new InvalidOperationException("Semantic operation evidence contains duplicate identities.");
        return source.LongCount(operation => !comparisonKeys.Contains(operation));
    }

    private static HashSet<StateEventIdentity> BuildEventIdentities(
        IReadOnlyCollection<StateEvent> events,
        string source)
    {
        if (events.Any(static stateEvent => string.IsNullOrWhiteSpace(stateEvent.EventId)))
            throw new InvalidOperationException($"Recovery {source} contains an empty StateEvent event ID.");
        var identities = events
            .Select(static stateEvent => new StateEventIdentity(
                stateEvent.Version,
                stateEvent.EventId))
            .ToHashSet();
        if (identities.Count != events.Count)
            throw new InvalidOperationException($"Recovery {source} contains duplicate StateEvent identities.");
        if (events.Select(static stateEvent => stateEvent.EventId).Distinct(StringComparer.Ordinal).Count()
            != events.Count ||
            events.Select(static stateEvent => stateEvent.Version).Distinct().Count() != events.Count)
        {
            throw new InvalidOperationException(
                $"Recovery {source} contains duplicate event IDs or state versions.");
        }
        return identities;
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
                .Where(static workload => workload.StreamShape.StartsWith(
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
                    var authority = observation.DurableAuthority;
                    if (observation.LedgerToDurableMissingEvents != 0 ||
                        observation.DurableToLedgerUnexpectedEvents != 0 ||
                        observation.DurableToCommittedPublicationMissingEvents != 0 ||
                        observation.CommittedPublicationToDurableUnexpectedEvents != 0 ||
                        observation.FinalAppendLedgerEvents != observation.FinalCommittedPublicationEvents ||
                        authority.LedgerToPublicationMissingEvents != 0 ||
                        authority.PublicationToLedgerUnexpectedEvents != 0 ||
                        authority.TailLedgerToDurableMissingEvents != 0 ||
                        authority.DurableToTailUnexpectedEvents != 0 ||
                        authority.CompactedBySnapshotEvents != authority.CompactionDeletedEvents ||
                        !authority.SnapshotCoversCompaction ||
                        !authority.SnapshotStateMatchesCommittedPublication ||
                        !authority.CheckpointMatchesAuthority ||
                        !authority.RetainedTailVersionsContinuous ||
                        !authority.FreshActivationStateMatchesLatestPublication ||
                        observation.PhaseOneGeneratedSemanticEvents <= 0 ||
                        observation.PhaseOneAttemptLocalGeneratedTailEvents < 0 ||
                        observation.PhaseOneCommittedWithoutGeneratedEvidence != 0 ||
                        observation.PhaseOneGeneratedSemanticEvents !=
                            observation.PhaseOneCommittedSemanticEvents +
                            observation.PhaseOneAttemptLocalGeneratedTailEvents ||
                        observation.RecoveryGeneratedSemanticEvents !=
                            observation.RecoveryCommittedSemanticEvents ||
                        observation.RecoveryGeneratedToCommittedMissingEvents != 0 ||
                        observation.RecoveryCommittedWithoutGeneratedEvidence != 0 ||
                        !IsValidMaterializedCheckpoint(observation.MaterializedCurrentState.PhaseOne) ||
                        !IsValidMaterializedCheckpoint(observation.MaterializedCurrentState.Final) ||
                        !observation.MaterializedCurrentState.DuplicateWriteIdempotent ||
                        !observation.MaterializedCurrentState.StaleWriteDidNotOverwrite ||
                        !string.Equals(
                            observation.RecoveredUserVisibleSemantics.SessionId,
                            observation.MaterializedCurrentState.Final.MaterializedReadModel?.SessionId,
                            StringComparison.Ordinal) ||
                        string.IsNullOrWhiteSpace(
                            observation.RecoveredUserVisibleSemantics.RecoveryGeneratedUsageSha256) ||
                        !observation.RecoveredUserVisibleSemantics.FinalContentMatchesRecoveryGeneration ||
                        !observation.RecoveredUserVisibleSemantics.FinalUsageMatchesRecoveryGeneration)
                    {
                        throw new InvalidOperationException(
                            $"Adapter {adapter.Adapter} fence {observation.Fence} sample {sample.Iteration} " +
                            "contains unreconciled final recovery evidence.");
                    }
                }
            }
        }
    }

    private static bool IsValidMaterializedCheckpoint(MaterializedCurrentStateCheckpoint checkpoint) =>
        checkpoint.ReadModelFound &&
        checkpoint.DurableIdentityMatchesCommittedPublication &&
        checkpoint.ReadModelMatchesCommittedPublication;

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
        ICommittedStatePublicationStateStore publicationStateStore,
        MeasurementConfig config,
        WorkloadProviderFactory provider,
        MeasurementToolExecutionPort toolExecutionPort,
        ISecretVault secretVault,
        InMemoryStreamProvider streams,
        bool initialize)
    {
        var services = new ServiceCollection()
            .AddSingleton<IEventStore>(eventStore)
            .AddSingleton<IEventSourcingSnapshotStore<RoleGAgentState>>(snapshotStore)
            .AddSingleton(publicationStateStore)
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
            toolExecutionPort,
            provider,
            [new StaticToolSource(tools)],
            secretVault)
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

    private static async Task<string> HashFileAsync(string path) =>
        Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant();

    private static string GetProgramSourcePath([CallerFilePath] string sourcePath = "") => sourcePath;
}

public sealed record CommandLineOptions(
    string ConfigPath,
    string OutputPath,
    string Adapter,
    bool VerifyOnly,
    string Measurement,
    string RunPhase)
{
    public const string WriteAmplificationMeasurement = "write-amplification";
    public const string RoleContentionMeasurement = "role-contention";
    public const string ProviderNormalizationMeasurement = "provider-normalization";

    public static CommandLineOptions Parse(IReadOnlyList<string> args)
    {
        string? config = null;
        string? output = null;
        var adapter = "all";
        var verify = false;
        var measurement = WriteAmplificationMeasurement;
        var runPhase = "baseline-pre-3135";
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
                case "--measurement":
                    measurement = RequireValue(args, ref index, "--measurement").Trim().ToLowerInvariant();
                    break;
                case "--run-phase":
                    runPhase = RequireValue(args, ref index, "--run-phase").Trim().ToLowerInvariant();
                    break;
                default:
                    throw new InvalidOperationException($"Unknown argument '{args[index]}'.");
            }
        }

        if (measurement is not WriteAmplificationMeasurement and
            not RoleContentionMeasurement and
            not ProviderNormalizationMeasurement)
            throw new InvalidOperationException($"Unknown measurement '{measurement}'.");
        if (runPhase is not "baseline-pre-3135" and not "post-3135")
            throw new InvalidOperationException($"Unknown run phase '{runPhase}'.");

        config ??= Path.Combine(
            AppContext.BaseDirectory,
            measurement switch
            {
                RoleContentionMeasurement => "role-contention.config.json",
                ProviderNormalizationMeasurement => "provider-normalization.config.json",
                _ => "streaming-write-amplification.config.json",
            });
        output ??= Path.Combine(
            Environment.CurrentDirectory,
            measurement switch
            {
                RoleContentionMeasurement => "role-contention.json",
                ProviderNormalizationMeasurement => "provider-normalization.json",
                _ => "streaming-write-amplification.json",
            });
        return new CommandLineOptions(config, output, adapter, verify, measurement, runPhase);
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
    MeasurementProvenance Provenance,
    EnvironmentFacts Environment,
    MeasurementConfig Config,
    MetricSemantics MetricSemantics,
    IReadOnlyList<AdapterResult> Adapters);

public sealed record MeasurementProvenance(
    string ProgramSha256,
    string ConfigSha256);

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
    long PublicationCheckpointLoadCalls,
    long PublicationCheckpointInitializeCalls,
    long PublicationCheckpointInitializeMutations,
    long PublicationCheckpointAdvanceCalls,
    long PublicationCheckpointAdvanceMutations,
    long PublicationCheckpointFailureRecordCalls,
    long PublicationCheckpointFailureRecordMutations,
    long PublicationCheckpointSerializedWriteBytes,
    double PublicationCheckpointDurationMs,
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
    long PhaseOneCommittedPublicationProgressEvents,
    long RecoveryAppendLedgerProgressEvents,
    long RecoveryCommittedPublicationProgressEvents,
    long FinalAppendLedgerProgressEvents,
    long FinalDurableReadbackProgressEvents,
    long FinalCommittedPublicationProgressEvents,
    long FinalAppendLedgerEvents,
    long FinalDurableReadbackEvents,
    long FinalCommittedPublicationEvents,
    long RecoveryAppendLedgerEventIdentityOverlap,
    long RecoveryCommittedPublicationEventIdentityOverlap,
    long RecoverySequenceOverlap,
    long RecoveryPayloadOverlapEvents,
    long RecoveryPayloadOverlapSerializedBytes,
    long PhaseOneDurableReadbackMissingEvents,
    long PhaseOneCommittedPublicationMissingEvents,
    long LedgerToDurableMissingEvents,
    long DurableToLedgerUnexpectedEvents,
    long DurableToCommittedPublicationMissingEvents,
    long CommittedPublicationToDurableUnexpectedEvents,
    long PhaseOneGeneratedSemanticEvents,
    long PhaseOneCommittedSemanticEvents,
    long RecoveryGeneratedSemanticEvents,
    long RecoveryCommittedSemanticEvents,
    long PhaseOneAttemptLocalGeneratedTailEvents,
    long PhaseOneCommittedWithoutGeneratedEvidence,
    long RecoveryGeneratedToCommittedMissingEvents,
    long RecoveryCommittedWithoutGeneratedEvidence,
    DurableAuthorityEvidence DurableAuthority,
    MaterializedCurrentStateEvidence MaterializedCurrentState,
    RecoveredUserVisibleSemanticEvidence RecoveredUserVisibleSemantics);

public sealed record DurableAuthorityEvidence(
    long StoreVersion,
    long SnapshotVersion,
    string SnapshotStateSha256,
    long PublicationCheckpointVersion,
    string PublicationCheckpointEventId,
    long PublicationCheckpointRevision,
    long CompactedThroughVersion,
    long PublishedVersionAtCompaction,
    long RetainedEventsAfterSnapshot,
    long AppendLedgerEvents,
    long CommittedPublicationEvents,
    long CompactedBySnapshotEvents,
    long ExpectedDurableTailEvents,
    long ActualDurableTailEvents,
    long CompactionDeletedEvents,
    long LedgerToPublicationMissingEvents,
    long PublicationToLedgerUnexpectedEvents,
    long TailLedgerToDurableMissingEvents,
    long DurableToTailUnexpectedEvents,
    bool SnapshotCoversCompaction,
    bool SnapshotStateMatchesCommittedPublication,
    bool CheckpointMatchesAuthority,
    bool RetainedTailVersionsContinuous,
    bool FreshActivationStateMatchesLatestPublication,
    string FreshActivationStateSha256,
    string LatestCommittedPublicationStateSha256);

public sealed record RecoveredUserVisibleSemanticEvidence(
    string SessionId,
    int GeneratedTextDeltaCount,
    string RecoveryGeneratedTextSha256,
    string MaterializedFinalContentSha256,
    string RecoveryGeneratedUsageSha256,
    string MaterializedUsageSha256,
    bool FinalContentMatchesRecoveryGeneration,
    bool FinalUsageMatchesRecoveryGeneration);

public sealed record MaterializedCurrentStateEvidence(
    MaterializedCurrentStateCheckpoint PhaseOne,
    MaterializedCurrentStateCheckpoint Final,
    bool DuplicateWriteIdempotent,
    bool StaleWriteDidNotOverwrite);

public sealed record MaterializedCurrentStateCheckpoint(
    bool ReadModelFound,
    StateEventIdentity DurableAuthority,
    RoleCurrentStateFacts CommittedPublication,
    RoleCurrentStateFacts? MaterializedReadModel,
    bool DurableIdentityMatchesCommittedPublication,
    bool ReadModelMatchesCommittedPublication);

public sealed record StateEventIdentity(long StateVersion, string EventId);

public sealed record RoleCurrentStateFacts(
    string ActorId,
    long StateVersion,
    string LastEventId,
    string StateRootSha256,
    int TrackedSessionCount,
    int TerminalSessionCount,
    int CompletedSessionCount,
    int FailedSessionCount,
    int BlockedSessionCount,
    long MaxProgressSequence,
    string SessionId,
    string FinalContentSha256,
    string UsageSha256);

internal sealed record ResourceControlSample(double CpuMs, long AllocatedBytes);
internal sealed record ProgressFact(
    string EventId,
    string SequenceKey,
    string PayloadFingerprint,
    long SerializedBytes);
internal sealed record GeneratedSemanticEvidenceSnapshot(
    string SessionId,
    string AttemptId,
    IReadOnlyList<string> Operations,
    int TextDeltaCount,
    string GeneratedTextSha256,
    string GeneratedUsageSha256);
internal sealed record CommittedPublicationSnapshot(
    EventEnvelope Envelope,
    long StateVersion,
    string EventId,
    RoleGAgentState State);

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
    Distribution PublicationCheckpointLoads,
    Distribution PublicationCheckpointInitializes,
    Distribution PublicationCheckpointAdvances,
    Distribution PublicationCheckpointFailureRecords,
    Distribution PublicationCheckpointSerializedWriteBytes,
    Distribution PublicationCheckpointDurationMs,
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
        Distribution.From(samples.Select(static sample => (double?)sample.PublicationCheckpointLoadCalls)),
        Distribution.From(samples.Select(static sample => (double?)sample.PublicationCheckpointInitializeCalls)),
        Distribution.From(samples.Select(static sample => (double?)sample.PublicationCheckpointAdvanceCalls)),
        Distribution.From(samples.Select(static sample => (double?)sample.PublicationCheckpointFailureRecordCalls)),
        Distribution.From(samples.Select(static sample => (double?)sample.PublicationCheckpointSerializedWriteBytes)),
        Distribution.From(samples.Select(static sample => (double?)sample.PublicationCheckpointDurationMs)),
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
    Distribution RecoveryCommittedPublicationEventIdentityOverlap,
    Distribution RecoverySequenceOverlap,
    Distribution RecoveryPayloadOverlapEvents,
    Distribution RecoveryPayloadOverlapSerializedBytes,
    Distribution PhaseOneDurableReadbackMissingEvents,
    Distribution PhaseOneCommittedPublicationMissingEvents,
    Distribution LedgerToDurableMissingEvents,
    Distribution DurableToLedgerUnexpectedEvents,
    Distribution DurableToCommittedPublicationMissingEvents,
    Distribution CommittedPublicationToDurableUnexpectedEvents,
    Distribution PhaseOneAttemptLocalGeneratedTailEvents,
    Distribution PhaseOneCommittedWithoutGeneratedEvidence,
    Distribution RecoveryGeneratedToCommittedMissingEvents,
    Distribution RecoveryCommittedWithoutGeneratedEvidence)
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
            Distribution.From(observations.Select(static item => (double?)item.RecoveryCommittedPublicationEventIdentityOverlap)),
            Distribution.From(observations.Select(static item => (double?)item.RecoverySequenceOverlap)),
            Distribution.From(observations.Select(static item => (double?)item.RecoveryPayloadOverlapEvents)),
            Distribution.From(observations.Select(static item => (double?)item.RecoveryPayloadOverlapSerializedBytes)),
            Distribution.From(observations.Select(static item => (double?)item.PhaseOneDurableReadbackMissingEvents)),
            Distribution.From(observations.Select(static item => (double?)item.PhaseOneCommittedPublicationMissingEvents)),
            Distribution.From(observations.Select(static item => (double?)item.LedgerToDurableMissingEvents)),
            Distribution.From(observations.Select(static item => (double?)item.DurableToLedgerUnexpectedEvents)),
            Distribution.From(observations.Select(static item => (double?)item.DurableToCommittedPublicationMissingEvents)),
            Distribution.From(observations.Select(static item => (double?)item.CommittedPublicationToDurableUnexpectedEvents)),
            Distribution.From(observations.Select(static item => (double?)item.PhaseOneAttemptLocalGeneratedTailEvents)),
            Distribution.From(observations.Select(static item => (double?)item.PhaseOneCommittedWithoutGeneratedEvidence)),
            Distribution.From(observations.Select(static item => (double?)item.RecoveryGeneratedToCommittedMissingEvents)),
            Distribution.From(observations.Select(static item => (double?)item.RecoveryCommittedWithoutGeneratedEvidence)));
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
    IEnumerable<IAgentToolSource> toolSources,
    ISecretVault chatToolRecoverySecretVault)
    : RoleGAgent(
        toolExecutionPort: toolExecutionPort,
        llmProviderFactory: llmProviderFactory,
        toolSources: toolSources,
        chatToolRecoverySecretVault: chatToolRecoverySecretVault)
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
    private int _available = 1;

    public Task<IActor?> GetAsync(string id) =>
        Task.FromResult<IActor?>(IsAvailable(id) ? actor : null);

    public Task<bool> ExistsAsync(string id) =>
        Task.FromResult(IsAvailable(id));

    public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
        where TAgent : IAgent =>
        throw new NotSupportedException("The measurement runtime owns one pre-created actor.");

    public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default) =>
        throw new NotSupportedException("The measurement runtime owns one pre-created actor.");

    public async Task DestroyAsync(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!string.Equals(id, actor.Id, StringComparison.Ordinal) ||
            Interlocked.Exchange(ref _available, 0) == 0)
        {
            return;
        }

        await actor.DeactivateAsync(ct);
    }

    public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
        throw new NotSupportedException("The measurement fixture has no topology links.");

    public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
        throw new NotSupportedException("The measurement fixture has no topology links.");

    private bool IsAvailable(string id) =>
        Volatile.Read(ref _available) == 1 &&
        string.Equals(id, actor.Id, StringComparison.Ordinal);
}

internal sealed class WorkloadProviderFactory(
    WorkloadDefinition workload,
    GeneratedSemanticEvidenceRecorder? generatedSemanticEvidence = null)
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
                yield return RecordGenerated(new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = $"call-{index}",
                        Name = $"measurement_tool_{index}",
                        ArgumentsJson = $"{{\"index\":{index}}}",
                    },
                });
                await Task.Yield();
            }
            yield return RecordGenerated(new LLMStreamChunk { IsLast = true, FinishReason = "tool_calls" });
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
                    yield return RecordGenerated(new LLMStreamChunk
                    {
                        DeltaReasoningContent = FixedChunk("reasoning", index, Workload.ChunkCharacters),
                    });
                    await Task.Yield();
                }
                await foreach (var chunk in TextChunks(Workload.TextChunks, Workload.ChunkCharacters, ct))
                    yield return chunk;
                break;
            case "media":
                for (var index = 0; index < Workload.MediaParts; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    yield return RecordGenerated(new LLMStreamChunk
                    {
                        DeltaContentPart = ContentPart.ImageUriPart(
                            $"https://example.invalid/measurement/{index}.png",
                            name: $"measurement-{index}.png"),
                    });
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

        yield return RecordGenerated(new LLMStreamChunk
        {
            IsLast = true,
            FinishReason = "stop",
            Usage = new TokenUsage(32, Math.Max(1, Workload.TextChunks), 32 + Math.Max(1, Workload.TextChunks)),
        });
    }

    private async IAsyncEnumerable<LLMStreamChunk> TextChunks(
        int count,
        int characters,
        [EnumeratorCancellation] CancellationToken ct)
    {
        for (var index = 0; index < count; index++)
        {
            ct.ThrowIfCancellationRequested();
            yield return RecordGenerated(
                new LLMStreamChunk { DeltaContent = FixedChunk("text", index, characters) });
            await Task.Yield();
        }
    }

    private LLMStreamChunk RecordGenerated(LLMStreamChunk chunk)
    {
        generatedSemanticEvidence?.Observe(chunk);
        return chunk;
    }

    private static string FixedChunk(string prefix, int index, int characters)
    {
        var header = $"{prefix}-{index:D3}:";
        return header + new string('x', Math.Max(1, characters - header.Length));
    }
}

internal sealed class GeneratedSemanticEvidenceRecorder(string sessionId, string attemptId)
{
    private readonly object _lock = new();
    private readonly List<string> _operations = new(64);
    private readonly StringBuilder _generatedText = new();
    private long _ordinal;
    private int _textDeltaCount;
    private string _generatedUsageSha256 = string.Empty;

    public void Observe(LLMStreamChunk chunk)
    {
        var generated = StreamingSemanticEvidence.FromGeneratedChunk(chunk);
        if (generated.Count == 0)
            return;
        lock (_lock)
        {
            foreach (var fingerprint in generated)
            {
                _operations.Add(StreamingSemanticEvidence.BuildOperationEvidence(
                    sessionId,
                    attemptId,
                    ++_ordinal,
                    fingerprint));
            }
            if (!string.IsNullOrEmpty(chunk.DeltaContent))
            {
                _generatedText.Append(chunk.DeltaContent);
                _textDeltaCount++;
            }
            if (chunk.Usage != null)
            {
                _generatedUsageSha256 = StreamingSemanticEvidence.HashUsage(
                    new TokenUsagePayload
                    {
                        PromptTokens = chunk.Usage.PromptTokens,
                        CompletionTokens = chunk.Usage.CompletionTokens,
                        TotalTokens = chunk.Usage.TotalTokens,
                    });
            }
        }
    }

    public GeneratedSemanticEvidenceSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new GeneratedSemanticEvidenceSnapshot(
                sessionId,
                attemptId,
                _operations.ToArray(),
                _textDeltaCount,
                StreamingSemanticEvidence.HashTextContent(_generatedText.ToString()),
                _generatedUsageSha256);
        }
    }
}

internal static class StreamingSemanticEvidence
{
    public static IReadOnlyList<string> FromGeneratedChunk(LLMStreamChunk chunk)
    {
        var fingerprints = new List<string>(2);
        if (!string.IsNullOrEmpty(chunk.DeltaContent))
        {
            fingerprints.Add(Fingerprint(
                "text_delta",
                new RoleChatTextDeltaProgress { Delta = chunk.DeltaContent }));
        }
        if (!string.IsNullOrEmpty(chunk.DeltaReasoningContent))
        {
            fingerprints.Add(Fingerprint(
                "reasoning_delta",
                new RoleChatReasoningDeltaProgress { Delta = chunk.DeltaReasoningContent }));
        }
        if (chunk.DeltaContentPart != null)
        {
            fingerprints.Add(Fingerprint(
                "media_part",
                ContentPartProtoMapper.ToProto(chunk.DeltaContentPart)));
        }
        if (chunk.DeltaToolCall != null)
        {
            fingerprints.Add(Fingerprint(
                "tool_start_identity",
                new RoleChatToolStartedProgress
                {
                    CallId = chunk.DeltaToolCall.Id ?? string.Empty,
                    ToolName = chunk.DeltaToolCall.Name ?? string.Empty,
                }));
        }
        if (chunk.Usage != null)
        {
            fingerprints.Add(Fingerprint(
                "usage",
                new TokenUsagePayload
                {
                    PromptTokens = chunk.Usage.PromptTokens,
                    CompletionTokens = chunk.Usage.CompletionTokens,
                    TotalTokens = chunk.Usage.TotalTokens,
                }));
        }
        return fingerprints;
    }

    public static string? FromCommittedProgress(RoleChatSessionProgressedEvent progress) =>
        progress.PayloadCase switch
        {
            RoleChatSessionProgressedEvent.PayloadOneofCase.TextDelta =>
                Fingerprint("text_delta", progress.TextDelta),
            RoleChatSessionProgressedEvent.PayloadOneofCase.ReasoningDelta =>
                Fingerprint("reasoning_delta", progress.ReasoningDelta),
            RoleChatSessionProgressedEvent.PayloadOneofCase.Media =>
                Fingerprint("media_part", progress.Media.Part),
            RoleChatSessionProgressedEvent.PayloadOneofCase.ToolStarted =>
                Fingerprint(
                    "tool_start_identity",
                    new RoleChatToolStartedProgress
                    {
                        CallId = progress.ToolStarted.CallId,
                        ToolName = progress.ToolStarted.ToolName,
                    }),
            RoleChatSessionProgressedEvent.PayloadOneofCase.Usage =>
                Fingerprint("usage", progress.Usage.Usage),
            _ => null,
        };

    public static string BuildOperationEvidence(
        string sessionId,
        string attemptId,
        long ordinal,
        string semanticFingerprint) =>
        $"{sessionId}\u001f{attemptId}\u001f{ordinal}\u001f{semanticFingerprint}";

    public static string HashTextContent(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content ?? string.Empty)));

    public static string HashUsage(TokenUsagePayload usage) => Fingerprint("usage", usage);

    private static string Fingerprint(string kind, IMessage payload)
    {
        var kindBytes = Encoding.UTF8.GetBytes(kind);
        var payloadBytes = payload.ToByteArray();
        var input = new byte[kindBytes.Length + 1 + payloadBytes.Length];
        kindBytes.CopyTo(input, 0);
        input[kindBytes.Length] = 0;
        payloadBytes.CopyTo(input, kindBytes.Length + 1);
        return $"{kind}:{Convert.ToHexString(SHA256.HashData(input))}";
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
    private int _executionCount;

    public int ExecutionCount => Volatile.Read(ref _executionCount);

    public async Task<AgentToolExecutionOutcome> ExecuteAsync(
        AgentToolExecutionRequest request,
        CancellationToken ct = default)
    {
        Interlocked.Increment(ref _executionCount);
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

internal sealed class MeasuringEventStore(
    IEventStore inner,
    bool captureCommittedEvents,
    Func<long?>? publishedVersionProvider = null) : IEventStore
{
    private readonly object _lock = new();
    private readonly List<StateEvent> _committedEvents = new(256);
    private readonly List<CompactionOperation> _compactions = [];
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
                _compactions.Add(new CompactionOperation(
                    toVersion,
                    publishedVersionProvider?.Invoke(),
                    deleted));
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
            _compactions.Clear();
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

    public CompactionEvidence CompactionSnapshot()
    {
        lock (_lock)
        {
            return new CompactionEvidence(
                _compactions.Count,
                _compactions.Count == 0
                    ? 0
                    : _compactions.Max(static operation => operation.CompactedThroughVersion),
                _compactions.Count == 0
                    ? 0
                    : _compactions.Max(static operation => operation.PublishedVersionAtCompaction ?? 0),
                _compactions.Sum(static operation => operation.DeletedEvents));
        }
    }
}

internal sealed record CompactionOperation(
    long CompactedThroughVersion,
    long? PublishedVersionAtCompaction,
    long DeletedEvents);

internal sealed record CompactionEvidence(
    long Calls,
    long CompactedThroughVersion,
    long PublishedVersionAtCompaction,
    long DeletedEvents);

internal sealed class MeasuringCommittedStatePublicationStateStore(
    ICommittedStatePublicationStateStore inner) : ICommittedStatePublicationStateStore
{
    private readonly object _lock = new();
    private PublicationStateMetrics _metrics = new();
    private CommittedStatePublicationState? _latestState;
    private long _lastCountedRevision;

    public long? LatestPublishedVersion
    {
        get
        {
            lock (_lock)
                return _latestState?.PublishedVersion;
        }
    }

    public async Task<CommittedStatePublicationState?> LoadAsync(
        string actorId,
        CancellationToken ct = default)
    {
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            var state = await inner.LoadAsync(actorId, ct);
            CaptureLatest(state);
            return state;
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

    public async Task<CommittedStatePublicationState> InitializeAsync(
        string actorId,
        long baselinePublishedVersion,
        CancellationToken ct = default)
    {
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            var state = await inner.InitializeAsync(actorId, baselinePublishedVersion, ct);
            CaptureMutation(state, static metrics => metrics.InitializeMutations++);
            return state;
        }
        finally
        {
            lock (_lock)
            {
                _metrics.InitializeCalls++;
                _metrics.InitializeDuration += Stopwatch.GetElapsedTime(startedAt);
            }
        }
    }

    public async Task<CommittedStatePublicationState> AdvanceAsync(
        string actorId,
        long expectedPublishedVersion,
        StateEvent publishedEvent,
        CancellationToken ct = default)
    {
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            var state = await inner.AdvanceAsync(actorId, expectedPublishedVersion, publishedEvent, ct);
            CaptureMutation(state, static metrics => metrics.AdvanceMutations++);
            return state;
        }
        finally
        {
            lock (_lock)
            {
                _metrics.AdvanceCalls++;
                _metrics.AdvanceDuration += Stopwatch.GetElapsedTime(startedAt);
            }
        }
    }

    public async Task<CommittedStatePublicationState> RecordFailureAsync(
        string actorId,
        long expectedPublishedVersion,
        StateEvent failedEvent,
        CommittedStatePublicationFailureStage stage,
        Exception error,
        CancellationToken ct = default)
    {
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            var state = await inner.RecordFailureAsync(
                actorId,
                expectedPublishedVersion,
                failedEvent,
                stage,
                error,
                ct);
            CaptureMutation(state, static metrics => metrics.FailureRecordMutations++);
            return state;
        }
        finally
        {
            lock (_lock)
            {
                _metrics.FailureRecordCalls++;
                _metrics.FailureRecordDuration += Stopwatch.GetElapsedTime(startedAt);
            }
        }
    }

    public void ResetMetrics()
    {
        lock (_lock)
        {
            _metrics = new PublicationStateMetrics();
            _lastCountedRevision = _latestState?.Revision ?? 0;
        }
    }

    public PublicationStateMetrics SnapshotMetrics()
    {
        lock (_lock)
            return _metrics with { };
    }

    public CommittedStatePublicationState? LatestStateSnapshot()
    {
        lock (_lock)
            return _latestState?.Clone();
    }

    private void CaptureLatest(CommittedStatePublicationState? state)
    {
        if (state == null)
            return;
        lock (_lock)
            _latestState = state.Clone();
    }

    private void CaptureMutation(
        CommittedStatePublicationState state,
        Action<PublicationStateMetrics> countMutation)
    {
        lock (_lock)
        {
            _latestState = state.Clone();
            if (state.Revision <= _lastCountedRevision)
                return;
            _lastCountedRevision = state.Revision;
            countMutation(_metrics);
            _metrics.SerializedWriteBytes += state.CalculateSize();
        }
    }
}

internal sealed record PublicationStateMetrics
{
    public long LoadCalls { get; set; }
    public long InitializeCalls { get; set; }
    public long InitializeMutations { get; set; }
    public long AdvanceCalls { get; set; }
    public long AdvanceMutations { get; set; }
    public long FailureRecordCalls { get; set; }
    public long FailureRecordMutations { get; set; }
    public long SerializedWriteBytes { get; set; }
    public TimeSpan LoadDuration { get; set; }
    public TimeSpan InitializeDuration { get; set; }
    public TimeSpan AdvanceDuration { get; set; }
    public TimeSpan FailureRecordDuration { get; set; }
    public TimeSpan TotalDuration =>
        LoadDuration + InitializeDuration + AdvanceDuration + FailureRecordDuration;
}

internal sealed class FailureInjectingEventStore(IEventStore inner) : IEventStore
{
    private int _successfulAppends;

    public int? FailAfterSuccessfulAppends { get; set; }
    public int SuccessfulAppends => _successfulAppends;

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

internal sealed record MeasurementRoleCurrentStateProjectionContext(string RootActorId)
    : IProjectionMaterializationContext
{
    public string ProjectionKind => "measurement-role-current-state";
}

internal sealed class MeasurementRoleCurrentStateProjector
    : MappedCurrentStateProjectionMaterializer<
        MeasurementRoleCurrentStateProjectionContext,
        RoleGAgentState,
        MeasurementRoleCurrentStateReadModel>
{
    public MeasurementRoleCurrentStateProjector(
        IProjectionWriteDispatcher<MeasurementRoleCurrentStateReadModel> writeDispatcher,
        IProjectionClock clock)
        : base(writeDispatcher, clock)
    {
    }

    protected override MeasurementRoleCurrentStateReadModel Map(
        MappedCurrentStateProjectionInput<MeasurementRoleCurrentStateProjectionContext, RoleGAgentState> input)
    {
        var sessions = input.State.Sessions.Values;
        var onlySession = input.State.Sessions.Count == 1
            ? input.State.Sessions.Single()
            : default(KeyValuePair<string, RoleChatSessionState>);
        return new MeasurementRoleCurrentStateReadModel
        {
            Id = input.Context.RootActorId,
            ActorId = input.Context.RootActorId,
            StateVersion = input.StateEvent.Version,
            LastEventId = input.StateEvent.EventId ?? string.Empty,
            UpdatedAt = input.ObservedAt,
            StateRootSha256 = Convert.ToHexString(SHA256.HashData(input.State.ToByteArray())),
            TrackedSessionCount = sessions.Count,
            TerminalSessionCount = sessions.Count(static session =>
                session.Outcome != RoleChatSessionOutcome.Unspecified),
            CompletedSessionCount = sessions.Count(static session =>
                session.Outcome == RoleChatSessionOutcome.Completed),
            FailedSessionCount = sessions.Count(static session =>
                session.Outcome == RoleChatSessionOutcome.Failed),
            BlockedSessionCount = sessions.Count(static session =>
                session.Outcome == RoleChatSessionOutcome.Blocked),
            MaxProgressSequence = sessions.Count == 0
                ? 0
                : sessions.Max(static session => session.LastProgressSequence),
            SessionId = onlySession.Key ?? string.Empty,
            FinalContentSha256 = StreamingSemanticEvidence.HashTextContent(
                onlySession.Value?.FinalContent ?? string.Empty),
            UsageSha256 = onlySession.Value?.Usage == null
                ? string.Empty
                : StreamingSemanticEvidence.HashUsage(onlySession.Value.Usage),
        };
    }
}

internal sealed class CommittedPublicationProjectionObserver(
    ICurrentStateProjectionMaterializer<MeasurementRoleCurrentStateProjectionContext> materializer,
    MeasurementRoleCurrentStateProjectionContext context)
{
    private readonly object _lock = new();
    private readonly List<StateEvent> _publishedEvents = new(256);
    private readonly List<EventEnvelope> _publishedEnvelopes = new(256);
    private readonly Dictionary<string, TaskCompletionSource> _drainMarkers = new(StringComparer.Ordinal);

    public async Task ObserveAsync(EventEnvelope envelope)
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
            return;
        }

        if (envelope.Payload?.Is(CommittedStateEventPublished.Descriptor) != true)
            return;
        var published = envelope.Payload.Unpack<CommittedStateEventPublished>();
        if (published.StateEvent != null)
        {
            lock (_lock)
            {
                _publishedEvents.Add(published.StateEvent.Clone());
                _publishedEnvelopes.Add(envelope.Clone());
            }
        }

        await materializer.ProjectAsync(context, envelope);
    }

    public async Task DrainAsync(IStream stream)
    {
        var markerId = $"publication-projection-drain-{Guid.NewGuid():N}";
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
            _drainMarkers.Add(markerId, completion);
        await stream.ProduceAsync(new EventEnvelope
        {
            Id = markerId,
            Payload = Any.Pack(new StringValue { Value = "measurement-publication-projection-drain" }),
            Route = EnvelopeRouteSemantics.CreateDirect("measurement-harness", stream.StreamId),
        });
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    public IReadOnlyList<StateEvent> PublishedEventsSnapshot()
    {
        lock (_lock)
            return _publishedEvents.Select(static stateEvent => stateEvent.Clone()).ToArray();
    }

    public IReadOnlyList<EventEnvelope> PublishedEnvelopesSnapshot()
    {
        lock (_lock)
            return _publishedEnvelopes.Select(static envelope => envelope.Clone()).ToArray();
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
    private EventSourcingSnapshot<TState>? _latestSnapshot;
    private bool _rejectCrashDeactivationSave;

    public async Task<EventSourcingSnapshot<TState>?> LoadAsync(
        string agentId,
        CancellationToken ct = default)
    {
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            var snapshot = await inner.LoadAsync(agentId, ct);
            if (snapshot != null)
            {
                lock (_lock)
                    _latestSnapshot = CloneSnapshot(snapshot);
            }
            return snapshot;
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
        lock (_lock)
        {
            if (_rejectCrashDeactivationSave)
            {
                throw new SimulatedProcessCrashException(
                    "snapshot persistence is unavailable after the injected process crash");
            }
        }
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            await inner.SaveAsync(agentId, snapshot, ct);
            lock (_lock)
            {
                _metrics.SaveCalls++;
                _metrics.SnapshotSerializedBytes += snapshot.State.CalculateSize();
                _latestSnapshot = CloneSnapshot(snapshot);
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

    public EventSourcingSnapshot<TState>? LatestSnapshot()
    {
        lock (_lock)
            return _latestSnapshot == null ? null : CloneSnapshot(_latestSnapshot);
    }

    public void RejectCrashDeactivationSave()
    {
        lock (_lock)
            _rejectCrashDeactivationSave = true;
    }

    public void ResumeSaves()
    {
        lock (_lock)
            _rejectCrashDeactivationSave = false;
    }

    private static EventSourcingSnapshot<TState> CloneSnapshot(EventSourcingSnapshot<TState> snapshot) =>
        new(snapshot.State.Clone(), snapshot.Version);
}

internal sealed record SnapshotMetrics
{
    public long LoadCalls { get; set; }
    public TimeSpan LoadDuration { get; set; }
    public long SaveCalls { get; set; }
    public long SnapshotSerializedBytes { get; set; }
    public TimeSpan SaveDuration { get; set; }
}

internal sealed class SimulatedProcessCrashException : Exception
{
    public SimulatedProcessCrashException(int successfulAppendFence)
        : base($"Simulated process crash after {successfulAppendFence} successful turn append calls.")
    {
    }

    public SimulatedProcessCrashException(string message) : base(message)
    {
    }
}

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
