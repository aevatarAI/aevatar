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
    public async Task ResolveParticipantsAsync_ShouldPreserveDistinctNodesWithSharedSlug()
    {
        var (coordinator, _) = CreateCoordinator();

        var participants = await coordinator.ResolveParticipantsAsync(
            "scope-1",
            "room-1",
            "test-token",
            CancellationToken.None);

        participants.Should().HaveCount(3);
        participants.Select(participant => participant.ParticipantId).Should().OnlyHaveUniqueItems();
        participants.Select(participant => participant.DisplayName).Should().OnlyHaveUniqueItems();
        participants.Select(participant => participant.DisplayName).Should().OnlyContain(name => name.StartsWith("OpenClaw-Node", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GenerateReplyAsync_ShouldReturnFailedOutcomeForUnavailableParticipant()
    {
        var (coordinator, llmProvider) = CreateCoordinator();
        var participants = await coordinator.ResolveParticipantsAsync(
            "scope-1",
            "room-1",
            "test-token",
            CancellationToken.None,
            preferredRoute: "/api/v1/proxy/s/openclaw/node-a");

        var roomParticipants = participants.Take(2).ToList();

        var outcome = await coordinator.GenerateReplyAsync(
            BuildReplyRequest(roomParticipants[0], roomParticipants, "test-token"),
            CancellationToken.None);

        outcome.IsSuccess.Should().BeFalse();
        outcome.FailureKind.Should().Be(StreamingProxyChatParticipantReplyFailureKind.Error);
        llmProvider.Requests.Should().ContainSingle();
        llmProvider.Requests[0].RequestId.Should().Contain("node-a");
        llmProvider.Requests.Should().OnlyContain(request => request.Metadata == null || request.Metadata.Count == 0);
        llmProvider.Requests.Should().OnlyContain(request =>
            request.LlmControl != null &&
            request.LlmControl.NyxIdAccessToken == "test-token");
        llmProvider.Requests.Should().OnlyContain(request =>
            request.LlmControl != null &&
            request.LlmControl.NyxIdRoutePreference != null &&
            request.LlmControl.NyxIdRoutePreference.Contains("/api/v1/proxy/s/openclaw/node-", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GenerateReplyAsync_ShouldReturnFailedOutcomeForUnavailableTextResponse()
    {
        var (coordinator, llmProvider) = CreateCoordinator(request =>
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

        var participants = await coordinator.ResolveParticipantsAsync(
            "scope-1",
            "room-1",
            "test-token",
            CancellationToken.None,
            preferredRoute: "/api/v1/proxy/s/openclaw/node-a");

        var roomParticipants = participants.Take(2).ToList();

        var outcome = await coordinator.GenerateReplyAsync(
            BuildReplyRequest(roomParticipants[0], roomParticipants, "test-token"),
            CancellationToken.None);

        outcome.IsSuccess.Should().BeFalse();
        outcome.FailureKind.Should().Be(StreamingProxyChatParticipantReplyFailureKind.ParticipantUnavailable);
        llmProvider.Requests.Should().ContainSingle();
        llmProvider.Requests[0].RequestId.Should().Contain("node-a");
    }

    [Fact]
    public async Task GenerateReplyAsync_ShouldUseStreamContentWhenSynchronousContentIsMissing()
    {
        var (coordinator, llmProvider) = CreateCoordinator(
            responseFactory: _ => new LLMResponse(),
            streamFactory: request =>
            [
                new LLMStreamChunk { DeltaContent = $"streamed reply from {request.RequestId}" },
                new LLMStreamChunk { FinishReason = "stop", IsLast = true },
            ]);

        var participants = await coordinator.ResolveParticipantsAsync(
            "scope-1",
            "room-1",
            "test-token",
            CancellationToken.None,
            preferredRoute: "/api/v1/proxy/s/openclaw/node-b");

        var roomParticipants = participants.Take(1).ToList();

        var outcome = await coordinator.GenerateReplyAsync(
            BuildReplyRequest(roomParticipants[0], roomParticipants, "test-token"),
            CancellationToken.None);

        llmProvider.Requests.Should().HaveCount(1);
        outcome.IsSuccess.Should().BeTrue();
        outcome.Content.Should().Contain("streamed reply from");
    }

    [Fact]
    public async Task ResolveParticipantsAsync_ShouldFallbackToLegacyStatus_WhenServicesEndpointIsMissing()
    {
        var handler = new StreamingProxyHttpHandler(servicesNotFound: true);
        var (coordinator, _) = CreateCoordinator(null, null, handler);

        var participants = await coordinator.ResolveParticipantsAsync(
            "scope-1",
            "room-1",
            "test-token",
            CancellationToken.None);

        handler.RequestPaths.Should().Equal("/api/v1/llm/services", "/api/v1/llm/status");
        participants.Should().ContainSingle();
        participants.Single().RoutePreference.Should().Be("/api/v1/proxy/s/openclaw/legacy");
        participants.Single().Model.Should().Be("legacy-model");
    }

    private static StreamingProxyChatParticipantReplyRequested BuildReplyRequest(
        StreamingProxyNyxParticipantDefinition participant,
        IReadOnlyList<StreamingProxyNyxParticipantDefinition> activeParticipants,
        string accessToken) =>
        new()
        {
            RoomId = "room-1",
            SessionId = "session-1",
            ParticipantId = participant.ParticipantId,
            DisplayName = participant.DisplayName,
            RoutePreference = participant.RoutePreference,
            Model = participant.Model ?? string.Empty,
            Round = 1,
            ParticipantIndex = 0,
            Prompt = "Discuss the roadmap for the next release.",
            AccessToken = accessToken,
            MaxRounds = 1,
            ActiveParticipants =
            {
                activeParticipants.Select(candidate => new StreamingProxyChatLifecycleParticipant
                {
                    ParticipantId = candidate.ParticipantId,
                    DisplayName = candidate.DisplayName,
                    RoutePreference = candidate.RoutePreference,
                    Model = candidate.Model ?? string.Empty,
                    Status = StreamingProxyChatLifecycleParticipantStatus.Active,
                }),
            },
        };

    private static (StreamingProxyNyxParticipantCoordinator Coordinator, RecordingLlmProvider Provider) CreateCoordinator()
        => CreateCoordinator(null);

    private static (StreamingProxyNyxParticipantCoordinator Coordinator, RecordingLlmProvider Provider) CreateCoordinator(
        Func<LLMRequest, LLMResponse>? responseFactory)
        => CreateCoordinator(responseFactory, null);

    private static (StreamingProxyNyxParticipantCoordinator Coordinator, RecordingLlmProvider Provider) CreateCoordinator(
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

        var coordinator = new StreamingProxyNyxParticipantCoordinator(
            llmFactory,
            configuration,
            httpClientFactory,
            NullLogger<StreamingProxyNyxParticipantCoordinator>.Instance);

        return (coordinator, provider);
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

}
