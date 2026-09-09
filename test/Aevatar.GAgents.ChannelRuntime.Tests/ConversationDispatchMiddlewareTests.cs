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
    public async Task InvokeAsync_ShouldCreateConversationThreadActor_AndDispatchInboundEnvelope()
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

        runtime.CreatedActorIds.Should().ContainSingle("channel-conversation-thread:lark:dm:user-2");
        runtime.HandledEnvelopesByActor.Should().ContainKey("channel-conversation-thread:lark:dm:user-2");
        var envelope = runtime.HandledEnvelopesByActor["channel-conversation-thread:lark:dm:user-2"].Should().ContainSingle().Which;
        envelope.Route.Direct.TargetActorId.Should().Be("channel-conversation-thread:lark:dm:user-2");
        envelope.Payload.Should().NotBeNull();
        envelope.Payload.Is(ChatActivity.Descriptor).Should().BeTrue();
        envelope.Payload.Unpack<ChatActivity>().Id.Should().Be("msg-2");
        nextCalls.Should().Be(1);
    }

    private sealed class RecordingActorRuntime : IActorRuntime, IActorDispatchPort
    {
        public List<string?> CreatedActorIds { get; } = [];
        public Dictionary<string, List<EventEnvelope>> HandledEnvelopesByActor { get; } = new(StringComparer.Ordinal);

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent
        {
            CreatedActorIds.Add(id);
            return Task.FromResult<IActor>(new RecordingActor(id ?? string.Empty, HandledEnvelopesByActor));
        }

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);

        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);

        public async Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            if (!HandledEnvelopesByActor.TryGetValue(actorId, out var envelopes))
            {
                envelopes = [];
                HandledEnvelopesByActor.Add(actorId, envelopes);
            }

            envelopes.Add(envelope);
            await Task.CompletedTask;
            return DispatchAdmissionFactory.Create(actorId, envelope);
        }

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingActor(
        string id,
        Dictionary<string, List<EventEnvelope>> handledEnvelopesByActor) : IActor
    {
        public string Id { get; } = id;

        public IAgent Agent => throw new NotSupportedException();

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            if (!handledEnvelopesByActor.TryGetValue(Id, out var envelopes))
            {
                envelopes = [];
                handledEnvelopesByActor.Add(Id, envelopes);
            }

            envelopes.Add(envelope);
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
