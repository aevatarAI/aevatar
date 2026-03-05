using System.Text.Json;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sisyphus.Application.Models.Upload;
using Sisyphus.Application.Services;

namespace Sisyphus.Application.Tests;

public class UploadPipelineServiceTests
{
    private readonly StubTarGzParserService _parser;
    private readonly StubChronoGraphWriteService _graphWriter = new();
    private readonly QueuedWorkflowRunCommandService _workflowService = new();
    private readonly GraphIdProvider _graphIdProvider = new();
    private readonly IOptions<UploadOptions> _uploadOptions = Options.Create(new UploadOptions
    {
        ApiBatchSize = 2,
        PurgeBatchSize = 1,
        PurgeMaxRetries = 1,
        PurgeConcurrency = 1,
    });
    private readonly List<(UploadSseEventType Type, object Payload)> _sseEvents = [];

    public UploadPipelineServiceTests()
    {
        _graphIdProvider.SetRead("read-id");
        _graphIdProvider.SetWrite("write-id");
        _parser = new StubTarGzParserService(NullLogger<TarGzParserService>.Instance);
    }

    private UploadPipelineService CreateSut() => new(
        _parser,
        _graphWriter,
        _workflowService,
        _graphIdProvider,
        _uploadOptions,
        NullLogger<UploadPipelineService>.Instance);

    private ValueTask EmitSse(UploadSseEventType type, object payload, CancellationToken _)
    {
        lock (_sseEvents)
            _sseEvents.Add((type, payload));
        return ValueTask.CompletedTask;
    }

    private static string MakeBatchNodePurgeJson(string kgId, int blueCount = 1) =>
        JsonSerializer.Serialize(new BatchNodePurgeResult
        {
            Results =
            [
                new BatchNodePurgeEntry
                {
                    KgId = kgId,
                    BlueNodes = Enumerable.Range(0, blueCount).Select(i => new BlueNodeOutput
                    {
                        TempId = $"b{i}",
                        Type = "theorem",
                        Abstract = $"Abstract {i}",
                        Body = $"Body {i}",
                    }).ToList(),
                    BlueEdges = blueCount > 1
                        ? [new() { Source = "b1", Target = "b0", EdgeType = "references" }]
                        : [],
                },
            ],
        });

    private static string MakeBatchEdgePurgeJson(
        string sourceKgId, string targetKgId, string sourceId, string targetId) =>
        JsonSerializer.Serialize(new BatchEdgePurgeResult
        {
            Results =
            [
                new BatchEdgePurgeEntry
                {
                    SourceKgId = sourceKgId,
                    TargetKgId = targetKgId,
                    BlueEdges = [new() { SourceId = sourceId, TargetId = targetId, EdgeType = "references" }],
                },
            ],
        });

    private static List<RedNode> MakeNodes(int count) =>
        Enumerable.Range(1, count).Select(i => new RedNode
        {
            KgId = $"KG-20260305-{i:D5}",
            Label = $"lbl-node-{i}",
            AtomType = "tp-note",
            TexContent = $"TeX content {i}",
        }).ToList();

    private static List<RedEdge> MakeEdges(List<RedNode> nodes) =>
        nodes.Count >= 2
            ? [new() { SourceKgId = nodes[0].KgId, TargetKgId = nodes[1].KgId, EdgeType = "inference_ref" }]
            : [];

    // ─── Pre-validation ───

    [Fact]
    public async Task Execute_PreValidationFails_EmitsValidationError()
    {
        _parser.NextResult = new TarGzParserService.ParseResult(
            [new() { KgId = "KG-00001", Label = "lbl", AtomType = "tp-note", TexContent = "x" }],
            [],
            ["missing-label"]);

        var sut = CreateSut();
        await sut.ExecuteAsync(Stream.Null, EmitSse, CancellationToken.None);

        _sseEvents.Should().Contain(e => e.Type == UploadSseEventType.VALIDATION_ERROR);
        _sseEvents.Last().Type.Should().Be(UploadSseEventType.STREAM_END);
    }

    [Fact]
    public async Task Execute_EmptyTarGz_CompletesGracefully()
    {
        _parser.NextResult = new TarGzParserService.ParseResult([], [], []);

        var sut = CreateSut();
        await sut.ExecuteAsync(Stream.Null, EmitSse, CancellationToken.None);

        _sseEvents.Should().Contain(e => e.Type == UploadSseEventType.UPLOAD_DONE);
        _sseEvents.Last().Type.Should().Be(UploadSseEventType.STREAM_END);
    }

    // ─── Phase 1 ───

    [Fact]
    public async Task Execute_Phase1_EmitsPhaseStartEvent()
    {
        var nodes = MakeNodes(1);
        _parser.NextResult = new TarGzParserService.ParseResult(nodes, [], []);
        _graphWriter.NextUuids = ["uuid-1", "blue-1"];
        _workflowService.EnqueueOutput(MakeBatchNodePurgeJson(nodes[0].KgId));

        await CreateSut().ExecuteAsync(Stream.Null, EmitSse, CancellationToken.None);

        _sseEvents.Should().Contain(e =>
            e.Type == UploadSseEventType.PHASE_START);
    }

    [Fact]
    public async Task Execute_Phase1_EmitsBatchDoneEvents()
    {
        var nodes = MakeNodes(3); // batch size 2 => 2 batches
        _parser.NextResult = new TarGzParserService.ParseResult(nodes, [], []);
        _graphWriter.NextUuids = ["uuid-1", "uuid-2", "uuid-3", "blue-1", "blue-2", "blue-3"];

        // Phase 2: 3 batch node purge workflow calls (PurgeBatchSize=1)
        foreach (var node in nodes)
            _workflowService.EnqueueOutput(MakeBatchNodePurgeJson(node.KgId));

        await CreateSut().ExecuteAsync(Stream.Null, EmitSse, CancellationToken.None);

        _sseEvents.Count(e => e.Type == UploadSseEventType.BATCH_DONE).Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Execute_Phase1_BuildsKgIdToUuidMapping()
    {
        var nodes = MakeNodes(2);
        _parser.NextResult = new TarGzParserService.ParseResult(nodes, [], []);
        _graphWriter.NextUuids = ["uuid-a", "uuid-b", "blue-a", "blue-b"];

        // Phase 2 workflows
        foreach (var node in nodes)
            _workflowService.EnqueueOutput(MakeBatchNodePurgeJson(node.KgId));

        await CreateSut().ExecuteAsync(Stream.Null, EmitSse, CancellationToken.None);

        // Verify nodes got UUIDs assigned
        nodes[0].GraphUuid.Should().Be("uuid-a");
        nodes[1].GraphUuid.Should().Be("uuid-b");
    }

    // ─── Phase 2 ───

    [Fact]
    public async Task Execute_Phase2_StartsAfterPhase1()
    {
        var nodes = MakeNodes(1);
        _parser.NextResult = new TarGzParserService.ParseResult(nodes, [], []);
        _graphWriter.NextUuids = ["uuid-1", "blue-1"];
        _workflowService.EnqueueOutput(MakeBatchNodePurgeJson(nodes[0].KgId));

        await CreateSut().ExecuteAsync(Stream.Null, EmitSse, CancellationToken.None);

        var phaseStarts = _sseEvents
            .Where(e => e.Type == UploadSseEventType.PHASE_START)
            .ToList();
        phaseStarts.Count.Should().BeGreaterThanOrEqualTo(2);

        // Phase 1 PHASE_DONE should come before Phase 2 PHASE_START
        var phase1Done = _sseEvents.FindIndex(e => e.Type == UploadSseEventType.PHASE_DONE);
        var phase2Start = _sseEvents.FindIndex(phase1Done, e =>
            e.Type == UploadSseEventType.PHASE_START);
        phase2Start.Should().BeGreaterThan(phase1Done);
    }

    [Fact]
    public async Task Execute_Phase2_EmitsNodePurgedEvent()
    {
        var nodes = MakeNodes(1);
        _parser.NextResult = new TarGzParserService.ParseResult(nodes, [], []);
        _graphWriter.NextUuids = ["uuid-1", "blue-uuid-1"];
        _workflowService.EnqueueOutput(MakeBatchNodePurgeJson(nodes[0].KgId));

        await CreateSut().ExecuteAsync(Stream.Null, EmitSse, CancellationToken.None);

        _sseEvents.Should().Contain(e => e.Type == UploadSseEventType.NODE_PURGED);
    }

    [Fact]
    public async Task Execute_Phase2_EmitsNodeFailedOnExhaustion()
    {
        var nodes = MakeNodes(1);
        _parser.NextResult = new TarGzParserService.ParseResult(nodes, [], []);
        _graphWriter.NextUuids = ["uuid-1"];

        // Return invalid JSON for all retries
        _workflowService.EnqueueOutput("invalid json");
        _workflowService.EnqueueOutput("still invalid");

        await CreateSut().ExecuteAsync(Stream.Null, EmitSse, CancellationToken.None);

        _sseEvents.Should().Contain(e => e.Type == UploadSseEventType.NODE_FAILED);
    }

    [Fact]
    public async Task Execute_Phase2_ContinuesAfterFailure()
    {
        var nodes = MakeNodes(2);
        _parser.NextResult = new TarGzParserService.ParseResult(nodes, [], []);
        _graphWriter.NextUuids = ["uuid-1", "uuid-2", "blue-1"];

        // First node batch fails (PurgeBatchSize=1, PurgeConcurrency=1 => sequential)
        _workflowService.EnqueueOutput("bad json");
        _workflowService.EnqueueOutput("still bad");
        // Second node batch succeeds
        _workflowService.EnqueueOutput(MakeBatchNodePurgeJson(nodes[1].KgId));

        await CreateSut().ExecuteAsync(Stream.Null, EmitSse, CancellationToken.None);

        _sseEvents.Should().Contain(e => e.Type == UploadSseEventType.NODE_FAILED);
        _sseEvents.Should().Contain(e => e.Type == UploadSseEventType.NODE_PURGED);
    }

    // ─── Phase 3 ───

    [Fact]
    public async Task Execute_Phase3_StartsAfterPhase2()
    {
        var nodes = MakeNodes(2);
        var edges = MakeEdges(nodes);
        _parser.NextResult = new TarGzParserService.ParseResult(nodes, edges, []);
        _graphWriter.NextUuids = ["uuid-1", "uuid-2", "blue-1", "blue-2"];

        // Phase 2 workflows
        _workflowService.EnqueueOutput(MakeBatchNodePurgeJson(nodes[0].KgId));
        _workflowService.EnqueueOutput(MakeBatchNodePurgeJson(nodes[1].KgId));

        // Phase 3 workflow
        _workflowService.EnqueueOutput(MakeBatchEdgePurgeJson(
            nodes[0].KgId, nodes[1].KgId, "blue-1", "blue-2"));

        await CreateSut().ExecuteAsync(Stream.Null, EmitSse, CancellationToken.None);

        // All PHASE_START events: Phase 1, Phase 2, Phase 3
        var phaseStarts = _sseEvents
            .Where(e => e.Type == UploadSseEventType.PHASE_START)
            .ToList();
        phaseStarts.Should().HaveCount(3);
    }

    [Fact]
    public async Task Execute_Phase3_EmitsEdgePurgedEvent()
    {
        var nodes = MakeNodes(2);
        var edges = MakeEdges(nodes);
        _parser.NextResult = new TarGzParserService.ParseResult(nodes, edges, []);
        _graphWriter.NextUuids = ["uuid-1", "uuid-2", "blue-1", "blue-2"];

        _workflowService.EnqueueOutput(MakeBatchNodePurgeJson(nodes[0].KgId));
        _workflowService.EnqueueOutput(MakeBatchNodePurgeJson(nodes[1].KgId));
        _workflowService.EnqueueOutput(MakeBatchEdgePurgeJson(
            nodes[0].KgId, nodes[1].KgId, "blue-1", "blue-2"));

        await CreateSut().ExecuteAsync(Stream.Null, EmitSse, CancellationToken.None);

        _sseEvents.Should().Contain(e => e.Type == UploadSseEventType.EDGE_PURGED);
    }

    [Fact]
    public async Task Execute_Phase3_EmptyResultIsValid()
    {
        var nodes = MakeNodes(2);
        var edges = MakeEdges(nodes);
        _parser.NextResult = new TarGzParserService.ParseResult(nodes, edges, []);
        _graphWriter.NextUuids = ["uuid-1", "uuid-2", "blue-1", "blue-2"];

        _workflowService.EnqueueOutput(MakeBatchNodePurgeJson(nodes[0].KgId));
        _workflowService.EnqueueOutput(MakeBatchNodePurgeJson(nodes[1].KgId));
        // Empty edge purge result in batch format
        _workflowService.EnqueueOutput(JsonSerializer.Serialize(new BatchEdgePurgeResult
        {
            Results =
            [
                new BatchEdgePurgeEntry
                {
                    SourceKgId = nodes[0].KgId,
                    TargetKgId = nodes[1].KgId,
                    BlueEdges = [],
                },
            ],
        }));

        await CreateSut().ExecuteAsync(Stream.Null, EmitSse, CancellationToken.None);

        _sseEvents.Should().Contain(e => e.Type == UploadSseEventType.EDGE_PURGED);
        _sseEvents.Should().NotContain(e => e.Type == UploadSseEventType.EDGE_FAILED);
    }

    // ─── End-to-End ───

    [Fact]
    public async Task Execute_FullPipeline_EmitsUploadDone()
    {
        var nodes = MakeNodes(2);
        var edges = MakeEdges(nodes);
        _parser.NextResult = new TarGzParserService.ParseResult(nodes, edges, []);
        _graphWriter.NextUuids = ["uuid-1", "uuid-2", "blue-1", "blue-2"];

        _workflowService.EnqueueOutput(MakeBatchNodePurgeJson(nodes[0].KgId));
        _workflowService.EnqueueOutput(MakeBatchNodePurgeJson(nodes[1].KgId));
        _workflowService.EnqueueOutput(MakeBatchEdgePurgeJson(
            nodes[0].KgId, nodes[1].KgId, "blue-1", "blue-2"));

        await CreateSut().ExecuteAsync(Stream.Null, EmitSse, CancellationToken.None);

        _sseEvents.Should().Contain(e => e.Type == UploadSseEventType.UPLOAD_DONE);
    }

    [Fact]
    public async Task Execute_FullPipeline_EndsWithStreamEnd()
    {
        var nodes = MakeNodes(1);
        _parser.NextResult = new TarGzParserService.ParseResult(nodes, [], []);
        _graphWriter.NextUuids = ["uuid-1", "blue-1"];
        _workflowService.EnqueueOutput(MakeBatchNodePurgeJson(nodes[0].KgId));

        await CreateSut().ExecuteAsync(Stream.Null, EmitSse, CancellationToken.None);

        _sseEvents.Last().Type.Should().Be(UploadSseEventType.STREAM_END);
    }

    // ─── Test Doubles ───

    /// <summary>
    /// Stub parser that returns a preconfigured result instead of parsing a real tar.gz.
    /// </summary>
    private sealed class StubTarGzParserService(
        Microsoft.Extensions.Logging.ILogger<TarGzParserService> logger) : TarGzParserService(logger)
    {
        public TarGzParserService.ParseResult? NextResult { get; set; }

        public override TarGzParserService.ParseResult ParseAndValidate(Stream tarGzStream) =>
            NextResult ?? throw new InvalidOperationException("StubTarGzParserService.NextResult not set");
    }

    /// <summary>
    /// Stub graph writer that returns configurable UUID lists.
    /// </summary>
    private sealed class StubChronoGraphWriteService : ChronoGraphWriteService
    {
        private int _callIndex;

        public StubChronoGraphWriteService()
            : base(new HttpClient(), Options.Create(new ChronoGraphOptions { BaseUrl = "http://fake" }),
                   new NyxIdTokenService(
                       new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
                       NullLogger<NyxIdTokenService>.Instance),
                   NullLogger<ChronoGraphWriteService>.Instance)
        {
        }

        /// <summary>UUIDs returned sequentially across all create calls.</summary>
        public List<string> NextUuids { get; set; } = [];

        public override Task<List<string>> CreateNodesAsync(
            string graphId, List<Dictionary<string, object>> nodeProperties, CancellationToken ct)
        {
            var uuids = new List<string>();
            for (var i = 0; i < nodeProperties.Count; i++)
            {
                var idx = Interlocked.Increment(ref _callIndex) - 1;
                uuids.Add(idx < NextUuids.Count ? NextUuids[idx] : $"auto-uuid-{idx}");
            }
            return Task.FromResult(uuids);
        }

        public override Task<List<string>> CreateEdgesAsync(
            string graphId, List<Dictionary<string, object>> edgeProperties, CancellationToken ct)
        {
            var uuids = new List<string>();
            for (var i = 0; i < edgeProperties.Count; i++)
                uuids.Add($"edge-uuid-{i}");
            return Task.FromResult(uuids);
        }
    }

    /// <summary>
    /// Workflow service that returns queued LLM outputs sequentially.
    /// </summary>
    private sealed class QueuedWorkflowRunCommandService : IWorkflowRunCommandService
    {
        private readonly Queue<string> _outputs = new();

        public void EnqueueOutput(string output) => _outputs.Enqueue(output);

        public Task<WorkflowChatRunExecutionResult> ExecuteAsync(
            WorkflowChatRunRequest request,
            Func<WorkflowOutputFrame, CancellationToken, ValueTask> emitAsync,
            Func<WorkflowChatRunStarted, CancellationToken, ValueTask>? onStartedAsync = null,
            CancellationToken ct = default)
        {
            string? output;
            lock (_outputs)
            {
                output = _outputs.Count > 0 ? _outputs.Dequeue() : null;
            }

            if (output is not null)
            {
                var frame = new WorkflowOutputFrame
                {
                    Type = "TEXT_MESSAGE_CONTENT",
                    Delta = output,
                };
                emitAsync(frame, ct).AsTask().Wait(ct);
            }

            return Task.FromResult(new WorkflowChatRunExecutionResult(
                WorkflowChatRunStartError.None,
                new WorkflowChatRunStarted("actor", "wf", "cmd"),
                new WorkflowChatRunFinalizeResult(WorkflowProjectionCompletionStatus.Completed, true)));
        }
    }
}
