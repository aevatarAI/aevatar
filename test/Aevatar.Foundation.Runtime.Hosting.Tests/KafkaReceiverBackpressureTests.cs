using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider;
using Confluent.Kafka;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using NSubstitute;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;
using Xunit.Abstractions;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

[CollectionDefinition(nameof(KafkaReceiverBackpressureCollection), DisableParallelization = true)]
public sealed class KafkaReceiverBackpressureCollection;

[Collection(nameof(KafkaReceiverBackpressureCollection))]
public sealed class KafkaReceiverBackpressureTests(ITestOutputHelper output)
{
    private const string PerformanceDiagnosticsEnvironmentVariable =
        "AEVATAR_KAFKA_RECEIVER_PERFORMANCE_DIAGNOSTICS";
    private const string PerformanceDiagnosticWatchdogSecondsEnvironmentVariable =
        "AEVATAR_KAFKA_RECEIVER_PERFORMANCE_WATCHDOG_SECONDS";
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultPerformanceDiagnosticWatchdog = TimeSpan.FromMinutes(10);

    [Theory]
    [InlineData(0, 2, 4)]
    [InlineData(2, 2, 4)]
    [InlineData(3, 2, 4)]
    [InlineData(2, 5, 4)]
    public void KafkaReceiverBufferWatermarks_WhenInvalid_ShouldFailWithSingleInvariant(
        int lowWatermark,
        int highWatermark,
        int capacity)
    {
        var options = CreateOptions(capacity, highWatermark, lowWatermark);

        var act = options.ValidateReceiverBufferWatermarks;

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*0 < ReceiverBufferLowWatermark < ReceiverBufferHighWatermark <= ReceiverBufferCapacity*");
    }

    [Fact]
    public void KafkaReceiverMessageBuffer_WhenProducerOutrunsDrain_ShouldNeverExceedCapacity()
    {
        const int capacity = 32;
        var message = Substitute.For<IBatchContainer>();
        var buffer = new KafkaReceiverMessageBuffer(capacity);

        var accepted = Enumerable.Range(0, capacity * 8)
            .Count(_ => buffer.TryWrite(message));

        accepted.Should().Be(capacity);
        buffer.Depth.Should().Be(capacity);
        buffer.TryWrite(message).Should().BeFalse();
        buffer.Depth.Should().Be(capacity);
    }

    [Fact]
    public async Task KafkaReceiverMessageBuffer_WithConcurrentOwnerAndPuller_ShouldRemainBoundedAndLossless()
    {
        const int capacity = 64;
        const int messageCount = 50_000;
        var message = Substitute.For<IBatchContainer>();
        var buffer = new KafkaReceiverMessageBuffer(capacity);
        using var start = new ManualResetEventSlim();
        var maximumDepth = 0;
        var consumed = 0;

        var producer = Task.Run(() =>
        {
            start.Wait();
            for (var produced = 0; produced < messageCount;)
            {
                if (!buffer.TryWrite(message))
                {
                    Thread.Yield();
                    continue;
                }

                produced++;
                UpdateMaximum(ref maximumDepth, buffer.Depth);
            }
        });
        var puller = Task.Run(() =>
        {
            start.Wait();
            while (consumed < messageCount)
            {
                if (!buffer.TryRead(out _))
                {
                    Thread.Yield();
                    continue;
                }

                consumed++;
                UpdateMaximum(ref maximumDepth, buffer.Depth);
            }
        });

        start.Set();
        await Task.WhenAll(producer, puller).WaitAsync(TestTimeout);

        consumed.Should().Be(messageCount);
        maximumDepth.Should().BeLessThanOrEqualTo(capacity);
        buffer.Depth.Should().Be(0);
    }

    [Fact]
    public async Task KafkaReceiver_WhenCrossingWatermarks_ShouldPauseAndResumeWithoutFlappingOnOwnerThread()
    {
        var harness = CreateHarness(capacity: 3, highWatermark: 2, lowWatermark: 1);
        await harness.Receiver.Initialize(TestTimeout);

        try
        {
            harness.Consumer.AddRecord(0);
            harness.Consumer.AddRecord(1);

            var firstPause = await harness.Consumer.ReadPauseAsync();
            firstPause.Should().Equal(harness.TopicPartition);
            harness.Receiver.BufferedMessageCount.Should().Be(2);
            var pausedConsumeCount = harness.Consumer.ConsumeCount;
            await harness.Consumer.AwaitConsumeCountAsync(pausedConsumeCount + 2);

            var firstBatch = await harness.Receiver.GetQueueMessagesAsync(1);
            firstBatch.Should().ContainSingle();
            var firstResume = await harness.Consumer.ReadResumeAsync();
            firstResume.Should().Equal(harness.TopicPartition);
            harness.Receiver.BufferedMessageCount.Should().Be(1);

            var consumeCount = harness.Consumer.ConsumeCount;
            await harness.Consumer.AwaitConsumeCountAsync(consumeCount + 3);

            harness.Consumer.PauseCallCount.Should().Be(1);
            harness.Consumer.ResumeCallCount.Should().Be(1);
            harness.Receiver.BufferedMessageCount.Should().Be(1);
        }
        finally
        {
            await harness.Receiver.Shutdown(TestTimeout);
        }

        harness.Consumer.ConsumerOperationThreadIds.Should().ContainSingle(
            "Assign, Pause, Resume, Consume, Commit, Seek, Close and Dispose must share one owner thread");
    }

    [Fact]
    public async Task KafkaReceiver_BelowHighWatermark_ShouldKeepItsFixedPartitionUnpaused()
    {
        var harness = CreateHarness(capacity: 8, highWatermark: 6, lowWatermark: 3);
        await harness.Receiver.Initialize(TestTimeout);

        try
        {
            harness.Consumer.AddRecord(0);
            harness.Consumer.AddRecord(1);
            await harness.Consumer.AwaitReturnedOffsetAsync(1);
            await harness.Consumer.AwaitConsumeCountAsync(harness.Consumer.ConsumeCount + 3);

            harness.Consumer.AssignedPartition.Should().Be(harness.TopicPartition);
            harness.Consumer.PauseCallCount.Should().Be(0);
            harness.Consumer.ResumeCallCount.Should().Be(0);
        }
        finally
        {
            await harness.Receiver.Shutdown(TestTimeout);
        }
    }

    [Fact]
    public async Task KafkaReceiver_AfterShutdownAndReinitialize_ShouldReacquireTheSameFixedPartition()
    {
        var options = CreateOptions(capacity: 3, highWatermark: 2, lowWatermark: 1);
        var mapper = new KafkaQueuePartitionMapper("backpressure-provider", 2);
        var queueId = mapper.GetAllQueues().First();
        var topicPartition = new TopicPartition(options.TopicName, new Partition(mapper.GetPartitionId(queueId)));
        var firstConsumer = CreateDeterministicConsumer(options);
        var secondConsumer = CreateDeterministicConsumer(options);
        var consumers = new ConcurrentQueue<IKafkaReceiverConsumer>(
            new IKafkaReceiverConsumer[] { firstConsumer, secondConsumer });
        var receiver = new KafkaProviderQueueAdapterReceiver(
            queueId,
            "backpressure-provider",
            options,
            mapper,
            "aevatar.events",
            _ => Task.CompletedTask,
            () => consumers.TryDequeue(out var consumer)
                ? consumer
                : throw new InvalidOperationException("No deterministic consumer remains for initialization."));

        try
        {
            await receiver.Initialize(TestTimeout);
            firstConsumer.AssignedPartition.Should().Be(topicPartition);
            await receiver.Shutdown(TestTimeout);
            firstConsumer.CloseCallCount.Should().Be(1);
            firstConsumer.DisposeCallCount.Should().Be(1);

            await receiver.Initialize(TestTimeout);
            secondConsumer.AssignedPartition.Should().Be(topicPartition,
                "Orleans reacquiring the same QueueId must recreate the fixed partition receiver");
            secondConsumer.AddRecord(0);
            secondConsumer.AddRecord(1);
            (await secondConsumer.ReadPauseAsync()).Should().Equal(topicPartition);
            receiver.BufferedMessageCount.Should().Be(2,
                "the reinitialized lifecycle must accept records after clearing the shutdown signal");
        }
        finally
        {
            await receiver.Shutdown(TestTimeout);
        }

        secondConsumer.CloseCallCount.Should().Be(1);
        secondConsumer.DisposeCallCount.Should().Be(1);
        firstConsumer.ConsumerOperationThreadIds.Should().ContainSingle();
        secondConsumer.ConsumerOperationThreadIds.Should().ContainSingle();
    }

    [Fact]
    public async Task KafkaReceiver_WhenShutdownCalledConcurrently_ShouldShareOneSuccessfulCleanupTask()
    {
        var harness = CreateHarness(capacity: 3, highWatermark: 2, lowWatermark: 1);
        await harness.Receiver.Initialize(TestTimeout);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callers = Enumerable.Range(0, 8)
            .Select(async _ =>
            {
                await start.Task;
                return harness.Receiver.Shutdown(TestTimeout);
            })
            .ToArray();

        start.SetResult();
        var shutdownTasks = await Task.WhenAll(callers);

        shutdownTasks.Should().OnlyContain(task => ReferenceEquals(task, shutdownTasks[0]));
        await Task.WhenAll(shutdownTasks);
        shutdownTasks[0].IsCompletedSuccessfully.Should().BeTrue();

        var repeatedShutdown = harness.Receiver.Shutdown(TestTimeout);
        repeatedShutdown.Should().BeSameAs(shutdownTasks[0]);
        await repeatedShutdown;
        harness.Consumer.CloseCallCount.Should().Be(1);
        harness.Consumer.DisposeCallCount.Should().Be(1);
    }

    [Fact]
    public async Task KafkaReceiver_WhenShutdownOverlapsGatedInitialization_ShouldCancelOneSharedGeneration()
    {
        var options = CreateOptions(capacity: 3, highWatermark: 2, lowWatermark: 1);
        var mapper = new KafkaQueuePartitionMapper("backpressure-provider", 2);
        var queueId = mapper.GetAllQueues().First();
        var transportReadyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTransportReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumerFactoryCallCount = 0;
        var receiver = new KafkaProviderQueueAdapterReceiver(
            queueId,
            "backpressure-provider",
            options,
            mapper,
            "aevatar.events",
            async _ =>
            {
                transportReadyEntered.TrySetResult();
                await releaseTransportReady.Task;
            },
            () =>
            {
                Interlocked.Increment(ref consumerFactoryCallCount);
                return CreateDeterministicConsumer(options);
            });
        var initializeCallersReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initializeCallers = Enumerable.Range(0, 8)
            .Select(async _ =>
            {
                await initializeCallersReady.Task;
                return receiver.Initialize(TestTimeout);
            })
            .ToArray();

        initializeCallersReady.SetResult();
        var initializeTasks = await Task.WhenAll(initializeCallers);
        initializeTasks.Should().OnlyContain(task => ReferenceEquals(task, initializeTasks[0]));
        await transportReadyEntered.Task.WaitAsync(TestTimeout);

        var shutdownCallersReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdownCallers = Enumerable.Range(0, 8)
            .Select(async _ =>
            {
                await shutdownCallersReady.Task;
                return receiver.Shutdown(TestTimeout);
            })
            .ToArray();
        shutdownCallersReady.SetResult();
        var shutdownTasks = await Task.WhenAll(shutdownCallers);

        shutdownTasks.Should().OnlyContain(task => ReferenceEquals(task, shutdownTasks[0]));
        shutdownTasks[0].IsCompleted.Should().BeFalse(
            "shutdown must wait for the in-flight transport-ready continuation to leave the generation");
        releaseTransportReady.TrySetResult();

        Func<Task> awaitInitialize = async () => await initializeTasks[0];
        await awaitInitialize.Should().ThrowAsync<OperationCanceledException>();
        await shutdownTasks[0].WaitAsync(TestTimeout);
        consumerFactoryCallCount.Should().Be(0,
            "a canceled transport-ready continuation must not start a consumer after shutdown");
    }

    [Fact]
    public async Task KafkaReceiver_WhenInitializeOverlapsShutdown_ShouldWaitAndPublishOneNextGeneration()
    {
        var options = CreateOptions(capacity: 3, highWatermark: 2, lowWatermark: 1);
        var mapper = new KafkaQueuePartitionMapper("backpressure-provider", 2);
        var queueId = mapper.GetAllQueues().First();
        var firstTransportReadyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstTransportReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondTransportReadyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondTransportReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transportReadyCallCount = 0;
        var consumer = CreateDeterministicConsumer(options);
        var consumerFactoryCallCount = 0;
        var receiver = new KafkaProviderQueueAdapterReceiver(
            queueId,
            "backpressure-provider",
            options,
            mapper,
            "aevatar.events",
            async _ =>
            {
                var call = Interlocked.Increment(ref transportReadyCallCount);
                if (call == 1)
                {
                    firstTransportReadyEntered.TrySetResult();
                    await releaseFirstTransportReady.Task;
                    return;
                }

                secondTransportReadyEntered.TrySetResult();
                await releaseSecondTransportReady.Task;
            },
            () =>
            {
                Interlocked.Increment(ref consumerFactoryCallCount);
                return consumer;
            });

        var firstInitialize = receiver.Initialize(TestTimeout);
        await firstTransportReadyEntered.Task.WaitAsync(TestTimeout);
        var firstShutdown = receiver.Shutdown(TestTimeout);

        var nextInitializeCallersReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextInitializeCallers = Enumerable.Range(0, 8)
            .Select(async _ =>
            {
                await nextInitializeCallersReady.Task;
                return receiver.Initialize(TestTimeout);
            })
            .ToArray();
        nextInitializeCallersReady.SetResult();
        var nextInitializeTasks = await Task.WhenAll(nextInitializeCallers);

        nextInitializeTasks.Should().OnlyContain(task => ReferenceEquals(task, nextInitializeTasks[0]));
        nextInitializeTasks[0].Should().NotBeSameAs(firstInitialize);
        transportReadyCallCount.Should().Be(1,
            "the next generation must not enter transport readiness before predecessor cleanup completes");

        releaseFirstTransportReady.TrySetResult();
        Func<Task> awaitFirstInitialize = async () => await firstInitialize;
        await awaitFirstInitialize.Should().ThrowAsync<OperationCanceledException>();
        await firstShutdown.WaitAsync(TestTimeout);
        await secondTransportReadyEntered.Task.WaitAsync(TestTimeout);
        nextInitializeTasks[0].IsCompleted.Should().BeFalse();

        releaseSecondTransportReady.TrySetResult();
        await nextInitializeTasks[0].WaitAsync(TestTimeout);
        consumerFactoryCallCount.Should().Be(1);
        await receiver.Shutdown(TestTimeout).WaitAsync(TestTimeout);
        consumer.CloseCallCount.Should().Be(1);
        consumer.DisposeCallCount.Should().Be(1);
    }

    [Fact]
    public async Task KafkaReceiver_WhenShutdownWinsBeforeQueuedOwnerLoopRuns_ShouldNotCreateOrAssignConsumer()
    {
        var options = CreateOptions(capacity: 3, highWatermark: 2, lowWatermark: 1);
        var mapper = new KafkaQueuePartitionMapper("backpressure-provider", 2);
        var queueId = mapper.GetAllQueues().First();
        var consumer = CreateDeterministicConsumer(options);
        var consumerFactoryCallCount = 0;
        var ownerLoopStarter = new GatedOwnerLoopStarter();
        var receiver = new KafkaProviderQueueAdapterReceiver(
            queueId,
            "backpressure-provider",
            options,
            mapper,
            "aevatar.events",
            _ => Task.CompletedTask,
            () =>
            {
                Interlocked.Increment(ref consumerFactoryCallCount);
                return consumer;
            },
            ownerLoopStarter: ownerLoopStarter.Start);

        var initialize = receiver.Initialize(TestTimeout);
        await ownerLoopStarter.AwaitScheduledAsync();
        consumerFactoryCallCount.Should().Be(0);

        var shutdown = receiver.Shutdown(TestTimeout);
        shutdown.IsCompleted.Should().BeFalse(
            "shutdown waits for the already-published owner-loop task to observe lifecycle cancellation");
        ownerLoopStarter.RunScheduled();

        Func<Task> awaitInitialize = async () => await initialize;
        await awaitInitialize.Should().ThrowAsync<OperationCanceledException>();
        await shutdown.WaitAsync(TestTimeout);
        consumerFactoryCallCount.Should().Be(0);
        consumer.AssignedPartition.Should().BeNull(
            "a queued delegate from a canceled generation must not assign its partition");
        consumer.CloseCallCount.Should().Be(0);
        consumer.DisposeCallCount.Should().Be(0);
    }

    [Fact]
    public async Task KafkaReceiver_WhenAssignFails_ShouldDisposeCreatedConsumerExactlyOnce()
    {
        var options = CreateOptions(capacity: 3, highWatermark: 2, lowWatermark: 1);
        var mapper = new KafkaQueuePartitionMapper("backpressure-provider", 2);
        var queueId = mapper.GetAllQueues().First();
        var consumer = CreateDeterministicConsumer(options);
        var assignFailure = new InvalidOperationException("assign failed on owner loop");
        consumer.FailNextAssign(assignFailure);
        var receiver = new KafkaProviderQueueAdapterReceiver(
            queueId,
            "backpressure-provider",
            options,
            mapper,
            "aevatar.events",
            _ => Task.CompletedTask,
            () => consumer);

        Func<Task> initialize = () => receiver.Initialize(TestTimeout);
        var initializationFailure = await initialize.Should().ThrowAsync<InvalidOperationException>();
        initializationFailure.Which.InnerException.Should().BeSameAs(assignFailure);
        consumer.AssignedPartition.Should().BeNull();
        consumer.CloseCallCount.Should().Be(1);
        consumer.DisposeCallCount.Should().Be(1);

        Func<Task> shutdown = () => receiver.Shutdown(TestTimeout);
        await shutdown.Should().ThrowAsync<InvalidOperationException>();
        consumer.CloseCallCount.Should().Be(1);
        consumer.DisposeCallCount.Should().Be(1);
    }

    [Fact]
    public async Task KafkaReceiver_WhenPollReturnsAtHardCapacity_ShouldRewindWithoutGrowingBuffer()
    {
        var harness = CreateHarness(capacity: 2, highWatermark: 2, lowWatermark: 1);
        await harness.Receiver.Initialize(TestTimeout);

        try
        {
            harness.Consumer.AddRecord(0);
            harness.Consumer.AddRecord(1);
            _ = await harness.Consumer.ReadPauseAsync();

            harness.Consumer.AddRecord(2);
            (await harness.Consumer.ReadSeekAsync()).Should().Be(2);

            harness.Receiver.BufferedMessageCount.Should().Be(2);
        }
        finally
        {
            await harness.Receiver.Shutdown(TestTimeout);
        }
    }

    [Fact]
    public async Task KafkaReceiver_AfterResume_ShouldNotCommitPastUnacknowledgedOffsetHole()
    {
        var harness = CreateHarness(capacity: 3, highWatermark: 2, lowWatermark: 1);
        await harness.Receiver.Initialize(TestTimeout);

        try
        {
            harness.Consumer.AddRecord(0);
            harness.Consumer.AddRecord(1);
            _ = await harness.Consumer.ReadPauseAsync();
            var firstBatches = await ReadKafkaBatchesByOffsetAsync(harness, [0, 1]);
            var offsetZero = firstBatches[0];
            var offsetOne = firstBatches[1];

            await harness.Receiver.MessagesDeliveredAsync([offsetOne]);
            var consumeCountAfterFirstAck = harness.Consumer.ConsumeCount;
            await harness.Consumer.AwaitConsumeCountAsync(consumeCountAfterFirstAck + 2);
            harness.Consumer.CommittedOffsets.Should().BeEmpty(
                "offset 0 is still below the contiguous ACK watermark");

            harness.Consumer.AddRecord(2);
            await harness.Consumer.AwaitReturnedOffsetAsync(2);
            await harness.Consumer.AwaitConsumeCountAsync(harness.Consumer.ConsumeCount + 1);
            var offsetTwo = (await harness.Receiver.GetQueueMessagesAsync(1))
                .OfType<KafkaProviderBatchContainer>()
                .Single();
            await harness.Receiver.MessagesDeliveredAsync([offsetTwo]);
            var consumeCountAfterSecondAck = harness.Consumer.ConsumeCount;
            await harness.Consumer.AwaitConsumeCountAsync(consumeCountAfterSecondAck + 2);
            harness.Consumer.CommittedOffsets.Should().BeEmpty(
                "resume must not skip the unacknowledged offset 0 hole");

            await harness.Receiver.MessagesDeliveredAsync([offsetZero]);
            var committedOffset = await harness.Consumer.ReadCommitAsync();
            committedOffset.Should().Be(3,
                "the commit cursor advances once, through the now-contiguous offsets 0, 1 and 2");
            harness.Consumer.CommittedOffsets.Should().Equal(3);
        }
        finally
        {
            await harness.Receiver.Shutdown(TestTimeout);
        }
    }

    [Fact]
    public async Task KafkaReceiver_WhenCommitFailsOnce_ShouldRetryThePreservedContiguousAckWatermark()
    {
        var harness = CreateHarness(capacity: 4, highWatermark: 3, lowWatermark: 1);
        await harness.Receiver.Initialize(TestTimeout);

        try
        {
            harness.Consumer.AddRecord(0);
            harness.Consumer.AddRecord(1);
            await harness.Consumer.AwaitReturnedOffsetAsync(1);
            var batches = await ReadKafkaBatchesByOffsetAsync(harness, [0, 1]);

            await harness.Receiver.MessagesDeliveredAsync([batches[1]]);
            harness.Consumer.FailNextCommit(new KafkaException(
                new Error(Confluent.Kafka.ErrorCode.Local_TimedOut, "transient commit failure")));
            await harness.Receiver.MessagesDeliveredAsync([batches[0]]);

            var committedOffset = await harness.Consumer.ReadCommitAsync();
            committedOffset.Should().Be(2);
            harness.Consumer.CommitAttemptCount.Should().Be(2,
                "the first broker failure must preserve the candidate for the next owner-loop attempt");
            harness.Consumer.CommittedOffsets.Should().Equal(2);
        }
        finally
        {
            await harness.Receiver.Shutdown(TestTimeout);
        }
    }

    [Fact]
    public async Task KafkaReceiver_WhenOwnerLoopFaults_ShouldSurfaceTheFaultThroughReceiverApis()
    {
        var harness = CreateHarness(capacity: 3, highWatermark: 2, lowWatermark: 1);
        await harness.Receiver.Initialize(TestTimeout);
        var resumeFailure = new InvalidOperationException("resume failed on owner loop");

        try
        {
            harness.Consumer.FailNextResume(resumeFailure);
            harness.Consumer.AddRecord(0);
            harness.Consumer.AddRecord(1);
            _ = await harness.Consumer.ReadPauseAsync();
            var deliveredBatch = await harness.Receiver.GetQueueMessagesAsync(1);
            await harness.Consumer.AwaitDisposedAsync();

            Func<Task> read = async () => _ = await harness.Receiver.GetQueueMessagesAsync(1);
            var readFailure = await read.Should().ThrowAsync<InvalidOperationException>();
            readFailure.Which.InnerException.Should().BeSameAs(resumeFailure);

            Func<Task> acknowledge = () => harness.Receiver.MessagesDeliveredAsync(deliveredBatch);
            var acknowledgementFailure = await acknowledge.Should().ThrowAsync<InvalidOperationException>();
            acknowledgementFailure.Which.Should().BeSameAs(readFailure.Which);

            Func<Task> shutdown = () => harness.Receiver.Shutdown(TestTimeout);
            var shutdownFailure = await shutdown.Should().ThrowAsync<InvalidOperationException>();
            shutdownFailure.Which.Should().BeSameAs(readFailure.Which);
        }
        finally
        {
            try
            {
                await harness.Receiver.Shutdown(TestTimeout);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    [Fact]
    public async Task KafkaReceiver_WhenConsumeErrorIsNonFatal_ShouldRetryAndContinueDelivery()
    {
        var harness = CreateHarness(capacity: 3, highWatermark: 2, lowWatermark: 1);
        await harness.Receiver.Initialize(TestTimeout);
        var consumeFailure = CreateConsumeException(isFatal: false);

        try
        {
            harness.Consumer.FailNextConsume(consumeFailure);
            harness.Consumer.AddRecord(0);
            harness.Consumer.AddRecord(1);

            _ = await harness.Consumer.ReadPauseAsync();
            var delivered = await harness.Receiver.GetQueueMessagesAsync(1);

            delivered.Should().ContainSingle();
            harness.Consumer.ConsumeCount.Should().BeGreaterThanOrEqualTo(3,
                "a non-fatal consume error must leave the owner loop available for retry");
            harness.Consumer.DisposeCallCount.Should().Be(0);
        }
        finally
        {
            await harness.Receiver.Shutdown(TestTimeout);
        }
    }

    [Fact]
    public async Task KafkaReceiver_WhenConsumeErrorIsFatal_ShouldSurfaceFaultAndRebuildWithNewConsumer()
    {
        var options = CreateOptions(capacity: 3, highWatermark: 2, lowWatermark: 1);
        var mapper = new KafkaQueuePartitionMapper("backpressure-provider", 2);
        var queueId = mapper.GetAllQueues().First();
        var topicPartition = new TopicPartition(options.TopicName, new Partition(mapper.GetPartitionId(queueId)));
        var firstConsumer = CreateDeterministicConsumer(options);
        var secondConsumer = CreateDeterministicConsumer(options);
        var consumers = new ConcurrentQueue<IKafkaReceiverConsumer>(
            new IKafkaReceiverConsumer[] { firstConsumer, secondConsumer });
        var receiver = new KafkaProviderQueueAdapterReceiver(
            queueId,
            "backpressure-provider",
            options,
            mapper,
            "aevatar.events",
            _ => Task.CompletedTask,
            () => consumers.TryDequeue(out var consumer)
                ? consumer
                : throw new InvalidOperationException("No deterministic consumer remains for initialization."));
        var consumeFailure = CreateConsumeException(isFatal: true);

        await receiver.Initialize(TestTimeout);
        firstConsumer.FailNextConsume(consumeFailure);
        await firstConsumer.AwaitDisposedAsync();

        Func<Task> read = async () => _ = await receiver.GetQueueMessagesAsync(1);
        var readFailure = await read.Should().ThrowAsync<InvalidOperationException>();
        readFailure.Which.InnerException.Should().BeSameAs(consumeFailure);

        Func<Task> acknowledge = () => receiver.MessagesDeliveredAsync([]);
        var acknowledgementFailure = await acknowledge.Should().ThrowAsync<InvalidOperationException>();
        acknowledgementFailure.Which.Should().BeSameAs(readFailure.Which);

        var firstShutdown = receiver.Shutdown(TestTimeout);
        var repeatedShutdown = receiver.Shutdown(TestTimeout);
        repeatedShutdown.Should().BeSameAs(firstShutdown);

        Func<Task> awaitFirstShutdown = async () => await firstShutdown;
        var firstShutdownFailure = await awaitFirstShutdown.Should().ThrowAsync<InvalidOperationException>();
        firstShutdownFailure.Which.Should().BeSameAs(readFailure.Which);
        Func<Task> awaitRepeatedShutdown = async () => await repeatedShutdown;
        var repeatedShutdownFailure = await awaitRepeatedShutdown.Should().ThrowAsync<InvalidOperationException>();
        repeatedShutdownFailure.Which.Should().BeSameAs(readFailure.Which);
        Func<Task> readAfterShutdown = async () => _ = await receiver.GetQueueMessagesAsync(1);
        var retainedLifecycleFailure = await readAfterShutdown.Should().ThrowAsync<InvalidOperationException>();
        retainedLifecycleFailure.Which.Should().BeSameAs(readFailure.Which,
            "shutdown cleanup must not clear a lifecycle fault before explicit reinitialization");

        await receiver.Initialize(TestTimeout);
        secondConsumer.AssignedPartition.Should().Be(topicPartition);
        secondConsumer.AddRecord(0);
        secondConsumer.AddRecord(1);
        _ = await secondConsumer.ReadPauseAsync();
        (await receiver.GetQueueMessagesAsync(1)).Should().ContainSingle(
            "explicit reinitialization must replace the failed owner loop and clear its lifecycle fault");

        var rebuiltLifecycleShutdown = receiver.Shutdown(TestTimeout);
        rebuiltLifecycleShutdown.Should().NotBeSameAs(firstShutdown);
        await rebuiltLifecycleShutdown;
    }

    [Fact]
    [Trait("Category", "PerformanceDiagnostic")]
    public async Task KafkaReceiverShape_ControlledMeasurement_ShouldReportNonGatingDiagnostics()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(PerformanceDiagnosticsEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Controlled performance diagnostics were not requested. Set {0}=1 and run this test explicitly.",
                PerformanceDiagnosticsEnvironmentVariable);
            return;
        }

        const int operationCount = 250_000;
        const int capacity = operationCount;
        const int warmupCount = 3;
        const int sampleCount = 9;
        var record = CreateBenchmarkRecord();
        var diagnosticWatchdog = ResolvePerformanceDiagnosticWatchdog();
        output.WriteLine("controlled measurement watchdog={0}", diagnosticWatchdog);

        for (var warmup = 0; warmup < warmupCount; warmup++)
        {
            _ = await MeasureReceiverShapeAsync(
                record, 25_000, capacity, useBoundedBuffer: false, diagnosticWatchdog);
            _ = await MeasureReceiverShapeAsync(
                record, 25_000, capacity, useBoundedBuffer: true, diagnosticWatchdog);
        }

        var baselineSamples = new List<ReceiverShapeMeasurement>(sampleCount);
        var boundedSamples = new List<ReceiverShapeMeasurement>(sampleCount);
        for (var sample = 0; sample < sampleCount; sample++)
        {
            // Alternate order so JIT, clock scaling and background load do not favor one path.
            if (sample % 2 == 0)
            {
                baselineSamples.Add(await MeasureReceiverShapeAsync(
                    record, operationCount, capacity, useBoundedBuffer: false, diagnosticWatchdog));
                boundedSamples.Add(await MeasureReceiverShapeAsync(
                    record, operationCount, capacity, useBoundedBuffer: true, diagnosticWatchdog));
            }
            else
            {
                boundedSamples.Add(await MeasureReceiverShapeAsync(
                    record, operationCount, capacity, useBoundedBuffer: true, diagnosticWatchdog));
                baselineSamples.Add(await MeasureReceiverShapeAsync(
                    record, operationCount, capacity, useBoundedBuffer: false, diagnosticWatchdog));
            }

            var baselineSample = baselineSamples[^1];
            var boundedSample = boundedSamples[^1];
            AssertReceiverShapeSemantics(baselineSample, boundedSample, operationCount);
            output.WriteLine(
                "sample {0}: old-unbounded={1:N0} msg/s, {2:N2} CPU us/msg, {3:N1} B/msg; " +
                "new-bounded={4:N0} msg/s, {5:N2} CPU us/msg, {6:N1} B/msg; ratio={7:P1}",
                sample + 1,
                baselineSample.Throughput,
                baselineSample.CpuMicrosecondsPerMessage,
                baselineSample.AllocatedBytesPerMessage,
                boundedSample.Throughput,
                boundedSample.CpuMicrosecondsPerMessage,
                boundedSample.AllocatedBytesPerMessage,
                boundedSample.Throughput / baselineSample.Throughput);
        }

        var baseline = Median(baselineSamples);
        var bounded = Median(boundedSamples);
        var throughputRatio = bounded.Throughput / baseline.Throughput;

        output.WriteLine(
            "median after {0} warmups, {1} x {2:N0}: old-unbounded={3:N0} msg/s, " +
            "{4:N2} CPU us/msg, {5:N1} B/msg; new-bounded={6:N0} msg/s, " +
            "{7:N2} CPU us/msg, {8:N1} B/msg; diagnostic ratio={9:P1} (no wall-clock gate)",
            warmupCount,
            sampleCount,
            operationCount,
            baseline.Throughput,
            baseline.CpuMicrosecondsPerMessage,
            baseline.AllocatedBytesPerMessage,
            bounded.Throughput,
            bounded.CpuMicrosecondsPerMessage,
            bounded.AllocatedBytesPerMessage,
            throughputRatio);

        const int transferCount = 1_000_000;
        var message = Substitute.For<IBatchContainer>();
        var unboundedTransfer = await MeasureConcurrentUnboundedQueueAsync(
            message, transferCount, diagnosticWatchdog);
        var boundedTransfer = await MeasureConcurrentBoundedBufferAsync(
            message, transferCount, transferCount, diagnosticWatchdog);
        output.WriteLine(
            "pure-buffer diagnostic: old-unbounded={0:N0} pairs/s; new-bounded={1:N0} pairs/s (no wall-clock gate)",
            transferCount / unboundedTransfer.TotalSeconds,
            transferCount / boundedTransfer.TotalSeconds);

        const int retentionCapacity = 1024;
        int[] backlogDepths = [256, 1024, 4096, 16_384, 32_768];
        foreach (var backlogDepth in backlogDepths)
        {
            var baselineRetention = MeasureUnboundedQueueRetention(message, backlogDepth);
            var boundedRetention = MeasureBoundedBufferRetention(message, backlogDepth, retentionCapacity);
            baselineRetention.RetainedMessages.Should().Be(backlogDepth);
            boundedRetention.RetainedMessages.Should().Be(Math.Min(backlogDepth, retentionCapacity));
            output.WriteLine(
                "backlog={0:N0}: old-unbounded retained={1:N0}, allocated={2:N0} B; " +
                "new-bounded retained={3:N0}, allocated={4:N0} B",
                backlogDepth,
                baselineRetention.RetainedMessages,
                baselineRetention.AllocatedBytes,
                boundedRetention.RetainedMessages,
                boundedRetention.AllocatedBytes);
        }
    }

    private static void AssertReceiverShapeSemantics(
        ReceiverShapeMeasurement baseline,
        ReceiverShapeMeasurement bounded,
        int operationCount)
    {
        var expectedChecksum = (long)operationCount * (operationCount - 1) / 2;
        baseline.RejectedWrites.Should().Be(0);
        bounded.RejectedWrites.Should().Be(0,
            "the controlled measurement must remain unsaturated");
        baseline.Checksum.Should().Be(expectedChecksum);
        bounded.Checksum.Should().Be(expectedChecksum,
            "both paths must pull the same sequence of Kafka offsets");
    }

    private static TimeSpan ResolvePerformanceDiagnosticWatchdog()
    {
        var configuredSeconds = Environment.GetEnvironmentVariable(
            PerformanceDiagnosticWatchdogSecondsEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredSeconds))
            return DefaultPerformanceDiagnosticWatchdog;

        if (!int.TryParse(configuredSeconds, out var seconds) || seconds <= 0)
        {
            throw new InvalidOperationException(
                $"{PerformanceDiagnosticWatchdogSecondsEnvironmentVariable} must be a positive integer.");
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private static ReceiverHarness CreateHarness(int capacity, int highWatermark, int lowWatermark)
    {
        var options = CreateOptions(capacity, highWatermark, lowWatermark);
        var mapper = new KafkaQueuePartitionMapper("backpressure-provider", 2);
        var queueId = mapper.GetAllQueues().First();
        var partitionId = mapper.GetPartitionId(queueId);
        var topicPartition = new TopicPartition(options.TopicName, new Partition(partitionId));
        var consumer = CreateDeterministicConsumer(options);
        var receiver = new KafkaProviderQueueAdapterReceiver(
            queueId,
            "backpressure-provider",
            options,
            mapper,
            "aevatar.events",
            _ => Task.CompletedTask,
            () => consumer);

        return new ReceiverHarness(receiver, consumer, options, topicPartition);
    }

    private static async Task<Dictionary<long, KafkaProviderBatchContainer>> ReadKafkaBatchesByOffsetAsync(
        ReceiverHarness harness,
        IReadOnlyCollection<long> expectedOffsets)
    {
        var batchesByOffset = new Dictionary<long, KafkaProviderBatchContainer>();
        while (expectedOffsets.Any(offset => !batchesByOffset.ContainsKey(offset)))
        {
            var batches = await harness.Receiver.GetQueueMessagesAsync(1);
            foreach (var batch in batches.OfType<KafkaProviderBatchContainer>())
            {
                if (expectedOffsets.Contains(batch.KafkaOffset))
                    batchesByOffset[batch.KafkaOffset] = batch;
            }
        }

        return batchesByOffset;
    }

    private static DeterministicKafkaReceiverConsumer CreateDeterministicConsumer(
        KafkaProviderTransportOptions options) =>
        new(options.TopicName, "aevatar.events", "actor-alpha");

    private static KafkaProviderTransportOptions CreateOptions(
        int capacity,
        int highWatermark,
        int lowWatermark) =>
        new()
        {
            TopicName = $"backpressure-{Guid.NewGuid():N}",
            ConsumerGroup = $"backpressure-{Guid.NewGuid():N}",
            TopicPartitionCount = 2,
            ReceiverBufferCapacity = capacity,
            ReceiverBufferHighWatermark = highWatermark,
            ReceiverBufferLowWatermark = lowWatermark,
        };

    private static ConsumeException CreateConsumeException(bool isFatal) =>
        new(
            new ConsumeResult<byte[], byte[]>(),
            new Error(
                isFatal ? Confluent.Kafka.ErrorCode.Local_Fatal : Confluent.Kafka.ErrorCode.Local_TimedOut,
                isFatal ? "fatal consume failure" : "transient consume failure",
                isFatal));

    private static async Task<TimeSpan> MeasureConcurrentUnboundedQueueAsync(
        IBatchContainer message,
        int operationCount,
        TimeSpan diagnosticWatchdog)
    {
        var queue = new ConcurrentQueue<IBatchContainer>();
        return await MeasureConcurrentTransferAsync(
            operationCount,
            () =>
            {
                queue.Enqueue(message);
                return true;
            },
            () => queue.TryDequeue(out _),
            diagnosticWatchdog);
    }

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref maximum);
            if (candidate <= current || Interlocked.CompareExchange(ref maximum, candidate, current) == current)
                return;
        }
    }

    private static async Task<TimeSpan> MeasureConcurrentBoundedBufferAsync(
        IBatchContainer message,
        int operationCount,
        int capacity,
        TimeSpan diagnosticWatchdog)
    {
        var buffer = new KafkaReceiverMessageBuffer(capacity);
        return await MeasureConcurrentTransferAsync(
            operationCount,
            () => buffer.TryWrite(message),
            () => buffer.TryRead(out _),
            diagnosticWatchdog);
    }

    private static async Task<TimeSpan> MeasureConcurrentTransferAsync(
        int operationCount,
        Func<bool> tryProduce,
        Func<bool> tryConsume,
        TimeSpan diagnosticWatchdog)
    {
        using var start = new ManualResetEventSlim();
        var producer = Task.Factory.StartNew(
            () =>
            {
                start.Wait();
                for (var produced = 0; produced < operationCount;)
                {
                    if (tryProduce())
                        produced++;
                    else
                        Thread.Yield();
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        var consumer = Task.Factory.StartNew(
            () =>
            {
                start.Wait();
                for (var consumed = 0; consumed < operationCount;)
                {
                    if (tryConsume())
                        consumed++;
                    else
                        Thread.Yield();
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        var stopwatch = Stopwatch.StartNew();
        start.Set();
        await Task.WhenAll(producer, consumer).WaitAsync(diagnosticWatchdog);
        stopwatch.Stop();
        return stopwatch.Elapsed;
    }

    private static BufferRetentionMeasurement MeasureUnboundedQueueRetention(
        IBatchContainer message,
        int overloadCount)
    {
        var queue = new ConcurrentQueue<IBatchContainer>();
        var allocatedBeforeOverload = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < overloadCount; i++)
            queue.Enqueue(message);
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBeforeOverload;

        return new BufferRetentionMeasurement(queue.Count, allocatedBytes);
    }

    private static BufferRetentionMeasurement MeasureBoundedBufferRetention(
        IBatchContainer message,
        int overloadCount,
        int capacity)
    {
        var buffer = new KafkaReceiverMessageBuffer(capacity);
        var allocatedBeforeOverload = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < overloadCount; i++)
            buffer.TryWrite(message);
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBeforeOverload;

        return new BufferRetentionMeasurement(buffer.Depth, allocatedBytes);
    }

    private static ConsumeResult<Ignore, byte[]> CreateBenchmarkRecord()
    {
        const string streamNamespace = "aevatar.events";
        const string streamId = "actor-benchmark";
        return new ConsumeResult<Ignore, byte[]>
        {
            Topic = "receiver-shape-benchmark",
            Partition = new Partition(0),
            Offset = new Offset(0),
            Message = new Message<Ignore, byte[]>
            {
                Headers =
                [
                    new Header(
                        KafkaProviderHeaderConstants.StreamNamespace,
                        Encoding.UTF8.GetBytes(streamNamespace)),
                    new Header(
                        KafkaProviderHeaderConstants.StreamId,
                        Encoding.UTF8.GetBytes(streamId)),
                ],
                Value = new EventEnvelope
                {
                    Id = "envelope-benchmark",
                    Payload = Any.Pack(new StringValue { Value = "receiver-shape" }),
                    Route = EnvelopeRouteSemantics.CreateDirect("publisher", streamId),
                }.ToByteArray(),
            },
        };
    }

    private static async Task<ReceiverShapeMeasurement> MeasureReceiverShapeAsync(
        ConsumeResult<Ignore, byte[]> record,
        int operationCount,
        int capacity,
        bool useBoundedBuffer,
        TimeSpan diagnosticWatchdog)
    {
        var queue = useBoundedBuffer ? null : new ConcurrentQueue<IBatchContainer>();
        var buffer = useBoundedBuffer ? new KafkaReceiverMessageBuffer(capacity) : null;
        var consumer = new ReceiverShapeKafkaConsumer(record);
        using var start = new ManualResetEventSlim();
        long checksum = 0;
        var rejectedWrites = 0;

        var owner = Task.Factory.StartNew(
            () =>
            {
                start.Wait();
                for (var produced = 0; produced < operationCount;)
                {
                    var consumeResult = consumer.Consume();
                    var streamNamespace = Encoding.UTF8.GetString(
                        consumeResult.Message.Headers
                            .Last(header => header.Key == KafkaProviderHeaderConstants.StreamNamespace)
                            .GetValueBytes());
                    var streamId = Encoding.UTF8.GetString(
                        consumeResult.Message.Headers
                            .Last(header => header.Key == KafkaProviderHeaderConstants.StreamId)
                            .GetValueBytes());
                    var envelope = EventEnvelope.Parser.ParseFrom(consumeResult.Message.Value);
                    var message = new KafkaProviderBatchContainer(
                        StreamId.Create(streamNamespace, streamId),
                        envelope,
                        new EventSequenceTokenV2(produced + 1),
                        consumeResult.Offset.Value);

                    var accepted = buffer?.TryWrite(message) ?? Enqueue(queue!, message);
                    if (accepted)
                        produced++;
                    else
                    {
                        rejectedWrites++;
                        Thread.Yield();
                    }
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        var puller = Task.Factory.StartNew(
            () =>
            {
                start.Wait();
                for (var consumed = 0; consumed < operationCount;)
                {
                    IBatchContainer? message;
                    var read = buffer?.TryRead(out message) ?? queue!.TryDequeue(out message);
                    if (!read)
                    {
                        Thread.Yield();
                        continue;
                    }

                    checksum += ((KafkaProviderBatchContainer)message!).KafkaOffset;
                    consumed++;
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var stopwatch = Stopwatch.StartNew();
        start.Set();
        await Task.WhenAll(owner, puller).WaitAsync(diagnosticWatchdog);
        stopwatch.Stop();
        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var cpuElapsed = process.TotalProcessorTime - cpuBefore;

        return new ReceiverShapeMeasurement(
            operationCount,
            stopwatch.Elapsed,
            cpuElapsed,
            allocatedBytes,
            checksum,
            rejectedWrites);
    }

    private static bool Enqueue(ConcurrentQueue<IBatchContainer> queue, IBatchContainer message)
    {
        queue.Enqueue(message);
        return true;
    }

    private static ReceiverShapeMeasurement Median(IReadOnlyCollection<ReceiverShapeMeasurement> samples) =>
        samples.OrderBy(sample => sample.Elapsed).ElementAt(samples.Count / 2);

    private sealed record ReceiverHarness(
        KafkaProviderQueueAdapterReceiver Receiver,
        DeterministicKafkaReceiverConsumer Consumer,
        KafkaProviderTransportOptions Options,
        TopicPartition TopicPartition);

    private sealed record BufferRetentionMeasurement(
        int RetainedMessages,
        long AllocatedBytes);

    private sealed record ReceiverShapeMeasurement(
        int MessageCount,
        TimeSpan Elapsed,
        TimeSpan CpuElapsed,
        long AllocatedBytes,
        long Checksum,
        int RejectedWrites)
    {
        public double Throughput => MessageCount / Elapsed.TotalSeconds;

        public double CpuMicrosecondsPerMessage =>
            CpuElapsed.TotalMicroseconds / MessageCount;

        public double AllocatedBytesPerMessage =>
            (double)AllocatedBytes / MessageCount;
    }

    private sealed class ReceiverShapeKafkaConsumer(ConsumeResult<Ignore, byte[]> record)
    {
        private long _offset = -1;

        public ConsumeResult<Ignore, byte[]> Consume()
        {
            record.Offset = new Offset(++_offset);
            return record;
        }
    }

    private sealed class GatedOwnerLoopStarter
    {
        private readonly TaskCompletionSource<Action> _scheduled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Action? _scheduledAction;

        public Task Start(Action action)
        {
            Interlocked.CompareExchange(ref _scheduledAction, action, null).Should().BeNull();
            _scheduled.TrySetResult(action).Should().BeTrue();
            return _completion.Task;
        }

        public Task AwaitScheduledAsync() => _scheduled.Task.WaitAsync(TestTimeout);

        public void RunScheduled()
        {
            try
            {
                (Volatile.Read(ref _scheduledAction) ??
                 throw new InvalidOperationException("No owner-loop action was scheduled."))();
                _completion.TrySetResult();
            }
            catch (Exception ex)
            {
                _completion.TrySetException(ex);
                throw;
            }
        }
    }

    private sealed class DeterministicKafkaReceiverConsumer(
        string topicName,
        string streamNamespace,
        string streamId)
        : IKafkaReceiverConsumer
    {
        private readonly ConcurrentQueue<Func<ConsumeResult<Ignore, byte[]>?>> _consumeSteps = new();
        private readonly SemaphoreSlim _consumeStepsAvailable = new(0);
        private readonly Channel<int> _consumeCalls = Channel.CreateUnbounded<int>();
        private readonly Channel<long> _returnedOffsets = Channel.CreateUnbounded<long>();
        private readonly Channel<TopicPartition[]> _pauseCalls = Channel.CreateUnbounded<TopicPartition[]>();
        private readonly Channel<TopicPartition[]> _resumeCalls = Channel.CreateUnbounded<TopicPartition[]>();
        private readonly Channel<long> _commitCalls = Channel.CreateUnbounded<long>();
        private readonly Channel<long> _seekCalls = Channel.CreateUnbounded<long>();
        private readonly Lock _stateLock = new();
        private readonly HashSet<int> _consumerOperationThreadIds = [];
        private readonly List<long> _committedOffsets = [];
        private readonly List<long> _seekOffsets = [];
        private TopicPartition? _assignedPartition;
        private int _consumeCount;
        private int _pauseCallCount;
        private int _resumeCallCount;
        private int _commitAttemptCount;
        private int _closeCallCount;
        private int _disposeCallCount;
        private Exception? _nextAssignFailure;
        private Exception? _nextResumeFailure;
        private KafkaException? _nextCommitFailure;
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TopicPartition? AssignedPartition
        {
            get
            {
                lock (_stateLock)
                    return _assignedPartition;
            }
        }

        public int ConsumeCount => Volatile.Read(ref _consumeCount);

        public int PauseCallCount => Volatile.Read(ref _pauseCallCount);

        public int ResumeCallCount => Volatile.Read(ref _resumeCallCount);

        public int CommitAttemptCount => Volatile.Read(ref _commitAttemptCount);

        public int CloseCallCount => Volatile.Read(ref _closeCallCount);

        public int DisposeCallCount => Volatile.Read(ref _disposeCallCount);

        public IReadOnlyCollection<int> ConsumerOperationThreadIds
        {
            get
            {
                lock (_stateLock)
                    return [.. _consumerOperationThreadIds];
            }
        }

        public IReadOnlyList<long> CommittedOffsets
        {
            get
            {
                lock (_stateLock)
                    return [.. _committedOffsets];
            }
        }

        public void Assign(TopicPartitionOffset partition)
        {
            RecordOwnerThread();
            if (Interlocked.Exchange(ref _nextAssignFailure, null) is { } failure)
                throw failure;

            lock (_stateLock)
                _assignedPartition = partition.TopicPartition;
        }

        public ConsumeResult<Ignore, byte[]>? Consume(TimeSpan timeout)
        {
            RecordOwnerThread();
            var consumeCount = Interlocked.Increment(ref _consumeCount);
            _consumeCalls.Writer.TryWrite(consumeCount);
            if (!_consumeStepsAvailable.Wait(timeout))
                return null;

            if (!_consumeSteps.TryDequeue(out var consumeStep))
                throw new InvalidOperationException("A signaled deterministic consume step was missing.");

            var result = consumeStep();
            if (result?.Message != null)
                _returnedOffsets.Writer.TryWrite(result.Offset.Value);
            return result;
        }

        public void Pause(IReadOnlyCollection<TopicPartition> partitions)
        {
            RecordOwnerThread();
            EnsureFixedPartition(partitions);
            Interlocked.Increment(ref _pauseCallCount);
            _pauseCalls.Writer.TryWrite([.. partitions]);
        }

        public void Resume(IReadOnlyCollection<TopicPartition> partitions)
        {
            RecordOwnerThread();
            EnsureFixedPartition(partitions);
            if (Interlocked.Exchange(ref _nextResumeFailure, null) is { } failure)
                throw failure;

            Interlocked.Increment(ref _resumeCallCount);
            _resumeCalls.Writer.TryWrite([.. partitions]);
        }

        public void Commit(IReadOnlyCollection<TopicPartitionOffset> offsets)
        {
            RecordOwnerThread();
            EnsureFixedPartition(offsets.Select(offset => offset.TopicPartition));
            Interlocked.Increment(ref _commitAttemptCount);
            if (Interlocked.Exchange(ref _nextCommitFailure, null) is { } failure)
                throw failure;

            foreach (var offset in offsets)
            {
                lock (_stateLock)
                    _committedOffsets.Add(offset.Offset.Value);
                _commitCalls.Writer.TryWrite(offset.Offset.Value);
            }
        }

        public void Seek(TopicPartitionOffset offset)
        {
            RecordOwnerThread();
            EnsureFixedPartition([offset.TopicPartition]);
            lock (_stateLock)
                _seekOffsets.Add(offset.Offset.Value);
            _seekCalls.Writer.TryWrite(offset.Offset.Value);
        }

        public void Close()
        {
            RecordOwnerThread();
            Interlocked.Increment(ref _closeCallCount);
        }

        public void Dispose()
        {
            RecordOwnerThread();
            Interlocked.Increment(ref _disposeCallCount);
            _disposed.TrySetResult();
            _consumeStepsAvailable.Dispose();
        }

        public void AddRecord(long offset)
        {
            AddConsumeStep(() => CreateRecord(offset, GetAssignedPartition()));
        }

        public void FailNextConsume(ConsumeException failure)
        {
            ArgumentNullException.ThrowIfNull(failure);
            AddConsumeStep(() => throw failure);
        }

        public void FailNextAssign(Exception failure)
        {
            ArgumentNullException.ThrowIfNull(failure);
            Interlocked.Exchange(ref _nextAssignFailure, failure).Should().BeNull();
        }

        public void FailNextResume(Exception failure)
        {
            ArgumentNullException.ThrowIfNull(failure);
            Interlocked.Exchange(ref _nextResumeFailure, failure).Should().BeNull();
        }

        public void FailNextCommit(KafkaException failure)
        {
            ArgumentNullException.ThrowIfNull(failure);
            Interlocked.Exchange(ref _nextCommitFailure, failure).Should().BeNull();
        }

        private Partition GetAssignedPartition()
        {
            lock (_stateLock)
                return (_assignedPartition ??
                    throw new InvalidOperationException("The deterministic consumer has not been assigned.")).Partition;
        }

        private ConsumeResult<Ignore, byte[]> CreateRecord(long offset, Partition partition) =>
            new()
            {
                Topic = topicName,
                Partition = partition,
                Offset = new Offset(offset),
                Message = new Message<Ignore, byte[]>
                {
                    Headers =
                    [
                        new Header(
                            KafkaProviderHeaderConstants.StreamNamespace,
                            Encoding.UTF8.GetBytes(streamNamespace)),
                        new Header(
                            KafkaProviderHeaderConstants.StreamId,
                            Encoding.UTF8.GetBytes(streamId)),
                    ],
                    Value = new EventEnvelope
                    {
                        Id = $"envelope-{offset}",
                        Payload = Any.Pack(new StringValue { Value = offset.ToString() }),
                        Route = EnvelopeRouteSemantics.CreateDirect("publisher", streamId),
                    }.ToByteArray(),
                },
            };

        public async Task<TopicPartition[]> ReadPauseAsync() =>
            await _pauseCalls.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);

        public async Task<TopicPartition[]> ReadResumeAsync() =>
            await _resumeCalls.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);

        public async Task<long> ReadCommitAsync() =>
            await _commitCalls.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);

        public async Task<long> ReadSeekAsync() =>
            await _seekCalls.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);

        public Task AwaitDisposedAsync() => _disposed.Task.WaitAsync(TestTimeout);

        public async Task AwaitReturnedOffsetAsync(long expectedOffset)
        {
            while (true)
            {
                var offset = await _returnedOffsets.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);
                if (offset == expectedOffset)
                    return;
            }
        }

        public async Task AwaitConsumeCountAsync(int expectedCount)
        {
            while (ConsumeCount < expectedCount)
                _ = await _consumeCalls.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);
        }

        private void AddConsumeStep(Func<ConsumeResult<Ignore, byte[]>?> consumeStep)
        {
            _consumeSteps.Enqueue(consumeStep);
            _consumeStepsAvailable.Release();
        }

        private void EnsureFixedPartition(IEnumerable<TopicPartition> partitions)
        {
            lock (_stateLock)
            {
                partitions.Should().OnlyContain(partition => partition == _assignedPartition,
                    "the receiver lifecycle is fixed to the QueueId-mapped Kafka partition");
            }
        }

        private void RecordOwnerThread()
        {
            lock (_stateLock)
                _consumerOperationThreadIds.Add(Environment.CurrentManagedThreadId);
        }
    }
}
