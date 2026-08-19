using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.Metadata;
using Aevatar.Workflow.Projection.Projectors;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Xunit.Abstractions;

namespace Aevatar.Workflow.Host.Api.Tests;

/// <summary>
/// Issue #3477 load shape: one workflow run actor committing 263 events (130 step request/completion
/// pairs) where a handful of steps carry 84 KB – 401 KB request-parameter values. The stream is
/// replayed through the real <see cref="WorkflowRunInsightReportArtifactProjector"/> twice:
/// (A) over an in-memory mutator to measure reducer/clone cost and document growth, and
/// (B) over the real <see cref="ElasticsearchProjectionDocumentStore{TReadModel,TKey}"/> mutator
/// against an in-process fake Elasticsearch to count requests, bytes read/written and the
/// serialization / ES-write share of the time. Timing is recorded via <see cref="ITestOutputHelper"/>,
/// not asserted (except a coarse total sanity bound); the request/byte invariants are asserted.
/// </summary>
[Trait("Category", "Benchmark")]
public sealed class WorkflowReportArtifactWriteCostBenchmarkTests
{
    private const string RootActorId = "workflow-run-3477";
    private const string RunId = "run-3477";
    private const string WorkflowName = "benchmark-report-artifact";
    private const string ProjectionKind = "workflow-execution-materialization";
    private const string RoleActorId = "role-actor-operator";
    private const int CommittedEventCount = 263;
    private const int StepCount = 130;
    private const int ReplaySampleCount = 10;
    private const int DocumentBytesReportStride = 50;
    private static readonly DateTimeOffset StreamStartedAt = DateTimeOffset.Parse("2026-08-18T09:00:00+00:00");
    private static readonly TimeSpan TotalSanityBound = TimeSpan.FromSeconds(60);

    // Production incident shape: the largest request-parameter values were 401 KB / 313 KB
    // (code_execute) and 160 / 111 / 88 / 84 KB (assign). Keyed by zero-based step ordinal.
    private static readonly IReadOnlyDictionary<int, LargeParameterShape> LargeParameterSteps =
        new Dictionary<int, LargeParameterShape>
        {
            [9] = new("code_execute", "code", 401 * 1024),
            [19] = new("assign", "value", 160 * 1024),
            [39] = new("code_execute", "code", 313 * 1024),
            [59] = new("assign", "value", 111 * 1024),
            [89] = new("assign", "value", 88 * 1024),
            [119] = new("assign", "value", 84 * 1024),
        };

    private readonly ITestOutputHelper _output;

    public WorkflowReportArtifactWriteCostBenchmarkTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task ReportArtifactProjector_263EventLargeParameterRun_RecordsWriteCostAndHoldsWriteInvariants()
    {
        var stream = BuildProductionShapedStream();
        AssertStreamShape(stream);
        var replaySample = PickReplaySample(stream);
        var lateOutOfOrderEvent = BuildLateOutOfOrderEvent();
        var totalStarted = Stopwatch.GetTimestamp();

        var inMemory = await ReplayOverInMemoryMutatorAsync(stream, replaySample, lateOutOfOrderEvent);
        var elasticsearch = await ReplayOverElasticsearchStoreAsync(stream, replaySample, lateOutOfOrderEvent);

        var totalElapsed = Stopwatch.GetElapsedTime(totalStarted);
        WriteReport(inMemory, elasticsearch, totalElapsed);

        // Cross-path identity: the JSON round trip through Elasticsearch must reproduce the exact
        // protobuf document the in-memory mutator produced.
        elasticsearch.FinalDocument.Should().Be(inMemory.FinalDocument);
        totalElapsed.Should().BeLessThan(TotalSanityBound);
    }

    // ---------------------------------------------------------------------------------------------
    // Phase A: in-memory mutator (reducer + clone + document growth)
    // ---------------------------------------------------------------------------------------------

    private static async Task<InMemoryReplayResult> ReplayOverInMemoryMutatorAsync(
        IReadOnlyList<CommittedEvent> stream,
        IReadOnlyList<CommittedEvent> replaySample,
        CommittedEvent lateOutOfOrderEvent)
    {
        var mutator = new TimedInMemoryReportMutator();
        var graphWriter = new CountingGraphWriter();
        var projector = new WorkflowRunInsightReportArtifactProjector(mutator, graphWriter);
        var context = BuildContext();

        var documentBytesByVersion = new long[CommittedEventCount + 1];
        var replayStarted = Stopwatch.GetTimestamp();
        foreach (var committed in stream)
        {
            await projector.ProjectAsync(context, committed.Envelope);
            documentBytesByVersion[committed.Version] = mutator.Stored[RootActorId].CalculateSize();
        }

        var replayElapsed = Stopwatch.GetElapsedTime(replayStarted);
        mutator.MutationCount.Should().Be(CommittedEventCount);
        mutator.AppliedCount.Should().Be(CommittedEventCount);
        graphWriter.UpsertCount.Should().Be(CommittedEventCount);
        AssertMonotonicNonDecreasing(documentBytesByVersion.Skip(1).ToArray(), "in-memory document bytes");

        var finalDocument = mutator.Stored[RootActorId].Clone();
        var shape = MeasureFinalDocument(finalDocument, stream);

        // Replaying already-applied events: one mutation (read) each, no applied write, no growth.
        // Only the exact head event is a byte-identical Duplicate (which still refreshes the graph);
        // every older replay is Stale and is dropped before the graph phase.
        var mutationsBefore = mutator.MutationCount;
        var appliedBefore = mutator.AppliedCount;
        var graphBefore = graphWriter.UpsertCount;
        foreach (var committed in replaySample)
            await projector.ProjectAsync(context, committed.Envelope);
        (mutator.MutationCount - mutationsBefore).Should().Be(ReplaySampleCount);
        mutator.AppliedCount.Should().Be(appliedBefore);
        (graphWriter.UpsertCount - graphBefore).Should().Be(replaySample.Count(x => x.Version == CommittedEventCount));
        AssertDocumentUnchanged(mutator.Stored[RootActorId], finalDocument);

        // An older, never-seen event arriving after a newer one: one read, no write, no growth.
        await projector.ProjectAsync(context, lateOutOfOrderEvent.Envelope);
        mutator.MutationCount.Should().Be(mutationsBefore + ReplaySampleCount + 1);
        mutator.AppliedCount.Should().Be(appliedBefore);
        graphWriter.UpsertCount.Should().Be(graphBefore + replaySample.Count(x => x.Version == CommittedEventCount));
        AssertDocumentUnchanged(mutator.Stored[RootActorId], finalDocument);

        return new InMemoryReplayResult(
            finalDocument,
            shape,
            documentBytesByVersion,
            mutator.ReducerMilliseconds.Take(CommittedEventCount).ToArray(),
            stream.Where(x => x.LargeParameter != null).Select(x => x.Version).ToArray(),
            replayElapsed);
    }

    // ---------------------------------------------------------------------------------------------
    // Phase B: real Elasticsearch store + mutator against an in-process fake Elasticsearch
    // ---------------------------------------------------------------------------------------------

    private static async Task<ElasticsearchReplayResult> ReplayOverElasticsearchStoreAsync(
        IReadOnlyList<CommittedEvent> stream,
        IReadOnlyList<CommittedEvent> replaySample,
        CommittedEvent lateOutOfOrderEvent)
    {
        var fakeElasticsearch = new FakeElasticsearchHandler();
        var options = new ElasticsearchProjectionDocumentStoreOptions
        {
            Endpoints = ["http://fake-elasticsearch.invalid:9200"],
            AutoCreateIndex = true,
        };
        using var store = new ElasticsearchProjectionDocumentStore<WorkflowRunInsightReportDocument, string>(
            options,
            new WorkflowRunInsightReportDocumentMetadataProvider().Metadata,
            keySelector: document => document.Id,
            keyFormatter: key => key,
            typeRegistry: TypeRegistry.Empty,
            httpMessageHandler: fakeElasticsearch);
        var timedMutator = new ReducerTimingMutator(store);
        var graphWriter = new CountingGraphWriter();
        var projector = new WorkflowRunInsightReportArtifactProjector(timedMutator, graphWriter);
        var context = BuildContext();

        var replayStarted = Stopwatch.GetTimestamp();
        foreach (var committed in stream)
            await projector.ProjectAsync(context, committed.Envelope);
        var replayElapsed = Stopwatch.GetElapsedTime(replayStarted);
        var handlerElapsed = fakeElasticsearch.TimeInsideHandler;
        var reducerMilliseconds = timedMutator.ReducerMilliseconds.ToArray();

        // Uncontended path: exactly one document GET followed by one conditional PUT per applied
        // event, plus the one-time index probes which all happen before the first document request.
        var streamGetCount = fakeElasticsearch.DocumentGetCount;
        var streamPutCount = fakeElasticsearch.DocumentPutCount;
        var indexProbeCount = fakeElasticsearch.IndexProbeCount;
        streamGetCount.Should().Be(CommittedEventCount);
        streamPutCount.Should().Be(CommittedEventCount);
        fakeElasticsearch.CreatePutCount.Should().Be(1);
        fakeElasticsearch.ConflictCount.Should().Be(0);
        indexProbeCount.Should().BeGreaterThan(0);
        fakeElasticsearch.RequestKinds.Count.Should().Be(CommittedEventCount + CommittedEventCount + indexProbeCount);
        fakeElasticsearch.RequestKinds.Take(indexProbeCount)
            .Should().OnlyContain(kind => kind == FakeRequestKind.IndexProbe);
        var documentRequests = fakeElasticsearch.RequestKinds.Skip(indexProbeCount).ToArray();
        for (var index = 0; index < documentRequests.Length; index += 2)
        {
            documentRequests[index].Should().Be(FakeRequestKind.DocumentGet, $"event {index / 2 + 1} must start with a GET");
            documentRequests[index + 1].Should().Be(FakeRequestKind.DocumentPut, $"event {index / 2 + 1} must commit with one PUT");
        }

        graphWriter.UpsertCount.Should().Be(CommittedEventCount);
        AssertMonotonicNonDecreasing(fakeElasticsearch.DocumentPutBodyBytes.ToArray(), "Elasticsearch PUT body bytes");

        var finalPutBody = fakeElasticsearch.GetStoredBody(RootActorId)
                           ?? throw new InvalidOperationException("Fake Elasticsearch holds no report document.");
        var finalDocument = ParseStoredDocument(finalPutBody);
        var shape = MeasureFinalDocument(finalDocument, stream);
        var bytesReadAfterStream = fakeElasticsearch.BytesRead;
        var bytesWrittenAfterStream = fakeElasticsearch.BytesWritten;

        // Replaying already-applied events: one GET each, zero PUTs, no growth.
        var getsBefore = fakeElasticsearch.DocumentGetCount;
        var putsBefore = fakeElasticsearch.DocumentPutCount;
        var graphBefore = graphWriter.UpsertCount;
        foreach (var committed in replaySample)
            await projector.ProjectAsync(context, committed.Envelope);
        (fakeElasticsearch.DocumentGetCount - getsBefore).Should().Be(ReplaySampleCount);
        fakeElasticsearch.DocumentPutCount.Should().Be(putsBefore);
        (graphWriter.UpsertCount - graphBefore).Should().Be(replaySample.Count(x => x.Version == CommittedEventCount));
        fakeElasticsearch.BytesWritten.Should().Be(bytesWrittenAfterStream);
        AssertDocumentUnchanged(ParseStoredDocument(fakeElasticsearch.GetStoredBody(RootActorId)!), finalDocument);
        var replayBytesRead = fakeElasticsearch.BytesRead - bytesReadAfterStream;

        // An older, never-seen event after a newer one: one GET, no PUT, no growth.
        await projector.ProjectAsync(context, lateOutOfOrderEvent.Envelope);
        fakeElasticsearch.DocumentGetCount.Should().Be(getsBefore + ReplaySampleCount + 1);
        fakeElasticsearch.DocumentPutCount.Should().Be(putsBefore);
        fakeElasticsearch.BytesWritten.Should().Be(bytesWrittenAfterStream);
        AssertDocumentUnchanged(ParseStoredDocument(fakeElasticsearch.GetStoredBody(RootActorId)!), finalDocument);

        var directSerialization = MeasureDirectJsonCost(
            finalDocument,
            fakeElasticsearch.RenderGetResponse(RootActorId)
            ?? throw new InvalidOperationException("Fake Elasticsearch holds no report document."));

        return new ElasticsearchReplayResult(
            finalDocument,
            shape,
            streamGetCount,
            streamPutCount,
            indexProbeCount,
            bytesReadAfterStream,
            bytesWrittenAfterStream,
            replayBytesRead,
            fakeElasticsearch.DocumentPutBodyBytes.ToArray(),
            reducerMilliseconds,
            replayElapsed,
            handlerElapsed,
            directSerialization);
    }

    // ---------------------------------------------------------------------------------------------
    // Stream synthesis
    // ---------------------------------------------------------------------------------------------

    private static IReadOnlyList<CommittedEvent> BuildProductionShapedStream()
    {
        var events = new List<CommittedEvent>(CommittedEventCount);
        long version = 1;
        events.Add(Commit(
            version++,
            new WorkflowRunExecutionStartedEvent
            {
                RunId = RunId,
                WorkflowName = WorkflowName,
                Input = "Benchmark input for the 263-event report artifact run.",
                DefinitionActorId = "definition-3477",
            },
            BuildState("running")));
        events.Add(Commit(
            version++,
            new WorkflowRoleActorLinkedEvent
            {
                RunId = RunId,
                RoleId = "operator",
                ChildActorId = RoleActorId,
            },
            BuildState("running")));

        for (var ordinal = 0; ordinal < StepCount; ordinal++)
        {
            var stepId = $"step-{ordinal + 1:000}";
            var request = BuildStepRequest(ordinal, stepId, out var largeParameter);
            events.Add(Commit(version++, request, BuildState("running"), stepId, largeParameter));
            events.Add(Commit(version++, BuildStepCompleted(ordinal, stepId, request.StepType), BuildState("running"), stepId));
        }

        events.Add(Commit(
            version,
            new WorkflowCompletedEvent
            {
                RunId = RunId,
                WorkflowName = WorkflowName,
                Success = true,
                Output = "Benchmark run completed.",
            },
            BuildState("completed", finalOutput: "Benchmark run completed.")));
        return events;
    }

    private static StepRequestEvent BuildStepRequest(
        int ordinal,
        string stepId,
        out LargeParameterValue? largeParameter)
    {
        largeParameter = null;
        var request = new StepRequestEvent
        {
            RunId = RunId,
            StepId = stepId,
            ExecutionId = $"exec-{stepId}",
            TargetRole = "operator",
            DisplayName = $"Step {ordinal + 1}",
        };

        if (LargeParameterSteps.TryGetValue(ordinal, out var shape))
        {
            var value = BuildLargeParameterValue(stepId, shape.ValueBytes);
            largeParameter = new LargeParameterValue(shape.ParameterKey, value);
            request.StepType = shape.StepType;
            request.Parameters[shape.ParameterKey] = value;
            if (shape.StepType == "code_execute")
            {
                request.Parameters["language"] = "python";
                request.Parameters["timeout_ms"] = "60000";
            }
            else
            {
                request.Parameters["target"] = $"payload_{ordinal}";
            }

            return request;
        }

        if (ordinal % 5 == 2)
        {
            request.StepType = "tool_call";
            request.Parameters["tool"] = "search_catalog";
            request.Parameters["query"] = $"catalog partition {ordinal} relevance ranking for the benchmark corpus";
            return request;
        }

        request.StepType = "llm_call";
        request.Parameters["prompt"] =
            $"Summarize partition {ordinal} of the benchmark corpus and list the three most relevant findings with a short rationale for each.";
        request.Parameters["temperature"] = "0.2";
        request.Parameters["max_tokens"] = "512";
        return request;
    }

    private static StepCompletedEvent BuildStepCompleted(int ordinal, string stepId, string stepType)
    {
        var completed = new StepCompletedEvent
        {
            RunId = RunId,
            StepId = stepId,
            ExecutionId = $"exec-{stepId}",
            Success = true,
            Outcome = WorkflowStepCompletionOutcome.Succeeded,
            Output = $"Step {stepId} ({stepType}) produced {(ordinal * 37) % 101} rows for partition {ordinal}.",
            WorkerId = RoleActorId,
            Annotations =
            {
                ["attempt"] = "1",
                ["duration_ms"] = (120 + ordinal).ToString(),
            },
            Usage = new WorkflowUsageMetrics
            {
                PromptTokens = 100 + ordinal,
                CompletionTokens = 40,
                TotalTokens = 140 + ordinal,
                Model = "gpt-test",
                Cost = 0.5,
                LatencyMs = 180,
            },
        };
        if (stepType == "assign")
        {
            completed.AssignedVariable = $"payload_{ordinal}";
            completed.AssignedValue = "ok";
        }

        return completed;
    }

    /// <summary>
    /// Deterministic, ASCII-only, exactly <paramref name="byteLength"/> UTF-8 bytes, free of anything the
    /// audit sanitizer would redact (no secrets, bearer/basic tokens, e-mail addresses or key=value
    /// secret assignments), so the stored copies can be compared byte-for-byte with the source.
    /// </summary>
    private static string BuildLargeParameterValue(string stepId, int byteLength)
    {
        var builder = new StringBuilder(byteLength + 128);
        var line = 0;
        while (builder.Length < byteLength)
        {
            builder.Append("block_").Append(line)
                .Append(" = transform_rows(").Append(stepId)
                .Append(", window_").Append(line % 17)
                .Append(", factor_").Append(line % 7)
                .Append(")\n");
            line++;
        }

        builder.Length = byteLength;
        return builder.ToString();
    }

    private static CommittedEvent BuildLateOutOfOrderEvent() =>
        Commit(
            150,
            new StepRequestEvent
            {
                RunId = RunId,
                StepId = "late-step",
                StepType = "llm_call",
                TargetRole = "operator",
                Parameters = { ["prompt"] = "This older event arrives after the head was already materialized." },
            },
            BuildState("running"),
            stepId: "late-step",
            eventId: "evt-150-late");

    private static IReadOnlyList<CommittedEvent> PickReplaySample(IReadOnlyList<CommittedEvent> stream)
    {
        // Seeded so the sample is stable across runs: nine already-applied events from the body of
        // the stream plus the head event, which exercises the byte-identical Duplicate path.
        var random = new Random(3477);
        var versions = new HashSet<long> { CommittedEventCount };
        while (versions.Count < ReplaySampleCount)
            versions.Add(random.Next(1, CommittedEventCount));
        return versions.OrderBy(x => x).Select(version => stream[(int)version - 1]).ToArray();
    }

    private static CommittedEvent Commit(
        long version,
        IMessage payload,
        WorkflowRunState state,
        string? stepId = null,
        LargeParameterValue? largeParameter = null,
        string? eventId = null)
    {
        var resolvedEventId = eventId ?? $"evt-{version:000}";
        var timestamp = Timestamp.FromDateTimeOffset(StreamStartedAt.AddSeconds(version));
        var envelope = new EventEnvelope
        {
            Id = $"outer-{version:000}",
            Timestamp = timestamp,
            Route = EnvelopeRouteSemantics.CreateObserverPublication(RootActorId),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = resolvedEventId,
                    Version = version,
                    Timestamp = timestamp,
                    EventData = Any.Pack(payload),
                },
                StateRoot = Any.Pack(state),
            }),
        };
        return new CommittedEvent(version, resolvedEventId, envelope, stepId, largeParameter);
    }

    private static WorkflowRunState BuildState(string status, string finalOutput = "") =>
        new()
        {
            RunId = RunId,
            WorkflowName = WorkflowName,
            LastCommandId = "cmd-3477",
            DefinitionActorId = "definition-3477",
            Status = status,
            Input = "Benchmark input for the 263-event report artifact run.",
            FinalOutput = finalOutput,
            Compiled = true,
        };

    private static WorkflowExecutionMaterializationContext BuildContext() =>
        new()
        {
            RootActorId = RootActorId,
            ProjectionKind = ProjectionKind,
        };

    // ---------------------------------------------------------------------------------------------
    // Measurements and invariants
    // ---------------------------------------------------------------------------------------------

    private static void AssertStreamShape(IReadOnlyList<CommittedEvent> stream)
    {
        stream.Should().HaveCount(CommittedEventCount);
        stream.Select(x => x.Version).Should().BeEquivalentTo(
            Enumerable.Range(1, CommittedEventCount).Select(x => (long)x),
            options => options.WithStrictOrdering());
        stream.Select(x => x.EventId).Should().OnlyHaveUniqueItems();
        stream.Where(x => x.LargeParameter != null).Should().HaveCount(LargeParameterSteps.Count);
        foreach (var committed in stream.Where(x => x.LargeParameter != null))
            Encoding.UTF8.GetByteCount(committed.LargeParameter!.Value).Should().Be(committed.LargeParameter.Value.Length);
    }

    private static FinalDocumentShape MeasureFinalDocument(
        WorkflowRunInsightReportDocument document,
        IReadOnlyList<CommittedEvent> stream)
    {
        document.StateVersion.Should().Be(CommittedEventCount);
        document.LastEventId.Should().Be(stream[^1].EventId);
        document.Steps.Should().HaveCount(StepCount);
        // 1 workflow.start + 130 step.request + 130 step.completed + 1 workflow.completed
        document.Timeline.Should().HaveCount(1 + StepCount * 2 + 1);
        document.CompletionStatus.Should().Be(WorkflowExecutionCompletionStatus.Completed);

        var totalBytes = document.ToByteArray().Length;
        var stepsOnly = new WorkflowRunInsightReportDocument();
        stepsOnly.StepEntries.AddRange(document.StepEntries);
        var timelineOnly = new WorkflowRunInsightReportDocument();
        timelineOnly.TimelineEntries.AddRange(document.TimelineEntries);
        var stepsBytes = stepsOnly.CalculateSize();
        var timelineBytes = timelineOnly.CalculateSize();

        // Phase-2 duplication: every large parameter value is stored at least twice, once in the step
        // trace request_parameters_map and once in the step.request timeline data_map.
        long largeParameterBytes = 0;
        var largeOccurrences = 0;
        foreach (var committed in stream.Where(x => x.LargeParameter != null))
        {
            var (key, value) = committed.LargeParameter!;
            largeParameterBytes += value.Length;
            var step = document.Steps.Single(x => x.StepId == committed.StepId);
            step.RequestParameters.Should().ContainKey(key);
            step.RequestParameters[key].Should().Be(value);
            var timelineHits = document.Timeline.Count(entry =>
                entry.StepId == committed.StepId &&
                entry.Stage == "step.request" &&
                entry.Data.TryGetValue(key, out var stored) &&
                stored == value);
            timelineHits.Should().BeGreaterThanOrEqualTo(1);
            largeOccurrences += 1 + timelineHits;
        }

        largeOccurrences.Should().BeGreaterThanOrEqualTo(2 * LargeParameterSteps.Count);
        var stepsShare = (double)stepsBytes / totalBytes;
        var timelineShare = (double)timelineBytes / totalBytes;
        stepsShare.Should().BeGreaterThan(0.40);
        timelineShare.Should().BeGreaterThan(0.40);

        return new FinalDocumentShape(totalBytes, stepsBytes, timelineBytes, largeParameterBytes, largeOccurrences);
    }

    private static void AssertDocumentUnchanged(
        WorkflowRunInsightReportDocument current,
        WorkflowRunInsightReportDocument expected)
    {
        current.StateVersion.Should().Be(expected.StateVersion);
        current.LastEventId.Should().Be(expected.LastEventId);
        current.Steps.Count.Should().Be(expected.Steps.Count);
        current.Timeline.Count.Should().Be(expected.Timeline.Count);
        current.CalculateSize().Should().Be(expected.CalculateSize());
    }

    private static void AssertMonotonicNonDecreasing(IReadOnlyList<long> series, string label)
    {
        series.Should().NotBeEmpty();
        for (var index = 1; index < series.Count; index++)
        {
            series[index].Should().BeGreaterThanOrEqualTo(
                series[index - 1],
                $"{label} must not shrink between version {index} and {index + 1}");
        }
    }

    private static WorkflowRunInsightReportDocument ParseStoredDocument(string json)
    {
        var parser = new JsonParser(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));
        return parser.Parse<WorkflowRunInsightReportDocument>(json);
    }

    /// <summary>
    /// Replicates, on the final document, the Elasticsearch writer's PUT pipeline (protobuf JSON format,
    /// JsonNode re-parse, ToJsonString, UTF-8 encode) and the reader's GET pipeline (JsonDocument parse of
    /// the ES response, <c>_source</c> raw text, protobuf JsonParser) so the serialization share of one
    /// write at full size is visible directly, not only as the derived remainder.
    /// </summary>
    private static DirectJsonCost MeasureDirectJsonCost(WorkflowRunInsightReportDocument document, string getResponseBody)
    {
        var formatter = new JsonFormatter(
            JsonFormatter.Settings.Default
                .WithPreserveProtoFieldNames(true)
                .WithFormatDefaultValues(true));
        var started = Stopwatch.GetTimestamp();
        var payload = JsonNode.Parse(formatter.Format(document)) as JsonObject
                      ?? throw new InvalidOperationException("Report document did not format as a JSON object.");
        payload["ProjectionDocumentId"] = RootActorId;
        var jsonBytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var formatElapsed = Stopwatch.GetElapsedTime(started);

        started = Stopwatch.GetTimestamp();
        using var response = System.Text.Json.JsonDocument.Parse(getResponseBody);
        var source = response.RootElement.GetProperty("_source").GetRawText();
        var parsed = ParseStoredDocument(source);
        var parseElapsed = Stopwatch.GetElapsedTime(started);
        parsed.StateVersion.Should().Be(document.StateVersion);

        return new DirectJsonCost(jsonBytes.Length, formatElapsed, parseElapsed);
    }

    private static (double P50, double P95, double Max) Percentiles(IReadOnlyList<double> samples)
    {
        var sorted = samples.Order().ToArray();
        double At(double percentile) =>
            sorted[Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1)];
        return (At(0.50), At(0.95), sorted[^1]);
    }

    /// <summary>Samples are indexed by stream order, so sample index + 1 is the committed version.</summary>
    private static long SlowestVersion(IReadOnlyList<double> samples)
    {
        var slowest = 0;
        for (var index = 1; index < samples.Count; index++)
        {
            if (samples[index] > samples[slowest])
                slowest = index;
        }

        return slowest + 1;
    }

    private void WriteReport(InMemoryReplayResult inMemory, ElasticsearchReplayResult elasticsearch, TimeSpan totalElapsed)
    {
        var memoryReducer = Percentiles(inMemory.ReducerMilliseconds);
        var esReducer = Percentiles(elasticsearch.ReducerMilliseconds);
        var esReducerTotal = TimeSpan.FromMilliseconds(elasticsearch.ReducerMilliseconds.Sum());
        var esDerivedSerialization = elasticsearch.ReplayElapsed - esReducerTotal - elasticsearch.HandlerElapsed;
        var shape = inMemory.Shape;
        var largeRequestVersions = inMemory.LargeParameterRequestVersions;

        string Bytes(long value) => value.ToString("N0");
        string Ms(TimeSpan value) => value.TotalMilliseconds.ToString("N0");
        string Pct(long part) => (100.0 * part / shape.TotalBytes).ToString("N1") + "%";
        string ReducerRow(IReadOnlyList<double> samples, (double P50, double P95, double Max) percentiles) =>
            $"{percentiles.P50:N2} / {percentiles.P95:N2} / {percentiles.Max:N2} (max @v{SlowestVersion(samples)})";
        string LargeReducerRow(IReadOnlyList<double> samples) =>
            largeRequestVersions.Sum(version => samples[(int)version - 1]).ToString("N0") + " ms over " +
            string.Join(",", largeRequestVersions.Select(version => $"v{version}"));

        var lines = new List<string>
        {
            $"== #3477 report-artifact write cost: {CommittedEventCount} committed events, {StepCount} steps, {LargeParameterSteps.Count} large parameters ==",
            $"{"metric",-44} | {"in-memory mutator",-38} | elasticsearch store (fake ES)",
            $"{"applied events",-44} | {CommittedEventCount,-38} | {CommittedEventCount}",
            $"{"document GET / PUT / index probes",-44} | {"n/a",-38} | {elasticsearch.DocumentGetCount} / {elasticsearch.DocumentPutCount} / {elasticsearch.IndexProbeCount}",
            $"{"bytes read (GET bodies) / written (PUT)",-44} | {"n/a",-38} | {Bytes(elasticsearch.BytesRead)} / {Bytes(elasticsearch.BytesWritten)}",
            $"{"final doc protobuf bytes",-44} | {Bytes(shape.TotalBytes),-38} | {Bytes(elasticsearch.Shape.TotalBytes)} (parsed from last PUT)",
            $"{"final doc JSON payload bytes (last PUT)",-44} | {"n/a",-38} | {Bytes(elasticsearch.PutBodyBytes[^1])}",
            $"{"steps-only / timeline-only bytes",-44} | {Bytes(shape.StepsBytes)} / {Bytes(shape.TimelineBytes)}",
            $"{"steps-only / timeline-only share",-44} | {Pct(shape.StepsBytes)} / {Pct(shape.TimelineBytes)}",
            $"{"large parameter source bytes / stored copies",-44} | {Bytes(shape.LargeParameterBytes)} / {shape.LargeParameterOccurrences} copies ({Bytes(shape.LargeParameterBytes * 2)} bytes duplicated)",
            $"{"reducer+clone ms p50 / p95 / max",-44} | {ReducerRow(inMemory.ReducerMilliseconds, memoryReducer),-38} | {ReducerRow(elasticsearch.ReducerMilliseconds, esReducer)}",
            $"{"reducer+clone ms total",-44} | {inMemory.ReducerMilliseconds.Sum().ToString("N0"),-38} | {esReducerTotal.TotalMilliseconds:N0}",
            $"{"reducer+clone ms on large-parameter requests",-44} | {LargeReducerRow(inMemory.ReducerMilliseconds),-38} | {LargeReducerRow(elasticsearch.ReducerMilliseconds)}",
            $"{"stream replay ms total",-44} | {Ms(inMemory.ReplayElapsed),-38} | {Ms(elasticsearch.ReplayElapsed)}",
            $"{"fake ES handler ms (inside SendAsync)",-44} | {"n/a",-38} | {Ms(elasticsearch.HandlerElapsed)}",
            $"{"serialize/parse + client ms (derived)",-44} | {"n/a",-38} | {Ms(esDerivedSerialization)}",
            $"{"final-size PUT format / GET parse ms (direct)",-44} | {"n/a",-38} | {Ms(elasticsearch.DirectJson.FormatElapsed)} / {Ms(elasticsearch.DirectJson.ParseElapsed)} ({Bytes(elasticsearch.DirectJson.JsonBytes)} JSON bytes)",
            $"{"replay 10 applied events",-44} | {"+10 reads, +0 writes",-38} | +10 GET, +0 PUT, {Bytes(elasticsearch.ReplayBytesRead)} bytes re-read",
            $"{"out-of-order older event",-44} | {"+1 read, +0 writes",-38} | +1 GET, +0 PUT",
            $"{"wall clock total (both phases)",-44} | {Ms(totalElapsed)} ms",
        };

        var documentBytesLine = new StringBuilder("doc protobuf bytes by version:");
        for (var version = DocumentBytesReportStride; version <= CommittedEventCount; version += DocumentBytesReportStride)
            documentBytesLine.Append($" v{version}={Bytes(inMemory.DocumentBytesByVersion[version])}");
        documentBytesLine.Append($" v{CommittedEventCount}={Bytes(inMemory.DocumentBytesByVersion[CommittedEventCount])}");
        lines.Add(documentBytesLine.ToString());

        var putBytesLine = new StringBuilder("ES PUT body bytes by version:  ");
        for (var version = DocumentBytesReportStride; version <= CommittedEventCount; version += DocumentBytesReportStride)
            putBytesLine.Append($" v{version}={Bytes(elasticsearch.PutBodyBytes[version - 1])}");
        putBytesLine.Append($" v{CommittedEventCount}={Bytes(elasticsearch.PutBodyBytes[^1])}");
        lines.Add(putBytesLine.ToString());

        foreach (var line in lines)
            _output.WriteLine(line);
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures
    // ---------------------------------------------------------------------------------------------

    private sealed record LargeParameterShape(string StepType, string ParameterKey, int ValueBytes);

    private sealed record LargeParameterValue(string Key, string Value);

    private sealed record CommittedEvent(
        long Version,
        string EventId,
        EventEnvelope Envelope,
        string? StepId,
        LargeParameterValue? LargeParameter);

    private sealed record FinalDocumentShape(
        long TotalBytes,
        long StepsBytes,
        long TimelineBytes,
        long LargeParameterBytes,
        int LargeParameterOccurrences);

    private sealed record DirectJsonCost(long JsonBytes, TimeSpan FormatElapsed, TimeSpan ParseElapsed);

    private sealed record InMemoryReplayResult(
        WorkflowRunInsightReportDocument FinalDocument,
        FinalDocumentShape Shape,
        IReadOnlyList<long> DocumentBytesByVersion,
        IReadOnlyList<double> ReducerMilliseconds,
        IReadOnlyList<long> LargeParameterRequestVersions,
        TimeSpan ReplayElapsed);

    private sealed record ElasticsearchReplayResult(
        WorkflowRunInsightReportDocument FinalDocument,
        FinalDocumentShape Shape,
        int DocumentGetCount,
        int DocumentPutCount,
        int IndexProbeCount,
        long BytesRead,
        long BytesWritten,
        long ReplayBytesRead,
        IReadOnlyList<long> PutBodyBytes,
        IReadOnlyList<double> ReducerMilliseconds,
        TimeSpan ReplayElapsed,
        TimeSpan HandlerElapsed,
        DirectJsonCost DirectJson);

    private sealed class CountingGraphWriter : IProjectionGraphWriter<WorkflowRunInsightReportDocument>
    {
        public int UpsertCount { get; private set; }

        public Task UpsertAsync(
            WorkflowRunInsightReportDocument readModel,
            string projectionKind,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            UpsertCount++;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Minimal in-memory mutator with the same read → reduce → evaluate → conditional commit contract as
    /// the provider mutators, timing only the reducer call (which includes the projector's own clone).
    /// </summary>
    private sealed class TimedInMemoryReportMutator
        : IProjectionDocumentMutator<WorkflowRunInsightReportDocument, string>
    {
        public Dictionary<string, WorkflowRunInsightReportDocument> Stored { get; } = new(StringComparer.Ordinal);

        public List<double> ReducerMilliseconds { get; } = [];

        public int MutationCount { get; private set; }

        public int AppliedCount { get; private set; }

        public Task<ProjectionDocumentMutationResult<WorkflowRunInsightReportDocument>> MutateAsync(
            string key,
            Func<WorkflowRunInsightReportDocument?, WorkflowRunInsightReportDocument> reducer,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            MutationCount++;
            Stored.TryGetValue(key, out var existing);
            var existingClone = existing?.Clone();
            var started = Stopwatch.GetTimestamp();
            var incoming = reducer(existingClone);
            ReducerMilliseconds.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            if (!string.Equals(incoming.Id, key, StringComparison.Ordinal))
                throw new InvalidOperationException("Projection mutation changed the document key.");

            var result = ProjectionWriteResultEvaluator.Evaluate(existing, incoming);
            if (result.IsApplied)
            {
                Stored[key] = incoming.Clone();
                AppliedCount++;
            }

            Stored.TryGetValue(key, out var committed);
            return Task.FromResult(new ProjectionDocumentMutationResult<WorkflowRunInsightReportDocument>(result, committed?.Clone()));
        }
    }

    /// <summary>Wraps a mutator to time the reducer the projector hands in, without altering its behavior.</summary>
    private sealed class ReducerTimingMutator
        : IProjectionDocumentMutator<WorkflowRunInsightReportDocument, string>
    {
        private readonly IProjectionDocumentMutator<WorkflowRunInsightReportDocument, string> _inner;

        public ReducerTimingMutator(IProjectionDocumentMutator<WorkflowRunInsightReportDocument, string> inner)
        {
            _inner = inner;
        }

        public List<double> ReducerMilliseconds { get; } = [];

        public Task<ProjectionDocumentMutationResult<WorkflowRunInsightReportDocument>> MutateAsync(
            string key,
            Func<WorkflowRunInsightReportDocument?, WorkflowRunInsightReportDocument> reducer,
            CancellationToken ct = default) =>
            _inner.MutateAsync(
                key,
                existing =>
                {
                    var started = Stopwatch.GetTimestamp();
                    try
                    {
                        return reducer(existing);
                    }
                    finally
                    {
                        ReducerMilliseconds.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    }
                },
                ct);
    }

    private enum FakeRequestKind
    {
        IndexProbe,
        DocumentGet,
        DocumentPut,
    }

    /// <summary>
    /// In-process Elasticsearch stand-in: answers the greenfield index lifecycle probes, stores the last
    /// PUT body per document key with <c>_seq_no</c>/<c>_primary_term</c>, serves it back on GET, enforces
    /// <c>_create</c> and <c>if_seq_no</c>/<c>if_primary_term</c> conditions with 409, and counts request
    /// kinds, bytes read (GET response bodies), bytes written (PUT request bodies) and time spent inside.
    /// </summary>
    private sealed class FakeElasticsearchHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, StoredDocument> _documents = new(StringComparer.Ordinal);
        private TimeSpan _insideHandler;

        public int DocumentGetCount { get; private set; }

        public int DocumentPutCount { get; private set; }

        public int CreatePutCount { get; private set; }

        public int IndexProbeCount { get; private set; }

        public int ConflictCount { get; private set; }

        public long BytesRead { get; private set; }

        public long BytesWritten { get; private set; }

        public List<long> DocumentPutBodyBytes { get; } = [];

        public List<FakeRequestKind> RequestKinds { get; } = [];

        public TimeSpan TimeInsideHandler => _insideHandler;

        public string? GetStoredBody(string key) =>
            _documents.TryGetValue(key, out var stored) ? stored.Body : null;

        public string? RenderGetResponse(string key) =>
            _documents.TryGetValue(key, out var stored) ? RenderFoundResponse("fake-index", key, stored) : null;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                var requestBody = request.Content == null
                    ? null
                    : await request.Content.ReadAsByteArrayAsync(cancellationToken);
                return Handle(request, requestBody);
            }
            finally
            {
                _insideHandler += Stopwatch.GetElapsedTime(started);
            }
        }

        private HttpResponseMessage Handle(HttpRequestMessage request, byte[]? requestBody)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Fake Elasticsearch request has no URI.");
            var path = uri.AbsolutePath.TrimStart('/');
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (request.Method == HttpMethod.Head || path.StartsWith("_alias/", StringComparison.Ordinal))
                return IndexProbe(HttpStatusCode.NotFound, request.Method == HttpMethod.Head ? "" : "{}");

            if (segments.Length == 3 && segments[1] is "_doc" or "_create")
            {
                var key = Uri.UnescapeDataString(segments[2]);
                if (request.Method == HttpMethod.Get)
                    return DocumentGet(segments[0], key);
                if (request.Method == HttpMethod.Put)
                {
                    return DocumentPut(
                        key,
                        create: segments[1] == "_create",
                        uri.Query,
                        requestBody ?? throw new InvalidOperationException("Document PUT without a body."));
                }
            }

            if (request.Method == HttpMethod.Put && segments.Length == 1)
                return IndexProbe(HttpStatusCode.OK, """{"acknowledged":true}""");

            throw new InvalidOperationException($"Unexpected fake Elasticsearch request {request.Method} {uri}.");
        }

        private HttpResponseMessage IndexProbe(HttpStatusCode statusCode, string body)
        {
            IndexProbeCount++;
            RequestKinds.Add(FakeRequestKind.IndexProbe);
            return Json(statusCode, body);
        }

        private HttpResponseMessage DocumentGet(string index, string key)
        {
            DocumentGetCount++;
            RequestKinds.Add(FakeRequestKind.DocumentGet);
            string body;
            HttpStatusCode status;
            if (_documents.TryGetValue(key, out var stored))
            {
                status = HttpStatusCode.OK;
                body = RenderFoundResponse(index, key, stored);
            }
            else
            {
                status = HttpStatusCode.NotFound;
                body = $$"""{"_index":"{{index}}","_id":"{{key}}","found":false}""";
            }

            BytesRead += Encoding.UTF8.GetByteCount(body);
            return Json(status, body);
        }

        private static string RenderFoundResponse(string index, string key, StoredDocument stored) =>
            $$"""{"_index":"{{index}}","_id":"{{key}}","_seq_no":{{stored.SeqNo}},"_primary_term":{{stored.PrimaryTerm}},"found":true,"_source":{{stored.Body}}}""";

        private HttpResponseMessage DocumentPut(string key, bool create, string query, byte[] requestBody)
        {
            DocumentPutCount++;
            RequestKinds.Add(FakeRequestKind.DocumentPut);
            _documents.TryGetValue(key, out var existing);

            if (create)
            {
                CreatePutCount++;
                if (existing != null)
                    return Conflict();
                return Store(key, requestBody, seqNo: 0, primaryTerm: 1, HttpStatusCode.Created, """{"result":"created"}""");
            }

            var (expectedSeqNo, expectedPrimaryTerm) = ParseConcurrencyCondition(query);
            if (existing == null || existing.SeqNo != expectedSeqNo || existing.PrimaryTerm != expectedPrimaryTerm)
                return Conflict();

            return Store(key, requestBody, existing.SeqNo + 1, existing.PrimaryTerm, HttpStatusCode.OK, """{"result":"updated"}""");
        }

        private HttpResponseMessage Store(
            string key,
            byte[] requestBody,
            long seqNo,
            long primaryTerm,
            HttpStatusCode status,
            string responseBody)
        {
            _documents[key] = new StoredDocument(Encoding.UTF8.GetString(requestBody), seqNo, primaryTerm);
            BytesWritten += requestBody.Length;
            DocumentPutBodyBytes.Add(requestBody.Length);
            return Json(status, responseBody);
        }

        private HttpResponseMessage Conflict()
        {
            ConflictCount++;
            return Json(HttpStatusCode.Conflict, """{"error":{"type":"version_conflict_engine_exception"}}""");
        }

        private static (long SeqNo, long PrimaryTerm) ParseConcurrencyCondition(string query)
        {
            long seqNo = -1;
            long primaryTerm = -1;
            foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = pair.IndexOf('=', StringComparison.Ordinal);
                if (separator < 0)
                    continue;
                var name = pair[..separator];
                var value = pair[(separator + 1)..];
                if (name == "if_seq_no")
                    seqNo = long.Parse(value);
                else if (name == "if_primary_term")
                    primaryTerm = long.Parse(value);
            }

            return (seqNo, primaryTerm);
        }

        private static HttpResponseMessage Json(HttpStatusCode statusCode, string body) =>
            new(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };

        private sealed record StoredDocument(string Body, long SeqNo, long PrimaryTerm);
    }
}
