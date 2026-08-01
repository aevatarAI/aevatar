using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
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
            await using var adapter = await AdapterContext.TryCreateAsync(adapterName);
            if (!adapter.Available)
            {
                adapterResults.Add(new AdapterResult(
                    adapterName,
                    "unavailable",
                    adapter.UnavailableReason,
                    []));
                continue;
            }

            Console.WriteLine($"Measuring adapter={adapterName}");
            var workloadResults = new List<WorkloadResult>();
            foreach (var workload in config.Workloads)
            {
                Console.WriteLine($"  workload={workload.Name}");
                for (var warmup = 0; warmup < config.WarmupIterations; warmup++)
                    _ = await RunSampleAsync(adapter.EventStore!, config, workload, warmup, measured: false);

                var samples = new List<TurnSample>(config.MeasuredIterations);
                for (var iteration = 0; iteration < config.MeasuredIterations; iteration++)
                {
                    samples.Add(await RunSampleAsync(
                        adapter.EventStore!,
                        config,
                        workload,
                        iteration,
                        measured: true));
                }

                workloadResults.Add(new WorkloadResult(
                    workload.Name,
                    workload.Kind,
                    samples,
                    WorkloadSummary.From(samples)));
            }

            adapterResults.Add(new AdapterResult(adapterName, "measured", null, workloadResults));
        }

        var output = new MeasurementOutput(
            1,
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
                "Serialized bytes are StateEvent protobuf sizes observed immediately before adapter append.",
                "Mailbox occupancy is in-flight plus queued chat turns. The harness awaits one dispatch at a time, so max occupancy is one; chunks remain inside that actor turn.",
                "Nearest-rank percentiles: ceil(p * sample_count) - 1 after ascending sort.",
                "CPU and memory are process-level deltas and include runtime noise; allocation uses GC.GetTotalAllocatedBytes(true)."),
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
        bool measured)
    {
        var sampleId = $"{workload.Name}-{(measured ? "m" : "w")}-{iteration}-{Guid.NewGuid():N}";
        var actorId = $"measure-role-{sampleId}";
        var eventStore = new MeasuringEventStore(baseStore);
        var snapshotStore = new MeasuringSnapshotStore<RoleGAgentState>(
            new InMemoryEventSourcingSnapshotStore<RoleGAgentState>());
        var provider = new WorkloadProviderFactory(workload);
        var actor = await CreateActorAsync(
            actorId,
            eventStore,
            snapshotStore,
            config,
            provider,
            initialize: true);

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
        var crashRedoEvents = 0L;
        var crashRedoBytes = 0L;
        var mailboxTurns = 1;
        TimeSpan? firstToken = null;
        ActorFixture? recoveredActor = null;

        if (string.Equals(workload.Kind, "crash_recovery", StringComparison.Ordinal))
        {
            eventStore.FailAfterSuccessfulAppends = config.CrashAfterSuccessfulAppends;
            try
            {
                await DispatchAsync(actor, actorId, CreateRequest(workload, sampleId));
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            var committedBeforeRecovery = eventStore.CommittedEventsSnapshot();
            var redo = committedBeforeRecovery
                .Where(static stateEvent => stateEvent.EventData.Is(RoleChatSessionProgressedEvent.Descriptor))
                .ToArray();
            crashRedoEvents = redo.LongLength;
            crashRedoBytes = redo.Sum(static stateEvent => (long)stateEvent.CalculateSize());
            firstToken = actor.Agent.FirstTokenLatency;

            await actor.LocalActor.DeactivateAsync();
            await actor.Services.DisposeAsync();
            eventStore.FailAfterSuccessfulAppends = null;
            var recoveryProvider = new WorkloadProviderFactory(workload);
            recoveredActor = await CreateActorAsync(
                actorId,
                eventStore,
                snapshotStore,
                config,
                recoveryProvider,
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

        var completion = Stopwatch.GetElapsedTime(startedAt);
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

        if (string.Equals(workload.Kind, "crash_recovery", StringComparison.Ordinal) && failure == null)
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
            managedHeapAfter - managedHeapBefore,
            workingSetAfter - workingSetBefore,
            firstToken?.TotalMilliseconds,
            completion.TotalMilliseconds,
            mailboxTurns,
            1,
            0,
            crashRedoEvents,
            crashRedoBytes,
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

    private static async Task<ActorFixture> CreateActorAsync(
        string actorId,
        MeasuringEventStore eventStore,
        MeasuringSnapshotStore<RoleGAgentState> snapshotStore,
        MeasurementConfig config,
        WorkloadProviderFactory provider,
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

        var streams = new InMemoryStreamProvider(
            new InMemoryStreamOptions(),
            NullLoggerFactory.Instance,
            new InMemoryStreamForwardingRegistry());
        var localActor = new LocalActor(agent, actorId, streams, NullLogger.Instance);
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
        if (config.SnapshotInterval < 1 || config.CrashAfterSuccessfulAppends < 3)
            throw new InvalidOperationException("Snapshot/crash fences are invalid.");

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
    public int CrashAfterSuccessfulAppends { get; init; }
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
    string ResourceDeltas);

public sealed record AdapterResult(
    string Adapter,
    string Status,
    string? UnavailableReason,
    IReadOnlyList<WorkloadResult> Workloads);

public sealed record WorkloadResult(
    string Workload,
    string StreamShape,
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
    double CpuMs,
    long AllocatedBytes,
    long ManagedHeapDeltaBytes,
    long WorkingSetDeltaBytes,
    double? FirstTokenLatencyMs,
    double CompletionLatencyMs,
    int MailboxTurnCount,
    int MailboxMaxOccupancy,
    int MailboxMaxQueued,
    long CrashRedoProgressEvents,
    long CrashRedoSerializedBytes,
    string? InjectedFailureType);

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
    Distribution CpuMs,
    Distribution AllocatedBytes,
    Distribution ManagedHeapDeltaBytes,
    Distribution WorkingSetDeltaBytes,
    Distribution FirstTokenLatencyMs,
    Distribution CompletionLatencyMs,
    Distribution MailboxMaxOccupancy,
    Distribution CrashRedoProgressEvents,
    Distribution CrashRedoSerializedBytes)
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
        Distribution.From(samples.Select(static sample => (double?)sample.CpuMs)),
        Distribution.From(samples.Select(static sample => (double?)sample.AllocatedBytes)),
        Distribution.From(samples.Select(static sample => (double?)sample.ManagedHeapDeltaBytes)),
        Distribution.From(samples.Select(static sample => (double?)sample.WorkingSetDeltaBytes)),
        Distribution.From(samples.Select(static sample => sample.FirstTokenLatencyMs)),
        Distribution.From(samples.Select(static sample => (double?)sample.CompletionLatencyMs)),
        Distribution.From(samples.Select(static sample => (double?)sample.MailboxMaxOccupancy)),
        Distribution.From(samples.Select(static sample => (double?)sample.CrashRedoProgressEvents)),
        Distribution.From(samples.Select(static sample => (double?)sample.CrashRedoSerializedBytes)));
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

internal sealed class MeasuringEventStore(IEventStore inner) : IEventStore
{
    private readonly object _lock = new();
    private readonly List<StateEvent> _committedEvents = [];
    private StoreMetrics _metrics = new();

    public int? FailAfterSuccessfulAppends { get; set; }

    public async Task<EventStoreCommitResult> AppendAsync(
        string agentId,
        IEnumerable<StateEvent> events,
        long expectedVersion,
        CancellationToken ct = default)
    {
        var batch = events.Select(static stateEvent => stateEvent.Clone()).ToArray();
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
            lock (_lock)
            {
                _metrics.SuccessfulAppendCalls++;
                _metrics.CommittedEventCount += batch.LongLength;
                _metrics.CommittedSerializedBytes += batch.Sum(static stateEvent => (long)stateEvent.CalculateSize());
                _committedEvents.AddRange(batch.Select(static stateEvent => stateEvent.Clone()));
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
            return _committedEvents.Select(static stateEvent => stateEvent.Clone()).ToArray();
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
        bool available,
        IEventStore? eventStore,
        string? unavailableReason,
        IConnectionMultiplexer? connection)
    {
        Name = name;
        Available = available;
        EventStore = eventStore;
        UnavailableReason = unavailableReason;
        _connection = connection;
    }

    public string Name { get; }
    public bool Available { get; }
    public IEventStore? EventStore { get; }
    public string? UnavailableReason { get; }

    public static async Task<AdapterContext> TryCreateAsync(string name)
    {
        if (string.Equals(name, "inmemory", StringComparison.Ordinal))
            return new AdapterContext(name, true, new InMemoryEventStore(), null, null);

        var connectionString = Environment.GetEnvironmentVariable("AEVATAR_TEST_GARNET_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new AdapterContext(
                name,
                false,
                null,
                "AEVATAR_TEST_GARNET_CONNECTION_STRING is not set. Start a Redis-protocol Garnet instance and rerun with the variable set.",
                null);
        }

        try
        {
            IConnectionMultiplexer? connection = null;
            try
            {
                connection = await ConnectionMultiplexer.ConnectAsync(connectionString);
                var prefix = $"aevatar:measure:role-stream:{Guid.NewGuid():N}";
                var store = new GarnetEventStore(connection, new GarnetEventStoreOptions
                {
                    ConnectionString = connectionString,
                    KeyPrefix = prefix,
                });
                _ = await store.GetVersionAsync("connectivity-probe");
                return new AdapterContext(name, true, store, null, connection);
            }
            catch
            {
                if (connection != null)
                    await connection.DisposeAsync();
                throw;
            }
        }
        catch (Exception ex)
        {
            return new AdapterContext(
                name,
                false,
                null,
                $"Garnet connectivity failed: {ex.GetType().Name}: {ex.Message}",
                null);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection != null)
            await _connection.DisposeAsync();
    }
}
