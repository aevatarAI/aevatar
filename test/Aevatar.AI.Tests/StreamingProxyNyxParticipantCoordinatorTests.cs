using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.GAgents.StreamingProxy;
using Aevatar.GAgents.StreamingProxy.Application.Rooms;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aevatar.AI.Tests;

public sealed class StreamingProxyNyxParticipantCoordinatorTests
{
    [Fact]
    public async Task EnsureParticipantsJoinedAsync_ShouldPreserveDistinctNodesWithSharedSlug()
    {
        var (coordinator, roomCommands, _) = CreateCoordinator();

        var participants = await coordinator.EnsureParticipantsJoinedAsync(
            "scope-1",
            "room-1",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            "test-token",
            CancellationToken.None);

        participants.Should().HaveCount(3);
        participants.Select(participant => participant.ParticipantId).Should().OnlyHaveUniqueItems();
        participants.Select(participant => participant.DisplayName).Should().OnlyHaveUniqueItems();
        participants.Select(participant => participant.DisplayName).Should().OnlyContain(name => name.StartsWith("OpenClaw-Node", StringComparison.Ordinal));

        roomCommands.JoinCommands.Should().HaveCount(3);
        roomCommands.JoinCommands.Select(command => command.AgentId).Should().OnlyHaveUniqueItems();
        roomCommands.PostMessageCommands.Should().BeEmpty();
        roomCommands.LeaveCommands.Should().BeEmpty();
    }

    [Fact]
    public async Task GenerateRepliesAsync_ShouldSkipUnavailableOpenerAndContinueWithHealthyParticipant()
    {
        var (coordinator, roomCommands, llmProvider) = CreateCoordinator();
        var participants = await coordinator.EnsureParticipantsJoinedAsync(
            "scope-1",
            "room-1",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            "test-token",
            CancellationToken.None,
            preferredRoute: "/api/v1/proxy/s/openclaw/node-a");

        var roomParticipants = participants.Take(2).ToList();

        await coordinator.GenerateRepliesAsync(
            roomParticipants,
            "room-1",
            "Discuss the roadmap for the next release.",
            "session-1",
            "test-token",
            CancellationToken.None);

        llmProvider.Requests.Should().HaveCount(2);
        llmProvider.Requests[0].RequestId.Should().Contain("node-a");
        llmProvider.Requests[1].RequestId.Should().Contain("node-b");
        llmProvider.Requests.Should().OnlyContain(request => request.Metadata == null || request.Metadata.Count == 0);
        llmProvider.Requests.Should().OnlyContain(request =>
            request.LlmControl != null &&
            request.LlmControl.NyxIdAccessToken == "test-token");
        llmProvider.Requests.Should().OnlyContain(request =>
            request.LlmControl != null &&
            request.LlmControl.NyxIdRoutePreference != null &&
            request.LlmControl.NyxIdRoutePreference.Contains("/api/v1/proxy/s/openclaw/node-", StringComparison.OrdinalIgnoreCase));

        roomCommands.PostMessageCommands.Should().HaveCount(1);
        roomCommands.PostMessageCommands.Should().NotContain(command => command.Content.StartsWith("当前暂时不可用", StringComparison.Ordinal));
        roomCommands.PostMessageCommands.Single().Content.Should().Contain("reply from");
        roomCommands.PostMessageCommands.Single().Content.Should().Contain("node-b");
        roomCommands.PostMessageCommands.Select(command => command.AgentId).Should().OnlyHaveUniqueItems();
        roomCommands.LeaveCommands.Should().HaveCount(1);
        roomCommands.LeaveCommands.Single().AgentId.Should().Contain("node-a");
    }

    [Fact]
    public async Task GenerateRepliesAsync_ShouldIgnoreUnavailableTextResponseAndContinueWithHealthyParticipant()
    {
        var (coordinator, roomCommands, llmProvider) = CreateCoordinator(request =>
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
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            "test-token",
            CancellationToken.None,
            preferredRoute: "/api/v1/proxy/s/openclaw/node-a");

        var roomParticipants = participants.Take(2).ToList();

        await coordinator.GenerateRepliesAsync(
            roomParticipants,
            "room-1",
            "Discuss the roadmap for the next release.",
            "session-1",
            "test-token",
            CancellationToken.None);

        llmProvider.Requests.Should().HaveCount(2);
        llmProvider.Requests[0].RequestId.Should().Contain("node-a");
        llmProvider.Requests[1].RequestId.Should().Contain("node-b");

        roomCommands.PostMessageCommands.Should().HaveCount(1);
        roomCommands.PostMessageCommands.Single().Content.Should().Contain("reply from");
        roomCommands.PostMessageCommands.Single().Content.Should().Contain("node-b");
        roomCommands.PostMessageCommands.Should().NotContain(command => command.Content.Contains("503", StringComparison.OrdinalIgnoreCase));
        roomCommands.LeaveCommands.Should().HaveCount(1);
        roomCommands.LeaveCommands.Single().AgentId.Should().Contain("node-a");
    }

    [Fact]
    public async Task GenerateRepliesAsync_ShouldUseStreamContentWhenSynchronousContentIsMissing()
    {
        var (coordinator, roomCommands, llmProvider) = CreateCoordinator(
            responseFactory: _ => new LLMResponse(),
            streamFactory: request =>
            [
                new LLMStreamChunk { DeltaContent = $"streamed reply from {request.RequestId}" },
                new LLMStreamChunk { FinishReason = "stop", IsLast = true },
            ]);

        var participants = await coordinator.EnsureParticipantsJoinedAsync(
            "scope-1",
            "room-1",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            "test-token",
            CancellationToken.None,
            preferredRoute: "/api/v1/proxy/s/openclaw/node-b");

        var roomParticipants = participants.Take(1).ToList();

        await coordinator.GenerateRepliesAsync(
            roomParticipants,
            "room-1",
            "Discuss the roadmap for the next release.",
            "session-1",
            "test-token",
            CancellationToken.None);

        llmProvider.Requests.Should().HaveCount(1);

        roomCommands.PostMessageCommands.Should().HaveCount(1);
        roomCommands.PostMessageCommands.Single().Content.Should().Contain("streamed reply from");
        roomCommands.PostMessageCommands.Single().SessionId.Should().Be("session-1");
        roomCommands.LeaveCommands.Should().BeEmpty();
    }

    [Fact]
    public async Task EnsureParticipantsJoinedAsync_ShouldFallbackToLegacyStatus_WhenServicesEndpointIsMissing()
    {
        var handler = new StreamingProxyHttpHandler(servicesNotFound: true);
        var (coordinator, roomCommands, _) = CreateCoordinator(null, null, handler);

        var participants = await coordinator.EnsureParticipantsJoinedAsync(
            "scope-1",
            "room-1",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            "test-token",
            CancellationToken.None);

        handler.RequestPaths.Should().Equal("/api/v1/llm/services", "/api/v1/llm/status");
        participants.Should().ContainSingle();
        participants.Single().RoutePreference.Should().Be("/api/v1/proxy/s/openclaw/legacy");
        participants.Single().Model.Should().Be("legacy-model");
        roomCommands.JoinCommands
            .Should()
            .ContainSingle(command => command.AgentId.Contains("svc-legacy", StringComparison.OrdinalIgnoreCase));
    }

    private static (StreamingProxyNyxParticipantCoordinator Coordinator, RecordingRoomCommandService RoomCommands, RecordingLlmProvider Provider) CreateCoordinator()
        => CreateCoordinator(null);

    private static (StreamingProxyNyxParticipantCoordinator Coordinator, RecordingRoomCommandService RoomCommands, RecordingLlmProvider Provider) CreateCoordinator(
        Func<LLMRequest, LLMResponse>? responseFactory)
        => CreateCoordinator(responseFactory, null);

    private static (StreamingProxyNyxParticipantCoordinator Coordinator, RecordingRoomCommandService RoomCommands, RecordingLlmProvider Provider) CreateCoordinator(
        Func<LLMRequest, LLMResponse>? responseFactory,
        Func<LLMRequest, IReadOnlyList<LLMStreamChunk>>? streamFactory,
        StreamingProxyHttpHandler? handler = null)
    {
        handler ??= new StreamingProxyHttpHandler();
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

        var roomCommands = new RecordingRoomCommandService();
        var coordinator = new StreamingProxyNyxParticipantCoordinator(
            roomCommands,
            llmFactory,
            configuration,
            httpClientFactory,
            NullLogger<StreamingProxyNyxParticipantCoordinator>.Instance);

        return (coordinator, roomCommands, provider);
    }

    private sealed class StreamingProxyHttpHandler(bool servicesNotFound = false) : HttpMessageHandler
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

        private static readonly string LegacyStatusJson = """
            {
              "providers": [
                {
                  "user_service_id": "svc-legacy",
                  "provider_slug": "openclaw",
                  "provider_name": "OpenClaw Legacy",
                  "status": "ready",
                  "proxy_url": "/api/v1/proxy/s/openclaw/legacy"
                }
              ],
              "models_by_provider": {
                "openclaw": ["legacy-model"]
              },
              "supported_models": ["fallback-model"]
            }
            """;

        public List<string> RequestPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            RequestPaths.Add(path);
            if (path.EndsWith("/api/v1/llm/services", StringComparison.Ordinal))
            {
                return Task.FromResult(servicesNotFound
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        Content = new StringContent("""{"error":"not found"}""", Encoding.UTF8, "application/json"),
                    }
                    : JsonResponse(ServicesJson));
            }

            if (path.EndsWith("/api/v1/llm/status", StringComparison.Ordinal))
                return Task.FromResult(JsonResponse(LegacyStatusJson));

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

        private LLMResponse BuildResponse(LLMRequest request)
        {
            if (_responseFactory != null)
                return _responseFactory(request);

            if (request.RequestId?.Contains("node-a", StringComparison.OrdinalIgnoreCase) == true)
                throw new InvalidOperationException("node-a is unavailable");

            return new LLMResponse
            {
                Content = $"reply from {request.RequestId}",
            };
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

    private sealed class RecordingRoomCommandService : IStreamingProxyRoomCommandService
    {
        public List<StreamingProxyRoomCreateCommand> CreateCommands { get; } = [];
        public List<StreamingProxyRoomJoinCommand> JoinCommands { get; } = [];
        public List<StreamingProxyRoomPostMessageCommand> PostMessageCommands { get; } = [];
        public List<StreamingProxyRoomLeaveCommand> LeaveCommands { get; } = [];
        public List<StreamingProxyRoomTerminalStateCommand> TerminalCommands { get; } = [];

        public Task<StreamingProxyRoomCreateResult> CreateRoomAsync(
            StreamingProxyRoomCreateCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCommands.Add(command);
            return Task.FromResult(new StreamingProxyRoomCreateResult(
                StreamingProxyRoomCreateStatus.Created,
                "room-1",
                "Room 1"));
        }

        public Task<StreamingProxyRoomPostMessageResult> PostMessageAsync(
            StreamingProxyRoomPostMessageCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PostMessageCommands.Add(command);
            return Task.FromResult(new StreamingProxyRoomPostMessageResult(
                StreamingProxyRoomPostMessageStatus.Accepted));
        }

        public Task<StreamingProxyRoomJoinResult> JoinAsync(
            StreamingProxyRoomJoinCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JoinCommands.Add(command);
            return Task.FromResult(new StreamingProxyRoomJoinResult(
                StreamingProxyRoomJoinStatus.Accepted,
                command.AgentId,
                command.DisplayName));
        }

        public Task<StreamingProxyRoomLeaveResult> LeaveAsync(
            StreamingProxyRoomLeaveCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LeaveCommands.Add(command);
            return Task.FromResult(new StreamingProxyRoomLeaveResult(
                StreamingProxyRoomLeaveStatus.Accepted,
                command.AgentId));
        }

        public Task PublishTerminalStateAsync(
            StreamingProxyRoomTerminalStateCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TerminalCommands.Add(command);
            return Task.CompletedTask;
        }
    }
}
