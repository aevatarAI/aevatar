using System.Text.Json;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sisyphus.Application.Models;
using Sisyphus.Application.Models.Graph;
using Sisyphus.Application.Models.Research;
using Sisyphus.Application.Models.Upload;
using Sisyphus.Application.Services;

namespace Sisyphus.Application.Tests;

public class ResearchLoopServiceTests
{
    private readonly StubChronoGraphReadService _readService = new();
    private readonly CapturingGraphWriteService _writeService = new();
    private readonly GraphIdProvider _graphIdProvider = new();
    private readonly QueuedWorkflowService _workflowService = new();
    private readonly IOptions<ResearchOptions> _opts = Options.Create(new ResearchOptions
    {
        LlmMaxRetries = 2,
        NodesPerRound = 3,
    });

    public ResearchLoopServiceTests()
    {
        _graphIdProvider.SetRead("read-id");
        _graphIdProvider.SetWrite("write-id");
    }

    private ResearchLoopService CreateSut() => new(
        _readService,
        _writeService,
        _graphIdProvider,
        _workflowService,
        _opts,
        NullLogger<ResearchLoopService>.Instance);

    [Fact]
    public async Task RunAsync_OneRound_EmitsExpectedEvents()
    {
        _readService.NextSnapshot = new BlueGraphSnapshot();
        _workflowService.EnqueueOutput(MakeValidNodePurgeJson());

        var events = new List<(ResearchSseEventType Type, object Payload)>();
        var cts = new CancellationTokenSource();

        var sut = CreateSut();

        // Cancel after the first round completes
        var task = sut.RunAsync(async (type, payload, ct) =>
        {
            lock (events) events.Add((type, payload));
            if (type == ResearchSseEventType.ROUND_DONE)
                cts.Cancel();
        }, cts.Token);

        await task;

        var types = events.Select(e => e.Type).ToList();
        types.Should().Contain(ResearchSseEventType.LOOP_STARTED);
        types.Should().Contain(ResearchSseEventType.ROUND_START);
        types.Should().Contain(ResearchSseEventType.GRAPH_READ);
        types.Should().Contain(ResearchSseEventType.LLM_CALL_START);
        types.Should().Contain(ResearchSseEventType.LLM_CALL_DONE);
        types.Should().Contain(ResearchSseEventType.GRAPH_WRITE_DONE);
        types.Should().Contain(ResearchSseEventType.ROUND_DONE);
        types.Should().Contain(ResearchSseEventType.LOOP_STOPPED);
    }

    [Fact]
    public async Task RunAsync_WritesBlueNodesWithSisyphusStatusPurified()
    {
        _readService.NextSnapshot = new BlueGraphSnapshot();
        _workflowService.EnqueueOutput(MakeValidNodePurgeJson());

        var cts = new CancellationTokenSource();
        var sut = CreateSut();

        await sut.RunAsync(async (type, _, ct) =>
        {
            if (type == ResearchSseEventType.ROUND_DONE)
                cts.Cancel();
        }, cts.Token);

        _writeService.CapturedNodeCalls.Should().NotBeEmpty();
        foreach (var call in _writeService.CapturedNodeCalls)
        {
            foreach (var item in call)
            {
                var props = (Dictionary<string, object>)item["properties"];
                props[SisyphusStatus.PropertyName].Should().Be(SisyphusStatus.Purified);
            }
        }
    }

    [Fact]
    public async Task RunAsync_InvalidLlmOutput_EmitsValidationFailed()
    {
        _readService.NextSnapshot = new BlueGraphSnapshot();
        // All attempts return bad JSON
        _workflowService.EnqueueOutput("not json");
        _workflowService.EnqueueOutput("still not json");

        var events = new List<(ResearchSseEventType Type, object Payload)>();
        var cts = new CancellationTokenSource();
        var sut = CreateSut();

        await sut.RunAsync(async (type, payload, ct) =>
        {
            lock (events) events.Add((type, payload));
            if (type == ResearchSseEventType.ROUND_DONE)
                cts.Cancel();
        }, cts.Token);

        events.Should().Contain(e => e.Type == ResearchSseEventType.VALIDATION_FAILED);
    }

    [Fact]
    public async Task RunAsync_AlreadyRunning_ThrowsInvalidOperation()
    {
        // Make the read service block until we release it
        var gate = new SemaphoreSlim(0, 1);
        var blockingReadService = new BlockingChronoGraphReadService(gate);
        _workflowService.EnqueueOutput(MakeValidNodePurgeJson());

        var sut = new ResearchLoopService(
            blockingReadService,
            _writeService,
            _graphIdProvider,
            _workflowService,
            _opts,
            NullLogger<ResearchLoopService>.Instance);

        var cts = new CancellationTokenSource();
        // Start the loop — it will block on GetBlueSnapshotAsync
        var runTask = sut.RunAsync(async (type, _, ct) =>
        {
            if (type == ResearchSseEventType.ROUND_DONE)
                cts.Cancel();
        }, cts.Token);

        // Give the loop time to start and hit the blocking read
        await Task.Delay(50);
        sut.IsRunning.Should().BeTrue();

        // Attempting to start again while running should throw
        var act = () => sut.RunAsync((_, _, _) => ValueTask.CompletedTask, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();

        // Release the gate so the loop can finish
        gate.Release();
        // Then cancel after one round
        await Task.Delay(50);
        cts.Cancel();

        await runTask;
    }

    [Fact]
    public async Task Stop_CancelsRunningLoop()
    {
        _readService.NextSnapshot = new BlueGraphSnapshot();
        _workflowService.EnqueueOutput(MakeValidNodePurgeJson());
        // Provide enough outputs for multiple rounds
        _workflowService.EnqueueOutput(MakeValidNodePurgeJson());
        _workflowService.EnqueueOutput(MakeValidNodePurgeJson());

        var events = new List<ResearchSseEventType>();
        var sut = CreateSut();

        var task = sut.RunAsync(async (type, _, ct) =>
        {
            lock (events) events.Add(type);
            // Stop after first GRAPH_WRITE_DONE
            if (type == ResearchSseEventType.GRAPH_WRITE_DONE)
                sut.Stop();
        }, CancellationToken.None);

        await task;

        sut.IsRunning.Should().BeFalse();
        events.Should().Contain(ResearchSseEventType.LOOP_STOPPED);
    }

    [Fact]
    public async Task RunAsync_ResearchEdgesHaveSisyphusStatusPurified()
    {
        _readService.NextSnapshot = new BlueGraphSnapshot();
        _workflowService.EnqueueOutput(MakeNodePurgeJsonWithEdges());

        var cts = new CancellationTokenSource();
        var sut = CreateSut();

        await sut.RunAsync(async (type, _, ct) =>
        {
            if (type == ResearchSseEventType.ROUND_DONE)
                cts.Cancel();
        }, cts.Token);

        _writeService.CapturedEdgeCalls.Should().NotBeEmpty();
        foreach (var call in _writeService.CapturedEdgeCalls)
        {
            foreach (var item in call)
            {
                var props = (Dictionary<string, object>)item["properties"];
                props[SisyphusStatus.PropertyName].Should().Be(SisyphusStatus.Purified);
            }
        }
    }

    private static string MakeValidNodePurgeJson() =>
        JsonSerializer.Serialize(new NodePurgeResult
        {
            BlueNodes =
            [
                new() { TempId = "b0", Type = "theorem", Abstract = "A theorem", Body = "Body" },
            ],
            BlueEdges = [],
        });

    private static string MakeNodePurgeJsonWithEdges() =>
        JsonSerializer.Serialize(new NodePurgeResult
        {
            BlueNodes =
            [
                new() { TempId = "b0", Type = "theorem", Abstract = "A theorem", Body = "Body" },
                new() { TempId = "b1", Type = "proof", Abstract = "A proof", Body = "Proof body" },
            ],
            BlueEdges =
            [
                new() { Source = "b1", Target = "b0", EdgeType = "proves" },
            ],
        });

    // ─── Test Doubles ───

    private sealed class BlockingChronoGraphReadService : ChronoGraphReadService
    {
        private readonly SemaphoreSlim _gate;
        private bool _firstCall = true;

        public BlockingChronoGraphReadService(SemaphoreSlim gate)
            : base(new HttpClient(),
                   Options.Create(new ChronoGraphOptions { BaseUrl = "http://fake" }),
                   new GraphIdProvider(),
                   new NyxIdTokenService(
                       new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
                       NullLogger<NyxIdTokenService>.Instance),
                   NullLogger<ChronoGraphReadService>.Instance)
        {
            _gate = gate;
        }

        public override async Task<BlueGraphSnapshot> GetBlueSnapshotAsync(CancellationToken ct = default)
        {
            if (_firstCall)
            {
                _firstCall = false;
                await _gate.WaitAsync(ct);
            }
            return new BlueGraphSnapshot();
        }
    }

    private sealed class StubChronoGraphReadService : ChronoGraphReadService
    {
        public BlueGraphSnapshot NextSnapshot { get; set; } = new();

        public StubChronoGraphReadService()
            : base(new HttpClient(),
                   Options.Create(new ChronoGraphOptions { BaseUrl = "http://fake" }),
                   new GraphIdProvider(),
                   new NyxIdTokenService(
                       new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
                       NullLogger<NyxIdTokenService>.Instance),
                   NullLogger<ChronoGraphReadService>.Instance)
        {
        }

        public override Task<BlueGraphSnapshot> GetBlueSnapshotAsync(CancellationToken ct = default)
            => Task.FromResult(NextSnapshot);
    }

    private sealed class CapturingGraphWriteService : ChronoGraphWriteService
    {
        private int _callIndex;

        public CapturingGraphWriteService()
            : base(new HttpClient(),
                   Options.Create(new ChronoGraphOptions { BaseUrl = "http://fake" }),
                   new NyxIdTokenService(
                       new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
                       NullLogger<NyxIdTokenService>.Instance),
                   NullLogger<ChronoGraphWriteService>.Instance)
        {
        }

        public List<List<Dictionary<string, object>>> CapturedNodeCalls { get; } = [];
        public List<List<Dictionary<string, object>>> CapturedEdgeCalls { get; } = [];

        public override Task<List<string>> CreateNodesAsync(
            string graphId, List<Dictionary<string, object>> nodeProperties, CancellationToken ct)
        {
            lock (CapturedNodeCalls)
                CapturedNodeCalls.Add(nodeProperties);
            var uuids = new List<string>();
            for (var i = 0; i < nodeProperties.Count; i++)
                uuids.Add($"uuid-{Interlocked.Increment(ref _callIndex)}");
            return Task.FromResult(uuids);
        }

        public override Task<List<string>> CreateEdgesAsync(
            string graphId, List<Dictionary<string, object>> edgeProperties, CancellationToken ct)
        {
            lock (CapturedEdgeCalls)
                CapturedEdgeCalls.Add(edgeProperties);
            return Task.FromResult(edgeProperties.Select((_, i) => $"edge-{i}").ToList());
        }
    }

    private sealed class QueuedWorkflowService : IWorkflowRunCommandService
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
