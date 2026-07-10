using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.Workflow.Core.Tests.Modules;

/// <summary>
/// Refactor (iter11/cluster-021): Old tests allowed per-drain head removal to satisfy FIFO assertions.
/// Refactor (iter11/cluster-021): New tests require cursor-based FIFO drain and source regression coverage.
/// </summary>
public class BackpressureHelperTests
{
    [Fact]
    public void ResolveMaxConcurrent_WithValidParameter_ReturnsValue()
    {
        var parameters = new Dictionary<string, string> { ["max_concurrent_workers"] = "5" };
        BackpressureHelper.ResolveMaxConcurrent(parameters).Should().Be(5);
    }

    [Fact]
    public void ResolveMaxConcurrent_WithNoParameter_ReturnsFallback()
    {
        var parameters = new Dictionary<string, string>();
        BackpressureHelper.ResolveMaxConcurrent(parameters, 10).Should().Be(10);
    }

    [Fact]
    public void ResolveMaxConcurrent_WithNullParameters_ReturnsFallback()
    {
        BackpressureHelper.ResolveMaxConcurrent(null, 15).Should().Be(15);
    }

    [Fact]
    public void ResolveMaxConcurrent_WithZero_ReturnsFallback()
    {
        var parameters = new Dictionary<string, string> { ["max_concurrent_workers"] = "0" };
        BackpressureHelper.ResolveMaxConcurrent(parameters, 10).Should().Be(10);
    }

    [Fact]
    public void ResolveMaxConcurrent_AllowsExplicitValueAboveDefault()
    {
        var parameters = new Dictionary<string, string> { ["max_concurrent_workers"] = "100" };
        BackpressureHelper.ResolveMaxConcurrent(parameters, 20).Should().Be(100);
    }

    [Fact]
    public void ResolveMaxConcurrent_ClampedToHardLimit()
    {
        var parameters = new Dictionary<string, string> { ["max_concurrent_workers"] = "999" };
        BackpressureHelper.ResolveMaxConcurrent(parameters).Should().Be(BackpressureHelper.MaxConcurrentWorkersHardLimit);
    }

    [Fact]
    public void ResolveMinConcurrent_WithValidParameter_ReturnsValue()
    {
        var parameters = new Dictionary<string, string> { ["min_concurrent_workers"] = "4" };
        BackpressureHelper.ResolveMinConcurrent(parameters, 10).Should().Be(4);
    }

    [Fact]
    public void ResolveMinConcurrent_ClampedToMaxConcurrent()
    {
        var parameters = new Dictionary<string, string> { ["min_concurrent_workers"] = "40" };
        BackpressureHelper.ResolveMinConcurrent(parameters, 6).Should().Be(6);
    }

    [Fact]
    public void TryAdmit_UnderLimit_ShouldAdmit()
    {
        var bp = BackpressureHelper.Initialize(3);
        var entry = MakeEntry("s1");

        BackpressureHelper.TryAdmit(bp, entry).Should().BeTrue();
        bp.ActiveWorkers.Should().Be(1);
        bp.Queue.Should().BeEmpty();
    }

    [Fact]
    public void TryAdmit_WithMinConcurrentLowerThanMax_ShouldQueueAfterFloor()
    {
        var bp = BackpressureHelper.Initialize(5, 2);

        BackpressureHelper.TryAdmit(bp, MakeEntry("s1")).Should().BeTrue();
        BackpressureHelper.TryAdmit(bp, MakeEntry("s2")).Should().BeTrue();
        BackpressureHelper.TryAdmit(bp, MakeEntry("s3")).Should().BeFalse();

        bp.ActiveWorkers.Should().Be(2);
        BackpressureHelper.QueuedCount(bp).Should().Be(1);
        bp.Queue[0].StepId.Should().Be("s3");
    }

    [Fact]
    public void TryAdmit_AtLimit_ShouldQueue()
    {
        var bp = BackpressureHelper.Initialize(2);
        BackpressureHelper.TryAdmit(bp, MakeEntry("s1")).Should().BeTrue();
        BackpressureHelper.TryAdmit(bp, MakeEntry("s2")).Should().BeTrue();

        BackpressureHelper.TryAdmit(bp, MakeEntry("s3")).Should().BeFalse();
        bp.ActiveWorkers.Should().Be(2);
        BackpressureHelper.QueuedCount(bp).Should().Be(1);
        bp.Queue[0].StepId.Should().Be("s3");
    }

    [Fact]
    public void TryDrainOne_WithQueuedWork_ShouldDequeue()
    {
        var bp = BackpressureHelper.Initialize(1);
        BackpressureHelper.TryAdmit(bp, MakeEntry("s1"));
        BackpressureHelper.TryAdmit(bp, MakeEntry("s2"));
        bp.Queue.Should().HaveCount(1);

        var next = BackpressureHelper.TryDrainOne(bp);

        next.Should().NotBeNull();
        next!.StepId.Should().Be("s2");
        bp.ActiveWorkers.Should().Be(1);
        BackpressureHelper.QueuedCount(bp).Should().Be(0);
    }

    [Fact]
    public void TryDrainOne_EmptyQueue_ShouldDecrementAndReturnNull()
    {
        var bp = BackpressureHelper.Initialize(3);
        BackpressureHelper.TryAdmit(bp, MakeEntry("s1"));
        bp.ActiveWorkers.Should().Be(1);

        var next = BackpressureHelper.TryDrainOne(bp);

        next.Should().BeNull();
        bp.ActiveWorkers.Should().Be(0);
    }

    [Fact]
    public void TryDrainOne_FIFOOrder()
    {
        var bp = BackpressureHelper.Initialize(1);
        BackpressureHelper.TryAdmit(bp, MakeEntry("s1"));
        BackpressureHelper.TryAdmit(bp, MakeEntry("s2"));
        BackpressureHelper.TryAdmit(bp, MakeEntry("s3"));
        bp.Queue.Should().HaveCount(2);

        var first = BackpressureHelper.TryDrainOne(bp);
        first!.StepId.Should().Be("s2");

        var second = BackpressureHelper.TryDrainOne(bp);
        second!.StepId.Should().Be("s3");

        var third = BackpressureHelper.TryDrainOne(bp);
        third.Should().BeNull();
    }

    [Fact]
    public void CompleteAndTopUp_ShouldRefillToConfiguredFloor()
    {
        var bp = BackpressureHelper.Initialize(6, 3);
        BackpressureHelper.TryAdmit(bp, MakeEntry("s1"));
        BackpressureHelper.TryAdmit(bp, MakeEntry("s2"));
        BackpressureHelper.TryAdmit(bp, MakeEntry("s3"));
        BackpressureHelper.TryAdmit(bp, MakeEntry("s4"));
        BackpressureHelper.TryAdmit(bp, MakeEntry("s5"));

        var topUps = BackpressureHelper.CompleteAndTopUp(bp);

        topUps.Select(entry => entry.StepId).Should().Equal("s4");
        bp.ActiveWorkers.Should().Be(3);
        BackpressureHelper.QueuedCount(bp).Should().Be(1);
    }

    [Fact]
    public void TryDrainOne_OldStateWithoutHeadIndex_ShouldStartAtQueueHead()
    {
        var oldState = new BackpressureQueueState
        {
            ActiveWorkers = 1,
            MaxConcurrentWorkers = 1,
        };
        oldState.Queue.Add(MakeEntry("old-s1"));
        oldState.Queue.Add(MakeEntry("old-s2"));
        var oldStateBytes = oldState.ToByteArray();

        var bp = BackpressureQueueState.Parser.ParseFrom(oldStateBytes);
        bp.HeadIndex.Should().Be(0);

        var next = BackpressureHelper.TryDrainOne(bp);

        next.Should().NotBeNull();
        next!.StepId.Should().Be("old-s1");
        BackpressureHelper.QueuedCount(bp).Should().Be(1);
    }

    [Fact]
    public void TryDrainOne_ShouldAdvanceHeadIndexWithoutPerDrainHeadRemoval()
    {
        var bp = BackpressureHelper.Initialize(1);
        BackpressureHelper.TryAdmit(bp, MakeEntry("active"));
        for (var i = 0; i < 1_000; i++)
            BackpressureHelper.TryAdmit(bp, MakeEntry($"queued-{i}"));

        var queueCountBeforeDrain = bp.Queue.Count;
        var first = BackpressureHelper.TryDrainOne(bp);

        first!.StepId.Should().Be("queued-0");
        bp.HeadIndex.Should().Be(1);
        bp.Queue.Count.Should().Be(queueCountBeforeDrain);
        BackpressureHelper.QueuedCount(bp).Should().Be(999);
    }

    [Fact]
    public void TryDrainOne_ShouldCompactConsumedPrefixAndPreserveFIFO()
    {
        var bp = BackpressureHelper.Initialize(1);
        BackpressureHelper.TryAdmit(bp, MakeEntry("active"));
        for (var i = 0; i < 4; i++)
            BackpressureHelper.TryAdmit(bp, MakeEntry($"queued-{i}"));

        BackpressureHelper.TryDrainOne(bp)!.StepId.Should().Be("queued-0");
        bp.HeadIndex.Should().Be(1);
        bp.Queue.Count.Should().Be(4);

        BackpressureHelper.TryDrainOne(bp)!.StepId.Should().Be("queued-1");
        bp.HeadIndex.Should().Be(2);
        bp.Queue.Count.Should().Be(4);

        BackpressureHelper.TryDrainOne(bp)!.StepId.Should().Be("queued-2");

        bp.HeadIndex.Should().Be(0);
        bp.Queue.Select(entry => entry.StepId).Should().Equal("queued-3");
        BackpressureHelper.QueuedCount(bp).Should().Be(1);

        BackpressureHelper.TryDrainOne(bp)!.StepId.Should().Be("queued-3");
        BackpressureHelper.TryDrainOne(bp).Should().BeNull();
    }

    [Fact]
    public void BackpressureHelperSource_ShouldNotRemoveQueueHeadInDrainPath()
    {
        var sourcePath = FindBackpressureHelperSource();
        var source = File.ReadAllText(sourcePath);

        source.Should().NotContain("RemoveAt(0)");
        source.Should().NotContain("bp.Queue[0]");
    }

    [Fact]
    public void ToStepRequest_ShouldConvertCorrectly()
    {
        var entry = new BackpressureQueueEntry
        {
            StepId = "s1",
            StepType = "llm_call",
            RunId = "r1",
            Input = "hello",
            TargetRole = "worker",
            Parameters = { ["key"] = "val" },
        };

        var request = BackpressureHelper.ToStepRequest(entry);

        request.StepId.Should().Be("s1");
        request.StepType.Should().Be("llm_call");
        request.RunId.Should().Be("r1");
        request.Input.Should().Be("hello");
        request.TargetRole.Should().Be("worker");
        request.Parameters.Should().ContainKey("key").WhoseValue.Should().Be("val");
    }

    [Fact]
    public void ToStepRequest_ShouldRoundtripQueuedInputFileRefs()
    {
        var entry = new BackpressureQueueEntry
        {
            StepId = "s-file",
            StepType = "tool_call",
            RunId = "run-file",
            Input = "payload",
            TargetRole = "worker",
        };
        entry.InputFileRefs.Add(BuildWorkflowFileRef("file-a"));
        entry.InputFileRefs.Add(BuildWorkflowFileRef("file-b"));

        var request = BackpressureHelper.ToStepRequest(entry);

        request.InputFileRefs.Select(fileRef => fileRef.FileId).Should().Equal("file-a", "file-b");
    }

    [Fact]
    public void ToQueueEntry_ShouldCaptureInputFileRefsForQueuedDispatch()
    {
        var fileRefs = new[]
        {
            BuildWorkflowFileRef("file-a"),
            BuildWorkflowFileRef("file-b"),
        };

        var entry = BackpressureHelper.ToQueueEntry(
            "s-file",
            "tool_call",
            "run-file",
            "payload",
            "worker",
            new Dictionary<string, string> { ["tool"] = "extract" },
            fileRefs);

        entry.InputFileRefs.Select(fileRef => fileRef.FileId).Should().Equal("file-a", "file-b");
        entry.Parameters.Should().ContainKey("tool").WhoseValue.Should().Be("extract");
    }

    private static BackpressureQueueEntry MakeEntry(string stepId) =>
        new() { StepId = stepId, StepType = "llm_call", RunId = "r1", Input = "test" };

    private static WorkflowFileRef BuildWorkflowFileRef(string fileId) =>
        new()
        {
            FileId = fileId,
            ArtifactId = $"workflow-file://{fileId}",
            SourceKind = WorkflowFileSourceKind.FormUpload,
            FileName = $"{fileId}.txt",
            MediaType = "text/plain",
            SizeBytes = 12,
            OwnerRunId = "run-file",
            OwnerScopeId = "scope-file",
        };

    private static string FindBackpressureHelperSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src",
                "workflow",
                "Aevatar.Workflow.Core",
                "Modules",
                "BackpressureHelper.cs");
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Unable to locate BackpressureHelper.cs from test base directory.");
    }
}
