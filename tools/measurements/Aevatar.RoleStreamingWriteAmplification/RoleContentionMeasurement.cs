using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
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
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Implementations.Local.Actors;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Foundation.Runtime.Streaming;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.RoleStreamingWriteAmplification;

internal static class RoleContentionMeasurement
{
    private const string SameActorScenario = "same_actor";
    private const string DistinctActorScenario = "distinct_actor";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static async Task<int> RunAsync(CommandLineOptions options)
    {
        if (options.Adapter is not "all" and not "inmemory")
        {
            throw new InvalidOperationException(
                "Role contention isolates actor mailbox behavior and supports only the in-memory adapter.");
        }

        var configPath = Path.GetFullPath(options.ConfigPath);
        var configBytes = await File.ReadAllBytesAsync(configPath);
        var config = JsonSerializer.Deserialize<RoleContentionConfig>(configBytes, JsonOptions)
                     ?? throw new InvalidOperationException("Role contention configuration is empty.");
        Validate(config);
        if (options.VerifyOnly)
        {
            await VerifyCleanupLifecycleAsync(config);
            Console.WriteLine(
                $"Role contention configuration valid: fast_sessions={config.FastSessionCount}, " +
                $"iterations={config.MeasuredIterations}; cleanup lifecycle verified.");
            return 0;
        }

        var samples = new Dictionary<string, List<RoleContentionRunSample>>(StringComparer.Ordinal)
        {
            [SameActorScenario] = [],
            [DistinctActorScenario] = [],
        };

        for (var warmup = 0; warmup < config.WarmupIterations; warmup++)
        {
            _ = await RunScenarioAsync(config, SameActorScenario, warmup, measured: false);
            _ = await RunScenarioAsync(config, DistinctActorScenario, warmup, measured: false);
        }

        for (var iteration = 0; iteration < config.MeasuredIterations; iteration++)
        {
            var order = iteration % 2 == 0
                ? new[] { SameActorScenario, DistinctActorScenario }
                : new[] { DistinctActorScenario, SameActorScenario };
            foreach (var scenario in order)
            {
                Console.WriteLine($"Measuring role contention scenario={scenario} iteration={iteration}");
                samples[scenario].Add(await RunScenarioAsync(config, scenario, iteration, measured: true));
            }
        }

        var scenarioResults = samples
            .Select(pair => new RoleContentionScenarioResult(
                pair.Key,
                pair.Key == SameActorScenario ? config.FastSessionCount + 1 : 1,
                pair.Value,
                RoleContentionScenarioSummary.From(pair.Value)))
            .OrderBy(static result => result.Scenario, StringComparer.Ordinal)
            .ToArray();
        var sameActor = scenarioResults.Single(static result => result.Scenario == SameActorScenario);
        var distinctActor = scenarioResults.Single(static result => result.Scenario == DistinctActorScenario);
        var output = new RoleContentionMeasurementOutput(
            2,
            DateTimeOffset.UtcNow,
            await ResolveGitCommitAsync(),
            await ResolveGitDirtyPathsAsync(),
            CalculateAssemblySha256(Assembly.GetExecutingAssembly()),
            CalculateAssemblySha256(typeof(RoleGAgent).Assembly),
            options.RunPhase,
            config.BaselineCodeCommit,
            Convert.ToHexString(SHA256.HashData(configBytes)),
            "scoped_role",
            new RoleContentionMetricLabelContract(
                ["entrypoint", "scenario", "turn_kind", "outcome"],
                ["actor_id", "session_id", "command_id", "correlation_id"]),
            config,
            scenarioResults,
            RoleContentionHolDelta.From(sameActor.Summary, distinctActor.Summary),
            options.RunPhase == "baseline-pre-3135"
                ? "Baseline only. Post-#3135 results must be produced from a descendant containing #3135 with the same config digest."
                : "Post-#3135 run. Compare only with a baseline that has the same config digest.");

        var outputPath = Path.GetFullPath(options.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(output, JsonOptions) + Environment.NewLine);
        Console.WriteLine($"Wrote {outputPath}");
        return 0;
    }

    private static async Task<RoleContentionRunSample> RunScenarioAsync(
        RoleContentionConfig config,
        string scenario,
        int iteration,
        bool measured)
    {
        var sampleKey = $"{scenario}-{(measured ? "m" : "w")}-{iteration}-{Guid.NewGuid():N}";
        var streams = new InMemoryStreamProvider(
            new InMemoryStreamOptions(),
            NullLoggerFactory.Instance,
            new InMemoryStreamForwardingRegistry());
        var eventStore = new InMemoryEventStore();
        var recorder = new RoleContentionRecorder();
        var slowGate = new RoleContentionSlowGate();
        var slowSessionId = $"slow-{sampleKey}";
        var fixtures = new List<RoleContentionActorFixture>();
        IReadOnlyList<RoleContentionTurnSample>? turns = null;
        IReadOnlyList<RoleContentionActorStateObservation>? stateObservations = null;
        Exception? scenarioFailure = null;

        try
        {
            var actorCount = scenario == SameActorScenario ? 1 : config.FastSessionCount + 1;
            for (var actorOrdinal = 0; actorOrdinal < actorCount; actorOrdinal++)
            {
                fixtures.Add(await CreateActorAsync(
                    $"measure-role-contention-{sampleKey}-{actorOrdinal}",
                    actorOrdinal,
                    eventStore,
                    streams,
                    config,
                    slowSessionId,
                    slowGate,
                    recorder));
            }

            var slowFixture = fixtures[0];
            var submitted = new List<Task>(config.FastSessionCount + 1)
            {
                SubmitTurn(
                    slowFixture,
                    recorder,
                    turnOrdinal: 0,
                    turnKind: "slow",
                    slowSessionId,
                    config.TimeoutMilliseconds),
            };
            await slowGate.WaitUntilEnteredAsync(TimeSpan.FromSeconds(config.WatchdogSeconds));

            for (var fastOrdinal = 1; fastOrdinal <= config.FastSessionCount; fastOrdinal++)
            {
                var fixture = scenario == SameActorScenario ? slowFixture : fixtures[fastOrdinal];
                submitted.Add(SubmitTurn(
                    fixture,
                    recorder,
                    fastOrdinal,
                    "fast",
                    $"fast-{sampleKey}-{fastOrdinal}",
                    config.TimeoutMilliseconds));
            }

            await ReleaseAfterYieldBudgetAsync(slowGate, config.SlowReleaseYieldCount);
            await Task.WhenAll(submitted).WaitAsync(TimeSpan.FromSeconds(config.WatchdogSeconds));

            turns = recorder.SnapshotTurns();
            stateObservations = fixtures.Select(static fixture => fixture.Agent.CaptureState()).ToArray();
        }
        catch (Exception ex)
        {
            scenarioFailure = ex;
        }

        slowGate.Release();
        var cleanup = await CleanupScenarioAsync(fixtures, streams, config.WatchdogSeconds);
        if (scenarioFailure is not null)
        {
            throw new InvalidOperationException(
                $"Role contention scenario failed; cleanup observed " +
                $"deactivations={cleanup.DeactivationCount}, " +
                $"failures={cleanup.CleanupFailureCount}, " +
                $"active_orphans={cleanup.OrphanedActiveActorCount}.",
                scenarioFailure);
        }

        return new RoleContentionRunSample(
            iteration,
            turns ?? throw new InvalidOperationException("Role contention turns were not captured."),
            recorder.MaxQueueDepthPerActor,
            recorder.MaxTotalQueueDepth,
            fixtures.Count,
            stateObservations ?? throw new InvalidOperationException("Role contention state was not captured."),
            stateObservations.Sum(static state => state.SerializedBytes),
            cleanup.DeactivationCount,
            cleanup.CleanupFailureCount,
            cleanup.OrphanedActiveActorCount);
    }

    private static async Task VerifyCleanupLifecycleAsync(RoleContentionConfig config)
    {
        var streams = new InMemoryStreamProvider(
            new InMemoryStreamOptions(),
            NullLoggerFactory.Instance,
            new InMemoryStreamForwardingRegistry());
        var fixture = await CreateActorAsync(
            $"verify-role-contention-cleanup-{Guid.NewGuid():N}",
            0,
            new InMemoryEventStore(),
            streams,
            config,
            "verify-slow-session",
            new RoleContentionSlowGate(),
            new RoleContentionRecorder());
        var cleanup = await CleanupScenarioAsync([fixture], streams, config.WatchdogSeconds);
        if (cleanup != new RoleContentionCleanupResult(1, 0, 0))
        {
            throw new InvalidOperationException(
                $"Role contention cleanup lifecycle verification failed: " +
                $"deactivations={cleanup.DeactivationCount}, " +
                $"failures={cleanup.CleanupFailureCount}, " +
                $"active_orphans={cleanup.OrphanedActiveActorCount}.");
        }

        var failingFixture = await CreateActorAsync(
            $"verify-role-contention-failed-cleanup-{Guid.NewGuid():N}",
            0,
            new InMemoryEventStore(),
            streams,
            config,
            "verify-failed-slow-session",
            new RoleContentionSlowGate(),
            new RoleContentionRecorder(),
            failDeactivation: true);
        var failedCleanup = await CleanupScenarioAsync([failingFixture], streams, config.WatchdogSeconds);
        if (failedCleanup != new RoleContentionCleanupResult(0, 1, 0))
        {
            throw new InvalidOperationException(
                $"Role contention failed-cleanup verification was not measured honestly: " +
                $"deactivations={failedCleanup.DeactivationCount}, " +
                $"failures={failedCleanup.CleanupFailureCount}, " +
                $"active_orphans={failedCleanup.OrphanedActiveActorCount}.");
        }
    }

    private static async Task<RoleContentionCleanupResult> CleanupScenarioAsync(
        IReadOnlyList<RoleContentionActorFixture> fixtures,
        InMemoryStreamProvider streams,
        int watchdogSeconds)
    {
        var deactivationCount = 0;
        var cleanupFailureCount = 0;
        var orphanedActiveActorCount = 0;
        foreach (var fixture in fixtures)
        {
            var observation = await fixture.DeactivateAndVerifyAsync();
            if (observation.Deactivated)
                deactivationCount++;
            cleanupFailureCount += observation.CleanupFailureCount;
            if (observation.AcceptedEventAfterDeactivation)
                orphanedActiveActorCount++;
        }

        foreach (var fixture in fixtures)
        {
            try
            {
                await DrainStreamAsync(streams, fixture.ActorId, watchdogSeconds);
            }
            catch
            {
                cleanupFailureCount++;
            }
        }

        return new RoleContentionCleanupResult(
            deactivationCount,
            cleanupFailureCount,
            orphanedActiveActorCount);
    }

    private static async Task SubmitTurn(
        RoleContentionActorFixture fixture,
        RoleContentionRecorder recorder,
        int turnOrdinal,
        string turnKind,
        string sessionId,
        int timeoutMilliseconds)
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Payload = Any.Pack(new ChatRequestEvent
            {
                Prompt = $"fixed {turnKind} contention prompt",
                SessionId = sessionId,
                CommandAttemptId = $"attempt-{sessionId}",
                TimeoutMs = timeoutMilliseconds,
            }),
            Route = EnvelopeRouteSemantics.CreateDirect("measurement-harness", fixture.ActorId),
        };
        recorder.Submitted(envelope.Id, fixture.ActorOrdinal, turnOrdinal, turnKind);
        await fixture.DispatchPort.DispatchAsync(fixture.ActorId, envelope);
        await recorder.WaitForCompletionAsync(envelope.Id);
    }

    private static async Task ReleaseAfterYieldBudgetAsync(RoleContentionSlowGate gate, int yieldCount)
    {
        for (var index = 0; index < yieldCount; index++)
            await Task.Yield();
        gate.Release();
    }

    private static async Task<RoleContentionActorFixture> CreateActorAsync(
        string actorId,
        int actorOrdinal,
        IEventStore eventStore,
        InMemoryStreamProvider streams,
        RoleContentionConfig config,
        string slowSessionId,
        RoleContentionSlowGate slowGate,
        RoleContentionRecorder recorder,
        bool failDeactivation = false)
    {
        var services = new ServiceCollection()
            .AddSingleton(eventStore)
            .AddSingleton<IEventSourcingSnapshotStore<RoleGAgentState>>(
                new InMemoryEventSourcingSnapshotStore<RoleGAgentState>())
            .AddSingleton(new EventSourcingRuntimeOptions
            {
                EnableSnapshots = true,
                SnapshotInterval = config.SnapshotInterval,
                EnableEventCompaction = false,
                RetainedEventsAfterSnapshot = config.SnapshotInterval,
            })
            .AddSingleton<IActorRuntimeCallbackScheduler, NoopRuntimeCallbackScheduler>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();
        var provider = new RoleContentionProvider(
            slowSessionId,
            slowGate,
            config.FastTextChunks,
            config.SlowTextChunks,
            config.ChunkCharacters);
        var agent = new RoleContentionGAgent(provider, recorder, actorOrdinal, failDeactivation)
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<IEventSourcingBehaviorFactory<RoleGAgentState>>(),
        };
        typeof(GAgentBase).GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(agent, [actorId]);

        var localActor = new LocalActor(agent, actorId, streams, NullLogger.Instance);
        var publisher = new LocalActorPublisher(actorId, static () => null, static () => 0, streams);
        agent.EventPublisher = publisher;
        typeof(GAgentBase)
            .GetProperty("CommittedStateEventPublisher", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(agent, publisher);
        await localActor.ActivateAsync();
        var runtime = new SingleActorRuntime(localActor);
        var dispatchPort = new LocalActorDispatchPort(runtime);
        var initializationCompleted = agent.ExpectInitializationCompletion();
        await dispatchPort.DispatchAsync(actorId, new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Payload = Any.Pack(new InitializeRoleAgentEvent
            {
                RoleName = "measurement-scoped-role",
                ProviderName = provider.Name,
                Model = "deterministic-contention",
                SystemPrompt = "Fixed scoped-role contention prompt.",
                MaxToolRounds = 1,
                MaxHistoryMessages = 64,
            }),
            Route = EnvelopeRouteSemantics.CreateDirect("measurement-harness", actorId),
        });
        await initializationCompleted.WaitAsync(TimeSpan.FromSeconds(config.WatchdogSeconds));
        return new RoleContentionActorFixture(
            actorId,
            actorOrdinal,
            agent,
            runtime,
            dispatchPort,
            services);
    }

    private static async Task DrainStreamAsync(
        InMemoryStreamProvider streams,
        string actorId,
        int watchdogSeconds)
    {
        var stream = streams.GetStream(actorId);
        var markerId = $"contention-drain-{Guid.NewGuid():N}";
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriptionProvider = new StreamProviderActorEventSubscriptionProvider(streams);
        await using var subscription = await subscriptionProvider.SubscribeAsync(
            actorId,
            (Func<EventEnvelope, Task>)(envelope =>
            {
                if (string.Equals(envelope.Id, markerId, StringComparison.Ordinal))
                    completion.TrySetResult();
                return Task.CompletedTask;
            }));
        await stream.ProduceAsync(new EventEnvelope
        {
            Id = markerId,
            Payload = Any.Pack(new StringValue { Value = "contention-stream-drain" }),
            Route = EnvelopeRouteSemantics.CreateDirect("measurement-harness", actorId),
        });
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(watchdogSeconds));
    }

    private static void Validate(RoleContentionConfig config)
    {
        if (config.SchemaVersion != 1 || config.WarmupIterations < 0 || config.MeasuredIterations < 3)
            throw new InvalidOperationException("Role contention schema or iteration counts are invalid.");
        if (config.FastSessionCount < 2 || config.FastSessionCount > 64)
            throw new InvalidOperationException("fastSessionCount must be between 2 and 64.");
        if (config.SlowReleaseYieldCount < 1 || config.FastTextChunks < 1 || config.SlowTextChunks < 1)
            throw new InvalidOperationException("Contention workload counts must be positive.");
        if (config.ChunkCharacters < 1 || config.TimeoutMilliseconds < 1 || config.WatchdogSeconds < 1)
            throw new InvalidOperationException("Contention timeout and chunk settings must be positive.");
        if (config.SnapshotInterval < 1 || string.IsNullOrWhiteSpace(config.BaselineCodeCommit))
            throw new InvalidOperationException("Snapshot interval and baseline code commit are required.");
    }

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

    private static async Task<IReadOnlyList<string>> ResolveGitDirtyPathsAsync()
    {
        using var process = Process.Start(new ProcessStartInfo(
            "git",
            "status --porcelain=v1 --untracked-files=no")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });
        if (process == null)
            return ["unknown"];

        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            return ["unknown"];

        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.TrimEnd('\r'))
            .Select(static line => line.Length > 3 ? line[3..] : line)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private static string CalculateAssemblySha256(Assembly assembly) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)));
}

public sealed record RoleContentionConfig
{
    public int SchemaVersion { get; init; }
    public string BaselineCodeCommit { get; init; } = string.Empty;
    public int WarmupIterations { get; init; }
    public int MeasuredIterations { get; init; }
    public int FastSessionCount { get; init; }
    public int SlowReleaseYieldCount { get; init; }
    public int FastTextChunks { get; init; }
    public int SlowTextChunks { get; init; }
    public int ChunkCharacters { get; init; }
    public int TimeoutMilliseconds { get; init; }
    public int WatchdogSeconds { get; init; }
    public int SnapshotInterval { get; init; }
}

public sealed record RoleContentionMeasurementOutput(
    int SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    string SourceCommit,
    IReadOnlyList<string> SourceDirtyPaths,
    string MeasurementAssemblySha256,
    string RoleAgentAssemblySha256,
    string RunPhase,
    string BaselineCodeCommit,
    string ConfigSha256,
    string Entrypoint,
    RoleContentionMetricLabelContract MetricLabels,
    RoleContentionConfig Config,
    IReadOnlyList<RoleContentionScenarioResult> Scenarios,
    RoleContentionHolDelta HeadOfLineDelta,
    string ComparisonStatus);

public sealed record RoleContentionMetricLabelContract(
    IReadOnlyList<string> Allowed,
    IReadOnlyList<string> Forbidden);

public sealed record RoleContentionScenarioResult(
    string Scenario,
    int ConcurrentSessionsPerActor,
    IReadOnlyList<RoleContentionRunSample> Samples,
    RoleContentionScenarioSummary Summary);

public sealed record RoleContentionRunSample(
    int Iteration,
    IReadOnlyList<RoleContentionTurnSample> Turns,
    int MaxQueueDepthPerActor,
    int MaxTotalQueueDepth,
    int ActivationCount,
    IReadOnlyList<RoleContentionActorStateObservation> ActorStates,
    int AggregateStateBytes,
    int DeactivationCount,
    int CleanupFailureCount,
    int OrphanedActiveActorCount);

public sealed record RoleContentionTurnSample(
    int ActorOrdinal,
    int TurnOrdinal,
    string TurnKind,
    double QueueTimeMs,
    double ServiceTimeMs,
    double CompletionLatencyMs,
    double? FirstOutputLatencyMs,
    string Outcome);

public sealed record RoleContentionActorStateObservation(
    int SerializedBytes,
    int TrackedSessionCount,
    int CompletedSessionCount);

internal sealed record RoleContentionCleanupResult(
    int DeactivationCount,
    int CleanupFailureCount,
    int OrphanedActiveActorCount);

internal sealed record RoleContentionActorCleanupObservation(
    bool Deactivated,
    int CleanupFailureCount,
    bool AcceptedEventAfterDeactivation);

public sealed record RoleContentionScenarioSummary(
    Distribution FastQueueTimeMs,
    Distribution FastServiceTimeMs,
    Distribution FastCompletionLatencyMs,
    Distribution FastFirstOutputLatencyMs,
    Distribution SlowQueueTimeMs,
    Distribution SlowServiceTimeMs,
    Distribution SlowCompletionLatencyMs,
    Distribution MaxQueueDepthPerActor,
    Distribution MaxTotalQueueDepth,
    Distribution ActivationCount,
    Distribution ActorStateBytes,
    Distribution AggregateStateBytes,
    int CleanupFailureCount,
    int OrphanedActiveActorCount)
{
    public static RoleContentionScenarioSummary From(IReadOnlyList<RoleContentionRunSample> samples)
    {
        var turns = samples.SelectMany(static sample => sample.Turns).ToArray();
        var fast = turns.Where(static turn => turn.TurnKind == "fast").ToArray();
        var slow = turns.Where(static turn => turn.TurnKind == "slow").ToArray();
        return new RoleContentionScenarioSummary(
            Distribution.From(fast.Select(static turn => (double?)turn.QueueTimeMs)),
            Distribution.From(fast.Select(static turn => (double?)turn.ServiceTimeMs)),
            Distribution.From(fast.Select(static turn => (double?)turn.CompletionLatencyMs)),
            Distribution.From(fast.Select(static turn => turn.FirstOutputLatencyMs)),
            Distribution.From(slow.Select(static turn => (double?)turn.QueueTimeMs)),
            Distribution.From(slow.Select(static turn => (double?)turn.ServiceTimeMs)),
            Distribution.From(slow.Select(static turn => (double?)turn.CompletionLatencyMs)),
            Distribution.From(samples.Select(static sample => (double?)sample.MaxQueueDepthPerActor)),
            Distribution.From(samples.Select(static sample => (double?)sample.MaxTotalQueueDepth)),
            Distribution.From(samples.Select(static sample => (double?)sample.ActivationCount)),
            Distribution.From(samples.SelectMany(static sample => sample.ActorStates)
                .Select(static state => (double?)state.SerializedBytes)),
            Distribution.From(samples.Select(static sample => (double?)sample.AggregateStateBytes)),
            samples.Sum(static sample => sample.CleanupFailureCount),
            samples.Sum(static sample => sample.OrphanedActiveActorCount));
    }
}

public sealed record RoleContentionHolDelta(
    PercentileDelta FastQueueTimeMs,
    PercentileDelta FastCompletionLatencyMs)
{
    public static RoleContentionHolDelta From(
        RoleContentionScenarioSummary sameActor,
        RoleContentionScenarioSummary distinctActor) =>
        new(
            PercentileDelta.Between(sameActor.FastQueueTimeMs, distinctActor.FastQueueTimeMs),
            PercentileDelta.Between(
                sameActor.FastCompletionLatencyMs,
                distinctActor.FastCompletionLatencyMs));
}

public sealed record PercentileDelta(double? P50, double? P95, double? P99)
{
    public static PercentileDelta Between(Distribution left, Distribution right) =>
        new(Subtract(left.P50, right.P50), Subtract(left.P95, right.P95), Subtract(left.P99, right.P99));

    private static double? Subtract(double? left, double? right) =>
        left.HasValue && right.HasValue ? left.Value - right.Value : null;
}

internal sealed class RoleContentionGAgent(
    RoleContentionProvider provider,
    RoleContentionRecorder recorder,
    int actorOrdinal,
    bool failDeactivation)
    : RoleGAgent(
        toolExecutionPort: new MeasurementToolExecutionPort(),
        llmProviderFactory: provider,
        toolSources: [])
{
    private string? _activeEnvelopeId;
    private TaskCompletionSource? _initializationCompletion;

    public Task ExpectInitializationCompletion()
    {
        _initializationCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        return _initializationCompletion.Task;
    }

    public RoleContentionActorStateObservation CaptureState() =>
        new(
            State.CalculateSize(),
            State.Sessions.Count,
            State.Sessions.Values.Count(static session => session.Completed));

    protected override Task OnDeactivateAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return failDeactivation
            ? Task.FromException(new InvalidOperationException("Injected cleanup verification failure."))
            : Task.CompletedTask;
    }

    protected override Task OnEventHandlerStartAsync(
        EventEnvelope envelope,
        string handlerName,
        object? payload,
        CancellationToken ct)
    {
        _ = handlerName;
        _ = payload;
        _ = ct;
        if (envelope.Payload?.Is(ChatRequestEvent.Descriptor) == true)
        {
            _activeEnvelopeId = envelope.Id;
            recorder.Started(envelope.Id, actorOrdinal);
        }
        return Task.CompletedTask;
    }

    protected override void OnFirstStreamedOutputObserved(TimeSpan elapsed)
    {
        _ = elapsed;
        if (_activeEnvelopeId is not null)
            recorder.FirstOutput(_activeEnvelopeId);
    }

    protected override Task OnEventHandlerEndAsync(
        EventEnvelope envelope,
        string handlerName,
        object? payload,
        TimeSpan duration,
        Exception? exception,
        CancellationToken ct)
    {
        _ = handlerName;
        _ = payload;
        _ = duration;
        _ = ct;
        if (envelope.Payload?.Is(InitializeRoleAgentEvent.Descriptor) == true)
        {
            if (exception == null)
                _initializationCompletion?.TrySetResult();
            else
                _initializationCompletion?.TrySetException(exception);
        }
        if (envelope.Payload?.Is(ChatRequestEvent.Descriptor) == true)
        {
            recorder.Ended(envelope.Id, actorOrdinal, exception);
            _activeEnvelopeId = null;
        }
        return Task.CompletedTask;
    }
}

internal sealed class RoleContentionProvider(
    string slowSessionId,
    RoleContentionSlowGate slowGate,
    int fastTextChunks,
    int slowTextChunks,
    int chunkCharacters) : ILLMProviderFactory, ILLMProvider
{
    public string Name => "measurement-role-contention";
    public ILLMProvider GetProvider(string name) => this;
    public ILLMProvider GetDefault() => this;
    public IReadOnlyList<string> GetAvailableProviders() => [Name];

    public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        LLMRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var slow = string.Equals(request.RequestId, slowSessionId, StringComparison.Ordinal);
        if (slow)
        {
            slowGate.MarkEntered();
            await slowGate.WaitForReleaseAsync(ct);
        }

        var chunks = slow ? slowTextChunks : fastTextChunks;
        var content = new string(slow ? 's' : 'f', chunkCharacters);
        for (var index = 0; index < chunks; index++)
        {
            ct.ThrowIfCancellationRequested();
            yield return new LLMStreamChunk { DeltaContent = content };
            await Task.Yield();
        }
        yield return new LLMStreamChunk { IsLast = true, FinishReason = "stop" };
    }
}

internal sealed class RoleContentionSlowGate
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void MarkEntered() => _entered.TrySetResult();
    public void Release() => _release.TrySetResult();
    public Task WaitUntilEnteredAsync(TimeSpan timeout) => _entered.Task.WaitAsync(timeout);
    public Task WaitForReleaseAsync(CancellationToken ct) => _release.Task.WaitAsync(ct);
}

internal sealed class RoleContentionRecorder
{
    private readonly object _lock = new();
    private readonly Dictionary<string, MutableTurn> _turns = new(StringComparer.Ordinal);
    private readonly Dictionary<int, int> _queuedByActor = [];
    private int _totalQueued;

    public int MaxQueueDepthPerActor { get; private set; }
    public int MaxTotalQueueDepth { get; private set; }

    public void Submitted(string envelopeId, int actorOrdinal, int turnOrdinal, string turnKind)
    {
        lock (_lock)
        {
            _turns.Add(
                envelopeId,
                new MutableTurn(actorOrdinal, turnOrdinal, turnKind, Stopwatch.GetTimestamp()));
            var queued = _queuedByActor.GetValueOrDefault(actorOrdinal) + 1;
            _queuedByActor[actorOrdinal] = queued;
            _totalQueued++;
            MaxQueueDepthPerActor = Math.Max(MaxQueueDepthPerActor, queued);
            MaxTotalQueueDepth = Math.Max(MaxTotalQueueDepth, _totalQueued);
        }
    }

    public void Started(string envelopeId, int actorOrdinal)
    {
        lock (_lock)
        {
            var turn = Require(envelopeId);
            turn.StartedAt = Stopwatch.GetTimestamp();
            _queuedByActor[actorOrdinal] = Math.Max(0, _queuedByActor.GetValueOrDefault(actorOrdinal) - 1);
            _totalQueued = Math.Max(0, _totalQueued - 1);
        }
    }

    public void FirstOutput(string envelopeId)
    {
        lock (_lock)
            Require(envelopeId).FirstOutputAt ??= Stopwatch.GetTimestamp();
    }

    public void Ended(string envelopeId, int actorOrdinal, Exception? exception)
    {
        _ = actorOrdinal;
        lock (_lock)
        {
            var turn = Require(envelopeId);
            turn.EndedAt = Stopwatch.GetTimestamp();
            turn.Outcome = exception == null ? "completed" : "failed";
            turn.Completion.TrySetResult();
        }
    }

    public Task WaitForCompletionAsync(string envelopeId)
    {
        lock (_lock)
            return Require(envelopeId).Completion.Task;
    }

    public IReadOnlyList<RoleContentionTurnSample> SnapshotTurns()
    {
        lock (_lock)
        {
            return _turns.Values
                .OrderBy(static turn => turn.TurnOrdinal)
                .Select(static turn => turn.ToSample())
                .ToArray();
        }
    }

    private MutableTurn Require(string envelopeId) =>
        _turns.TryGetValue(envelopeId, out var turn)
            ? turn
            : throw new InvalidOperationException("Contention recorder observed an unknown envelope.");

    private sealed class MutableTurn(
        int actorOrdinal,
        int turnOrdinal,
        string turnKind,
        long submittedAt)
    {
        public int ActorOrdinal { get; } = actorOrdinal;
        public int TurnOrdinal { get; } = turnOrdinal;
        public string TurnKind { get; } = turnKind;
        public long SubmittedAt { get; } = submittedAt;
        public long StartedAt { get; set; }
        public long? FirstOutputAt { get; set; }
        public long EndedAt { get; set; }
        public string Outcome { get; set; } = "unknown";
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RoleContentionTurnSample ToSample()
        {
            if (StartedAt == 0 || EndedAt == 0)
                throw new InvalidOperationException("Contention turn did not reach a terminal handler observation.");
            return new RoleContentionTurnSample(
                ActorOrdinal,
                TurnOrdinal,
                TurnKind,
                ElapsedMilliseconds(SubmittedAt, StartedAt),
                ElapsedMilliseconds(StartedAt, EndedAt),
                ElapsedMilliseconds(SubmittedAt, EndedAt),
                FirstOutputAt.HasValue ? ElapsedMilliseconds(SubmittedAt, FirstOutputAt.Value) : null,
                Outcome);
        }

        private static double ElapsedMilliseconds(long start, long end) =>
            Stopwatch.GetElapsedTime(start, end).TotalMilliseconds;
    }
}

internal sealed class RoleContentionActorFixture(
    string actorId,
    int actorOrdinal,
    RoleContentionGAgent agent,
    IActorRuntime runtime,
    IActorDispatchPort dispatchPort,
    ServiceProvider services)
{
    public string ActorId { get; } = actorId;
    public int ActorOrdinal { get; } = actorOrdinal;
    public RoleContentionGAgent Agent { get; } = agent;
    public IActorRuntime Runtime { get; } = runtime;
    public IActorDispatchPort DispatchPort { get; } = dispatchPort;

    public async Task<RoleContentionActorCleanupObservation> DeactivateAndVerifyAsync()
    {
        var deactivated = false;
        var cleanupFailureCount = 0;
        try
        {
            await Runtime.DestroyAsync(ActorId);
            deactivated = true;
        }
        catch
        {
            cleanupFailureCount++;
        }

        var acceptedEventAfterDeactivation = false;
        try
        {
            acceptedEventAfterDeactivation = await AcceptsEventAfterDeactivationAsync();
        }
        catch
        {
            cleanupFailureCount++;
        }

        try
        {
            await services.DisposeAsync();
        }
        catch
        {
            cleanupFailureCount++;
        }

        return new RoleContentionActorCleanupObservation(
            deactivated,
            cleanupFailureCount,
            acceptedEventAfterDeactivation);
    }

    private async Task<bool> AcceptsEventAfterDeactivationAsync()
    {
        try
        {
            await DispatchPort.DispatchAsync(ActorId, new EventEnvelope
            {
                Id = $"contention-active-probe-{Guid.NewGuid():N}",
                Payload = Any.Pack(new StringValue { Value = "contention-active-probe" }),
                Route = EnvelopeRouteSemantics.CreateDirect("measurement-harness", ActorId),
            });
            return true;
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("not found", StringComparison.Ordinal))
        {
            return false;
        }
    }
}
