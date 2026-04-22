using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Aevatar.GAgents.Channel.Protocol.Tests;

public sealed class DurableInboxSubscriberTests
{
    [Fact]
    public async Task OnNextAsync_WhenPipelineSucceeds_ReturnsAfterPipelineCompletes()
    {
        // return→commit path: OnNextAsync must not return until the pipeline has actually run.
        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = BuildPipeline(async (ctx, next, ct) =>
        {
            invoked.TrySetResult();
            await next();
        });

        await using var subscriber = BuildSubscriber(pipeline);
        subscriber.Start();

        await subscriber.OnNextAsync(CreateActivity("act-1"));

        // If OnNextAsync returned, the pipeline must have been invoked (completion handshake).
        invoked.Task.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    public async Task OnNextInlineAsync_WhenPipelineThrows_PropagatesToCaller()
    {
        // throw→redeliver path via the inline variant: stream observer must receive the exception
        // so the persistent provider re-delivers instead of committing.
        var pipeline = BuildPipeline((ctx, next, ct) =>
            throw new InvalidOperationException("boom"));

        await using var subscriber = BuildSubscriber(pipeline);
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => subscriber.OnNextInlineAsync(CreateActivity("act-throw")));
        ex.Message.ShouldBe("boom");
    }

    [Fact]
    public async Task OnNextAsync_WhenPipelineThrows_PropagatesThroughCompletionHandshake()
    {
        // throw→redeliver path via the buffered variant. With the per-item handshake, exceptions
        // surfaced by the worker must reach the observer through OnNextAsync's returned task, not
        // just the background worker task.
        var pipeline = BuildPipeline((ctx, next, ct) =>
            throw new InvalidOperationException("boom"));

        await using var subscriber = BuildSubscriber(pipeline);
        subscriber.Start();

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => subscriber.OnNextAsync(CreateActivity("act-throw")));
        ex.Message.ShouldBe("boom");
    }

    [Fact]
    public async Task OnNextAsync_WhenBufferFull_ThrowsTimeoutExceptionForRedelivery()
    {
        // bounded channel saturation path: worker is busy, buffer is at capacity, and a third
        // producer's write request times out so the observer throws and the provider re-delivers.
        var stall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = BuildPipeline(async (ctx, next, ct) =>
        {
            await stall.Task; // Hold the first activity so downstream back-pressures.
            await next();
        });

        await using var subscriber = BuildSubscriber(pipeline, bufferCapacity: 1, producerTimeout: TimeSpan.FromMilliseconds(50));
        subscriber.Start();

        // Fire-and-forget the first two sends because their completion handshake will block on the
        // stall; we only need back-pressure to build up behind the worker.
        var first = subscriber.OnNextAsync(CreateActivity("act-1"));
        var second = subscriber.OnNextAsync(CreateActivity("act-2"));

        var ex = await Should.ThrowAsync<TimeoutException>(() => subscriber.OnNextAsync(CreateActivity("act-3")));
        ex.Message.ShouldContain("buffer full");

        stall.TrySetResult();
        await first;
        await second;
    }

    [Fact]
    public async Task OnNextAsync_AutoStartsWorker_WhenStartWasNotCalled()
    {
        // Wiring OnNextAsync directly as a stream handler must not hang if the caller forgets to
        // call Start() — the subscriber auto-starts the worker on first delivery.
        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = BuildPipeline(async (ctx, next, ct) =>
        {
            invoked.TrySetResult();
            await next();
        });

        await using var subscriber = BuildSubscriber(pipeline);
        // Deliberately skip subscriber.Start().
        await subscriber.OnNextAsync(CreateActivity("act-autostart"));
        invoked.Task.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    public async Task OnNextAsync_WhenDisposedWithInFlightItems_SignalsRedelivery()
    {
        // If the subscriber is torn down while items are still buffered, the observer for those
        // items must surface an exception instead of hanging forever.
        var stall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = BuildPipeline(async (ctx, next, ct) =>
        {
            await stall.Task;
            await next();
        });

        var subscriber = BuildSubscriber(pipeline, bufferCapacity: 4);
        subscriber.Start();
        var pending = subscriber.OnNextAsync(CreateActivity("act-1"));
        await subscriber.DisposeAsync();

        await Should.ThrowAsync<Exception>(() => pending);
        stall.TrySetResult();
    }

    private static ChannelPipeline BuildPipeline(Func<ITurnContext, Func<Task>, CancellationToken, Task> handler)
    {
        var mw = new DelegateMiddleware(handler);
        return new MiddlewarePipelineBuilder()
            .Use(mw)
            .Build(new ServiceCollection().BuildServiceProvider());
    }

    private static DurableInboxSubscriber BuildSubscriber(
        ChannelPipeline pipeline,
        int bufferCapacity = DurableInboxSubscriber.DefaultBufferCapacity,
        TimeSpan? producerTimeout = null)
    {
        return new DurableInboxSubscriber(
            pipeline,
            new ServiceCollection().BuildServiceProvider(),
            activity => new MiddlewarePipelineTests.StubTurnContext(),
            NullLogger<DurableInboxSubscriber>.Instance,
            bufferCapacity,
            producerTimeout);
    }

    private static ChatActivity CreateActivity(string id) => new()
    {
        Id = id,
        Bot = new BotInstanceId { Value = "ops-bot" },
        ChannelId = new ChannelId { Value = "slack" },
        Conversation = new ConversationReference
        {
            Channel = new ChannelId { Value = "slack" },
            Bot = new BotInstanceId { Value = "ops-bot" },
            Scope = ConversationScope.Channel,
            CanonicalKey = "slack:team:C1",
        },
    };

    private sealed class DelegateMiddleware : IChannelMiddleware
    {
        private readonly Func<ITurnContext, Func<Task>, CancellationToken, Task> _invoke;

        public DelegateMiddleware(Func<ITurnContext, Func<Task>, CancellationToken, Task> invoke)
        {
            _invoke = invoke;
        }

        public Task InvokeAsync(ITurnContext context, Func<Task> next, CancellationToken ct) => _invoke(context, next, ct);
    }
}
