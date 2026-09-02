using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class NyxIdRelayIngressPortTests
{
    [Fact]
    public async Task AcceptAsync_ShouldScopeConversationActorIdByTenant()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort();
        var port = new NyxIdRelayIngressPort(
            runtime,
            dispatchPort,
            NullLogger<NyxIdRelayIngressPort>.Instance);
        var canonicalKey = "lark:group:oc_group_1";

        var first = await port.AcceptAsync(BuildRequest("scope-a", canonicalKey, "msg-a"), CancellationToken.None);
        var second = await port.AcceptAsync(BuildRequest("scope-b", canonicalKey, "msg-b"), CancellationToken.None);
        var repeated = await port.AcceptAsync(BuildRequest("scope-a", canonicalKey, "msg-c"), CancellationToken.None);

        first.ActorId.Should().NotBe(second.ActorId);
        first.ActorId.Should().Be(repeated.ActorId);
        first.ActorId.Should().MatchRegex(":scope:[0-9a-f]{64}$");
        second.ActorId.Should().MatchRegex(":scope:[0-9a-f]{64}$");
        runtime.CreatedActorIds.Should().Equal(first.ActorId, second.ActorId, repeated.ActorId);
        dispatchPort.Dispatches.Select(dispatch => dispatch.ActorId)
            .Should().Equal(first.ActorId, second.ActorId, repeated.ActorId);
    }

    private static NyxIdRelayIngressRequest BuildRequest(string scopeId, string canonicalKey, string messageId) =>
        new(
            scopeId,
            new ChatActivity
            {
                Id = messageId,
                Type = ActivityType.Message,
                ChannelId = ChannelId.From("lark"),
                Bot = BotInstanceId.From("reg-1"),
                Conversation = ConversationReference.Create(
                    ChannelId.From("lark"),
                    BotInstanceId.From("reg-1"),
                    ConversationScope.Group,
                    partition: "oc_group_1",
                    "group",
                    "oc_group_1"),
                From = new ParticipantRef { CanonicalId = "ou_user_1" },
                Content = new MessageContent { Text = "hello" },
            },
            ReplyToken: "reply-token-1",
            ReplyTokenExpiresAtUnixMs: 1,
            RelayApiKeyId: "api-key-1",
            CallbackJti: "jti-1",
            CallbackObservedAtUnixMs: 2,
            CallbackReplayExpiresAtUnixMs: 3);

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        public List<string> CreatedActorIds { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        {
            var actorId = id ?? Guid.NewGuid().ToString("N");
            CreatedActorIds.Add(actorId);
            return Task.FromResult<IActor>(new StubActor(actorId));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);
        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);
        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Dispatches { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Dispatches.Add((actorId, envelope));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class StubActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = new StubAgent();
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class StubAgent : IAgent
    {
        public string Id => "stub";
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("stub");
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
