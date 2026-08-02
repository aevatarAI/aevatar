using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
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
using Aevatar.AI.LLMProviders.MEAI;
using Aevatar.AI.LLMProviders.NyxId;
using Aevatar.AI.LLMProviders.Tornado;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.Tools;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Implementations.Local.Actors;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Foundation.Runtime.Streaming;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using LlmTornado;
using LlmTornado.Code;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using AevatarChatMessage = Aevatar.AI.Abstractions.LLMProviders.ChatMessage;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Aevatar.RoleStreamingWriteAmplification;

internal static class ProviderNormalizationMeasurement
{
    private const string ToolArgumentsJson = "{\"index\":1}";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static async Task<int> RunAsync(CommandLineOptions options)
    {
        var config = await LoadConfigAsync(options.ConfigPath);
        ValidateConfig(config);
        if (options.VerifyOnly)
        {
            Console.WriteLine(
                $"Provider normalization configuration valid: providers={config.Providers.Count}, " +
                $"warmups={config.WarmupIterations}, samples={config.MeasuredIterations}.");
            return 0;
        }

        await using var server = await OpenAICompatibleLoopbackServer.StartAsync();
        var results = new List<ProviderNormalizationResult>();
        foreach (var providerName in config.Providers)
        {
            Console.WriteLine($"Measuring provider normalization={providerName}");
            var requestFacts = providerName == "meai"
                ? new ProviderRequestFactsCollector()
                : server.FactsFor(providerName);
            for (var warmup = 0; warmup < config.WarmupIterations; warmup++)
                _ = await RunSampleAsync(providerName, warmup, config, requestFacts, server);

            requestFacts.Reset();
            var samples = new List<ProviderNormalizationSample>(config.MeasuredIterations);
            for (var iteration = 0; iteration < config.MeasuredIterations; iteration++)
                samples.Add(await RunSampleAsync(providerName, iteration, config, requestFacts, server));

            results.Add(new ProviderNormalizationResult(
                providerName,
                DescribeCoverage(providerName),
                requestFacts.Snapshot(),
                samples,
                ProviderNormalizationSummary.From(samples)));
        }

        ValidateResults(config, results);
        var output = new ProviderNormalizationOutput(
            SchemaVersion: 1,
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            SourceCommit: await ResolveGitCommitAsync(),
            Environment: new ProviderNormalizationEnvironment(
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString(),
                RuntimeInformation.FrameworkDescription),
            Provenance: await BuildProvenanceAsync(options.ConfigPath),
            MetricSemantics: new ProviderNormalizationMetricSemantics(
                "Every sample traverses the repository provider, RoleGAgent, LocalActor mailbox, and IActorDispatchPort.",
                "Evidence is read from committed typed progress/completion events, append ledger, durable readback, and committed publication identities; SDK update counts are not correctness evidence.",
                "Nearest-rank percentiles; with 12 samples p95 and p99 both select the maximum.",
                ["provider", "path", "stream", "usage_opt_in", "auth_present", "user_agent_present", "tools_advertised"]),
            Config: config,
            Providers: results);

        var outputPath = Path.GetFullPath(options.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(output, JsonOptions) + Environment.NewLine);
        Console.WriteLine($"Wrote {outputPath}");
        return 0;
    }

    private static async Task<ProviderNormalizationSample> RunSampleAsync(
        string providerName,
        int iteration,
        ProviderNormalizationConfig config,
        ProviderRequestFactsCollector requestFacts,
        OpenAICompatibleLoopbackServer server)
    {
        var actorId = $"provider-normalization-{Guid.NewGuid():N}";
        var sessionId = $"session-{Guid.NewGuid():N}";
        var baseStore = new InMemoryEventStore();
        var eventStore = new MeasuringEventStore(baseStore, captureCommittedEvents: true);
        var snapshotStore = new InMemoryEventSourcingSnapshotStore<RoleGAgentState>();
        var streams = new InMemoryStreamProvider(
            new InMemoryStreamOptions(),
            NullLoggerFactory.Instance,
            new InMemoryStreamForwardingRegistry());
        var publicationObserver = new ProviderPublicationObserver();
        var subscriptionProvider = new StreamProviderActorEventSubscriptionProvider(streams);
        await using var publicationSubscription = await subscriptionProvider.SubscribeAsync(
            actorId,
            (Func<EventEnvelope, Task>)publicationObserver.ObserveAsync);
        var provider = CreateProvider(providerName, requestFacts, server);
        var actor = await CreateActorAsync(
            actorId,
            eventStore,
            snapshotStore,
            streams,
            provider,
            config,
            initialize: true);

        await publicationObserver.DrainAsync(streams.GetStream(actorId));
        var durableBefore = await baseStore.GetEventsAsync(actorId);
        var publicationCountBefore = publicationObserver.PublishedEventsSnapshot().Count;
        eventStore.Reset();
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var startedAt = Stopwatch.GetTimestamp();

        await DispatchAsync(actor, actorId, new ChatRequestEvent
        {
            Prompt = "fixed provider normalization prompt",
            SessionId = sessionId,
            CommandAttemptId = $"attempt-{iteration}",
            TimeoutMs = 30_000,
        });
        var completionLatency = Stopwatch.GetElapsedTime(startedAt);
        process.Refresh();
        var cpuMs = Math.Max(0, (process.TotalProcessorTime - cpuBefore).TotalMilliseconds);
        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        await publicationObserver.DrainAsync(streams.GetStream(actorId));
        var appendLedger = eventStore.CommittedEventsSnapshot();
        var durable = (await baseStore.GetEventsAsync(actorId)).Skip(durableBefore.Count).ToArray();
        var publications = publicationObserver.PublishedEventsSnapshot().Skip(publicationCountBefore).ToArray();
        var storeMetrics = eventStore.Snapshot();

        await actor.LocalActor.DeactivateAsync();
        await actor.Services.DisposeAsync();

        var evidence = ValidateCommittedEvidence(
            providerName,
            appendLedger,
            durable,
            publications,
            sessionId);
        return new ProviderNormalizationSample(
            iteration,
            storeMetrics.AppendAttempts,
            storeMetrics.CommittedEventCount,
            storeMetrics.CommittedSerializedBytes,
            storeMetrics.TotalIoDuration.TotalMilliseconds,
            actor.Agent.FirstTokenLatency?.TotalMilliseconds,
            completionLatency.TotalMilliseconds,
            cpuMs,
            allocatedBytes,
            evidence.ProgressEventCount,
            evidence.ProgressSequenceMonotonic,
            evidence.TextObserved,
            evidence.ReasoningObserved,
            evidence.MediaObserved,
            evidence.ToolStartedObserved,
            evidence.ToolCompletedObserved,
            evidence.UsageObserved,
            evidence.TerminalObserved,
            evidence.UniqueCompletion,
            evidence.ToolArgumentsSha256,
            evidence.AppendLedgerMatchesDurableReadback,
            evidence.DurableReadbackMatchesPublication);
    }

    private static ProviderCommittedEvidence ValidateCommittedEvidence(
        string providerName,
        IReadOnlyList<StateEvent> appendLedger,
        IReadOnlyList<StateEvent> durable,
        IReadOnlyList<StateEvent> publications,
        string sessionId)
    {
        var ledgerIds = EventIds(appendLedger, "append ledger");
        var durableIds = EventIds(durable, "durable readback");
        var publicationIds = EventIds(publications, "committed publication");
        var ledgerMatchesDurable = ledgerIds.SetEquals(durableIds);
        var durableMatchesPublication = durableIds.SetEquals(publicationIds);
        if (!ledgerMatchesDurable || !durableMatchesPublication)
        {
            throw new InvalidOperationException(
                $"{providerName} event identity reconciliation failed: ledger={ledgerIds.Count}, " +
                $"durable={durableIds.Count}, publication={publicationIds.Count}, " +
                $"ledgerMissing={ledgerIds.Except(durableIds).Count()}, " +
                $"durableMissing={durableIds.Except(ledgerIds).Count()}, " +
                $"publicationMissing={durableIds.Except(publicationIds).Count()}, " +
                $"publicationUnexpected={publicationIds.Except(durableIds).Count()}.");
        }

        var completions = durable
            .Where(static item => item.EventData?.Is(RoleChatSessionCompletedEvent.Descriptor) == true)
            .Select(static item => item.EventData.Unpack<RoleChatSessionCompletedEvent>())
            .Where(item => string.Equals(item.SessionId, sessionId, StringComparison.Ordinal))
            .ToArray();
        if (completions.Length != 1)
            throw new InvalidOperationException($"{providerName} committed {completions.Length} completion events.");
        var completion = completions[0];
        if (completion.Outcome != RoleChatSessionOutcome.Completed)
        {
            throw new InvalidOperationException(
                $"{providerName} did not commit a completed terminal outcome: " +
                $"outcome={completion.Outcome}, failure={completion.FailureCode}, safe={completion.SafeMessage}.");
        }

        var progress = durable
            .Where(static item => item.EventData?.Is(RoleChatSessionProgressedEvent.Descriptor) == true)
            .Select(static item => item.EventData.Unpack<RoleChatSessionProgressedEvent>())
            .Where(item => string.Equals(item.SessionId, sessionId, StringComparison.Ordinal))
            .Concat(completion.TerminalProgress)
            .ToArray();
        var monotonic = progress.Select(static item => item.Sequence)
            .Zip(progress.Select(static item => item.Sequence).Skip(1), static (left, right) => right > left)
            .All(static increasing => increasing);
        if (!monotonic)
            throw new InvalidOperationException($"{providerName} committed non-monotonic progress sequence.");

        var toolCall = completion.ToolCalls.SingleOrDefault();
        var argumentsSha256 = string.Empty;
        if (providerName is "meai" or "nyxid")
        {
            if (toolCall == null || !string.Equals(toolCall.ToolName, "measurement_tool_1", StringComparison.Ordinal))
                throw new InvalidOperationException($"{providerName} did not preserve the normalized tool call.");
            argumentsSha256 = HashText(toolCall.ArgumentsJson);
            if (!string.Equals(argumentsSha256, HashText(ToolArgumentsJson), StringComparison.Ordinal))
                throw new InvalidOperationException($"{providerName} changed final tool arguments.");
        }
        else if (completion.ToolCalls.Count != 0)
        {
            throw new InvalidOperationException("Tornado unexpectedly committed an unsupported tool call.");
        }

        var evidence = new ProviderCommittedEvidence(
            progress.Length,
            monotonic,
            progress.Any(static item => item.PayloadCase == RoleChatSessionProgressedEvent.PayloadOneofCase.TextDelta),
            progress.Any(static item => item.PayloadCase == RoleChatSessionProgressedEvent.PayloadOneofCase.ReasoningDelta),
            progress.Any(static item => item.PayloadCase == RoleChatSessionProgressedEvent.PayloadOneofCase.Media),
            progress.Any(static item => item.PayloadCase == RoleChatSessionProgressedEvent.PayloadOneofCase.ToolStarted),
            progress.Any(static item => item.PayloadCase == RoleChatSessionProgressedEvent.PayloadOneofCase.ToolCompleted),
            progress.Any(static item => item.PayloadCase == RoleChatSessionProgressedEvent.PayloadOneofCase.Usage),
            progress.Any(static item => item.PayloadCase == RoleChatSessionProgressedEvent.PayloadOneofCase.Terminal),
            true,
            argumentsSha256,
            ledgerMatchesDurable,
            durableMatchesPublication);

        if (!evidence.TextObserved || !evidence.UsageObserved || !evidence.TerminalObserved)
        {
            throw new InvalidOperationException($"{providerName} did not commit every supported normalized shape.");
        }
        if (providerName is "meai" or "nyxid" &&
            (!evidence.ToolStartedObserved || !evidence.ToolCompletedObserved))
        {
            throw new InvalidOperationException($"{providerName} did not commit normalized tool lifecycle facts.");
        }
        if (providerName is "meai" or "nyxid" && !evidence.ReasoningObserved)
            throw new InvalidOperationException($"{providerName} did not commit normalized reasoning.");
        if (providerName == "meai" && !evidence.MediaObserved)
            throw new InvalidOperationException("MEAI did not commit normalized media.");
        if (providerName == "tornado" && (evidence.ReasoningObserved || evidence.MediaObserved))
            throw new InvalidOperationException("Tornado unexpectedly reported unsupported reasoning/media shapes.");
        return evidence;
    }

    private static HashSet<string> EventIds(IReadOnlyList<StateEvent> events, string source)
    {
        if (events.Any(static item => string.IsNullOrWhiteSpace(item.EventId)))
            throw new InvalidOperationException($"{source} contains an empty event ID.");
        var ids = events.Select(static item => item.EventId).ToHashSet(StringComparer.Ordinal);
        if (ids.Count != events.Count)
            throw new InvalidOperationException($"{source} contains duplicate event IDs.");
        return ids;
    }

    private static ILLMProvider CreateProvider(
        string providerName,
        ProviderRequestFactsCollector requestFacts,
        OpenAICompatibleLoopbackServer server) => providerName switch
    {
        "meai" => new MEAILLMProvider(
            "meai",
            new DeterministicChatClient(requestFacts),
            toolExecutionPort: new MeasurementToolExecutionPort()),
        "nyxid" => new NyxIdLLMProvider(
            "nyxid",
            "deterministic-model",
            new Uri(server.BaseUri, "api/v1/llm/gateway/v1").ToString(),
            static () => "measurement-token",
            toolExecutionPort: new MeasurementToolExecutionPort()),
        "tornado" => new TornadoLLMProvider(
            "tornado",
            new TornadoApi(server.BaseUri, "measurement-token", LLmProviders.OpenAi),
            "deterministic-model"),
        _ => throw new InvalidOperationException($"Unknown provider '{providerName}'."),
    };

    private static async Task<ActorFixture> CreateActorAsync(
        string actorId,
        IEventStore eventStore,
        IEventSourcingSnapshotStore<RoleGAgentState> snapshotStore,
        InMemoryStreamProvider streams,
        ILLMProvider provider,
        ProviderNormalizationConfig config,
        bool initialize)
    {
        var services = new ServiceCollection()
            .AddSingleton(eventStore)
            .AddSingleton(snapshotStore)
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
        var factory = new DirectProviderFactory(provider);
        var tool = new MeasurementTool("measurement_tool_1");
        var agent = new MeasuredRoleGAgent(
            new MeasurementToolExecutionPort(),
            factory,
            [new StaticToolSource([tool])],
            new InMemorySecretVault())
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
        var fixture = new ActorFixture(
            agent,
            localActor,
            new LocalActorDispatchPort(new SingleActorRuntime(localActor)),
            services);
        if (initialize)
        {
            await DispatchAsync(fixture, actorId, new InitializeRoleAgentEvent
            {
                RoleName = "provider-normalization-role",
                ProviderName = provider.Name,
                Model = "deterministic-model",
                SystemPrompt = "Fixed provider normalization measurement system prompt.",
                MaxToolRounds = 3,
                MaxHistoryMessages = 16,
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
            Route = EnvelopeRouteSemantics.CreateDirect("provider-normalization-measurement", actorId),
        });
        var error = await handled.WaitAsync(TimeSpan.FromSeconds(30));
        if (error != null)
            throw error;
    }

    private static ProviderCoverage DescribeCoverage(string providerName) => providerName switch
    {
        "meai" => new ProviderCoverage(true, true, true, true, true, true, null),
        "nyxid" => new ProviderCoverage(true, true, false, true, true, true,
            "Streaming output media is not surfaced by the OpenAI-compatible NyxID route/SDK path."),
        "tornado" => new ProviderCoverage(true, false, false, false, true, true,
            "Reasoning/media are unsupported. MapRequest does not map LLMRequest.Tools and LlmTornado only emits its tool accumulator when request.Tools is non-empty, so this adapter cannot currently surface tool deltas (toolsAdvertisedToUpstream=false)."),
        _ => throw new InvalidOperationException($"Unknown provider '{providerName}'."),
    };

    private static void ValidateResults(
        ProviderNormalizationConfig config,
        IReadOnlyList<ProviderNormalizationResult> results)
    {
        if (results.Count != config.Providers.Count ||
            results.Any(result => result.Samples.Count != config.MeasuredIterations))
        {
            throw new InvalidOperationException("Provider normalization sample count is incomplete.");
        }
        if (results.Select(static result => result.Provider).ToHashSet(StringComparer.Ordinal).Count != results.Count)
            throw new InvalidOperationException("Provider normalization output contains duplicate providers.");
        if (results.SelectMany(static result => result.Samples).Any(static sample =>
                !sample.ProgressSequenceMonotonic ||
                !sample.UniqueCompletion ||
                !sample.AppendLedgerMatchesDurableReadback ||
                !sample.DurableReadbackMatchesPublication))
        {
            throw new InvalidOperationException("Provider normalization committed evidence is incomplete.");
        }
    }

    private static async Task<ProviderNormalizationConfig> LoadConfigAsync(string path)
    {
        await using var stream = File.OpenRead(Path.GetFullPath(path));
        return await JsonSerializer.DeserializeAsync<ProviderNormalizationConfig>(stream, JsonOptions)
               ?? throw new InvalidOperationException("Provider normalization configuration is empty.");
    }

    private static void ValidateConfig(ProviderNormalizationConfig config)
    {
        if (config.SchemaVersion != 1)
            throw new InvalidOperationException($"Unsupported provider normalization schema {config.SchemaVersion}.");
        if (config.WarmupIterations != 2 || config.MeasuredIterations != 12)
            throw new InvalidOperationException("Provider normalization requires exactly 2 warmups and 12 samples.");
        if (config.SnapshotInterval < 1 || config.RetainedEventsAfterSnapshot < 0)
            throw new InvalidOperationException("Provider normalization snapshot configuration is invalid.");
        var required = new HashSet<string>(["meai", "nyxid", "tornado"], StringComparer.Ordinal);
        if (!required.SetEquals(config.Providers))
            throw new InvalidOperationException("Provider normalization must cover meai, nyxid, and tornado exactly once.");
    }

    private static async Task<ProviderNormalizationProvenance> BuildProvenanceAsync(string configPath)
    {
        var sources = new[]
        {
            "src/Aevatar.AI.LLMProviders.MEAI/MEAILLMProvider.cs",
            "src/Aevatar.AI.LLMProviders.NyxId/NyxIdLLMProvider.cs",
            "src/Aevatar.AI.LLMProviders.Tornado/TornadoLLMProvider.cs",
            "src/Aevatar.AI.Core/RoleGAgent.cs",
            "tools/measurements/Aevatar.RoleStreamingWriteAmplification/ProviderNormalizationMeasurement.cs",
        };
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in sources)
            hashes[source] = await HashFileAsync(Path.GetFullPath(source));
        return new ProviderNormalizationProvenance(
            await HashFileAsync(Path.GetFullPath(configPath)),
            hashes,
            "Loopback OpenAI-compatible SSE; no external network or provider credentials.");
    }

    private static async Task<string> HashFileAsync(string path) =>
        Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant();

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

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

    private sealed class DirectProviderFactory(ILLMProvider provider) : ILLMProviderFactory
    {
        public ILLMProvider GetProvider(string name) => provider;
        public ILLMProvider GetDefault() => provider;
        public IReadOnlyList<string> GetAvailableProviders() => [provider.Name];
    }

    private sealed class DeterministicChatClient(ProviderRequestFactsCollector requestFacts) : IChatClient
    {
        private int _round;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<MeaiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new MeaiChatMessage(ChatRole.Assistant, "fallback")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MeaiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            requestFacts.Observe(new ProviderRequestFact(
                "in-process-ichatclient",
                true,
                options?.RawRepresentationFactory != null,
                false,
                false,
                options?.Tools?.Count > 0));
            return StreamAsync(Interlocked.Increment(ref _round), cancellationToken);
        }

        public object? GetService(System.Type serviceType, object? serviceKey = null) => null;
        public void Dispose()
        {
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
            int round,
            [EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (round == 1)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "meai-prefix");
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [new TextReasoningContent("meai-reasoning")]);
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [new DataContent(new byte[] { 0x89, 0x50, 0x4e, 0x47 }, "image/png")]);
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [
                        new FunctionCallContent(
                            "call-1",
                            "measurement_tool_1",
                            new Dictionary<string, object?> { ["index"] = 1 }),
                    ])
                {
                    FinishReason = Microsoft.Extensions.AI.ChatFinishReason.ToolCalls,
                };
                await Task.Yield();
                yield break;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, "meai-final")
            {
                FinishReason = Microsoft.Extensions.AI.ChatFinishReason.Stop,
            };
            yield return new ChatResponseUpdate(
                ChatRole.Assistant,
                [
                    new UsageContent(new UsageDetails
                    {
                        InputTokenCount = 11,
                        OutputTokenCount = 7,
                        TotalTokenCount = 18,
                    }),
                ]);
            await Task.Yield();
        }
    }

    private sealed class ProviderPublicationObserver
    {
        private readonly object _lock = new();
        private readonly List<StateEvent> _published = [];
        private readonly Dictionary<string, TaskCompletionSource> _drainMarkers = new(StringComparer.Ordinal);

        public Task ObserveAsync(EventEnvelope envelope)
        {
            TaskCompletionSource? drain = null;
            lock (_lock)
            {
                if (_drainMarkers.Remove(envelope.Id, out var marker))
                    drain = marker;
                else if (envelope.Payload?.Is(CommittedStateEventPublished.Descriptor) == true)
                {
                    var published = envelope.Payload.Unpack<CommittedStateEventPublished>();
                    if (published.StateEvent != null)
                        _published.Add(published.StateEvent.Clone());
                }
            }
            drain?.TrySetResult();
            return Task.CompletedTask;
        }

        public async Task DrainAsync(IStream stream)
        {
            var markerId = $"provider-normalization-drain-{Guid.NewGuid():N}";
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock)
                _drainMarkers.Add(markerId, completion);
            await stream.ProduceAsync(new EventEnvelope
            {
                Id = markerId,
                Payload = Any.Pack(new StringValue { Value = "provider-normalization-drain" }),
                Route = EnvelopeRouteSemantics.CreateDirect("provider-normalization-measurement", stream.StreamId),
            });
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }

        public IReadOnlyList<StateEvent> PublishedEventsSnapshot()
        {
            lock (_lock)
                return _published.Select(static item => item.Clone()).ToArray();
        }
    }

    private sealed class ProviderRequestFactsCollector
    {
        private readonly object _lock = new();
        private readonly List<ProviderRequestFact> _facts = [];

        public void Observe(ProviderRequestFact fact)
        {
            lock (_lock)
                _facts.Add(fact);
        }

        public void Reset()
        {
            lock (_lock)
                _facts.Clear();
        }

        public IReadOnlyList<ProviderRequestFact> Snapshot()
        {
            lock (_lock)
                return _facts.ToArray();
        }
    }

    private sealed class OpenAICompatibleLoopbackServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _serveTask;

        private OpenAICompatibleLoopbackServer(HttpListener listener, Uri baseUri)
        {
            _listener = listener;
            BaseUri = baseUri;
            _serveTask = ServeAsync();
        }

        public Uri BaseUri { get; }
        public ProviderRequestFactsCollector NyxIdFacts { get; } = new();
        public ProviderRequestFactsCollector TornadoFacts { get; } = new();

        public static Task<OpenAICompatibleLoopbackServer> StartAsync()
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            var uri = new Uri($"http://127.0.0.1:{port}/");
            var listener = new HttpListener();
            listener.Prefixes.Add(uri.ToString());
            listener.Start();
            return Task.FromResult(new OpenAICompatibleLoopbackServer(listener, uri));
        }

        public ProviderRequestFactsCollector FactsFor(string providerName) => providerName switch
        {
            "nyxid" => NyxIdFacts,
            "tornado" => TornadoFacts,
            _ => throw new InvalidOperationException($"No loopback facts for provider '{providerName}'."),
        };

        private async Task ServeAsync()
        {
            while (!_shutdown.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().WaitAsync(_shutdown.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (HttpListenerException) when (_shutdown.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    await HandleAsync(context);
                }
                catch (Exception ex)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = ex.GetType().Name }));
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var path = context.Request.Url?.AbsolutePath ?? string.Empty;
            var provider = path.Contains("/api/v1/llm/gateway/v1/", StringComparison.Ordinal)
                ? "nyxid"
                : "tornado";
            var stream = root.TryGetProperty("stream", out var streamElement) && streamElement.ValueKind == JsonValueKind.True;
            var usageOptIn = root.TryGetProperty("stream_options", out var streamOptions) &&
                             streamOptions.TryGetProperty("include_usage", out var includeUsage) &&
                             includeUsage.ValueKind == JsonValueKind.True;
            var toolsAdvertised = root.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array;
            FactsFor(provider).Observe(new ProviderRequestFact(
                path,
                stream,
                usageOptIn,
                !string.IsNullOrWhiteSpace(context.Request.Headers["Authorization"]),
                !string.IsNullOrWhiteSpace(context.Request.UserAgent),
                toolsAdvertised));

            var hasToolResult = HasToolResult(root);
            var response = BuildSse(provider, hasToolResult);
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "text/event-stream";
            context.Response.SendChunked = true;
            var bytes = Encoding.UTF8.GetBytes(response);
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }

        private static bool HasToolResult(JsonElement root)
        {
            if (!root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
                return false;
            foreach (var message in messages.EnumerateArray())
            {
                if (message.TryGetProperty("role", out var role) && role.GetString() == "tool")
                    return true;
                if (message.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.String &&
                    content.GetString()?.Contains("\"ok\":true", StringComparison.Ordinal) == true)
                {
                    return true;
                }
            }
            return false;
        }

        private static string BuildSse(string provider, bool hasToolResult)
        {
            var frames = new List<object>();
            if (provider == "tornado")
            {
                frames.Add(Chunk(new { content = "tornado-final" }, "stop"));
                frames.Add(UsageChunk());
                return string.Join(string.Empty, frames.Select(frame => $"data: {JsonSerializer.Serialize(frame)}\n\n")) +
                       "data: [DONE]\n\n";
            }
            if (!hasToolResult)
            {
                frames.Add(Chunk(new { content = $"{provider}-prefix" }, null));
                if (provider == "nyxid")
                    frames.Add(Chunk(new { reasoning_content = "nyxid-reasoning" }, null));
                frames.Add(Chunk(new
                {
                    tool_calls = new[]
                    {
                        new
                        {
                            index = 0,
                            id = "call-1",
                            type = "function",
                            function = new { name = "measurement_tool_1", arguments = ToolArgumentsJson },
                        },
                    },
                }, "tool_calls"));
            }
            else
            {
                if (provider == "nyxid")
                    frames.Add(Chunk(new { reasoning_content = "nyxid-final-reasoning" }, null));
                frames.Add(Chunk(new { content = $"{provider}-final" }, "stop"));
                frames.Add(UsageChunk());
            }
            return string.Join(string.Empty, frames.Select(frame => $"data: {JsonSerializer.Serialize(frame)}\n\n")) +
                   "data: [DONE]\n\n";
        }

        private static object Chunk(object delta, string? finishReason) => new
        {
            id = "chatcmpl-measurement",
            @object = "chat.completion.chunk",
            created = 0,
            model = "deterministic-model",
            choices = new[] { new { index = 0, delta, finish_reason = finishReason } },
        };

        private static object UsageChunk() => new
        {
            id = "chatcmpl-measurement",
            @object = "chat.completion.chunk",
            created = 0,
            model = "deterministic-model",
            choices = Array.Empty<object>(),
            usage = new { prompt_tokens = 11, completion_tokens = 7, total_tokens = 18 },
        };

        public async ValueTask DisposeAsync()
        {
            await _shutdown.CancelAsync();
            _listener.Close();
            try
            {
                await _serveTask;
            }
            catch (OperationCanceledException)
            {
            }
            _shutdown.Dispose();
        }
    }
}

public sealed record ProviderNormalizationConfig
{
    public int SchemaVersion { get; init; }
    public int WarmupIterations { get; init; }
    public int MeasuredIterations { get; init; }
    public int SnapshotInterval { get; init; }
    public bool EnableEventCompaction { get; init; }
    public int RetainedEventsAfterSnapshot { get; init; }
    public List<string> Providers { get; init; } = [];
}

public sealed record ProviderNormalizationOutput(
    int SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    string SourceCommit,
    ProviderNormalizationEnvironment Environment,
    ProviderNormalizationProvenance Provenance,
    ProviderNormalizationMetricSemantics MetricSemantics,
    ProviderNormalizationConfig Config,
    IReadOnlyList<ProviderNormalizationResult> Providers);

public sealed record ProviderNormalizationEnvironment(
    string OsDescription,
    string ProcessArchitecture,
    string Framework);

public sealed record ProviderNormalizationProvenance(
    string ConfigSha256,
    IReadOnlyDictionary<string, string> SourceSha256,
    string NetworkBoundary);

public sealed record ProviderNormalizationMetricSemantics(
    string ExecutionPath,
    string CorrectnessEvidence,
    string Percentiles,
    IReadOnlyList<string> AllowedMetricLabels);

public sealed record ProviderCoverage(
    bool Text,
    bool Reasoning,
    bool Media,
    bool Tool,
    bool Usage,
    bool Terminal,
    string? UnsupportedReason);

public sealed record ProviderRequestFact(
    string Path,
    bool Stream,
    bool UsageOptIn,
    bool AuthPresent,
    bool UserAgentPresent,
    bool ToolsAdvertised);

public sealed record ProviderNormalizationResult(
    string Provider,
    ProviderCoverage Coverage,
    IReadOnlyList<ProviderRequestFact> RequestFacts,
    IReadOnlyList<ProviderNormalizationSample> Samples,
    ProviderNormalizationSummary Summary);

public sealed record ProviderNormalizationSample(
    int Iteration,
    long AppendAttempts,
    long CommittedEvents,
    long CommittedSerializedBytes,
    double EventStoreIoDurationMs,
    double? FirstTokenLatencyMs,
    double CompletionLatencyMs,
    double CpuMs,
    long AllocatedBytes,
    int ProgressEventCount,
    bool ProgressSequenceMonotonic,
    bool TextObserved,
    bool ReasoningObserved,
    bool MediaObserved,
    bool ToolStartedObserved,
    bool ToolCompletedObserved,
    bool UsageObserved,
    bool TerminalObserved,
    bool UniqueCompletion,
    string ToolArgumentsSha256,
    bool AppendLedgerMatchesDurableReadback,
    bool DurableReadbackMatchesPublication);

public sealed record ProviderNormalizationSummary(
    Distribution AppendAttempts,
    Distribution CommittedEvents,
    Distribution CommittedSerializedBytes,
    Distribution EventStoreIoDurationMs,
    Distribution FirstTokenLatencyMs,
    Distribution CompletionLatencyMs,
    Distribution CpuMs,
    Distribution AllocatedBytes)
{
    public static ProviderNormalizationSummary From(IReadOnlyList<ProviderNormalizationSample> samples) => new(
        Distribution.From(samples.Select(static item => (double?)item.AppendAttempts)),
        Distribution.From(samples.Select(static item => (double?)item.CommittedEvents)),
        Distribution.From(samples.Select(static item => (double?)item.CommittedSerializedBytes)),
        Distribution.From(samples.Select(static item => (double?)item.EventStoreIoDurationMs)),
        Distribution.From(samples.Select(static item => item.FirstTokenLatencyMs)),
        Distribution.From(samples.Select(static item => (double?)item.CompletionLatencyMs)),
        Distribution.From(samples.Select(static item => (double?)item.CpuMs)),
        Distribution.From(samples.Select(static item => (double?)item.AllocatedBytes)));
}

internal sealed record ProviderCommittedEvidence(
    int ProgressEventCount,
    bool ProgressSequenceMonotonic,
    bool TextObserved,
    bool ReasoningObserved,
    bool MediaObserved,
    bool ToolStartedObserved,
    bool ToolCompletedObserved,
    bool UsageObserved,
    bool TerminalObserved,
    bool UniqueCompletion,
    string ToolArgumentsSha256,
    bool AppendLedgerMatchesDurableReadback,
    bool DurableReadbackMatchesPublication);
