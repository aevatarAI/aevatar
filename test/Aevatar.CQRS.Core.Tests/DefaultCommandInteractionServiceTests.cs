using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Core.Commands;
using Aevatar.CQRS.Core.Interactions;
using Aevatar.CQRS.Core.Streaming;
using FluentAssertions;
using System.Threading.Channels;

namespace Aevatar.CQRS.Core.Tests;

public sealed class DefaultCommandInteractionServiceTests
{
    [Fact]
    public async Task ExecuteAsync_ShouldBindObservationBeforeDispatch_ThenEmitAcceptedAndPump()
    {
        var order = new List<string>();
        var sink = new EventChannel<string>();
        sink.Push("done:completed");
        sink.Complete();

        var target = new TestTarget("target-1", sink);
        var receipt = new TestReceipt("target-1", "receipt-before-observe");
        var execution = CreateExecution(target, receipt, commandId: "cmd-observe");
        var pipeline = new RecordingInteractionPipeline(
            CommandTargetResolution<CommandDispatchExecution<TestTarget, TestReceipt>, string>.Success(execution),
            order);
        var observation = new RecordingObservationLifecycle(order);
        var receiptFactory = new RecordingTestReceiptFactory(order, new TestReceipt("target-1", "receipt-after-observe"));
        var accepted = new List<TestReceipt>();
        var frames = new List<string>();
        var service = CreateService(
            pipeline,
            observationLifecycle: observation,
            receiptFactory: receiptFactory);

        var result = await service.ExecuteAsync(
            "command-observe",
            (frame, _) =>
            {
                order.Add("emit");
                frames.Add(frame);
                return ValueTask.CompletedTask;
            },
            (acceptedReceipt, _) =>
            {
                order.Add("accepted");
                accepted.Add(acceptedReceipt);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Receipt.Should().Be(receiptFactory.Receipt);
        accepted.Should().ContainSingle().Which.Should().Be(receiptFactory.Receipt);
        frames.Should().ContainSingle().Which.Should().Be("done:completed");
        observation.Calls.Should().ContainSingle();
        observation.Calls[0].Execution.Should().Be(execution);
        order.IndexOf("prepare").Should().BeLessThan(order.IndexOf("observe"));
        order.IndexOf("observe").Should().BeLessThan(order.IndexOf("dispatch"));
        order.IndexOf("dispatch").Should().BeLessThan(order.IndexOf("accepted"));
        order.IndexOf("accepted").Should().BeLessThan(order.IndexOf("emit"));
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStartLivePumpBeforeDispatchPreparedCompletes()
    {
        var order = new List<string>();
        var sink = new EventChannel<string>();
        var target = new TestTarget("target-1", sink);
        var receipt = new TestReceipt("target-1", "receipt-live-before-dispatch");
        var execution = CreateExecution(target, receipt, commandId: "cmd-live-before-dispatch");
        var pipeline = new GatedDispatchPipeline(
            CommandTargetResolution<CommandDispatchExecution<TestTarget, TestReceipt>, string>.Success(execution),
            order);
        var observation = new RecordingObservationLifecycle(order);
        var completionPolicy = new RecordingCompletionPolicy();
        var outputStream = new RecordingEventOutputStream();
        var accepted = new List<TestReceipt>();
        var frames = new List<string>();
        var service = CreateService(
            pipeline,
            completionPolicy: completionPolicy,
            observationLifecycle: observation,
            outputStream: outputStream);

        var resultTask = service.ExecuteAsync(
            "command-live-before-dispatch",
            (frame, _) =>
            {
                order.Add("emit:" + frame);
                frames.Add(frame);
                return ValueTask.CompletedTask;
            },
            (acceptedReceipt, _) =>
            {
                order.Add("accepted");
                accepted.Add(acceptedReceipt);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        await pipeline.DispatchStarted.Task;
        await outputStream.Started.Task;

        await sink.PushAsync("progress", CancellationToken.None);
        (await completionPolicy.ObservedEvents.Reader.ReadAsync(CancellationToken.None))
            .Should().Be("progress");

        accepted.Should().BeEmpty();
        frames.Should().BeEmpty();
        order.IndexOf("observe").Should().BeLessThan(order.IndexOf("dispatch"));

        await sink.PushAsync("done:completed", CancellationToken.None);
        (await completionPolicy.ObservedEvents.Reader.ReadAsync(CancellationToken.None))
            .Should().Be("done:completed");
        frames.Should().BeEmpty();
        completionPolicy.Events.Should().Equal("progress", "done:completed");
        outputStream.Completed.Task.IsCompleted.Should().BeTrue();
        order.Should().NotContain("dispatch-admitted");

        sink.Complete();
        pipeline.ReleaseDispatch();

        var result = await resultTask;

        result.Succeeded.Should().BeTrue();
        result.Receipt.Should().Be(receipt);
        result.FinalizeResult.Should().Be(new CommandInteractionFinalizeResult<string>("completed", true));
        outputStream.PumpCalls.Should().Be(1);
        accepted.Should().ContainSingle().Which.Should().Be(receipt);
        frames.Should().Equal("progress", "done:completed");
        completionPolicy.Events.Should().Equal("progress", "done:completed");
        target.ReleaseCalls.Should().ContainSingle();
        order.IndexOf("accepted").Should().BeGreaterThan(order.IndexOf("dispatch-admitted"));
        order.IndexOf("emit:progress").Should().BeGreaterThan(order.IndexOf("accepted"));
    }

    [Fact]
    public async Task ExecuteAsync_WhenDispatchFailsAfterPumpStarts_ShouldCancelPumpAndNotEmitAccepted()
    {
        var order = new List<string>();
        var sink = new EventChannel<string>();
        var target = new TestTarget("target-1", sink);
        var receipt = new TestReceipt("target-1", "receipt-dispatch-fails");
        var execution = CreateExecution(target, receipt, commandId: "cmd-dispatch-fails");
        var pipeline = new GatedDispatchPipeline(
            CommandTargetResolution<CommandDispatchExecution<TestTarget, TestReceipt>, string>.Success(execution),
            order);
        var preparation = new RecordingObservationScopePreparation(order);
        var observation = new RecordingObservationLifecycle(order);
        var completionPolicy = new RecordingCompletionPolicy();
        var outputStream = new RecordingEventOutputStream();
        var durableResolver = new RecordingDurableResolver(CommandDurableCompletionObservation<string>.Incomplete);
        var accepted = new List<TestReceipt>();
        var service = CreateService(
            pipeline,
            completionPolicy: completionPolicy,
            durableResolver: durableResolver,
            observationLifecycle: observation,
            observationScopePreparation: preparation,
            outputStream: outputStream);

        var resultTask = service.ExecuteAsync(
            "command-dispatch-fails",
            static (_, _) => ValueTask.CompletedTask,
            (acceptedReceipt, _) =>
            {
                accepted.Add(acceptedReceipt);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        await pipeline.DispatchStarted.Task;
        await outputStream.Started.Task;

        await sink.PushAsync("progress-before-failure", CancellationToken.None);
        (await completionPolicy.ObservedEvents.Reader.ReadAsync(CancellationToken.None))
            .Should().Be("progress-before-failure");

        var dispatchException = new InvalidOperationException("dispatch admission failed");
        pipeline.FailDispatch(dispatchException);

        var act = async () => await resultTask;
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("dispatch admission failed");

        accepted.Should().BeEmpty();
        pipeline.DispatchCalls.Should().Be(1);
        preparation.ReleaseCalls.Should().Be(1);
        target.ReleaseCalls.Should().BeEmpty();
        durableResolver.Calls.Should().Be(0);
        outputStream.PumpCalls.Should().Be(1);
        outputStream.Completed.Task.IsCompleted.Should().BeTrue();
        order.Should().ContainInOrder(
            "prepare",
            "prepare-observation-scope",
            "observe",
            "dispatch",
            "release-prepared-observation-scope");
    }

    [Fact]
    public async Task ExecuteAsync_WhenObservationBindingFails_ShouldReturnFailureWithoutDispatchOrAccepted()
    {
        var order = new List<string>();
        var target = new TestTarget("target-1", new EventChannel<string>());
        var pipeline = new RecordingInteractionPipeline(
            CommandTargetResolution<CommandDispatchExecution<TestTarget, TestReceipt>, string>.Success(
                CreateExecution(target, new TestReceipt("target-1", "receipt-1"))),
            order);
        var observation = new RecordingObservationLifecycle(order, "observation_failed");
        var accepted = new List<TestReceipt>();
        var service = CreateService(
            pipeline,
            observationLifecycle: observation,
            receiptFactory: new RecordingTestReceiptFactory(order, new TestReceipt("target-1", "unused")));

        var result = await service.ExecuteAsync(
            "command-fail",
            static (_, _) => ValueTask.CompletedTask,
            (acceptedReceipt, _) =>
            {
                accepted.Add(acceptedReceipt);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("observation_failed");
        pipeline.DispatchCalls.Should().Be(0);
        accepted.Should().BeEmpty();
        target.ReleaseCalls.Should().BeEmpty();
        order.Should().Equal("prepare", "observe");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldPrepareObservationScopeBeforeObservationBinding()
    {
        var order = new List<string>();
        var sink = new EventChannel<string>();
        sink.Push("done:completed");
        sink.Complete();

        var target = new TestTarget("target-1", sink);
        var execution = CreateExecution(target, new TestReceipt("target-1", "receipt-1"));
        var pipeline = new RecordingInteractionPipeline(
            CommandTargetResolution<CommandDispatchExecution<TestTarget, TestReceipt>, string>.Success(execution),
            order);
        var preparation = new RecordingObservationScopePreparation(order);
        var observation = new RecordingObservationLifecycle(order);
        var service = CreateService(
            pipeline,
            observationLifecycle: observation,
            observationScopePreparation: preparation);

        var result = await service.ExecuteAsync(
            "command-observe",
            static (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        preparation.Calls.Should().ContainSingle();
        preparation.Calls[0].Execution.Should().Be(execution);
        order.Should().StartWith("prepare", "prepare-observation-scope", "observe", "dispatch");
    }

    [Fact]
    public async Task ExecuteAsync_WhenObservationScopePreparationFails_ShouldReturnFailureWithoutDispatch()
    {
        var order = new List<string>();
        var target = new TestTarget("target-1", new EventChannel<string>());
        var pipeline = new RecordingInteractionPipeline(
            CommandTargetResolution<CommandDispatchExecution<TestTarget, TestReceipt>, string>.Success(
                CreateExecution(target, new TestReceipt("target-1", "receipt-1"))),
            order);
        var preparation = new RecordingObservationScopePreparation(order, failure: "projection_unavailable");
        var service = CreateService(
            pipeline,
            observationScopePreparation: preparation);

        var result = await service.ExecuteAsync(
            "command-fail",
            static (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("projection_unavailable");
        pipeline.DispatchCalls.Should().Be(0);
        preparation.ReleaseCalls.Should().Be(0);
        target.ReleaseCalls.Should().BeEmpty();
        order.Should().Equal("prepare", "prepare-observation-scope");
    }

    [Fact]
    public async Task ExecuteAsync_WhenObservationBindingFails_ShouldReleasePreparedObservationScope()
    {
        var order = new List<string>();
        var target = new TestTarget("target-1", new EventChannel<string>());
        var pipeline = new RecordingInteractionPipeline(
            CommandTargetResolution<CommandDispatchExecution<TestTarget, TestReceipt>, string>.Success(
                CreateExecution(target, new TestReceipt("target-1", "receipt-1"))),
            order);
        var preparation = new RecordingObservationScopePreparation(order);
        var observation = new RecordingObservationLifecycle(order, "projection_unavailable");
        var service = CreateService(
            pipeline,
            observationLifecycle: observation,
            observationScopePreparation: preparation);

        var result = await service.ExecuteAsync(
            "command-fail",
            static (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("projection_unavailable");
        pipeline.DispatchCalls.Should().Be(0);
        preparation.ReleaseCalls.Should().Be(1);
        target.ReleaseCalls.Should().BeEmpty();
        order.Should().Equal("prepare", "prepare-observation-scope", "observe", "release-prepared-observation-scope");
    }

    [Fact]
    public async Task ExecuteAsync_WhenDispatchFails_ShouldReturnFailure()
    {
        var service = CreateService(
            new TestDispatchPipeline(CommandTargetResolution<CommandDispatchExecution<TestTarget, TestReceipt>, string>.Failure("dispatch_failed")));

        var result = await service.ExecuteAsync(
            "command-1",
            static (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("dispatch_failed");
        result.Receipt.Should().BeNull();
        result.FinalizeResult.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_WhenStreamObservesTerminalEvent_ShouldEmitAcceptedFramesFinalizeAndRelease()
    {
        var sink = new EventChannel<string>();
        sink.Push("progress");
        sink.Push("done:completed");
        sink.Push("after_terminal");
        sink.Complete();

        var target = new TestTarget("target-1", sink);
        var finalizeEmitter = new RecordingFinalizeEmitter();
        var accepted = new List<TestReceipt>();
        var frames = new List<string>();
        var receipt = new TestReceipt("target-1", "receipt-1");
        var service = CreateService(
            new TestDispatchPipeline(CommandTargetResolution<CommandDispatchExecution<TestTarget, TestReceipt>, string>.Success(
                new CommandDispatchExecution<TestTarget, TestReceipt>
                {
                    Target = target,
                    Context = new CommandContext("target-1", "cmd-1", "corr-1", new Dictionary<string, string>()),
                    Envelope = new Aevatar.Foundation.Abstractions.EventEnvelope { Id = "env-1" },
                    Receipt = receipt,
                })),
            finalizeEmitter: finalizeEmitter);

        var result = await service.ExecuteAsync(
            "command-1",
            (frame, _) =>
            {
                frames.Add(frame);
                return ValueTask.CompletedTask;
            },
            (acceptedReceipt, _) =>
            {
                accepted.Add(acceptedReceipt);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Receipt.Should().Be(receipt);
        result.FinalizeResult.Should().Be(new CommandInteractionFinalizeResult<string>("completed", true));
        accepted.Should().ContainSingle().Which.Should().Be(receipt);
        frames.Should().Equal("progress", "done:completed");
        finalizeEmitter.Calls.Should().ContainSingle();
        finalizeEmitter.Calls[0].Receipt.Should().Be(receipt);
        finalizeEmitter.Calls[0].Completion.Should().Be("completed");
        finalizeEmitter.Calls[0].Completed.Should().BeTrue();
        target.ReleaseCalls.Should().ContainSingle();
        target.ReleaseCalls[0].Cleanup.ObservedCompleted.Should().BeTrue();
        target.ReleaseCalls[0].Cleanup.ObservedCompletion.Should().Be("completed");
        target.ReleaseCalls[0].Cleanup.DurableCompletion.HasTerminalCompletion.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_WhenLiveStreamNeverCompletes_ShouldUseDurableCompletionForFinalizeAndCleanup()
    {
        var sink = new EventChannel<string>();
        sink.Push("progress");
        sink.Complete();

        var target = new TestTarget("target-1", sink);
        var finalizeEmitter = new RecordingFinalizeEmitter();
        var durableResolver = new RecordingDurableResolver(
            new CommandDurableCompletionObservation<string>(true, "durable_completed"));
        var receipt = new TestReceipt("target-1", "receipt-2");
        var service = CreateService(
            new TestDispatchPipeline(CommandTargetResolution<CommandDispatchExecution<TestTarget, TestReceipt>, string>.Success(
                new CommandDispatchExecution<TestTarget, TestReceipt>
                {
                    Target = target,
                    Context = new CommandContext("target-1", "cmd-2", "corr-2", new Dictionary<string, string>()),
                    Envelope = new Aevatar.Foundation.Abstractions.EventEnvelope { Id = "env-2" },
                    Receipt = receipt,
                })),
            finalizeEmitter: finalizeEmitter,
            durableResolver: durableResolver);

        var result = await service.ExecuteAsync(
            "command-2",
            static (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.FinalizeResult.Should().Be(new CommandInteractionFinalizeResult<string>("durable_completed", true));
        durableResolver.Calls.Should().Be(1);
        finalizeEmitter.Calls.Should().ContainSingle();
        finalizeEmitter.Calls[0].Completion.Should().Be("durable_completed");
        target.ReleaseCalls.Should().ContainSingle();
        target.ReleaseCalls[0].Cleanup.ObservedCompleted.Should().BeTrue();
        target.ReleaseCalls[0].Cleanup.DurableCompletion.HasTerminalCompletion.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_WhenPumpCompletesWithoutTerminalEvent_ShouldUseDurableFallbackOnce()
    {
        var sink = new EventChannel<string>();
        sink.Push("progress");
        sink.Complete();

        var target = new TestTarget("target-1", sink);
        var receipt = new TestReceipt("target-1", "receipt-durable-once");
        var durableResolver = new RecordingDurableResolver(
            new CommandDurableCompletionObservation<string>(true, "durable_completed"));
        var finalizeEmitter = new RecordingFinalizeEmitter();
        var service = CreateService(
            new TestDispatchPipeline(CommandTargetResolution<CommandDispatchExecution<TestTarget, TestReceipt>, string>.Success(
                CreateExecution(target, receipt, commandId: "cmd-durable-once"))),
            finalizeEmitter: finalizeEmitter,
            durableResolver: durableResolver);

        var result = await service.ExecuteAsync(
            "command-durable-once",
            static (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.FinalizeResult.Should().Be(new CommandInteractionFinalizeResult<string>("durable_completed", true));
        durableResolver.Calls.Should().Be(1);
        finalizeEmitter.Calls.Should().ContainSingle();
        target.ReleaseCalls.Should().ContainSingle();
        target.ReleaseCalls[0].Cleanup.ObservedCompleted.Should().BeTrue();
        target.ReleaseCalls[0].Cleanup.ObservedCompletion.Should().Be("durable_completed");
        target.ReleaseCalls[0].Cleanup.DurableCompletion.HasTerminalCompletion.Should().BeTrue();
        target.ReleaseCalls[0].Cleanup.DurableCompletion.Completion.Should().Be("durable_completed");
    }

    [Fact]
    public async Task ExecuteAsync_WhenLiveStreamStaysOpenAndDurableTerminalExists_ShouldFinalizePromptly()
    {
        var sink = new EventChannel<string>();
        var target = new TestTarget("target-1", sink);
        var receipt = new TestReceipt("target-1", "receipt-exact-replay");
        var durableResolver = new RecordingDurableResolver(
            new CommandDurableCompletionObservation<string>(true, "durable_completed"));
        var finalizeEmitter = new RecordingFinalizeEmitter();
        var service = CreateService(
            new TestDispatchPipeline(CommandTargetResolution<CommandDispatchExecution<TestTarget, TestReceipt>, string>.Success(
                CreateExecution(target, receipt, commandId: "cmd-exact-replay"))),
            finalizeEmitter: finalizeEmitter,
            durableResolver: durableResolver,
            probeDurableCompletionWhileLive: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        var result = await service.ExecuteAsync(
            "command-exact-replay",
            static (_, _) => ValueTask.CompletedTask,
            ct: timeout.Token);

        result.Succeeded.Should().BeTrue();
        result.FinalizeResult.Should().Be(
            new CommandInteractionFinalizeResult<string>("durable_completed", true));
        durableResolver.Calls.Should().Be(1);
        finalizeEmitter.Calls.Should().ContainSingle();
        target.ReleaseCalls.Should().ContainSingle();
        target.ReleaseCalls[0].Cleanup.DurableCompletion.HasTerminalCompletion.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_WhenDurableWinsBeforeLiveTerminalIsBuffered_ShouldFinalizeDurableTerminal()
    {
        var sink = new EventChannel<string>();
        sink.Push("done:live");
        var target = new TestTarget("target-1", sink);
        var receipt = new TestReceipt("target-1", "receipt-terminal-race");
        var durableResolver = new GatedDurableResolver();
        var outputStream = new GatedBeforeEmissionOutputStream();
        var finalizeEmitter = new RecordingFinalizeEmitter();
        var service = CreateService(
            new TestDispatchPipeline(CommandTargetResolution<CommandDispatchExecution<TestTarget, TestReceipt>, string>.Success(
                CreateExecution(target, receipt, commandId: "cmd-terminal-race"))),
            finalizeEmitter: finalizeEmitter,
            durableResolver: durableResolver,
            outputStream: outputStream,
            probeDurableCompletionWhileLive: true);

        var execution = service.ExecuteAsync(
            "command-terminal-race",
            static (_, _) => ValueTask.CompletedTask);
        await outputStream.EventRead.Task.WaitAsync(TimeSpan.FromSeconds(1));
        durableResolver.Complete(
            new CommandDurableCompletionObservation<string>(true, "durable_completed"));

        var result = await execution.WaitAsync(TimeSpan.FromSeconds(1));

        result.FinalizeResult.Should().Be(
            new CommandInteractionFinalizeResult<string>("durable_completed", true));
        finalizeEmitter.Calls.Should().ContainSingle();
        finalizeEmitter.Calls[0].Completion.Should().Be("durable_completed");
    }

    [Fact]
    public async Task ExecuteAsync_WhenDurableProbeIsEnabled_ShouldResolveBeforeDispatchAndPreserveFreshLiveFrames()
    {
        var order = new List<string>();
        var sink = new EventChannel<string>();
        sink.Push("rich-card");
        sink.Push("done:live");
        sink.Complete();
        var target = new TestTarget("target-1", sink);
        var receipt = new TestReceipt("target-1", "receipt-live-buffered");
        var durableResolver = new RecordingDurableResolver(
            CommandDurableCompletionObservation<string>.Incomplete,
            order);
        var finalizeEmitter = new RecordingFinalizeEmitter();
        var frames = new List<string>();
        var service = CreateService(
            new RecordingInteractionPipeline(
                CommandTargetResolution<CommandDispatchExecution<TestTarget, TestReceipt>, string>.Success(
                    CreateExecution(target, receipt, commandId: "cmd-live-buffered")),
                order),
            finalizeEmitter: finalizeEmitter,
            durableResolver: durableResolver,
            probeDurableCompletionWhileLive: true);

        var result = await service.ExecuteAsync(
            "command-live-buffered",
            (frame, _) =>
            {
                frames.Add(frame);
                return ValueTask.CompletedTask;
            });

        result.FinalizeResult.Should().Be(
            new CommandInteractionFinalizeResult<string>("live", true));
        frames.Should().Equal("rich-card", "done:live");
        order.IndexOf("durable").Should().BeLessThan(order.IndexOf("dispatch"));
        finalizeEmitter.Calls.Should().ContainSingle();
        finalizeEmitter.Calls[0].Completion.Should().Be("live");
    }

    [Fact]
    public async Task ExecuteAsync_WhenCleanupFailsAfterSuccess_ShouldThrowCleanupFailure()
    {
        var sink = new EventChannel<string>();
        sink.Push("done:completed");
        sink.Complete();

        var target = new TestTarget("target-1", sink);
        target.ReleaseException = new InvalidOperationException("cleanup failed");
        var receipt = new TestReceipt("target-1", "receipt-3");
        var service = CreateService(
            new TestDispatchPipeline(CommandTargetResolution<CommandDispatchExecution<TestTarget, TestReceipt>, string>.Success(
                new CommandDispatchExecution<TestTarget, TestReceipt>
                {
                    Target = target,
                    Context = new CommandContext("target-1", "cmd-3", "corr-3", new Dictionary<string, string>()),
                    Envelope = new Aevatar.Foundation.Abstractions.EventEnvelope { Id = "env-3" },
                    Receipt = receipt,
                })));

        var act = () => service.ExecuteAsync(
            "command-3",
            static (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("cleanup failed");
    }

    [Fact]
    public async Task ExecuteAsync_WhenDurableResolutionThrows_ShouldNotRetryDuringCleanup()
    {
        var sink = new EventChannel<string>();
        sink.Push("progress");
        sink.Complete();

        var target = new TestTarget("target-1", sink);
        var receipt = new TestReceipt("target-1", "receipt-4");
        var durableResolver = new ThrowingDurableResolver(new TimeoutException("durable-timeout"));
        var service = CreateService(
            new TestDispatchPipeline(CommandTargetResolution<CommandDispatchExecution<TestTarget, TestReceipt>, string>.Success(
                new CommandDispatchExecution<TestTarget, TestReceipt>
                {
                    Target = target,
                    Context = new CommandContext("target-1", "cmd-4", "corr-4", new Dictionary<string, string>()),
                    Envelope = new Aevatar.Foundation.Abstractions.EventEnvelope { Id = "env-4" },
                    Receipt = receipt,
                })),
            durableResolver: durableResolver);

        var act = () => service.ExecuteAsync(
            "command-4",
            static (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>()
            .WithMessage("durable-timeout");
        durableResolver.Calls.Should().Be(1);
        target.ReleaseCalls.Should().ContainSingle();
        target.ReleaseCalls[0].Cleanup.ObservedCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_WhenPreDispatchDurableProbeThrows_ShouldReleaseInteractionResources()
    {
        var target = new TestTarget("target-1", new EventChannel<string>());
        var receipt = new TestReceipt("target-1", "receipt-preflight-failure");
        var durableResolver = new ThrowingDurableResolver(new TimeoutException("durable-timeout"));
        var service = CreateService(
            new TestDispatchPipeline(CommandTargetResolution<CommandDispatchExecution<TestTarget, TestReceipt>, string>.Success(
                CreateExecution(target, receipt, commandId: "cmd-preflight-failure"))),
            durableResolver: durableResolver,
            probeDurableCompletionWhileLive: true);

        var act = () => service.ExecuteAsync(
            "command-preflight-failure",
            static (_, _) => ValueTask.CompletedTask);

        await act.Should().ThrowAsync<TimeoutException>()
            .WithMessage("durable-timeout");
        durableResolver.Calls.Should().Be(1);
        target.ReleaseCalls.Should().ContainSingle();
    }

    [Fact]
    public async Task ExecuteAsync_WhenEmitFails_ShouldResolveDurableCompletionAndPreserveExecutionFailureOverCleanupFailure()
    {
        var sink = new EventChannel<string>();
        sink.Push("progress");
        sink.Complete();

        var target = new TestTarget("target-1", sink)
        {
            ReleaseException = new InvalidOperationException("cleanup failed"),
        };
        var receipt = new TestReceipt("target-1", "receipt-5");
        var durableResolver = new RecordingDurableResolver(
            new CommandDurableCompletionObservation<string>(true, "durable_after_emit_failure"));
        var service = CreateService(
            new TestDispatchPipeline(CommandTargetResolution<CommandDispatchExecution<TestTarget, TestReceipt>, string>.Success(
                new CommandDispatchExecution<TestTarget, TestReceipt>
                {
                    Target = target,
                    Context = new CommandContext("target-1", "cmd-5", "corr-5", new Dictionary<string, string>()),
                    Envelope = new Aevatar.Foundation.Abstractions.EventEnvelope { Id = "env-5" },
                    Receipt = receipt,
                })),
            durableResolver: durableResolver);

        var act = () => service.ExecuteAsync(
            "command-5",
            static (_, _) => throw new InvalidOperationException("emit failed"),
            ct: CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("emit failed");
        durableResolver.Calls.Should().Be(1);
        target.ReleaseCalls.Should().ContainSingle();
        target.ReleaseCalls[0].Cleanup.ObservedCompleted.Should().BeFalse();
        target.ReleaseCalls[0].Cleanup.DurableCompletion.HasTerminalCompletion.Should().BeTrue();
        target.ReleaseCalls[0].Cleanup.DurableCompletion.Completion.Should().Be("durable_after_emit_failure");
    }

    private static DefaultCommandInteractionService<string, TestTarget, TestReceipt, string, string, string, string> CreateService(
        ICommandDispatchPipeline<string, TestTarget, TestReceipt, string> dispatchPipeline,
        ICommandCompletionPolicy<string, string>? completionPolicy = null,
        ICommandFinalizeEmitter<TestReceipt, string, string>? finalizeEmitter = null,
        ICommandDurableCompletionResolver<TestReceipt, string>? durableResolver = null,
        ICommandObservationLifecycle<string, TestTarget, TestReceipt, string>? observationLifecycle = null,
        ICommandReceiptFactory<TestTarget, TestReceipt>? receiptFactory = null,
        ICommandObservationScopeLeasePreparation<string, TestTarget, TestReceipt, string>? observationScopePreparation = null,
        IEventOutputStream<string, string>? outputStream = null,
        bool probeDurableCompletionWhileLive = false) =>
        new(
            dispatchPipeline,
            outputStream ?? new DefaultEventOutputStream<string, string>(new PassThroughFrameMapper()),
            completionPolicy ?? new TestCompletionPolicy(),
            finalizeEmitter ?? new RecordingFinalizeEmitter(),
            durableResolver ?? new RecordingDurableResolver(CommandDurableCompletionObservation<string>.Incomplete),
            logger: null,
            observationLifecycle,
            receiptFactory,
            observationScopePreparation,
            probeDurableCompletionWhileLive);

    private static CommandDispatchExecution<TestTarget, TestReceipt> CreateExecution(
        TestTarget target,
        TestReceipt receipt,
        string commandId = "cmd-1") =>
        new()
        {
            Target = target,
            Context = new CommandContext(target.TargetId, commandId, "corr-1", new Dictionary<string, string>()),
            Envelope = new Aevatar.Foundation.Abstractions.EventEnvelope { Id = $"env-{commandId}" },
            Receipt = receipt,
        };

    private sealed record TestReceipt(string TargetId, string ReceiptId);

    private sealed class TestTarget(string targetId, IEventSink<string> sink)
        : ICommandEventTarget<string>,
          ICommandInteractionCleanupTarget<TestReceipt, string>
    {
        public string TargetId { get; } = targetId;
        public List<(TestReceipt Receipt, CommandInteractionCleanupContext<string> Cleanup)> ReleaseCalls { get; } = [];
        public Exception? ReleaseException { get; set; }

        public IEventSink<string> RequireLiveSink() => sink;

        public Task ReleaseAfterInteractionAsync(
            TestReceipt receipt,
            CommandInteractionCleanupContext<string> cleanup,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ReleaseCalls.Add((receipt, cleanup));
            if (ReleaseException != null)
                throw ReleaseException;

            return Task.CompletedTask;
        }
    }

    private sealed class TestDispatchPipeline(
        CommandTargetResolution<CommandDispatchExecution<TestTarget, TestReceipt>, string> result)
        : ICommandDispatchPipeline<string, TestTarget, TestReceipt, string>
    {
        public Task<CommandTargetResolution<CommandDispatchExecution<TestTarget, TestReceipt>, string>> PrepareAsync(
            string command,
            CancellationToken ct = default)
        {
            _ = command;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }

        public Task<DispatchAdmission> DispatchPreparedAsync(
            CommandDispatchExecution<TestTarget, TestReceipt> execution,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(execution);
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(DispatchAdmissionFactory.Create(execution.Target.TargetId, execution.Envelope));
        }

        public Task<CommandTargetResolution<CommandDispatchExecution<TestTarget, TestReceipt>, string>> DispatchAsync(
            string command,
            CancellationToken ct = default) =>
            DispatchAsyncCore(command, ct);

        private async Task<CommandTargetResolution<CommandDispatchExecution<TestTarget, TestReceipt>, string>> DispatchAsyncCore(
            string command,
            CancellationToken ct)
        {
            var prepared = await PrepareAsync(command, ct);
            if (!prepared.Succeeded || prepared.Target == null)
                return prepared;

            var admission = await DispatchPreparedAsync(prepared.Target, ct);
            return CommandTargetResolution<CommandDispatchExecution<TestTarget, TestReceipt>, string>.Success(
                prepared.Target with { Admission = admission });
        }
    }

    private sealed class RecordingInteractionPipeline(
        CommandTargetResolution<CommandDispatchExecution<TestTarget, TestReceipt>, string> result,
        List<string> order)
        : ICommandDispatchPipeline<string, TestTarget, TestReceipt, string>
    {
        public int DispatchCalls { get; private set; }

        public Task<CommandTargetResolution<CommandDispatchExecution<TestTarget, TestReceipt>, string>> PrepareAsync(
            string command,
            CancellationToken ct = default)
        {
            _ = command;
            ct.ThrowIfCancellationRequested();
            order.Add("prepare");
            return Task.FromResult(result);
        }

        public Task<DispatchAdmission> DispatchPreparedAsync(
            CommandDispatchExecution<TestTarget, TestReceipt> execution,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(execution);
            ct.ThrowIfCancellationRequested();
            DispatchCalls++;
            order.Add("dispatch");
            return Task.FromResult(DispatchAdmissionFactory.Create(execution.Target.TargetId, execution.Envelope));
        }

        public async Task<CommandTargetResolution<CommandDispatchExecution<TestTarget, TestReceipt>, string>> DispatchAsync(
            string command,
            CancellationToken ct = default)
        {
            var prepared = await PrepareAsync(command, ct);
            if (!prepared.Succeeded || prepared.Target == null)
                return prepared;

            var admission = await DispatchPreparedAsync(prepared.Target, ct);
            return CommandTargetResolution<CommandDispatchExecution<TestTarget, TestReceipt>, string>.Success(
                prepared.Target with { Admission = admission });
        }
    }

    private sealed class GatedDispatchPipeline(
        CommandTargetResolution<CommandDispatchExecution<TestTarget, TestReceipt>, string> result,
        List<string> order)
        : ICommandDispatchPipeline<string, TestTarget, TestReceipt, string>
    {
        private readonly TaskCompletionSource<DispatchAdmission> _dispatchGate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DispatchCalls { get; private set; }

        public TaskCompletionSource DispatchStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<CommandTargetResolution<CommandDispatchExecution<TestTarget, TestReceipt>, string>> PrepareAsync(
            string command,
            CancellationToken ct = default)
        {
            _ = command;
            ct.ThrowIfCancellationRequested();
            order.Add("prepare");
            return Task.FromResult(result);
        }

        public async Task<DispatchAdmission> DispatchPreparedAsync(
            CommandDispatchExecution<TestTarget, TestReceipt> execution,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(execution);
            ct.ThrowIfCancellationRequested();
            DispatchCalls++;
            order.Add("dispatch");
            DispatchStarted.TrySetResult();
            await using var registration = ct.Register(
                static state => ((TaskCompletionSource<DispatchAdmission>)state!).TrySetCanceled(),
                _dispatchGate);
            var admission = await _dispatchGate.Task.ConfigureAwait(false);
            order.Add("dispatch-admitted");
            return admission;
        }

        public async Task<CommandTargetResolution<CommandDispatchExecution<TestTarget, TestReceipt>, string>> DispatchAsync(
            string command,
            CancellationToken ct = default)
        {
            var prepared = await PrepareAsync(command, ct);
            if (!prepared.Succeeded || prepared.Target == null)
                return prepared;

            var admission = await DispatchPreparedAsync(prepared.Target, ct);
            return CommandTargetResolution<CommandDispatchExecution<TestTarget, TestReceipt>, string>.Success(
                prepared.Target with { Admission = admission });
        }

        public void ReleaseDispatch()
        {
            var execution = result.Target
                ?? throw new InvalidOperationException("Cannot release dispatch without a prepared target.");
            _dispatchGate.TrySetResult(DispatchAdmissionFactory.Create(execution.Target.TargetId, execution.Envelope));
        }

        public void FailDispatch(Exception exception) => _dispatchGate.TrySetException(exception);
    }

    private sealed class RecordingObservationLifecycle(List<string> order, string? failure = null)
        : ICommandObservationLifecycle<string, TestTarget, TestReceipt, string>
    {
        public List<(string Command, CommandDispatchExecution<TestTarget, TestReceipt> Execution)> Calls { get; } = [];

        public Task<CommandObservationBindingResult<string>> BindAsync(
            string command,
            CommandDispatchExecution<TestTarget, TestReceipt> execution,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            order.Add("observe");
            Calls.Add((command, execution));
            return Task.FromResult(failure == null
                ? CommandObservationBindingResult<string>.Success()
                : CommandObservationBindingResult<string>.Failure(failure));
        }
    }

    private sealed class RecordingObservationScopePreparation(List<string> order, string? failure = null)
        : ICommandObservationScopeLeasePreparation<string, TestTarget, TestReceipt, string>
    {
        public List<(string Command, CommandDispatchExecution<TestTarget, TestReceipt> Execution)> Calls { get; } = [];
        public int ReleaseCalls { get; private set; }

        public Task<CommandObservationScopeLeasePreparationResult<string>> PrepareAsync(
            string command,
            CommandDispatchExecution<TestTarget, TestReceipt> execution,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            order.Add("prepare-observation-scope");
            Calls.Add((command, execution));

            if (failure != null)
                return Task.FromResult(CommandObservationScopeLeasePreparationResult<string>.Failure(failure));

            return Task.FromResult(CommandObservationScopeLeasePreparationResult<string>.Success(
                new Handle(() =>
                {
                    ReleaseCalls++;
                    order.Add("release-prepared-observation-scope");
                    return Task.CompletedTask;
                })));
        }

        private sealed class Handle(Func<Task> releaseAsync) : ICommandObservationScopeLeasePreparationHandle
        {
            public Task ReleaseAsync(CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                return releaseAsync();
            }
        }
    }

    private sealed class RecordingTestReceiptFactory(List<string> order, TestReceipt receipt)
        : ICommandReceiptFactory<TestTarget, TestReceipt>
    {
        public TestReceipt Receipt => receipt;

        public TestReceipt Create(TestTarget target, CommandContext context)
        {
            _ = target;
            _ = context;
            order.Add("receipt");
            return receipt;
        }
    }

    private sealed class TestCompletionPolicy : ICommandCompletionPolicy<string, string>
    {
        public string IncompleteCompletion => string.Empty;

        public bool TryResolve(string evt, out string completion)
        {
            if (evt.StartsWith("done:", StringComparison.Ordinal))
            {
                completion = evt["done:".Length..];
                return true;
            }

            completion = string.Empty;
            return false;
        }
    }

    private sealed class RecordingCompletionPolicy : ICommandCompletionPolicy<string, string>
    {
        private readonly TestCompletionPolicy _inner = new();

        public List<string> Events { get; } = [];

        public Channel<string> ObservedEvents { get; } = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = true,
            });

        public string IncompleteCompletion => _inner.IncompleteCompletion;

        public bool TryResolve(string evt, out string completion)
        {
            Events.Add(evt);
            ObservedEvents.Writer.TryWrite(evt);
            return _inner.TryResolve(evt, out completion);
        }
    }

    private sealed class RecordingEventOutputStream : IEventOutputStream<string, string>
    {
        private readonly DefaultEventOutputStream<string, string> _inner = new(new PassThroughFrameMapper());

        public int PumpCalls { get; private set; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task PumpAsync(
            IAsyncEnumerable<string> events,
            Func<string, CancellationToken, ValueTask> emitAsync,
            Func<string, bool>? shouldStop = null,
            CancellationToken ct = default)
        {
            PumpCalls++;
            Started.TrySetResult();

            try
            {
                await _inner.PumpAsync(events, emitAsync, shouldStop, ct).ConfigureAwait(false);
                Completed.TrySetResult();
            }
            catch (Exception ex)
            {
                Completed.TrySetException(ex);
                throw;
            }
        }
    }

    private sealed class RecordingFinalizeEmitter : ICommandFinalizeEmitter<TestReceipt, string, string>
    {
        public List<(TestReceipt Receipt, string Completion, bool Completed)> Calls { get; } = [];

        public Task EmitAsync(
            TestReceipt receipt,
            string completion,
            bool completed,
            Func<string, CancellationToken, ValueTask> emitAsync,
            CancellationToken ct = default)
        {
            _ = emitAsync;
            ct.ThrowIfCancellationRequested();
            Calls.Add((receipt, completion, completed));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingDurableResolver(
        CommandDurableCompletionObservation<string> observation,
        List<string>? order = null)
        : ICommandDurableCompletionResolver<TestReceipt, string>
    {
        public int Calls { get; private set; }

        public Task<CommandDurableCompletionObservation<string>> ResolveAsync(
            TestReceipt receipt,
            CancellationToken ct = default)
        {
            _ = receipt;
            ct.ThrowIfCancellationRequested();
            Calls++;
            order?.Add("durable");
            return Task.FromResult(observation);
        }
    }

    private sealed class ThrowingDurableResolver(Exception exception)
        : ICommandDurableCompletionResolver<TestReceipt, string>
    {
        public int Calls { get; private set; }

        public Task<CommandDurableCompletionObservation<string>> ResolveAsync(
            TestReceipt receipt,
            CancellationToken ct = default)
        {
            _ = receipt;
            ct.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromException<CommandDurableCompletionObservation<string>>(exception);
        }
    }

    private sealed class PassThroughFrameMapper : IEventFrameMapper<string, string>
    {
        public string Map(string evt) => evt;
    }

    private sealed class GatedBeforeEmissionOutputStream : IEventOutputStream<string, string>
    {
        private readonly TaskCompletionSource _emitGate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource EventRead { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task PumpAsync(
            IAsyncEnumerable<string> events,
            Func<string, CancellationToken, ValueTask> emitAsync,
            Func<string, bool>? shouldStop = null,
            CancellationToken ct = default)
        {
            await foreach (var evt in events.WithCancellation(ct))
            {
                EventRead.TrySetResult();
                await _emitGate.Task.WaitAsync(ct);
                await emitAsync(evt, ct);
                if (shouldStop?.Invoke(evt) == true)
                    break;
            }
        }
    }

    private sealed class GatedDurableResolver
        : ICommandDurableCompletionResolver<TestReceipt, string>
    {
        private readonly TaskCompletionSource<CommandDurableCompletionObservation<string>> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete(CommandDurableCompletionObservation<string> completion) =>
            _completion.TrySetResult(completion);

        public Task<CommandDurableCompletionObservation<string>> ResolveAsync(
            TestReceipt receipt,
            CancellationToken ct = default)
        {
            _ = receipt;
            return _completion.Task.WaitAsync(ct);
        }
    }
}

public sealed class FallbackCommandServiceTests
{
    [Fact]
    public async Task FallbackCommandInteractionService_ShouldRetryWithFallbackCommand_WhenPolicyMatches()
    {
        var service = new FallbackCommandInteractionService<string, string, string, string, string>(
            new RecordingInteractionService
            {
                InteractionException = new InvalidOperationException("primary failed"),
                Result = CommandInteractionResult<string, string, string>.Success("receipt", new CommandInteractionFinalizeResult<string>("done", true)),
            },
            new RetryOnInvalidOperationPolicy());

        var result = await service.ExecuteAsync(
            "primary",
            static (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Receipt.Should().Be("receipt");
    }

    [Fact]
    public async Task FallbackCommandDispatchService_ShouldRetryWithFallbackCommand_WhenPolicyMatches()
    {
        var service = new FallbackCommandDispatchService<string, string, string>(
            new RecordingDispatchService
            {
                DispatchException = new InvalidOperationException("primary failed"),
                Result = CommandDispatchResult<string, string>.Success("receipt"),
            },
            new RetryOnInvalidOperationPolicy());

        var result = await service.DispatchAsync("primary", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Receipt.Should().Be("receipt");
    }

    private sealed class RecordingInteractionService : ICommandInteractionService<string, string, string, string, string>
    {
        public Exception? InteractionException { get; set; }
        public CommandInteractionResult<string, string, string> Result { get; set; } =
            CommandInteractionResult<string, string, string>.Failure("failed");

        private bool _hasThrown;

        public Task<CommandInteractionResult<string, string, string>> ExecuteAsync(
            string command,
            Func<string, CancellationToken, ValueTask> emitAsync,
            Func<string, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            _ = command;
            _ = emitAsync;
            _ = onAcceptedAsync;
            ct.ThrowIfCancellationRequested();
            if (!_hasThrown && InteractionException != null)
            {
                _hasThrown = true;
                throw InteractionException;
            }

            return Task.FromResult(Result);
        }

        async Task<RealtimeSessionResult<string, string, string>> IRealtimeSession<string, string, string, string, string>.ExecuteAsync(
            string inbound,
            Func<string, CancellationToken, ValueTask> emitAsync,
            Func<string, CancellationToken, ValueTask>? onAcceptedAsync,
            CancellationToken ct)
        {
            return await ExecuteAsync(inbound, emitAsync, onAcceptedAsync, ct);
        }
    }

    private sealed class RecordingDispatchService : ICommandDispatchService<string, string, string>
    {
        public Exception? DispatchException { get; set; }
        public CommandDispatchResult<string, string> Result { get; set; } =
            CommandDispatchResult<string, string>.Failure("failed");

        private bool _hasThrown;

        public Task<CommandDispatchResult<string, string>> DispatchAsync(
            string command,
            CancellationToken ct = default)
        {
            _ = command;
            ct.ThrowIfCancellationRequested();
            if (!_hasThrown && DispatchException != null)
            {
                _hasThrown = true;
                throw DispatchException;
            }

            return Task.FromResult(Result);
        }
    }

    private sealed class RetryOnInvalidOperationPolicy : ICommandFallbackPolicy<string>
    {
        public bool TryCreateFallbackCommand(string command, Exception exception, out string fallbackCommand)
        {
            if (exception is InvalidOperationException)
            {
                fallbackCommand = command + ":fallback";
                return true;
            }

            fallbackCommand = command;
            return false;
        }
    }
}
