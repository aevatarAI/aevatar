using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.StreamingProxy;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ParticipantStoreEntry = Aevatar.Studio.Application.Studio.Abstractions.StreamingProxyParticipant;

namespace Aevatar.AI.Tests;

public sealed class StreamingProxyNyxParticipantCoordinatorTests
{
    [Fact]
    public async Task EnsureParticipantsJoinedAsync_ShouldPreserveDistinctNodesWithSharedSlug()
    {
        var (coordinator, actor, store, _) = CreateCoordinator();

        var participants = await coordinator.EnsureParticipantsJoinedAsync(
            "scope-1",
            "room-1",
            actor,
            store,
            "test-token",
            CancellationToken.None);

        participants.Should().HaveCount(3);
        participants.Select(participant => participant.ParticipantId).Should().OnlyHaveUniqueItems();
        participants.Select(participant => participant.DisplayName).Should().OnlyHaveUniqueItems();
        participants.Select(participant => participant.DisplayName).Should().OnlyContain(name => name.StartsWith("OpenClaw-Node", StringComparison.Ordinal));

        var joinedEvents = actor.Events
            .Where(envelope => envelope.Payload!.Is(GroupChatParticipantJoinedEvent.Descriptor))
            .Select(envelope => envelope.Payload!.Unpack<GroupChatParticipantJoinedEvent>())
            .ToList();

        joinedEvents.Should().HaveCount(3);
        joinedEvents.Select(evt => evt.AgentId).Should().OnlyHaveUniqueItems();

        store.ListParticipants("room-1").Should().HaveCount(3);
    }

    [Fact]
    public async Task GenerateRepliesAsync_ShouldSkipUnavailableOpenerAndContinueWithHealthyParticipant()
    {
        var (coordinator, actor, store, llmProvider) = CreateCoordinator();
        var participants = await coordinator.EnsureParticipantsJoinedAsync(
            "scope-1",
            "room-1",
            actor,
            store,
            "test-token",
            CancellationToken.None,
            preferredRoute: "/api/v1/proxy/s/openclaw/node-a");

        var roomParticipants = participants.Take(2).ToList();

        await coordinator.GenerateRepliesAsync(
            roomParticipants,
            actor,
            "Discuss the roadmap for the next release.",
            "session-1",
            "test-token",
            CancellationToken.None,
            store,
            "room-1");

        llmProvider.Requests.Should().HaveCount(2);
        llmProvider.Requests[0].RequestId.Should().Contain("node-a");
        llmProvider.Requests[1].RequestId.Should().Contain("node-b");

        var messageEvents = actor.Events
            .Where(envelope => envelope.Payload!.Is(GroupChatMessageEvent.Descriptor))
            .Select(envelope => envelope.Payload!.Unpack<GroupChatMessageEvent>())
            .ToList();

        var leftEvents = actor.Events
            .Where(envelope => envelope.Payload!.Is(GroupChatParticipantLeftEvent.Descriptor))
            .Select(envelope => envelope.Payload!.Unpack<GroupChatParticipantLeftEvent>())
            .ToList();

        messageEvents.Should().HaveCount(1);
        messageEvents.Should().NotContain(evt => evt.Content.StartsWith("当前暂时不可用", StringComparison.Ordinal));
        messageEvents.Single().Content.Should().Contain("reply from");
        messageEvents.Single().Content.Should().Contain("node-b");
        messageEvents.Select(evt => evt.AgentId).Should().OnlyHaveUniqueItems();
        leftEvents.Should().HaveCount(1);
        leftEvents.Single().AgentId.Should().Contain("node-a");
        store.ListParticipants("room-1").Should().HaveCount(2);
    }

    [Fact]
    public async Task GenerateRepliesAsync_ShouldIgnoreUnavailableTextResponseAndContinueWithHealthyParticipant()
    {
        var (coordinator, actor, store, llmProvider) = CreateCoordinator(request =>
        {
            if (request.RequestId?.Contains("node-a", StringComparison.OrdinalIgnoreCase) == true)
            {
                return new LLMResponse
                {
                    Content = "当前暂时不可用: Service request failed.\nStatus: 503 (Service Unavailable)",
                };
            }

            return new LLMResponse
            {
                Content = $"reply from {request.RequestId}",
            };
        });

        var participants = await coordinator.EnsureParticipantsJoinedAsync(
            "scope-1",
            "room-1",
            actor,
            store,
            "test-token",
            CancellationToken.None,
            preferredRoute: "/api/v1/proxy/s/openclaw/node-a");

        var roomParticipants = participants.Take(2).ToList();

        await coordinator.GenerateRepliesAsync(
            roomParticipants,
            actor,
            "Discuss the roadmap for the next release.",
            "session-1",
            "test-token",
            CancellationToken.None,
            store,
            "room-1");

        llmProvider.Requests.Should().HaveCount(2);
        llmProvider.Requests[0].RequestId.Should().Contain("node-a");
        llmProvider.Requests[1].RequestId.Should().Contain("node-b");

        var messageEvents = actor.Events
            .Where(envelope => envelope.Payload!.Is(GroupChatMessageEvent.Descriptor))
            .Select(envelope => envelope.Payload!.Unpack<GroupChatMessageEvent>())
            .ToList();

        var leftEvents = actor.Events
            .Where(envelope => envelope.Payload!.Is(GroupChatParticipantLeftEvent.Descriptor))
            .Select(envelope => envelope.Payload!.Unpack<GroupChatParticipantLeftEvent>())
            .ToList();

        messageEvents.Should().HaveCount(1);
        messageEvents.Single().Content.Should().Contain("reply from");
        messageEvents.Single().Content.Should().Contain("node-b");
        messageEvents.Should().NotContain(evt => evt.Content.Contains("503", StringComparison.OrdinalIgnoreCase));
        leftEvents.Should().HaveCount(1);
        leftEvents.Single().AgentId.Should().Contain("node-a");
        store.ListParticipants("room-1").Should().HaveCount(2);
    }

    [Fact]
    public async Task GenerateRepliesAsync_ShouldUseStreamContentWhenSynchronousContentIsMissing()
    {
        var (coordinator, actor, store, llmProvider) = CreateCoordinator(
            responseFactory: _ => new LLMResponse(),
            streamFactory: request =>
            [
                new LLMStreamChunk { DeltaContent = $"streamed reply from {request.RequestId}" },
                new LLMStreamChunk { FinishReason = "stop", IsLast = true },
            ]);

        var participants = await coordinator.EnsureParticipantsJoinedAsync(
            "scope-1",
            "room-1",
            actor,
            store,
            "test-token",
            CancellationToken.None,
            preferredRoute: "/api/v1/proxy/s/openclaw/node-b");

        var roomParticipants = participants.Take(1).ToList();

        await coordinator.GenerateRepliesAsync(
            roomParticipants,
            actor,
            "Discuss the roadmap for the next release.",
            "session-1",
            "test-token",
            CancellationToken.None,
            store,
            "room-1");

        llmProvider.Requests.Should().HaveCount(1);

        var messageEvents = actor.Events
            .Where(envelope => envelope.Payload!.Is(GroupChatMessageEvent.Descriptor))
            .Select(envelope => envelope.Payload!.Unpack<GroupChatMessageEvent>())
            .ToList();

        var leftEvents = actor.Events
            .Where(envelope => envelope.Payload!.Is(GroupChatParticipantLeftEvent.Descriptor))
            .Select(envelope => envelope.Payload!.Unpack<GroupChatParticipantLeftEvent>())
            .ToList();

        messageEvents.Should().HaveCount(1);
        messageEvents.Single().Content.Should().Contain("streamed reply from");
        messageEvents.Single().SessionId.Should().Be("session-1");
        leftEvents.Should().BeEmpty();
    }

    private static (StreamingProxyNyxParticipantCoordinator Coordinator, RecordingActor Actor, RecordingParticipantStore Store, RecordingLlmProvider Provider) CreateCoordinator()
        => CreateCoordinator(null);

    private static (StreamingProxyNyxParticipantCoordinator Coordinator, RecordingActor Actor, RecordingParticipantStore Store, RecordingLlmProvider Provider) CreateCoordinator(
        Func<LLMRequest, LLMResponse>? responseFactory)
        => CreateCoordinator(responseFactory, null);

    private static (StreamingProxyNyxParticipantCoordinator Coordinator, RecordingActor Actor, RecordingParticipantStore Store, RecordingLlmProvider Provider) CreateCoordinator(
        Func<LLMRequest, LLMResponse>? responseFactory,
        Func<LLMRequest, IReadOnlyList<LLMStreamChunk>>? streamFactory)
    {
        var handler = new StreamingProxyHttpHandler();
        var httpClient = new HttpClient(handler);
        var httpClientFactory = new StubHttpClientFactory(httpClient);
        var provider = new RecordingLlmProvider(responseFactory, streamFactory);
        var llmFactory = new StubLlmProviderFactory(provider);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cli:App:NyxId:Authority"] = "https://nyx.example.com",
            })
            .Build();

        var coordinator = new StreamingProxyNyxParticipantCoordinator(
            llmFactory,
            configuration,
            httpClientFactory,
            NullLogger<StreamingProxyNyxParticipantCoordinator>.Instance);

        return (coordinator, new RecordingActor("room-1"), new RecordingParticipantStore(), provider);
    }

    private sealed class StreamingProxyHttpHandler : HttpMessageHandler
    {
        private static readonly string ServicesJson = """
            {
              "services": [
                {
                  "user_service_id": "svc-node-a",
                  "service_slug": "openclaw",
                  "display_name": "OpenClaw-Node",
                  "status": "ready",
                  "route_value": "/api/v1/proxy/s/openclaw/node-a",
                  "node_id": "node-a",
                  "allowed": true,
                  "models": ["claude-sonnet-4-5-20250929"]
                },
                {
                  "user_service_id": "svc-node-b",
                  "service_slug": "openclaw",
                  "display_name": "OpenClaw-Node",
                  "status": "ready",
                  "route_value": "/api/v1/proxy/s/openclaw/node-b",
                  "node_id": "node-b",
                  "allowed": true,
                  "models": ["claude-sonnet-4-5-20250929"]
                },
                {
                  "user_service_id": "svc-node-c",
                  "service_slug": "openclaw",
                  "display_name": "OpenClaw-Node",
                  "status": "ready",
                  "route_value": "/api/v1/proxy/s/openclaw/node-c",
                  "node_id": "node-c",
                  "allowed": true,
                  "models": ["claude-sonnet-4-5-20250929"]
                }
              ]
            }
            """;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/api/v1/llm/services", StringComparison.Ordinal))
                return Task.FromResult(JsonResponse(ServicesJson));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"error":"not found"}""", Encoding.UTF8, "application/json"),
            });
        }

        private static HttpResponseMessage JsonResponse(string json) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubLlmProviderFactory(RecordingLlmProvider provider) : ILLMProviderFactory
    {
        public ILLMProvider GetProvider(string name) => provider;

        public ILLMProvider GetDefault() => provider;

        public IReadOnlyList<string> GetAvailableProviders() => ["nyxid"];
    }

    private sealed class RecordingLlmProvider(
        Func<LLMRequest, LLMResponse>? responseFactory = null,
        Func<LLMRequest, IReadOnlyList<LLMStreamChunk>>? streamFactory = null) : ILLMProvider
    {
        private readonly Func<LLMRequest, LLMResponse>? _responseFactory = responseFactory;
        private readonly Func<LLMRequest, IReadOnlyList<LLMStreamChunk>>? _streamFactory = streamFactory;

        public string Name => "nyxid";

        public List<LLMRequest> Requests { get; } = [];

        public Task<LLMResponse> ChatAsync(LLMRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);

            if (_responseFactory != null)
                return Task.FromResult(_responseFactory(request));

            if (request.RequestId?.Contains("node-a", StringComparison.OrdinalIgnoreCase) == true)
                throw new InvalidOperationException("node-a is unavailable");

            return Task.FromResult(new LLMResponse
            {
                Content = $"reply from {request.RequestId}",
            });
        }

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);

            if (_streamFactory != null)
            {
                foreach (var chunk in _streamFactory(request))
                {
                    ct.ThrowIfCancellationRequested();
                    yield return chunk;
                }

                yield break;
            }

            if (_responseFactory != null)
            {
                var response = _responseFactory(request);
                if (!string.IsNullOrWhiteSpace(response.Content))
                    yield return new LLMStreamChunk { DeltaContent = response.Content };

                yield return new LLMStreamChunk
                {
                    FinishReason = response.FinishReason ?? "stop",
                    IsLast = true,
                    Usage = response.Usage,
                };
                yield break;
            }

            if (request.RequestId?.Contains("node-a", StringComparison.OrdinalIgnoreCase) == true)
                throw new InvalidOperationException("node-a is unavailable");

            yield return new LLMStreamChunk
            {
                DeltaContent = $"reply from {request.RequestId}",
            };
            yield return new LLMStreamChunk
            {
                FinishReason = "stop",
                IsLast = true,
            };
        }
    }

    private sealed class RecordingActor(string id) : IActor
    {
        private readonly IAgent _agent = new RecordingAgent(id);

        public List<EventEnvelope> Events { get; } = [];

        public string Id { get; } = id;

        public IAgent Agent => _agent;

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            Events.Add(envelope);
            return Task.CompletedTask;
        }

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class RecordingParticipantStore : IStreamingProxyParticipantStore
    {
        private readonly Dictionary<string, List<ParticipantStoreEntry>> _rooms = new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyList<ParticipantStoreEntry>> ListAsync(
            string roomId,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ParticipantStoreEntry> participants = _rooms.TryGetValue(roomId, out var existing)
                ? existing.ToList()
                : [];
            return Task.FromResult(participants);
        }

        public Task AddAsync(
            string roomId,
            string agentId,
            string displayName,
            CancellationToken cancellationToken = default)
        {
            if (!_rooms.TryGetValue(roomId, out var participants))
            {
                participants = [];
                _rooms[roomId] = participants;
            }

            participants.RemoveAll(entry => string.Equals(entry.AgentId, agentId, StringComparison.OrdinalIgnoreCase));
            participants.Add(new ParticipantStoreEntry(agentId, displayName, DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }

        public Task RemoveParticipantAsync(
            string roomId,
            string agentId,
            CancellationToken cancellationToken = default)
        {
            if (_rooms.TryGetValue(roomId, out var participants))
            {
                participants.RemoveAll(entry => string.Equals(entry.AgentId, agentId, StringComparison.OrdinalIgnoreCase));
                if (participants.Count == 0)
                    _rooms.Remove(roomId);
            }

            return Task.CompletedTask;
        }

        public Task RemoveRoomAsync(string roomId, CancellationToken cancellationToken = default)
        {
            _rooms.Remove(roomId);
            return Task.CompletedTask;
        }

        public IReadOnlyList<ParticipantStoreEntry> ListParticipants(string roomId) =>
            _rooms.TryGetValue(roomId, out var participants)
                ? participants
                : [];
    }

    private sealed class RecordingAgent(string id) : IAgent
    {
        public string Id { get; } = id;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("recording-agent");

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
