using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Aevatar.GAgents.Channel.Protocol.Tests;

public sealed class DurableInboxSubscriberTests
{
    [Fact]
    public async Task OnNextAsync_WhenPipelineSucceeds_ReturnsToCaller()
    {
        // return→commit path: caller is free to commit the stream offset.
        var completed = new TaskCompletionSource<ChatActivity>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = BuildPipeline((ctx, next, ct) =>
        {
            completed.TrySetResult(ctx.Activity);
            return next();
        });

        await using var subscriber = BuildSubscriber(pipeline);
        subscriber.Start();

        var activity = CreateActivity("act-1");
        await subscriber.OnNextAsync(activity);

        var processed = await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        processed.Id.ShouldBe("act-1");
    }

    [Fact]
    public async Task OnNextInlineAsync_WhenPipelineThrows_PropagatesToCaller()
    {
        // throw→redeliver path: stream observer must receive the exception so the persistent
        // provider re-delivers instead of committing.
        var pipeline = BuildPipeline((ctx, next, ct) =>
            throw new InvalidOperationException("boom"));

        await using var subscriber = BuildSubscriber(pipeline);
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => subscriber.OnNextInlineAsync(CreateActivity("act-throw")));
        ex.Message.ShouldBe("boom");
    }

    [Fact]
    public async Task OnNextAsync_WhenBufferFull_ThrowsTimeoutExceptionForRedelivery()
    {
        // bounded channel saturation path: producer timeout elapses so the observer throws and
        // the provider re-delivers the blocked activity.
        var stall = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = BuildPipeline(async (ctx, next, ct) =>
        {
            await stall.Task; // Hold the first activity so the buffer fills.
            await next();
        });

        await using var subscriber = BuildSubscriber(pipeline, bufferCapacity: 1, producerTimeout: TimeSpan.FromMilliseconds(50));
        subscriber.Start();

        // Fill the single-slot buffer; worker reads it and awaits the stall.
        await subscriber.OnNextAsync(CreateActivity("act-1"));
        // Fill the single-slot buffer second slot which sits in the channel.
        await subscriber.OnNextAsync(CreateActivity("act-2"));
        // Third attempt must time out because the buffer is saturated and the worker is blocked.
        var ex = await Should.ThrowAsync<TimeoutException>(() => subscriber.OnNextAsync(CreateActivity("act-3")));
        ex.Message.ShouldContain("buffer full");

        stall.TrySetResult(true);
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
