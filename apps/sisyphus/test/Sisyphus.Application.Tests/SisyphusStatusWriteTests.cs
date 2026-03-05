using System.Text.Json;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sisyphus.Application.Models;
using Sisyphus.Application.Models.Upload;
using Sisyphus.Application.Services;

namespace Sisyphus.Application.Tests;

public class SisyphusStatusWriteTests
{
    private readonly CapturingChronoGraphWriteService _graphWriter = new();
    private readonly QueuedWorkflowRunCommandService _workflowService = new();
    private readonly GraphIdProvider _graphIdProvider = new();
    private readonly IOptions<UploadOptions> _uploadOptions = Options.Create(new UploadOptions
    {
        ApiBatchSize = 50,
        PurgeBatchSize = 1,
        PurgeMaxRetries = 1,
        PurgeConcurrency = 1,
    });
    private readonly List<(UploadSseEventType Type, object Payload)> _sseEvents = [];

    public SisyphusStatusWriteTests()
    {
        _graphIdProvider.SetRead("read-id");
        _graphIdProvider.SetWrite("write-id");
    }

    private UploadPipelineService CreateSut() => new(
        new StubTarGzParserService(NullLogger<TarGzParserService>.Instance),
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

    [Fact]
    public void RedNodes_HaveSisyphusStatusRaw()
    {
        var node = new RedNode
        {
            KgId = "KG-001", Label = "lbl", AtomType = "tp-note", TexContent = "tex",
        };
        var item = InvokeBuildRedNodeItem(node);

        var props = (Dictionary<string, object>)item["properties"];
        props[SisyphusStatus.PropertyName].Should().Be(SisyphusStatus.Raw);
    }

    [Fact]
    public void RedEdges_HaveSisyphusStatusRaw()
    {
        var edge = new RedEdge
        {
            SourceKgId = "KG-001", TargetKgId = "KG-002",
            EdgeType = "inference_ref", SourceUuid = "uuid-1", TargetUuid = "uuid-2",
        };
        var item = InvokeBuildRedEdgeItem(edge);

        var props = (Dictionary<string, object>)item["properties"];
        props[SisyphusStatus.PropertyName].Should().Be(SisyphusStatus.Raw);
    }

    [Fact]
    public async Task FullPipeline_BlueNodesHaveSisyphusStatusPurified()
    {
        var nodes = new List<RedNode>
        {
            new() { KgId = "KG-001", Label = "lbl", AtomType = "tp-note", TexContent = "tex" },
        };

        var sut = new UploadPipelineService(
            new StubTarGzParserService(NullLogger<TarGzParserService>.Instance)
                { NextResult = new TarGzParserService.ParseResult(nodes, [], []) },
            _graphWriter,
            _workflowService,
            _graphIdProvider,
            _uploadOptions,
            NullLogger<UploadPipelineService>.Instance);

        _graphWriter.NextUuids = ["uuid-1", "blue-1"];
        _workflowService.EnqueueOutput(JsonSerializer.Serialize(new BatchNodePurgeResult
        {
            Results =
            [
                new BatchNodePurgeEntry
                {
                    KgId = "KG-001",
                    BlueNodes = [new() { TempId = "b0", Type = "theorem", Abstract = "A", Body = "B" }],
                    BlueEdges = [],
                },
            ],
        }));

        await sut.ExecuteAsync(Stream.Null, EmitSse, CancellationToken.None);

        // Verify blue node writes have sisyphus_status = purified
        var blueNodeWrites = _graphWriter.CapturedNodeCalls
            .Where(call => call.Any(item =>
                item.TryGetValue("type", out var t) && t.ToString() == "theorem"))
            .SelectMany(call => call)
            .ToList();

        blueNodeWrites.Should().NotBeEmpty();
        foreach (var item in blueNodeWrites)
        {
            var props = (Dictionary<string, object>)item["properties"];
            props[SisyphusStatus.PropertyName].Should().Be(SisyphusStatus.Purified);
        }
    }

    [Fact]
    public async Task FullPipeline_PurifiedFromEdgesHaveSisyphusStatusPurified()
    {
        var nodes = new List<RedNode>
        {
            new() { KgId = "KG-001", Label = "lbl", AtomType = "tp-note", TexContent = "tex" },
        };

        var sut = new UploadPipelineService(
            new StubTarGzParserService(NullLogger<TarGzParserService>.Instance)
                { NextResult = new TarGzParserService.ParseResult(nodes, [], []) },
            _graphWriter,
            _workflowService,
            _graphIdProvider,
            _uploadOptions,
            NullLogger<UploadPipelineService>.Instance);

        _graphWriter.NextUuids = ["uuid-1", "blue-1"];
        _workflowService.EnqueueOutput(JsonSerializer.Serialize(new BatchNodePurgeResult
        {
            Results =
            [
                new BatchNodePurgeEntry
                {
                    KgId = "KG-001",
                    BlueNodes = [new() { TempId = "b0", Type = "theorem", Abstract = "A", Body = "B" }],
                    BlueEdges = [],
                },
            ],
        }));

        await sut.ExecuteAsync(Stream.Null, EmitSse, CancellationToken.None);

        // Find PURIFIED_FROM edge writes
        var purifiedFromEdges = _graphWriter.CapturedEdgeCalls
            .SelectMany(call => call)
            .Where(item => item.TryGetValue("type", out var t) && t.ToString() == "PURIFIED_FROM")
            .ToList();

        purifiedFromEdges.Should().NotBeEmpty();
        foreach (var item in purifiedFromEdges)
        {
            var props = (Dictionary<string, object>)item["properties"];
            props[SisyphusStatus.PropertyName].Should().Be(SisyphusStatus.Purified);
        }
    }

    [Fact]
    public void SisyphusStatus_Constants_AreCorrect()
    {
        SisyphusStatus.Raw.Should().Be("raw");
        SisyphusStatus.Purified.Should().Be("purified");
        SisyphusStatus.PropertyName.Should().Be("sisyphus_status");
    }

    // Use reflection to call internal static methods
    private static Dictionary<string, object> InvokeBuildRedNodeItem(RedNode node)
    {
        var method = typeof(UploadPipelineService).GetMethod("BuildRedNodeItem",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (Dictionary<string, object>)method.Invoke(null, [node])!;
    }

    private static Dictionary<string, object> InvokeBuildRedEdgeItem(RedEdge edge)
    {
        var method = typeof(UploadPipelineService).GetMethod("BuildRedEdgeItem",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (Dictionary<string, object>)method.Invoke(null, [edge])!;
    }

    // ─── Test Doubles ───

    private sealed class StubTarGzParserService(
        Microsoft.Extensions.Logging.ILogger<TarGzParserService> logger) : TarGzParserService(logger)
    {
        public TarGzParserService.ParseResult? NextResult { get; set; }

        public override TarGzParserService.ParseResult ParseAndValidate(Stream tarGzStream) =>
            NextResult ?? throw new InvalidOperationException("StubTarGzParserService.NextResult not set");
    }

    private sealed class CapturingChronoGraphWriteService : ChronoGraphWriteService
    {
        private int _callIndex;

        public CapturingChronoGraphWriteService()
            : base(new HttpClient(), Options.Create(new ChronoGraphOptions { BaseUrl = "http://fake" }),
                   new NyxIdTokenService(
                       new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
                       NullLogger<NyxIdTokenService>.Instance),
                   NullLogger<ChronoGraphWriteService>.Instance)
        {
        }

        public List<string> NextUuids { get; set; } = [];
        public List<List<Dictionary<string, object>>> CapturedNodeCalls { get; } = [];
        public List<List<Dictionary<string, object>>> CapturedEdgeCalls { get; } = [];

        public override Task<List<string>> CreateNodesAsync(
            string graphId, List<Dictionary<string, object>> nodeProperties, CancellationToken ct)
        {
            lock (CapturedNodeCalls)
                CapturedNodeCalls.Add(nodeProperties);
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
            lock (CapturedEdgeCalls)
                CapturedEdgeCalls.Add(edgeProperties);
            var uuids = new List<string>();
            for (var i = 0; i < edgeProperties.Count; i++)
                uuids.Add($"edge-uuid-{i}");
            return Task.FromResult(uuids);
        }
    }

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
