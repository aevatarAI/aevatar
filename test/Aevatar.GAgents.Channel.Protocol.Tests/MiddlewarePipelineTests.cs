using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Aevatar.GAgents.Channel.Protocol.Tests;

public sealed class MiddlewarePipelineTests
{
    [Fact]
    public async Task InvokeAsync_RunsMiddlewareInRegistrationOrder()
    {
        var trace = new List<string>();
        var mw1 = new DelegateMiddleware(async (ctx, next, ct) => { trace.Add("m1:before"); await next(); trace.Add("m1:after"); });
        var mw2 = new DelegateMiddleware(async (ctx, next, ct) => { trace.Add("m2:before"); await next(); trace.Add("m2:after"); });

        var pipeline = new MiddlewarePipelineBuilder()
            .Use(mw1)
            .Use(mw2)
            .Build(new ServiceCollection().BuildServiceProvider());

        var ctx = new StubTurnContext();
        await pipeline.InvokeAsync(ctx, () => { trace.Add("terminal"); return Task.CompletedTask; }, CancellationToken.None);

        trace.ShouldBe(new[] { "m1:before", "m2:before", "terminal", "m2:after", "m1:after" });
    }

    [Fact]
    public async Task InvokeAsync_WhenMiddlewareDoesNotCallNext_TerminalIsNotInvoked()
    {
        var terminalCalled = false;
        var shortCircuit = new DelegateMiddleware((ctx, next, ct) => Task.CompletedTask);

        var pipeline = new MiddlewarePipelineBuilder()
            .Use(shortCircuit)
            .Build(new ServiceCollection().BuildServiceProvider());

        await pipeline.InvokeAsync(new StubTurnContext(), () => { terminalCalled = true; return Task.CompletedTask; }, CancellationToken.None);

        terminalCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task InvokeAsync_WhenMiddlewareThrows_PropagatesUp()
    {
        var throwing = new DelegateMiddleware((ctx, next, ct) => throw new InvalidOperationException("boom"));
        var pipeline = new MiddlewarePipelineBuilder()
            .Use(throwing)
            .Build(new ServiceCollection().BuildServiceProvider());

        await Should.ThrowAsync<InvalidOperationException>(
            () => pipeline.InvokeAsync(new StubTurnContext(), () => Task.CompletedTask, CancellationToken.None));
    }

    [Fact]
    public void MiddlewarePipelineBuilder_NullMiddleware_Throws()
    {
        var builder = new MiddlewarePipelineBuilder();
        Should.Throw<ArgumentNullException>(() => builder.Use((IChannelMiddleware)null!));
    }

    private sealed class DelegateMiddleware : IChannelMiddleware
    {
        private readonly Func<ITurnContext, Func<Task>, CancellationToken, Task> _invoke;

        public DelegateMiddleware(Func<ITurnContext, Func<Task>, CancellationToken, Task> invoke)
        {
            _invoke = invoke;
        }

        public Task InvokeAsync(ITurnContext context, Func<Task> next, CancellationToken ct) => _invoke(context, next, ct);
    }

    internal sealed class StubTurnContext : ITurnContext
    {
        public ChatActivity Activity { get; } = new()
        {
            Id = "act-1",
            Bot = new BotInstanceId { Value = "ops-bot" },
            ChannelId = new ChannelId { Value = "slack" },
            Conversation = new ConversationReference
            {
                Channel = new ChannelId { Value = "slack" },
                Bot = new BotInstanceId { Value = "ops-bot" },
                Scope = ConversationScope.Channel,
                CanonicalKey = "slack:team:channel",
            },
        };

        public ChannelBotDescriptor Bot { get; } = ChannelBotDescriptor.Create(
            "reg-1",
            ChannelId.From("slack"),
            BotInstanceId.From("ops-bot"));

        public IServiceProvider Services { get; } = new ServiceCollection().BuildServiceProvider();

        public Task<EmitResult> SendAsync(MessageContent content, CancellationToken ct) => Task.FromResult(EmitResult.Sent("sent-1"));

        public Task<EmitResult> ReplyAsync(MessageContent content, CancellationToken ct) => Task.FromResult(EmitResult.Sent("reply-1"));

        public Task<StreamingHandle> BeginStreamingReplyAsync(MessageContent initial, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<EmitResult> UpdateAsync(string activityId, MessageContent content, CancellationToken ct) =>
            Task.FromResult(EmitResult.Sent(activityId));

        public Task DeleteAsync(string activityId, CancellationToken ct) => Task.CompletedTask;
    }
}
