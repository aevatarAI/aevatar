using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Core.Tests.Modules;

public sealed class BackpressureModuleTopUpTests
{
    [Fact]
    public async Task ParallelFanOutModule_ShouldHonorMinConcurrentWorkersAndTopUp()
    {
        var module = new ParallelFanOutModule();
        var context = new RecordingWorkflowContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "fanout",
                StepType = "parallel",
                RunId = "run-parallel-floor",
                Input = "payload",
                TargetRole = "worker",
                Parameters =
                {
                    ["parallel_count"] = "4",
                    ["min_concurrent_workers"] = "2",
                    ["max_concurrent_workers"] = "4",
                },
            }),
            context,
            CancellationToken.None);

        context.Published.Select(x => x.Event).OfType<StepRequestEvent>().Select(x => x.StepId)
            .Should().Equal("fanout_sub_0", "fanout_sub_1");
        context.Published.Select(x => x.Event).OfType<BackpressureAppliedEvent>().Should().ContainSingle()
            .Which.QueuedCount.Should().Be(1);

        context.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = "fanout_sub_0",
                RunId = "run-parallel-floor",
                Success = true,
                Output = "done-0",
            }),
            context,
            CancellationToken.None);

        context.Published.Select(x => x.Event).OfType<StepRequestEvent>().Select(x => x.StepId)
            .Should().Equal("fanout_sub_2");
    }

    [Fact]
    public async Task ForEachModule_ShouldHonorMinConcurrentWorkersAndTopUp()
    {
        var module = new ForEachModule();
        var context = new RecordingWorkflowContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "foreach-floor",
                StepType = "foreach",
                RunId = "run-foreach-floor",
                Input = "alpha\n---\nbeta\n---\ngamma\n---\ndelta",
                Parameters =
                {
                    ["sub_step_type"] = "transform",
                    ["min_concurrent_workers"] = "2",
                    ["max_concurrent_workers"] = "4",
                },
            }),
            context,
            CancellationToken.None);

        context.Published.Select(x => x.Event).OfType<StepRequestEvent>().Select(x => x.StepId)
            .Should().Equal("foreach-floor_item_0", "foreach-floor_item_1");

        context.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = "foreach-floor_item_0",
                RunId = "run-foreach-floor",
                Success = true,
                Output = "A",
            }),
            context,
            CancellationToken.None);

        context.Published.Select(x => x.Event).OfType<StepRequestEvent>().Select(x => x.StepId)
            .Should().Equal("foreach-floor_item_2");
    }

    [Fact]
    public async Task ForEachModule_ShouldDispatchOneChildPerInputFileRefInOrder()
    {
        var module = new ForEachModule();
        var context = new RecordingWorkflowContext();
        var first = BuildWorkflowFileRef("file-a");
        var second = BuildWorkflowFileRef("file-b");

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "foreach-files",
                StepType = "foreach",
                RunId = "run-foreach-files",
                Input = "ignored textual input",
                Parameters =
                {
                    ["items_source"] = "input_file_refs",
                    ["sub_step_type"] = "tool_call",
                    ["max_concurrent_workers"] = "4",
                },
                InputFileRefs = { first, second },
            }),
            context,
            CancellationToken.None);

        var requests = context.Published.Select(x => x.Event).OfType<StepRequestEvent>().ToArray();
        requests.Select(request => request.StepId).Should().Equal("foreach-files_item_0", "foreach-files_item_1");
        requests.Select(request => request.InputFileRefs.Should().ContainSingle().Subject.FileId)
            .Should().Equal("file-a", "file-b");
        requests.Select(request => request.Input).Should().Equal("workflow-file://file-a", "workflow-file://file-b");
    }

    [Fact]
    public async Task ForEachModule_ShouldPreserveQueuedInputFileRefWhenBackpressureTopsUp()
    {
        var module = new ForEachModule();
        var context = new RecordingWorkflowContext();
        var first = BuildWorkflowFileRef("file-a");
        var second = BuildWorkflowFileRef("file-b");

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "foreach-files-floor",
                StepType = "foreach",
                RunId = "run-foreach-files-floor",
                Parameters =
                {
                    ["items_source"] = "input_file_refs",
                    ["sub_step_type"] = "tool_call",
                    ["min_concurrent_workers"] = "1",
                    ["max_concurrent_workers"] = "2",
                },
                InputFileRefs = { first, second },
            }),
            context,
            CancellationToken.None);

        context.Published.Select(x => x.Event).OfType<StepRequestEvent>().Should().ContainSingle()
            .Which.InputFileRefs.Should().ContainSingle().Which.FileId.Should().Be("file-a");

        context.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = "foreach-files-floor_item_0",
                RunId = "run-foreach-files-floor",
                Success = true,
                Output = "first done",
            }),
            context,
            CancellationToken.None);

        var topUp = context.Published.Select(x => x.Event).OfType<StepRequestEvent>().Should().ContainSingle().Subject;
        topUp.StepId.Should().Be("foreach-files-floor_item_1");
        topUp.InputFileRefs.Should().ContainSingle().Which.FileId.Should().Be("file-b");
        topUp.Input.Should().Be("workflow-file://file-b");
    }

    [Fact]
    public async Task ForEachModule_ShouldPublishTypedFileResultsWithPerFileErrors()
    {
        var module = new ForEachModule();
        var context = new RecordingWorkflowContext();
        var first = BuildWorkflowFileRef("file-a");
        var second = BuildWorkflowFileRef("file-b");

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "foreach-files-result",
                StepType = "foreach",
                RunId = "run-foreach-files-result",
                Parameters =
                {
                    ["items_source"] = "input_file_refs",
                    ["sub_step_type"] = "tool_call",
                },
                InputFileRefs = { first, second },
            }),
            context,
            CancellationToken.None);

        context.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = "foreach-files-result_item_1",
                RunId = "run-foreach-files-result",
                Success = false,
                Error = "extract failed",
            }),
            context,
            CancellationToken.None);

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = "foreach-files-result_item_0",
                RunId = "run-foreach-files-result",
                Success = true,
                Output = "descriptor output",
            }),
            context,
            CancellationToken.None);

        var completed = context.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Should().ContainSingle().Subject;
        completed.StepId.Should().Be("foreach-files-result");
        completed.Success.Should().BeFalse();
        completed.Error.Should().Be("one or more foreach items failed");
        completed.Output.Should().NotContain("data_base64");
        completed.Output.Should().NotContain("base64");
        completed.Output.Should().Be("descriptor output\n---\n");
        completed.FileItemResults.Should().NotBeNull();
        completed.FileItemResults.Results.Should().HaveCount(2);
        completed.FileItemResults.Results[0].Index.Should().Be(0);
        completed.FileItemResults.Results[0].Success.Should().BeTrue();
        completed.FileItemResults.Results[0].FileRef.FileId.Should().Be("file-a");
        completed.FileItemResults.Results[0].FileRef.ArtifactId.Should().Be("workflow-file://file-a");
        completed.FileItemResults.Results[0].Output.Should().Be("descriptor output");
        completed.FileItemResults.Results[1].Index.Should().Be(1);
        completed.FileItemResults.Results[1].Success.Should().BeFalse();
        completed.FileItemResults.Results[1].FileRef.FileId.Should().Be("file-b");
        completed.FileItemResults.Results[1].Error.Should().Be("extract failed");

        var parsed = StepCompletedEvent.Parser.ParseFrom(completed.ToByteArray());
        parsed.FileItemResults.Results[1].FileRef.FileId.Should().Be("file-b");
    }

    [Fact]
    public async Task ForEachModule_ShouldCheckpointAllDispatchIntentsBeforePublishingChildren()
    {
        var module = new ForEachModule();
        var context = new RecordingWorkflowContext();
        var checkpointObserved = false;
        EventEnvelopePublishOptions? firstChildOptions = null;

        context.BeforePublish = (evt, options) =>
        {
            if (evt is not StepRequestEvent { StepId: "foreach-checkpoint_item_0" })
                return;

            var state = context.LoadState<ForEachModuleState>("foreach");
            var parent = state.Parents["run-checkpoint:foreach-checkpoint"];
            parent.PendingDispatches.Should().HaveCount(2);
            state.Backpressure.ActiveWorkers.Should().Be(2);
            checkpointObserved = true;
            firstChildOptions = options;
        };

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "foreach-checkpoint",
                StepType = "foreach",
                RunId = "run-checkpoint",
                Input = "alpha\n---\nbeta",
                Parameters = { ["sub_step_type"] = "transform" },
            }),
            context,
            CancellationToken.None);

        checkpointObserved.Should().BeTrue();
        var requests = context.Published.Select(x => x.Event).OfType<StepRequestEvent>().ToArray();
        requests.Should().HaveCount(2);
        requests[0].ExecutionId.Should().Be("foreach-child-execution:run-checkpoint:foreach-checkpoint:0");
        requests[0].IdempotencyKey.Should().Be("foreach-child:run-checkpoint:foreach-checkpoint:0");
        firstChildOptions.Should().NotBeNull();
        firstChildOptions!.Delivery!.OperationId.Should().Be(requests[0].IdempotencyKey);
    }

    [Fact]
    public async Task ForEachModule_TwentyConcurrentChildren_ShouldCheckpointDispatchLedgerAsBatch()
    {
        var module = new ForEachModule();
        var context = new RecordingWorkflowContext();
        var items = string.Join("\n---\n", Enumerable.Range(0, 20).Select(index => $"item-{index}"));

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "foreach-batch",
                StepType = "foreach",
                RunId = "run-batch",
                Input = items,
                Parameters =
                {
                    ["sub_step_type"] = "transform",
                    ["max_concurrent_workers"] = "20",
                },
            }),
            context,
            CancellationToken.None);

        var requests = context.Published.Select(x => x.Event).OfType<StepRequestEvent>().ToArray();
        requests.Should().HaveCount(20);
        requests.Select(request => request.StepId)
            .Should().Equal(Enumerable.Range(0, 20).Select(index => $"foreach-batch_item_{index}"));
        context.SaveAttempts.Should().Be(2,
            "the durable intent fence and one batch acknowledgement are sufficient for 20 children");

        var state = context.LoadState<ForEachModuleState>("foreach");
        var parent = state.Parents["run-batch:foreach-batch"];
        parent.PendingDispatches.Should().BeEmpty();
        parent.DispatchedStepIds.Should().HaveCount(20);
        state.Backpressure.ActiveWorkers.Should().Be(20);
    }

    [Fact]
    public async Task ForEachModule_TwentyConcurrentChildren_ShouldOverlapPublicationsAfterIntentFence()
    {
        var module = new ForEachModule();
        var context = new RecordingWorkflowContext();
        var allPublicationsStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePublications = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedCount = 0;
        context.BeforePublishAsync = async (evt, _, ct) =>
        {
            if (evt is not StepRequestEvent { StepId: var stepId } ||
                !stepId.StartsWith("foreach-overlap_item_", StringComparison.Ordinal))
            {
                return;
            }

            if (Interlocked.Increment(ref startedCount) == 20)
                allPublicationsStarted.TrySetResult(true);
            await releasePublications.Task.WaitAsync(ct);
        };

        var handling = module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "foreach-overlap",
                StepType = "foreach",
                RunId = "run-overlap",
                Input = string.Join("\n---\n", Enumerable.Range(0, 20).Select(index => $"item-{index}")),
                Parameters =
                {
                    ["sub_step_type"] = "transform",
                    ["max_concurrent_workers"] = "20",
                },
            }),
            context,
            CancellationToken.None);

        try
        {
            await allPublicationsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Volatile.Read(ref startedCount).Should().Be(20);
            context.SaveAttempts.Should().Be(1, "all child intents must be durable before publication begins");
        }
        finally
        {
            releasePublications.TrySetResult(true);
            await handling;
        }

        context.Published.Select(x => x.Event).OfType<StepRequestEvent>().Should().HaveCount(20);
        context.SaveAttempts.Should().Be(2);
    }

    [Fact]
    public async Task ForEachModule_FortyChildren_ShouldCapConcurrentPublicationsAtTwenty()
    {
        var module = new ForEachModule();
        var context = new RecordingWorkflowContext();
        var firstBatchStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePublications = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedCount = 0;
        var activeCount = 0;
        var maxActiveCount = 0;
        context.BeforePublishAsync = async (evt, _, ct) =>
        {
            if (evt is not StepRequestEvent { StepId: var stepId } ||
                !stepId.StartsWith("foreach-cap_item_", StringComparison.Ordinal))
            {
                return;
            }

            var active = Interlocked.Increment(ref activeCount);
            var observedMax = Volatile.Read(ref maxActiveCount);
            while (observedMax < active)
            {
                var prior = Interlocked.CompareExchange(ref maxActiveCount, active, observedMax);
                if (prior == observedMax)
                    break;
                observedMax = prior;
            }
            if (Interlocked.Increment(ref startedCount) == 20)
                firstBatchStarted.TrySetResult(true);

            try
            {
                await releasePublications.Task.WaitAsync(ct);
            }
            finally
            {
                Interlocked.Decrement(ref activeCount);
            }
        };

        var handling = module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "foreach-cap",
                StepType = "foreach",
                RunId = "run-cap",
                Input = string.Join("\n---\n", Enumerable.Range(0, 40).Select(index => $"item-{index}")),
                Parameters =
                {
                    ["sub_step_type"] = "transform",
                    ["max_concurrent_workers"] = "40",
                },
            }),
            context,
            CancellationToken.None);

        try
        {
            await firstBatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Volatile.Read(ref startedCount).Should().Be(20);
            Volatile.Read(ref maxActiveCount).Should().Be(20);
        }
        finally
        {
            releasePublications.TrySetResult(true);
            await handling;
        }

        context.Published.Select(x => x.Event).OfType<StepRequestEvent>().Should().HaveCount(40);
        Volatile.Read(ref maxActiveCount).Should().Be(20);
    }

    [Fact]
    public async Task ForEachModule_PartiallyCancelledConcurrentPublication_ShouldReplayEntireDurableBatch()
    {
        var module = new ForEachModule();
        var context = new RecordingWorkflowContext();
        var allPublicationsStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdPublications = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedCount = 0;
        context.BeforePublishAsync = async (evt, _, ct) =>
        {
            if (evt is not StepRequestEvent { StepId: var stepId } ||
                !stepId.StartsWith("foreach-cancel_item_", StringComparison.Ordinal))
            {
                return;
            }

            if (Interlocked.Increment(ref startedCount) == 20)
                allPublicationsStarted.TrySetResult(true);
            var itemIndex = int.Parse(
                stepId[(stepId.LastIndexOf('_') + 1)..],
                System.Globalization.CultureInfo.InvariantCulture);
            if (itemIndex < 5)
                return;
            await holdPublications.Task.WaitAsync(ct);
        };
        using var cts = new CancellationTokenSource();
        var parentRequest = new StepRequestEvent
        {
            StepId = "foreach-cancel",
            StepType = "foreach",
            RunId = "run-cancel",
            Input = string.Join("\n---\n", Enumerable.Range(0, 20).Select(index => $"item-{index}")),
            Parameters =
            {
                ["sub_step_type"] = "transform",
                ["max_concurrent_workers"] = "20",
            },
        };
        var handling = module.HandleAsync(
            Envelope(parentRequest),
            context,
            cts.Token);

        try
        {
            await allPublicationsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            cts.Cancel();
        }

        Func<Task> cancellation = () => handling;
        await cancellation.Should().ThrowAsync<OperationCanceledException>();
        context.SaveAttempts.Should().Be(1);
        var escaped = context.Published.Select(x => x.Event).OfType<StepRequestEvent>().ToArray();
        escaped.Select(request => request.StepId).Should().Equal(Enumerable.Range(0, 5)
            .Select(index => $"foreach-cancel_item_{index}"));
        var durableParent = context.LoadState<ForEachModuleState>("foreach")
            .Parents["run-cancel:foreach-cancel"];
        durableParent.PendingDispatches.Should().HaveCount(20);
        durableParent.DispatchedStepIds.Should().BeEmpty();

        context.BeforePublishAsync = null;
        context.Published.Clear();
        await module.HandleAsync(Envelope(parentRequest), context, CancellationToken.None);

        var replayed = context.Published.Select(x => x.Event).OfType<StepRequestEvent>().ToArray();
        replayed.Should().HaveCount(20);
        replayed.Select(request => request.ExecutionId).Should().Equal(Enumerable.Range(0, 20)
            .Select(index => $"foreach-child-execution:run-cancel:foreach-cancel:{index}"));
        replayed.Select(request => request.IdempotencyKey).Should().Equal(Enumerable.Range(0, 20)
            .Select(index => $"foreach-child:run-cancel:foreach-cancel:{index}"));
        replayed.Take(5).Select(request => request.ExecutionId)
            .Should().Equal(escaped.Select(request => request.ExecutionId));
        replayed.Take(5).Select(request => request.IdempotencyKey)
            .Should().Equal(escaped.Select(request => request.IdempotencyKey));
        context.LoadState<ForEachModuleState>("foreach")
            .Parents["run-cancel:foreach-cancel"].PendingDispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task ForEachModule_DuplicateParentRequest_ShouldPreserveProgressAndNotRedispatchBatch()
    {
        var module = new ForEachModule();
        var context = new RecordingWorkflowContext();
        var parentRequest = new StepRequestEvent
        {
            StepId = "foreach-replay",
            StepType = "foreach",
            RunId = "run-replay",
            ExecutionId = "parent-exec-a",
            IdempotencyKey = "parent-idem",
            Input = "alpha\n---\nbeta",
            Parameters = { ["sub_step_type"] = "transform" },
        };

        await module.HandleAsync(Envelope(parentRequest), context, CancellationToken.None);
        var firstChild = context.Published.Select(x => x.Event).OfType<StepRequestEvent>().First();
        context.Published.Clear();
        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = firstChild.StepId,
                RunId = "run-replay",
                ExecutionId = firstChild.ExecutionId,
                Success = true,
                Output = "A",
            }),
            context,
            CancellationToken.None);

        context.Published.Clear();
        await module.HandleAsync(Envelope(parentRequest), context, CancellationToken.None);

        context.Published.Select(x => x.Event).OfType<StepRequestEvent>().Should().BeEmpty();
        var state = context.LoadState<ForEachModuleState>("foreach");
        state.Parents["run-replay:foreach-replay:execution:parent-exec-a"].CollectedStepIds
            .Should().Equal(firstChild.StepId);
    }

    [Fact]
    public async Task ForEachModule_ParentRedeliveryWithExecutionId_ShouldAdoptLegacyStateWithoutRefanout()
    {
        var module = new ForEachModule();
        var context = new RecordingWorkflowContext();
        var legacyState = new ForEachModuleState
        {
            Backpressure = BackpressureHelper.Initialize(1),
        };
        legacyState.Backpressure.ActiveWorkers = 1;
        legacyState.Backpressure.Queue.Add(BackpressureHelper.ToQueueEntry(
            "foreach-upgrade_item_2",
            "transform",
            "run-upgrade",
            "gamma",
            string.Empty,
            null));
        legacyState.Parents["run-upgrade:foreach-upgrade"] = new ForEachParentState
        {
            Expected = 3,
            Collected =
            {
                new ForEachItemResult { Index = 0, Success = true, Output = "A" },
            },
            CollectedStepIds = { "foreach-upgrade_item_0" },
            DispatchedStepIds = { "foreach-upgrade_item_0", "foreach-upgrade_item_1" },
        };
        await context.SaveStateAsync("foreach", legacyState);

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "foreach-upgrade",
                StepType = "foreach",
                RunId = "run-upgrade",
                ExecutionId = "parent-exec-upgrade",
                IdempotencyKey = "parent-idem-upgrade",
                Input = "replacement input must not be parsed",
                Parameters = { ["sub_step_type"] = "notify" },
            }),
            context,
            CancellationToken.None);

        context.Published.Select(x => x.Event).OfType<StepRequestEvent>().Should().BeEmpty();
        var recovered = context.LoadState<ForEachModuleState>("foreach");
        recovered.Parents.Should().ContainSingle();
        recovered.Parents.Should().NotContainKey("run-upgrade:foreach-upgrade");
        var parent = recovered.Parents[
            "run-upgrade:foreach-upgrade:execution:parent-exec-upgrade"];
        parent.Expected.Should().Be(3);
        parent.Collected.Should().ContainSingle().Which.Output.Should().Be("A");
        parent.CollectedStepIds.Should().Equal("foreach-upgrade_item_0");
        parent.DispatchedStepIds.Should().Equal("foreach-upgrade_item_0", "foreach-upgrade_item_1");
        parent.SettledWorkerStepIds.Should().Equal("foreach-upgrade_item_0");
        parent.ParentRunId.Should().Be("run-upgrade");
        parent.ParentStepId.Should().Be("foreach-upgrade");
        parent.ParentExecutionId.Should().Be("parent-exec-upgrade");
        parent.ParentIdempotencyKey.Should().Be("parent-idem-upgrade");
        recovered.Backpressure.ActiveWorkers.Should().Be(1);
        BackpressureHelper.QueuedCount(recovered.Backpressure).Should().Be(1);
        recovered.Backpressure.Queue[recovered.Backpressure.HeadIndex].StepId
            .Should().Be("foreach-upgrade_item_2");
        recovered.Backpressure.Queue[recovered.Backpressure.HeadIndex].Input.Should().Be("gamma");
    }

    [Fact]
    public async Task ForEachModule_FailedInitialChildPublish_ShouldRecoverFromDurableRetry()
    {
        var module = new ForEachModule();
        var context = new RecordingWorkflowContext
        {
            FailPublishOnce = evt => evt is StepRequestEvent { StepId: "foreach-recover_item_0" },
        };

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "foreach-recover",
                StepType = "foreach",
                RunId = "run-recover",
                Input = "alpha",
                Parameters = { ["sub_step_type"] = "transform" },
            }),
            context,
            CancellationToken.None);

        context.Published.Select(x => x.Event).OfType<StepRequestEvent>().Should().BeEmpty();
        context.LoadState<ForEachModuleState>("foreach")
            .Parents["run-recover:foreach-recover"].PendingDispatches.Should().ContainSingle();
        var retry = context.Scheduled.Should().ContainSingle().Subject.Event
            .Should().BeOfType<ForEachPublicationRetryFiredEvent>().Subject;

        await module.HandleAsync(Envelope(retry), context, CancellationToken.None);

        var recovered = context.Published.Select(x => x.Event).OfType<StepRequestEvent>()
            .Should().ContainSingle().Subject;
        recovered.ExecutionId.Should().Be("foreach-child-execution:run-recover:foreach-recover:0");
        recovered.IdempotencyKey.Should().Be("foreach-child:run-recover:foreach-recover:0");
        context.LoadState<ForEachModuleState>("foreach")
            .Parents["run-recover:foreach-recover"].PendingDispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task ForEachModule_LegacyPendingChild_ShouldFenceStableIdentityBeforePublishing()
    {
        var module = new ForEachModule();
        var context = new RecordingWorkflowContext();
        var parentKey = "run-legacy-pending:foreach-legacy-pending";
        var state = new ForEachModuleState
        {
            Backpressure = BackpressureHelper.Initialize(1),
        };
        state.Backpressure.ActiveWorkers = 1;
        state.Parents[parentKey] = new ForEachParentState
        {
            Expected = 1,
            ParentRunId = "run-legacy-pending",
            ParentStepId = "foreach-legacy-pending",
            PendingDispatches =
            {
                BackpressureHelper.ToQueueEntry(
                    "foreach-legacy-pending_item_0",
                    "transform",
                    "run-legacy-pending",
                    "alpha",
                    string.Empty,
                    null),
            },
        };
        await context.SaveStateAsync("foreach", state);
        var identityWasDurableBeforePublish = false;
        context.BeforePublish = (evt, options) =>
        {
            if (evt is not StepRequestEvent request)
                return;

            var durableEntry = context.LoadState<ForEachModuleState>("foreach")
                .Parents[parentKey].PendingDispatches.Single();
            durableEntry.ExecutionId.Should().Be(request.ExecutionId);
            durableEntry.IdempotencyKey.Should().Be(request.IdempotencyKey);
            options!.Delivery!.OperationId.Should().Be(request.IdempotencyKey);
            identityWasDurableBeforePublish = true;
        };

        await module.HandleAsync(
            Envelope(new ForEachPublicationRetryFiredEvent { ParentKey = parentKey }),
            context,
            CancellationToken.None);

        identityWasDurableBeforePublish.Should().BeTrue();
        context.SaveAttempts.Should().Be(3, "the legacy identity fence precedes the acknowledgement checkpoint");
        var published = context.Published.Select(x => x.Event).OfType<StepRequestEvent>()
            .Should().ContainSingle().Subject;
        published.ExecutionId.Should()
            .Be("foreach-child-execution:run-legacy-pending:foreach-legacy-pending:0");
        published.IdempotencyKey.Should()
            .Be("foreach-child:run-legacy-pending:foreach-legacy-pending:0");
    }

    [Fact]
    public async Task ForEachModule_PartialBatchPublishFailure_ShouldCheckpointSuccessesAndRecoverOnlyFailedChild()
    {
        var module = new ForEachModule();
        var context = new RecordingWorkflowContext
        {
            FailPublishOnce = evt => evt is StepRequestEvent { StepId: "foreach-batch-recover_item_7" },
        };
        var items = string.Join("\n---\n", Enumerable.Range(0, 20).Select(index => $"item-{index}"));

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "foreach-batch-recover",
                StepType = "foreach",
                RunId = "run-batch-recover",
                Input = items,
                Parameters =
                {
                    ["sub_step_type"] = "transform",
                    ["max_concurrent_workers"] = "20",
                },
            }),
            context,
            CancellationToken.None);

        context.Published.Select(x => x.Event).OfType<StepRequestEvent>().Select(request => request.StepId)
            .Should().Equal(Enumerable.Range(0, 20)
                .Where(index => index != 7)
                .Select(index => $"foreach-batch-recover_item_{index}"));
        context.SaveAttempts.Should().Be(2);
        var checkpoint = context.LoadState<ForEachModuleState>("foreach")
            .Parents["run-batch-recover:foreach-batch-recover"];
        checkpoint.DispatchedStepIds.Should().HaveCount(19);
        checkpoint.PendingDispatches.Select(entry => entry.StepId)
            .Should().Equal("foreach-batch-recover_item_7");

        var retry = context.Scheduled.Should().ContainSingle().Subject.Event
            .Should().BeOfType<ForEachPublicationRetryFiredEvent>().Subject;
        context.Published.Clear();
        await module.HandleAsync(Envelope(retry), context, CancellationToken.None);

        context.Published.Select(x => x.Event).OfType<StepRequestEvent>().Select(request => request.StepId)
            .Should().Equal("foreach-batch-recover_item_7");
        context.SaveAttempts.Should().Be(3);
        var recovered = context.LoadState<ForEachModuleState>("foreach")
            .Parents["run-batch-recover:foreach-batch-recover"];
        recovered.PendingDispatches.Should().BeEmpty();
        recovered.DispatchedStepIds.Should().HaveCount(20);
    }

    [Fact]
    public async Task ForEachModule_PostDispatchPublishSaveFailure_ShouldPropagateAndKeepDurableIntent()
    {
        var module = new ForEachModule();
        var context = new RecordingWorkflowContext
        {
            FailSaveAttempt = 2,
        };

        var act = () => module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "foreach-dispatch-save-failure",
                StepType = "foreach",
                RunId = "run-dispatch-save-failure",
                Input = "alpha",
                Parameters = { ["sub_step_type"] = "transform" },
            }),
            context,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("simulated save failure");

        context.Published.Select(x => x.Event).OfType<StepRequestEvent>()
            .Should().ContainSingle().Which.StepId.Should().Be("foreach-dispatch-save-failure_item_0");
        context.Scheduled.Should().BeEmpty();
        var durableParent = context.LoadState<ForEachModuleState>("foreach")
            .Parents["run-dispatch-save-failure:foreach-dispatch-save-failure"];
        durableParent.PendingDispatches.Should().ContainSingle()
            .Which.StepId.Should().Be("foreach-dispatch-save-failure_item_0");
        durableParent.DispatchedStepIds.Should().BeEmpty();
    }

    [Fact]
    public async Task ForEachModule_BatchAcknowledgementSaveFailure_ShouldReplayStableChildIdentities()
    {
        var module = new ForEachModule();
        var context = new RecordingWorkflowContext
        {
            FailSaveAttempt = 2,
        };
        var parentRequest = new StepRequestEvent
        {
            StepId = "foreach-batch-save-failure",
            StepType = "foreach",
            RunId = "run-batch-save-failure",
            ExecutionId = "parent-execution",
            IdempotencyKey = "parent-idempotency",
            Input = string.Join("\n---\n", Enumerable.Range(0, 20).Select(index => $"item-{index}")),
            Parameters =
            {
                ["sub_step_type"] = "transform",
                ["max_concurrent_workers"] = "20",
            },
        };

        var firstAttempt = () => module.HandleAsync(
            Envelope(parentRequest),
            context,
            CancellationToken.None);
        await firstAttempt.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("simulated save failure");

        var firstBatch = context.Published.Select(x => x.Event).OfType<StepRequestEvent>().ToArray();
        firstBatch.Should().HaveCount(20);
        var durableParent = context.LoadState<ForEachModuleState>("foreach")
            .Parents["run-batch-save-failure:foreach-batch-save-failure:execution:parent-execution"];
        durableParent.PendingDispatches.Should().HaveCount(20);
        durableParent.DispatchedStepIds.Should().BeEmpty();

        context.Published.Clear();
        await module.HandleAsync(Envelope(parentRequest), context, CancellationToken.None);

        var replayedBatch = context.Published.Select(x => x.Event).OfType<StepRequestEvent>().ToArray();
        replayedBatch.Should().HaveCount(20);
        replayedBatch.Select(request => request.StepId).Should().Equal(firstBatch.Select(request => request.StepId));
        replayedBatch.Select(request => request.ExecutionId)
            .Should().Equal(firstBatch.Select(request => request.ExecutionId));
        replayedBatch.Select(request => request.IdempotencyKey)
            .Should().Equal(firstBatch.Select(request => request.IdempotencyKey));
        context.SaveAttempts.Should().Be(4);
        context.LoadState<ForEachModuleState>("foreach")
            .Parents["run-batch-save-failure:foreach-batch-save-failure:execution:parent-execution"]
            .PendingDispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task ForEachModule_DuplicateChildCompletion_ShouldNotSettleWorkerOrTopUpTwice()
    {
        var module = new ForEachModule();
        var context = new RecordingWorkflowContext();
        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "foreach-settlement",
                StepType = "foreach",
                RunId = "run-settlement",
                Input = "alpha\n---\nbeta\n---\ngamma",
                Parameters =
                {
                    ["sub_step_type"] = "transform",
                    ["max_concurrent_workers"] = "1",
                },
            }),
            context,
            CancellationToken.None);

        context.Published.Clear();
        var firstCompletion = new StepCompletedEvent
        {
            StepId = "foreach-settlement_item_0",
            RunId = "run-settlement",
            Success = true,
            Output = "A",
        };
        await module.HandleAsync(Envelope(firstCompletion), context, CancellationToken.None);
        context.Published.Select(x => x.Event).OfType<StepRequestEvent>().Select(x => x.StepId)
            .Should().Equal("foreach-settlement_item_1");

        context.Published.Clear();
        await module.HandleAsync(Envelope(firstCompletion), context, CancellationToken.None);

        context.Published.Select(x => x.Event).OfType<StepRequestEvent>().Should().BeEmpty();
        var state = context.LoadState<ForEachModuleState>("foreach");
        state.Backpressure.ActiveWorkers.Should().Be(1);
        BackpressureHelper.QueuedCount(state.Backpressure).Should().Be(1);
        state.Parents["run-settlement:foreach-settlement"].SettledWorkerStepIds
            .Should().Equal("foreach-settlement_item_0");
    }

    [Fact]
    public async Task ForEachModule_LegacyCollectedCompletion_ShouldNotSettleWorkerAgain()
    {
        var module = new ForEachModule();
        var context = new RecordingWorkflowContext();
        var legacyState = new ForEachModuleState
        {
            Backpressure = BackpressureHelper.Initialize(1),
        };
        legacyState.Backpressure.ActiveWorkers = 1;
        legacyState.Parents["run-legacy:foreach-legacy"] = new ForEachParentState
        {
            Expected = 2,
            CollectedStepIds = { "foreach-legacy_item_0" },
            Collected =
            {
                new ForEachItemResult { Index = 0, Success = true, Output = "A" },
            },
        };
        await context.SaveStateAsync("foreach", legacyState);

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = "foreach-legacy_item_0",
                RunId = "run-legacy",
                Success = true,
                Output = "A",
            }),
            context,
            CancellationToken.None);

        var recovered = context.LoadState<ForEachModuleState>("foreach");
        recovered.Backpressure.ActiveWorkers.Should().Be(1);
        recovered.Parents["run-legacy:foreach-legacy"].Collected.Should().ContainSingle();
        recovered.Parents["run-legacy:foreach-legacy"].SettledWorkerStepIds
            .Should().Equal("foreach-legacy_item_0");
    }

    [Fact]
    public async Task ForEachModule_FailedTopUpPublish_ShouldRecoverFromDuplicateCompletion()
    {
        var module = new ForEachModule();
        var context = new RecordingWorkflowContext();
        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "foreach-topup-recover",
                StepType = "foreach",
                RunId = "run-topup-recover",
                Input = "alpha\n---\nbeta",
                Parameters =
                {
                    ["sub_step_type"] = "transform",
                    ["max_concurrent_workers"] = "1",
                },
            }),
            context,
            CancellationToken.None);

        context.Published.Clear();
        context.FailPublishOnce = evt => evt is StepRequestEvent { StepId: "foreach-topup-recover_item_1" };
        var firstCompletion = new StepCompletedEvent
        {
            StepId = "foreach-topup-recover_item_0",
            RunId = "run-topup-recover",
            Success = true,
            Output = "A",
        };
        await module.HandleAsync(Envelope(firstCompletion), context, CancellationToken.None);

        context.LoadState<ForEachModuleState>("foreach")
            .Parents["run-topup-recover:foreach-topup-recover"].PendingDispatches
            .Should().ContainSingle().Which.StepId.Should().Be("foreach-topup-recover_item_1");

        await module.HandleAsync(Envelope(firstCompletion), context, CancellationToken.None);

        context.Published.Select(x => x.Event).OfType<StepRequestEvent>().Select(x => x.StepId)
            .Should().Equal("foreach-topup-recover_item_1");
        context.LoadState<ForEachModuleState>("foreach").Backpressure.ActiveWorkers.Should().Be(1);
    }

    [Fact]
    public async Task ForEachModule_FailedTerminalPublish_ShouldRetryThenKeepTombstone()
    {
        var module = new ForEachModule();
        var context = new RecordingWorkflowContext();
        var parentRequest = new StepRequestEvent
        {
            StepId = "foreach-terminal-recover",
            StepType = "foreach",
            RunId = "run-terminal-recover",
            Input = "alpha",
            Parameters = { ["sub_step_type"] = "transform" },
        };
        await module.HandleAsync(Envelope(parentRequest), context, CancellationToken.None);

        context.Published.Clear();
        context.FailPublishOnce = evt => evt is StepCompletedEvent { StepId: "foreach-terminal-recover" };
        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = "foreach-terminal-recover_item_0",
                RunId = "run-terminal-recover",
                Success = true,
                Output = "A",
            }),
            context,
            CancellationToken.None);

        var pending = context.LoadState<ForEachModuleState>("foreach");
        pending.Parents["run-terminal-recover:foreach-terminal-recover"].PendingCompletion.Should().NotBeNull();
        pending.CompletionTombstones.Should().BeEmpty();
        var retry = context.Scheduled.Last().Event.Should().BeOfType<ForEachPublicationRetryFiredEvent>().Subject;

        await module.HandleAsync(Envelope(retry), context, CancellationToken.None);

        context.Published.Select(x => x.Event).OfType<StepCompletedEvent>()
            .Should().ContainSingle().Which.Output.Should().Be("A");
        var completed = context.LoadState<ForEachModuleState>("foreach");
        completed.Parents.Should().BeEmpty();
        completed.CompletionTombstones.Should().ContainKey("run-terminal-recover:foreach-terminal-recover");

        context.Published.Clear();
        await module.HandleAsync(Envelope(parentRequest), context, CancellationToken.None);
        context.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task ForEachModule_PostTerminalPublishSaveFailure_ShouldPropagateAndKeepDurableOutbox()
    {
        var module = new ForEachModule();
        var context = new RecordingWorkflowContext();
        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "foreach-terminal-save-failure",
                StepType = "foreach",
                RunId = "run-terminal-save-failure",
                Input = "alpha",
                Parameters = { ["sub_step_type"] = "transform" },
            }),
            context,
            CancellationToken.None);

        context.Published.Clear();
        context.FailSaveAttempt = context.SaveAttempts + 2;
        var act = () => module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = "foreach-terminal-save-failure_item_0",
                RunId = "run-terminal-save-failure",
                Success = true,
                Output = "A",
            }),
            context,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("simulated save failure");

        context.Published.Select(x => x.Event).OfType<StepCompletedEvent>()
            .Should().ContainSingle().Which.StepId.Should().Be("foreach-terminal-save-failure");
        context.Scheduled.Should().BeEmpty();
        var durableState = context.LoadState<ForEachModuleState>("foreach");
        durableState.CompletionTombstones.Should().BeEmpty();
        durableState.Parents["run-terminal-save-failure:foreach-terminal-save-failure"]
            .PendingCompletion.Should().NotBeNull();
    }

    [Fact]
    public async Task ForEachModule_NewParentExecutionAfterTombstone_ShouldRunAgain()
    {
        var module = new ForEachModule();
        var context = new RecordingWorkflowContext();
        var firstParent = new StepRequestEvent
        {
            StepId = "foreach-attempt",
            StepType = "foreach",
            RunId = "run-attempt",
            ExecutionId = "parent-exec-1",
            IdempotencyKey = "parent-idem",
            Input = "alpha",
            Parameters = { ["sub_step_type"] = "transform" },
        };

        await module.HandleAsync(Envelope(firstParent), context, CancellationToken.None);
        var firstChild = context.Published.Select(x => x.Event).OfType<StepRequestEvent>().Single();
        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = firstChild.StepId,
                RunId = firstChild.RunId,
                ExecutionId = firstChild.ExecutionId,
                Success = true,
                Output = "first",
            }),
            context,
            CancellationToken.None);

        var firstTerminal = context.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single();
        firstTerminal.ExecutionId.Should().Be("parent-exec-1");

        context.Published.Clear();
        var secondParent = firstParent.Clone();
        secondParent.ExecutionId = "parent-exec-2";
        await module.HandleAsync(Envelope(secondParent), context, CancellationToken.None);

        var secondChild = context.Published.Select(x => x.Event).OfType<StepRequestEvent>()
            .Should().ContainSingle().Subject;
        secondChild.StepId.Should().NotBe(firstChild.StepId);
        secondChild.StepId.Should().StartWith("foreach-attempt_execution_");
        secondChild.ExecutionId.Should().Be("foreach-child-execution:run-attempt:foreach-attempt:parent-exec-2:0");
        secondChild.IdempotencyKey.Should().Be("foreach-child:run-attempt:foreach-attempt:parent-idem:parent-exec-2:0");
        secondChild.ExecutionId.Should().NotBe(firstChild.ExecutionId);
        var state = context.LoadState<ForEachModuleState>("foreach");
        state.CompletionTombstones.Should().ContainKey("run-attempt:foreach-attempt:execution:parent-exec-1");
        state.Parents.Should().ContainKey("run-attempt:foreach-attempt:execution:parent-exec-2");
    }

    [Fact]
    public async Task ForEachModule_SequentialParentAfterTombstone_ShouldUseNewBackpressureConfiguration()
    {
        var module = new ForEachModule();
        var context = new RecordingWorkflowContext();
        var firstParent = new StepRequestEvent
        {
            StepId = "foreach-sequential",
            StepType = "foreach",
            RunId = "run-sequential",
            ExecutionId = "parent-exec-1",
            Input = "alpha\n---\nbeta\n---\ngamma\n---\ndelta",
            Parameters =
            {
                ["sub_step_type"] = "transform",
                ["max_concurrent_workers"] = "4",
            },
        };

        await module.HandleAsync(Envelope(firstParent), context, CancellationToken.None);
        var firstChildren = context.Published.Select(x => x.Event).OfType<StepRequestEvent>().ToArray();
        firstChildren.Should().HaveCount(4);

        foreach (var child in firstChildren)
        {
            await module.HandleAsync(
                Envelope(new StepCompletedEvent
                {
                    StepId = child.StepId,
                    RunId = child.RunId,
                    ExecutionId = child.ExecutionId,
                    Success = true,
                    Output = child.Input,
                }),
                context,
                CancellationToken.None);
        }

        context.Published.Clear();
        var secondParent = firstParent.Clone();
        secondParent.ExecutionId = "parent-exec-2";
        secondParent.Input = "epsilon\n---\nzeta";
        secondParent.Parameters["max_concurrent_workers"] = "1";

        await module.HandleAsync(Envelope(secondParent), context, CancellationToken.None);

        context.Published.Select(x => x.Event).OfType<StepRequestEvent>().Should().ContainSingle();
        var state = context.LoadState<ForEachModuleState>("foreach");
        state.Backpressure.MaxConcurrentWorkers.Should().Be(1);
        state.Backpressure.ActiveWorkers.Should().Be(1);
        BackpressureHelper.QueuedCount(state.Backpressure).Should().Be(1);
    }

    [Fact]
    public async Task ForEachModule_OldParentRequestAfterMoreThan256Attempts_ShouldRemainSuppressed()
    {
        var module = new ForEachModule();
        var context = new RecordingWorkflowContext();
        var parentRequest = new StepRequestEvent
        {
            StepId = "foreach-many-attempts",
            StepType = "foreach",
            RunId = "run-many-attempts",
        };

        for (var attempt = 0; attempt < 257; attempt++)
        {
            parentRequest.ExecutionId = $"parent-exec-{attempt}";
            await module.HandleAsync(Envelope(parentRequest), context, CancellationToken.None);
        }

        context.Published.Clear();
        parentRequest.ExecutionId = "parent-exec-0";
        await module.HandleAsync(Envelope(parentRequest), context, CancellationToken.None);

        context.Published.Should().BeEmpty();
        var state = context.LoadState<ForEachModuleState>("foreach");
        state.Parents.Should().BeEmpty();
        state.CompletionTombstones.Should().HaveCount(257);
        state.CompletionTombstones.Should().ContainKey(
            "run-many-attempts:foreach-many-attempts:execution:parent-exec-0");
    }

    [Fact]
    public async Task ForEachModule_StaleChildFromPreviousExecution_ShouldNotPolluteNewAttempt()
    {
        var module = new ForEachModule();
        var context = new RecordingWorkflowContext();
        var firstParent = new StepRequestEvent
        {
            StepId = "foreach-stale-attempt",
            StepType = "foreach",
            RunId = "run-stale-attempt",
            ExecutionId = "parent-exec-old",
            Input = "alpha",
            Parameters = { ["sub_step_type"] = "transform" },
        };
        await module.HandleAsync(Envelope(firstParent), context, CancellationToken.None);
        var oldChild = context.Published.Select(x => x.Event).OfType<StepRequestEvent>().Single();
        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = oldChild.StepId,
                RunId = oldChild.RunId,
                Success = true,
                Output = "old",
            }),
            context,
            CancellationToken.None);

        context.Published.Clear();
        var newParent = firstParent.Clone();
        newParent.ExecutionId = "parent-exec-new";
        await module.HandleAsync(Envelope(newParent), context, CancellationToken.None);
        var newChild = context.Published.Select(x => x.Event).OfType<StepRequestEvent>().Single();
        newChild.StepId.Should().NotBe(oldChild.StepId);
        context.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = oldChild.StepId,
                RunId = oldChild.RunId,
                Success = true,
                Output = "stale",
            }),
            context,
            CancellationToken.None);

        context.Published.OfType<(IMessage Event, TopologyAudience Direction)>()
            .Select(x => x.Event).OfType<StepCompletedEvent>().Should().BeEmpty();
        var state = context.LoadState<ForEachModuleState>("foreach");
        var newAttempt = state.Parents["run-stale-attempt:foreach-stale-attempt:execution:parent-exec-new"];
        newAttempt.Collected.Should().BeEmpty();
        newAttempt.CollectedStepIds.Should().BeEmpty();
    }

    [Fact]
    public async Task MapReduceModule_ShouldHonorMinConcurrentWorkersAndTopUp()
    {
        var module = new MapReduceModule();
        var context = new RecordingWorkflowContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "map-floor",
                StepType = "map_reduce",
                RunId = "run-map-floor",
                Input = "alpha\n---\nbeta\n---\ngamma\n---\ndelta",
                Parameters =
                {
                    ["map_step_type"] = "transform",
                    ["min_concurrent_workers"] = "2",
                    ["max_concurrent_workers"] = "4",
                },
            }),
            context,
            CancellationToken.None);

        context.Published.Select(x => x.Event).OfType<StepRequestEvent>().Select(x => x.StepId)
            .Should().Equal("map-floor_map_0", "map-floor_map_1");

        context.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = "map-floor_map_0",
                RunId = "run-map-floor",
                Success = true,
                Output = "A",
            }),
            context,
            CancellationToken.None);

        context.Published.Select(x => x.Event).OfType<StepRequestEvent>().Select(x => x.StepId)
            .Should().Equal("map-floor_map_2");
    }

    [Fact]
    public async Task WaitSignalModule_ShouldAllowExtendedLongTimeoutWindow()
    {
        var module = new WaitSignalModule();
        var context = new RecordingWorkflowContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "wait-long",
                StepType = "wait_signal",
                RunId = "run-long-wait",
                Input = "fallback",
                Parameters =
                {
                    ["signal_name"] = "codex_worker_done",
                    ["timeout_ms"] = "86400000",
                },
            }),
            context,
            CancellationToken.None);

        var waiting = context.Published.Select(x => x.Event).OfType<WaitingForSignalEvent>().Single();
        waiting.TimeoutMs.Should().Be(86_400_000);

        var scheduled = context.Scheduled.Should().ContainSingle().Subject;
        scheduled.DueTime.Should().Be(TimeSpan.FromHours(24));
        scheduled.Event.Should().BeOfType<WaitSignalTimeoutFiredEvent>().Which.TimeoutMs.Should().Be(86_400_000);
    }

    private static EventEnvelope Envelope(IMessage evt) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
        };

    private static WorkflowFileRef BuildWorkflowFileRef(string fileId) =>
        new()
        {
            FileId = fileId,
            ArtifactId = $"workflow-file://{fileId}",
            SourceKind = WorkflowFileSourceKind.FormUpload,
            FileName = $"{fileId}.txt",
            MediaType = "text/plain",
            SizeBytes = 12,
            Sha256 = $"sha-{fileId}",
            CreatedAtUnixMs = 1710000000000,
            ExpiresAtUnixMs = 1710003600000,
            OwnerRunId = "run-owner",
            OwnerScopeId = "scope-owner",
        };

    private sealed class RecordingWorkflowContext : IWorkflowExecutionContext
    {
        private readonly Dictionary<string, Any> _states = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _callbackGenerations = new(StringComparer.Ordinal);
        private readonly object _publicationGate = new();

        public EventEnvelope InboundEnvelope { get; } = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        public string AgentId => "workflow-agent";

        public string RunId => "workflow-run";

        public IServiceProvider Services { get; } = new EmptyServiceProvider();

        public ILogger Logger { get; } = NullLogger.Instance;

        public List<(IMessage Event, TopologyAudience Direction)> Published { get; } = [];

        public List<ScheduledCallback> Scheduled { get; } = [];

        public Func<IMessage, bool>? FailPublishOnce { get; set; }

        public int? FailSaveAttempt { get; set; }

        public int SaveAttempts { get; private set; }

        public Action<IMessage, EventEnvelopePublishOptions?>? BeforePublish { get; set; }

        public Func<IMessage, EventEnvelopePublishOptions?, CancellationToken, Task>? BeforePublishAsync { get; set; }

        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;

        public long GetTimestamp() => 1;

        public TimeSpan GetElapsedTime(long startingTimestamp)
        {
            _ = startingTimestamp;
            return TimeSpan.Zero;
        }

        public TState LoadState<TState>(string scopeKey)
            where TState : class, IMessage<TState>, new()
        {
            if (!_states.TryGetValue(scopeKey, out var packed) || !packed.Is(new TState().Descriptor))
                return new TState();

            return packed.Unpack<TState>() ?? new TState();
        }

        public IReadOnlyList<KeyValuePair<string, TState>> LoadStates<TState>(string scopeKeyPrefix = "")
            where TState : class, IMessage<TState>, new() =>
            _states
                .Where(x => string.IsNullOrEmpty(scopeKeyPrefix) || x.Key.StartsWith(scopeKeyPrefix, StringComparison.Ordinal))
                .Where(x => x.Value.Is(new TState().Descriptor))
                .Select(x => new KeyValuePair<string, TState>(x.Key, x.Value.Unpack<TState>() ?? new TState()))
                .ToList();

        public Task SaveStateAsync<TState>(string scopeKey, TState state, CancellationToken ct = default)
            where TState : class, IMessage<TState>
        {
            ct.ThrowIfCancellationRequested();
            SaveAttempts++;
            if (SaveAttempts == FailSaveAttempt)
                throw new InvalidOperationException("simulated save failure");

            _states[scopeKey] = Any.Pack(state);
            return Task.CompletedTask;
        }

        public Task ClearStateAsync(string scopeKey, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _states.Remove(scopeKey);
            return Task.CompletedTask;
        }

        public async Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            BeforePublish?.Invoke(evt, options);
            if (BeforePublishAsync != null)
                await BeforePublishAsync(evt, options, ct);

            lock (_publicationGate)
            {
                if (FailPublishOnce?.Invoke(evt) == true)
                {
                    FailPublishOnce = null;
                    throw new InvalidOperationException("simulated publish failure");
                }

                Published.Add((evt, audience));
            }
        }

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _ = options;
            var generation = _callbackGenerations.GetValueOrDefault(callbackId, 0) + 1;
            _callbackGenerations[callbackId] = generation;
            Scheduled.Add(new ScheduledCallback(callbackId, generation, dueTime, evt));
            return Task.FromResult(new RuntimeCallbackLease(AgentId, callbackId, generation, RuntimeCallbackBackend.InMemory));
        }

        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage =>
            throw new NotSupportedException();
    }

    private sealed record ScheduledCallback(string CallbackId, long Generation, TimeSpan DueTime, IMessage Event);

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(System.Type serviceType) => null;
    }
}
