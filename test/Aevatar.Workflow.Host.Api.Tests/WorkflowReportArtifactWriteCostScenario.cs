using System.Text;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Host.Api.Tests;

/// <summary>
/// Issue #3477 load shape: one workflow run actor committing a start event, a role link, one
/// request/completion pair per step and a completion event, where a handful of steps carry large
/// request-parameter values. The same synthesizer feeds the default-suite write invariants test
/// (reduced shape) and the real-Elasticsearch write-cost benchmark (production shape), so the
/// documents both lanes produce are comparable.
/// </summary>
internal sealed class WorkflowReportArtifactWriteCostScenario
{
    public const string RootActorId = "workflow-run-3477";
    public const string RunId = "run-3477";
    public const string WorkflowName = "benchmark-report-artifact";
    public const string ProjectionKind = "workflow-execution-materialization";
    public const string RoleActorId = "role-actor-operator";
    private const string LateEventId = "evt-late-out-of-order";
    private static readonly DateTimeOffset StreamStartedAt = DateTimeOffset.Parse("2026-08-18T09:00:00+00:00");

    // Production incident shape: the largest request-parameter values were 401 KB / 313 KB
    // (code_execute) and 160 / 111 / 88 / 84 KB (assign). Keyed by zero-based step ordinal.
    private static readonly IReadOnlyDictionary<int, LargeParameterShape> ProductionLargeParameterSteps =
        new Dictionary<int, LargeParameterShape>
        {
            [9] = new("code_execute", "code", 401 * 1024),
            [19] = new("assign", "value", 160 * 1024),
            [39] = new("code_execute", "code", 313 * 1024),
            [59] = new("assign", "value", 111 * 1024),
            [89] = new("assign", "value", 88 * 1024),
            [119] = new("assign", "value", 84 * 1024),
        };

    private static readonly IReadOnlyDictionary<int, LargeParameterShape> ReducedLargeParameterSteps =
        new Dictionary<int, LargeParameterShape>
        {
            [2] = new("code_execute", "code", 64 * 1024),
        };

    private WorkflowReportArtifactWriteCostScenario(
        int stepCount,
        IReadOnlyDictionary<int, LargeParameterShape> largeParameterSteps,
        IReadOnlyList<CommittedEvent> events)
    {
        StepCount = stepCount;
        LargeParameterSteps = largeParameterSteps;
        Events = events;
        LateOutOfOrderEvent = BuildLateOutOfOrderEvent(events.Count / 2);
        Context = new WorkflowExecutionMaterializationContext
        {
            RootActorId = RootActorId,
            ProjectionKind = ProjectionKind,
        };
    }

    /// <summary>130 steps, 263 committed events, six large parameters (the production incident).</summary>
    public static WorkflowReportArtifactWriteCostScenario ProductionShape() => Build(130, ProductionLargeParameterSteps);

    /// <summary>6 steps, 15 committed events, one 64 KB parameter: the default-suite invariants shape.</summary>
    public static WorkflowReportArtifactWriteCostScenario ReducedShape() => Build(6, ReducedLargeParameterSteps);

    public int StepCount { get; }

    public IReadOnlyDictionary<int, LargeParameterShape> LargeParameterSteps { get; }

    public IReadOnlyList<CommittedEvent> Events { get; }

    public int CommittedEventCount => Events.Count;

    public CommittedEvent HeadEvent => Events[^1];

    /// <summary>An older, never-seen event (mid-stream version, unique event id) that arrives after the head.</summary>
    public CommittedEvent LateOutOfOrderEvent { get; }

    public WorkflowExecutionMaterializationContext Context { get; }

    public IReadOnlyList<long> LargeParameterRequestVersions =>
        Events.Where(x => x.LargeParameter != null).Select(x => x.Version).ToArray();

    /// <summary>
    /// Stable sample of already-applied events: <paramref name="count"/> - 1 seeded picks from the body of
    /// the stream plus the head event, which exercises the byte-identical Duplicate path.
    /// </summary>
    public IReadOnlyList<CommittedEvent> PickReplaySample(int count)
    {
        var random = new Random(3477);
        var versions = new HashSet<long> { CommittedEventCount };
        while (versions.Count < count)
            versions.Add(random.Next(1, CommittedEventCount));
        return versions.OrderBy(x => x).Select(version => Events[(int)version - 1]).ToArray();
    }

    public void AssertStreamShape()
    {
        Events.Should().HaveCount(1 + 1 + StepCount * 2 + 1);
        Events.Select(x => x.Version).Should().BeEquivalentTo(
            Enumerable.Range(1, CommittedEventCount).Select(x => (long)x),
            options => options.WithStrictOrdering());
        Events.Select(x => x.EventId).Should().OnlyHaveUniqueItems();
        Events.Where(x => x.LargeParameter != null).Should().HaveCount(LargeParameterSteps.Count);
        foreach (var committed in Events.Where(x => x.LargeParameter != null))
            Encoding.UTF8.GetByteCount(committed.LargeParameter!.Value).Should().Be(committed.LargeParameter.Value.Length);
        LateOutOfOrderEvent.Version.Should().BeLessThan(CommittedEventCount);
        Events.Select(x => x.EventId).Should().NotContain(LateOutOfOrderEvent.EventId);
    }

    /// <summary>
    /// Asserts the fully materialized report shape and the store-once invariant: every large value is
    /// retained in one immutable execution evidence record while the step and timeline share its reference.
    /// </summary>
    public FinalDocumentShape MeasureFinalDocument(WorkflowRunInsightReportDocument document)
    {
        document.StateVersion.Should().Be(CommittedEventCount);
        document.LastEventId.Should().Be(HeadEvent.EventId);
        document.Steps.Should().HaveCount(StepCount);
        document.RequestEvidenceById.Should().HaveCount(StepCount);
        // 1 workflow.start + N step.request + N step.completed + 1 workflow.completed
        document.Timeline.Should().HaveCount(1 + StepCount * 2 + 1);
        document.CompletionStatus.Should().Be(WorkflowExecutionCompletionStatus.Completed);

        var totalBytes = document.CalculateSize();
        var stepsOnly = new WorkflowRunInsightReportDocument();
        stepsOnly.StepEntries.AddRange(document.StepEntries);
        var timelineOnly = new WorkflowRunInsightReportDocument();
        timelineOnly.TimelineEntries.AddRange(document.TimelineEntries);
        var evidenceOnly = new WorkflowRunInsightReportDocument();
        evidenceOnly.RequestEvidenceById.Add(document.RequestEvidenceById);
        var stepsBytes = stepsOnly.CalculateSize();
        var timelineBytes = timelineOnly.CalculateSize();
        var evidenceBytes = evidenceOnly.CalculateSize();

        long largeParameterBytes = 0;
        var largeOccurrences = 0;
        foreach (var committed in Events.Where(x => x.LargeParameter != null))
        {
            var (key, value) = committed.LargeParameter!;
            largeParameterBytes += value.Length;
            var step = document.Steps.Single(x => x.StepId == committed.StepId);
            step.RequestParameters.Should().BeEmpty();
            step.RequestEvidenceReference.Should().NotBeNull();
            step.RequestEvidenceReference.ExecutionId.Should().Be($"exec-{committed.StepId}");

            document.RequestEvidenceById.TryGetValue(
                    step.RequestEvidenceReference.EvidenceId,
                    out var evidence)
                .Should().BeTrue();
            evidence!.StepId.Should().Be(committed.StepId);
            evidence.ExecutionId.Should().Be($"exec-{committed.StepId}");
            evidence.SourceEventId.Should().Be(committed.EventId);
            evidence.ParametersMap.Should().ContainKey(key).WhoseValue.Should().Be(value);
            evidence.SourceParameterUtf8Bytes.Should().BeGreaterThanOrEqualTo(value.Length);
            evidence.RetainedParameterUtf8Bytes.Should().BeGreaterThanOrEqualTo(value.Length);
            evidence.RetainedParameterSha256.Should().MatchRegex("^[0-9a-f]{64}$");

            var timelineEntry = document.Timeline.Should().ContainSingle(entry =>
                entry.StepId == committed.StepId && entry.Stage == "step.request").Subject;
            timelineEntry.Data.Should().NotContainKey(key);
            timelineEntry.RequestEvidenceReference.Should().NotBeNull();
            timelineEntry.RequestEvidenceReference.Should().Be(step.RequestEvidenceReference);

            var evidenceHits = document.RequestEvidenceById.Values.Count(item =>
                item.ParametersMap.TryGetValue(key, out var stored) && stored == value);
            var stepHits = document.Steps.Count(item =>
                item.RequestParameters.TryGetValue(key, out var stored) && stored == value);
            var timelineHits = document.Timeline.Count(item =>
                item.Data.TryGetValue(key, out var stored) && stored == value);
            evidenceHits.Should().Be(1);
            stepHits.Should().Be(0);
            timelineHits.Should().Be(0);
            largeOccurrences += evidenceHits + stepHits + timelineHits;
        }

        largeOccurrences.Should().Be(LargeParameterSteps.Count);
        ((long)evidenceBytes).Should().BeGreaterThan(largeParameterBytes);

        return new FinalDocumentShape(
            totalBytes,
            stepsBytes,
            timelineBytes,
            evidenceBytes,
            largeParameterBytes,
            largeOccurrences);
    }

    public static void AssertDocumentUnchanged(
        WorkflowRunInsightReportDocument current,
        WorkflowRunInsightReportDocument expected)
    {
        current.StateVersion.Should().Be(expected.StateVersion);
        current.LastEventId.Should().Be(expected.LastEventId);
        current.Steps.Count.Should().Be(expected.Steps.Count);
        current.Timeline.Count.Should().Be(expected.Timeline.Count);
        current.CalculateSize().Should().Be(expected.CalculateSize());
    }

    public static void AssertMonotonicNonDecreasing(IReadOnlyList<long> series, string label)
    {
        series.Should().NotBeEmpty();
        for (var index = 1; index < series.Count; index++)
        {
            series[index].Should().BeGreaterThanOrEqualTo(
                series[index - 1],
                $"{label} must not shrink between version {index} and {index + 1}");
        }
    }

    public static WorkflowRunInsightReportDocument ParseStoredDocument(string json)
    {
        var parser = new JsonParser(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));
        return parser.Parse<WorkflowRunInsightReportDocument>(json);
    }

    // ---------------------------------------------------------------------------------------------
    // Stream synthesis
    // ---------------------------------------------------------------------------------------------

    private static WorkflowReportArtifactWriteCostScenario Build(
        int stepCount,
        IReadOnlyDictionary<int, LargeParameterShape> largeParameterSteps)
    {
        var events = new List<CommittedEvent>(stepCount * 2 + 3);
        long version = 1;
        events.Add(Commit(
            version++,
            new WorkflowRunExecutionStartedEvent
            {
                RunId = RunId,
                WorkflowName = WorkflowName,
                Input = "Benchmark input for the report artifact run.",
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

        for (var ordinal = 0; ordinal < stepCount; ordinal++)
        {
            var stepId = $"step-{ordinal + 1:000}";
            var request = BuildStepRequest(ordinal, stepId, largeParameterSteps, out var largeParameter);
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
        return new WorkflowReportArtifactWriteCostScenario(stepCount, largeParameterSteps, events);
    }

    private static StepRequestEvent BuildStepRequest(
        int ordinal,
        string stepId,
        IReadOnlyDictionary<int, LargeParameterShape> largeParameterSteps,
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

        if (largeParameterSteps.TryGetValue(ordinal, out var shape))
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

    private static CommittedEvent BuildLateOutOfOrderEvent(long version) =>
        Commit(
            version,
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
            eventId: LateEventId);

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
            Input = "Benchmark input for the report artifact run.",
            FinalOutput = finalOutput,
            Compiled = true,
        };

    public sealed record LargeParameterShape(string StepType, string ParameterKey, int ValueBytes);

    public sealed record LargeParameterValue(string Key, string Value);

    public sealed record CommittedEvent(
        long Version,
        string EventId,
        EventEnvelope Envelope,
        string? StepId,
        LargeParameterValue? LargeParameter);

    public sealed record FinalDocumentShape(
        long TotalBytes,
        long StepsBytes,
        long TimelineBytes,
        long EvidenceBytes,
        long LargeParameterBytes,
        int LargeParameterOccurrences);

    public sealed class CountingGraphWriter : IProjectionGraphWriter<WorkflowRunInsightReportDocument>
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
}
