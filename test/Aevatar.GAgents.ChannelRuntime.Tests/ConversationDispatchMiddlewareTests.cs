using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ConversationDispatchMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_ShouldPassThrough_WhenCanonicalKeyIsMissing()
    {
        var runtime = new RecordingActorRuntime();
        var middleware = new ConversationDispatchMiddleware(runtime);
        var nextCalls = 0;

        await middleware.InvokeAsync(
            new StubTurnContext(new ChatActivity
            {
                Id = "msg-1",
                Conversation = new ConversationReference(),
            }),
            () =>
            {
                nextCalls++;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        runtime.CreatedActorIds.Should().BeEmpty();
        nextCalls.Should().Be(1);
    }

    [Fact]
    public async Task InvokeAsync_ShouldCreateConversationActor_AndDispatchInboundEnvelope()
    {
        var runtime = new RecordingActorRuntime();
        var middleware = new ConversationDispatchMiddleware(runtime);
        var activity = new ChatActivity
        {
            Id = "msg-2",
            Type = ActivityType.Message,
            Conversation = new ConversationReference
            {
                CanonicalKey = "lark:dm:user-2",
            },
            Content = new MessageContent
            {
                Text = "hello",
            },
        };
        var nextCalls = 0;

        await middleware.InvokeAsync(
            new StubTurnContext(activity),
            () =>
            {
                nextCalls++;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        runtime.CreatedActorIds.Should().ContainSingle("channel-conversation:lark:dm:user-2");
        runtime.Actor.HandledEnvelopes.Should().ContainSingle();
        var envelope = runtime.Actor.HandledEnvelopes[0];
        envelope.Route.Direct.TargetActorId.Should().Be("channel-conversation:lark:dm:user-2");
        envelope.Payload.Should().NotBeNull();
        envelope.Payload.Is(ChatActivity.Descriptor).Should().BeTrue();
        envelope.Payload.Unpack<ChatActivity>().Id.Should().Be("msg-2");
        nextCalls.Should().Be(1);
    }

    private sealed class RecordingActorRuntime : IActorRuntime, IActorDispatchPort
    {
        public RecordingActor Actor { get; } = new("channel-conversation:lark:dm:user-2");
        public List<string?> CreatedActorIds { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent
        {
            CreatedActorIds.Add(id);
            Actor.IdValue = id ?? string.Empty;
            return Task.FromResult<IActor>(Actor);
        }

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);

        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);

        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default) =>
            Actor.HandleEventAsync(envelope, ct);

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingActor(string id) : IActor
    {
        public string Id => IdValue;

        public string IdValue { get; set; } = id;

        public IAgent Agent => throw new NotSupportedException();

        public List<EventEnvelope> HandledEnvelopes { get; } = [];

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            HandledEnvelopes.Add(envelope);
            return Task.CompletedTask;
        }

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class StubTurnContext(ChatActivity activity) : ITurnContext
    {
        public ChatActivity Activity { get; } = activity;

        public ChannelBotDescriptor Bot => ChannelBotDescriptor.Create(
            "reg-1",
            ChannelId.From("lark"),
            BotInstanceId.From("reg-1"),
            "scope-1");

        public IServiceProvider Services { get; } = new ServiceCollection().BuildServiceProvider();

        public Task<EmitResult> SendAsync(MessageContent content, CancellationToken ct) => throw new NotSupportedException();

        public Task<EmitResult> ReplyAsync(MessageContent content, CancellationToken ct) => throw new NotSupportedException();

        public Task<StreamingHandle> BeginStreamingReplyAsync(MessageContent initial, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<EmitResult> UpdateAsync(string activityId, MessageContent content, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string activityId, CancellationToken ct) => throw new NotSupportedException();
    }
}
