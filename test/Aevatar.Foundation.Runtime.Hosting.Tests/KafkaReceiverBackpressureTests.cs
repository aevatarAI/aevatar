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
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

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
            "Assign, Assignment, Pause, Resume, Consume, Commit, Close and Dispose must share one owner thread");
    }

    [Fact]
    public async Task KafkaReceiver_BelowHighWatermark_ShouldNotReadAssignmentOnEveryPoll()
    {
        var harness = CreateHarness(capacity: 8, highWatermark: 6, lowWatermark: 3);
        await harness.Receiver.Initialize(TestTimeout);

        try
        {
            harness.Consumer.AddRecord(0);
            harness.Consumer.AddRecord(1);
            await harness.Consumer.AwaitReturnedOffsetAsync(1);
            await harness.Consumer.AwaitConsumeCountAsync(harness.Consumer.ConsumeCount + 3);

            harness.Consumer.AssignmentReadCount.Should().Be(1,
                "steady-state polls below the high watermark do not need assignment reconciliation");
        }
        finally
        {
            await harness.Receiver.Shutdown(TestTimeout);
        }
    }

    [Fact]
    public async Task KafkaReceiver_WhenAssignmentChangesWhilePaused_ShouldForgetRevokedAndPauseNewPartition()
    {
        var harness = CreateHarness(capacity: 3, highWatermark: 2, lowWatermark: 1);
        var replacementPartition = new TopicPartition(harness.Options.TopicName, new Partition(1));
        await harness.Receiver.Initialize(TestTimeout);

        try
        {
            harness.Consumer.AddRecord(0);
            harness.Consumer.AddRecord(1);
            (await harness.Consumer.ReadPauseAsync()).Should().Equal(harness.TopicPartition);

            harness.Consumer.ChangeAssignmentOnNextConsume(replacementPartition);
            (await harness.Consumer.ReadPauseAsync()).Should().Equal(replacementPartition);

            var drained = await harness.Receiver.GetQueueMessagesAsync(1);
            drained.Should().ContainSingle();
            (await harness.Consumer.ReadResumeAsync()).Should().Equal(replacementPartition);

            harness.Consumer.ResumedPartitions.Should().NotContain(harness.TopicPartition,
                "a revoked partition must be removed from the paused set before resume");
            harness.Consumer.ConsumeCount.Should().BeGreaterThan(2,
                "the owner loop must continue polling while its partition is paused");
        }
        finally
        {
            await harness.Receiver.Shutdown(TestTimeout);
        }
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
            var firstBatches = await harness.Receiver.GetQueueMessagesAsync(2);
            var offsetZero = firstBatches.OfType<KafkaProviderBatchContainer>().Single(x => x.KafkaOffset == 0);
            var offsetOne = firstBatches.OfType<KafkaProviderBatchContainer>().Single(x => x.KafkaOffset == 1);

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
    public async Task KafkaReceiverMessageBuffer_ShouldBoundRetentionAndRetainTransportHeadroom()
    {
        const int operationCount = 1_000_000;
        const int capacity = 1024;
        int[] backlogDepths = [256, 1024, 4096, 16_384, 32_768];
        var message = Substitute.For<IBatchContainer>();

        await MeasureConcurrentUnboundedQueueAsync(message, 10_000);
        await MeasureConcurrentBoundedBufferAsync(message, 10_000, 10_000);
        var baselineElapsed = await MeasureConcurrentUnboundedQueueAsync(message, operationCount);
        var boundedElapsed = await MeasureConcurrentBoundedBufferAsync(
            message,
            operationCount,
            operationCount);
        var baselineCurve = backlogDepths
            .Select(depth => MeasureUnboundedQueueRetention(message, depth))
            .ToArray();
        var boundedCurve = backlogDepths
            .Select(depth => MeasureBoundedBufferRetention(message, depth, capacity))
            .ToArray();
        var baseline = baselineCurve[^1];
        var bounded = boundedCurve[^1];

        bounded.RetainedMessages.Should().Be(capacity);
        baseline.RetainedMessages.Should().Be(backlogDepths[^1]);
        bounded.AllocatedBytes.Should().BeLessThan(baseline.AllocatedBytes,
            "the bounded queue allocates segments only up to its configured retention ceiling");
        (operationCount / boundedElapsed.TotalSeconds).Should().BeGreaterThan(
            500_000,
            "the owner-writer / Orleans-puller buffer must retain ample headroom above Kafka transport throughput");

        output.WriteLine(
            "old-unbounded: {0:N0} ops/s, {1:N0} retained, {2:N0} B allocated; " +
            "new-bounded: {3:N0} ops/s, {4:N0} retained, {5:N0} B allocated",
            operationCount / baselineElapsed.TotalSeconds,
            baseline.RetainedMessages,
            baseline.AllocatedBytes,
            operationCount / boundedElapsed.TotalSeconds,
            bounded.RetainedMessages,
            bounded.AllocatedBytes);

        for (var i = 0; i < backlogDepths.Length; i++)
        {
            baselineCurve[i].RetainedMessages.Should().Be(backlogDepths[i]);
            boundedCurve[i].RetainedMessages.Should().Be(Math.Min(backlogDepths[i], capacity));
            output.WriteLine(
                "backlog={0:N0}: old-unbounded retained={1:N0}, allocated={2:N0} B; " +
                "new-bounded retained={3:N0}, allocated={4:N0} B",
                backlogDepths[i],
                baselineCurve[i].RetainedMessages,
                baselineCurve[i].AllocatedBytes,
                boundedCurve[i].RetainedMessages,
                boundedCurve[i].AllocatedBytes);
        }
    }

    [Fact]
    public async Task KafkaReceiverShape_SteadyState_ShouldNotRegressMateriallyAgainstUnboundedQueue()
    {
        const int operationCount = 100_000;
        const int capacity = operationCount;
        const int sampleCount = 5;
        var record = CreateBenchmarkRecord();

        _ = await MeasureReceiverShapeAsync(record, 10_000, capacity, useBoundedBuffer: false);
        _ = await MeasureReceiverShapeAsync(record, 10_000, capacity, useBoundedBuffer: true);

        var baselineSamples = new List<ReceiverShapeMeasurement>(sampleCount);
        var boundedSamples = new List<ReceiverShapeMeasurement>(sampleCount);
        for (var sample = 0; sample < sampleCount; sample++)
        {
            // Alternate order so JIT, clock scaling and background load do not favor one path.
            if (sample % 2 == 0)
            {
                baselineSamples.Add(await MeasureReceiverShapeAsync(
                    record, operationCount, capacity, useBoundedBuffer: false));
                boundedSamples.Add(await MeasureReceiverShapeAsync(
                    record, operationCount, capacity, useBoundedBuffer: true));
            }
            else
            {
                boundedSamples.Add(await MeasureReceiverShapeAsync(
                    record, operationCount, capacity, useBoundedBuffer: true));
                baselineSamples.Add(await MeasureReceiverShapeAsync(
                    record, operationCount, capacity, useBoundedBuffer: false));
            }
        }

        var baseline = Median(baselineSamples);
        var bounded = Median(boundedSamples);
        var throughputRatio = bounded.Throughput / baseline.Throughput;

        baseline.RejectedWrites.Should().Be(0);
        bounded.RejectedWrites.Should().Be(0,
            "the receiver-shape comparison must measure unsaturated steady state, not backpressure recovery");
        throughputRatio.Should().BeGreaterThanOrEqualTo(
            0.80,
            "the bounded buffer must not cause a material regression in the receiver-shaped steady-state path");
        bounded.Checksum.Should().Be(baseline.Checksum,
            "both paths must pull the same sequence of Kafka offsets");

        output.WriteLine(
            "receiver-shape median of {0} x {1:N0}: " +
            "old-unbounded={2:N0} msg/s, {3:N2} CPU us/msg, {4:N1} B/msg; " +
            "new-bounded={5:N0} msg/s, {6:N2} CPU us/msg, {7:N1} B/msg; " +
            "throughput ratio={8:P1}",
            sampleCount,
            operationCount,
            baseline.Throughput,
            baseline.CpuMicrosecondsPerMessage,
            baseline.AllocatedBytesPerMessage,
            bounded.Throughput,
            bounded.CpuMicrosecondsPerMessage,
            bounded.AllocatedBytesPerMessage,
            throughputRatio);
    }

    private static ReceiverHarness CreateHarness(int capacity, int highWatermark, int lowWatermark)
    {
        var options = CreateOptions(capacity, highWatermark, lowWatermark);
        var mapper = new KafkaQueuePartitionMapper("backpressure-provider", 2);
        var queueId = mapper.GetAllQueues().First();
        var partitionId = mapper.GetPartitionId(queueId);
        var topicPartition = new TopicPartition(options.TopicName, new Partition(partitionId));
        var consumer = new DeterministicKafkaReceiverConsumer(
            options.TopicName,
            "aevatar.events",
            "actor-alpha");
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

    private static async Task<TimeSpan> MeasureConcurrentUnboundedQueueAsync(
        IBatchContainer message,
        int operationCount)
    {
        var queue = new ConcurrentQueue<IBatchContainer>();
        return await MeasureConcurrentTransferAsync(
            operationCount,
            () =>
            {
                queue.Enqueue(message);
                return true;
            },
            () => queue.TryDequeue(out _));
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
        int capacity)
    {
        var buffer = new KafkaReceiverMessageBuffer(capacity);
        return await MeasureConcurrentTransferAsync(
            operationCount,
            () => buffer.TryWrite(message),
            () => buffer.TryRead(out _));
    }

    private static async Task<TimeSpan> MeasureConcurrentTransferAsync(
        int operationCount,
        Func<bool> tryProduce,
        Func<bool> tryConsume)
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
        await Task.WhenAll(producer, consumer).WaitAsync(TestTimeout);
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
        bool useBoundedBuffer)
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
        await Task.WhenAll(owner, puller).WaitAsync(TestTimeout);
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
        private readonly List<TopicPartition> _assignment = [];
        private readonly List<TopicPartition> _resumedPartitions = [];
        private readonly List<long> _committedOffsets = [];
        private readonly List<long> _seekOffsets = [];
        private int _consumeCount;
        private int _assignmentReadCount;
        private int _pauseCallCount;
        private int _resumeCallCount;

        public IReadOnlyList<TopicPartition> Assignment
        {
            get
            {
                RecordOwnerThread();
                Interlocked.Increment(ref _assignmentReadCount);
                lock (_stateLock)
                    return [.. _assignment];
            }
        }

        public int ConsumeCount => Volatile.Read(ref _consumeCount);

        public int AssignmentReadCount => Volatile.Read(ref _assignmentReadCount);

        public int PauseCallCount => Volatile.Read(ref _pauseCallCount);

        public int ResumeCallCount => Volatile.Read(ref _resumeCallCount);

        public IReadOnlyCollection<int> ConsumerOperationThreadIds
        {
            get
            {
                lock (_stateLock)
                    return [.. _consumerOperationThreadIds];
            }
        }

        public IReadOnlyList<TopicPartition> ResumedPartitions
        {
            get
            {
                lock (_stateLock)
                    return [.. _resumedPartitions];
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
            lock (_stateLock)
            {
                _assignment.Clear();
                _assignment.Add(partition.TopicPartition);
            }
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
            EnsureCurrentlyAssigned(partitions);
            Interlocked.Increment(ref _pauseCallCount);
            _pauseCalls.Writer.TryWrite([.. partitions]);
        }

        public void Resume(IReadOnlyCollection<TopicPartition> partitions)
        {
            RecordOwnerThread();
            EnsureCurrentlyAssigned(partitions);
            Interlocked.Increment(ref _resumeCallCount);
            lock (_stateLock)
                _resumedPartitions.AddRange(partitions);
            _resumeCalls.Writer.TryWrite([.. partitions]);
        }

        public void Commit(IReadOnlyCollection<TopicPartitionOffset> offsets)
        {
            RecordOwnerThread();
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
            lock (_stateLock)
                _seekOffsets.Add(offset.Offset.Value);
            _seekCalls.Writer.TryWrite(offset.Offset.Value);
        }

        public void Close() => RecordOwnerThread();

        public void Dispose()
        {
            RecordOwnerThread();
            _consumeStepsAvailable.Dispose();
        }

        public void AddRecord(long offset)
        {
            AddConsumeStep(() => new ConsumeResult<Ignore, byte[]>
            {
                Topic = topicName,
                Partition = GetAssignedPartition(),
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
            });
        }

        private Partition GetAssignedPartition()
        {
            lock (_stateLock)
                return _assignment.Single().Partition;
        }

        public void ChangeAssignmentOnNextConsume(TopicPartition partition)
        {
            AddConsumeStep(() =>
            {
                lock (_stateLock)
                {
                    _assignment.Clear();
                    _assignment.Add(partition);
                }
                return null;
            });
        }

        public async Task<TopicPartition[]> ReadPauseAsync() =>
            await _pauseCalls.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);

        public async Task<TopicPartition[]> ReadResumeAsync() =>
            await _resumeCalls.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);

        public async Task<long> ReadCommitAsync() =>
            await _commitCalls.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);

        public async Task<long> ReadSeekAsync() =>
            await _seekCalls.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);

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

        private void EnsureCurrentlyAssigned(IEnumerable<TopicPartition> partitions)
        {
            lock (_stateLock)
            {
                partitions.Should().OnlyContain(partition => _assignment.Contains(partition),
                    "Pause/Resume must never target a revoked partition");
            }
        }

        private void RecordOwnerThread()
        {
            lock (_stateLock)
                _consumerOperationThreadIds.Add(Environment.CurrentManagedThreadId);
        }
    }
}
