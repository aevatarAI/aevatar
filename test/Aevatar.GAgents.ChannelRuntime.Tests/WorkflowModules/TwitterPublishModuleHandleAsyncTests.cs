using System.Net;
using System.Text;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.GAgents.Scheduled.WorkflowModules;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core.Execution;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests.WorkflowModules;

/// <summary>
/// End-to-end module-level coverage for <c>TwitterPublishModule.HandleAsync</c> — the
/// classification matrix is in <see cref="TwitterPublishOutcomeTests"/>; this file pins the
/// dispatch contract (path, slug, body) so we don't accidentally regress what goes on the
/// wire to NyxID.
/// </summary>
public sealed class TwitterPublishModuleHandleAsyncTests
{
    [Fact]
    public async Task HandleAsync_PostsToTweetsPath_WithoutDoublingTheV2Prefix()
    {
        // PR #461 review (commit 781c5bda follow-up): the api-twitter NyxID provider seed
        // sets `base_url: https://api.x.com/2`, with the API version baked into the base URL.
        // The publish path must therefore be `/tweets`, NOT `/2/tweets`. Regressing to the
        // doubled prefix would produce `https://api.x.com/2/2/tweets` and 404 every approved
        // tweet in production. NyxIdServiceApiHints.cs:58 documents the invariant.
        //
        // The test mocks the NyxID HTTP layer with a routing handler so we capture the exact
        // proxy path the module dispatches, plus the request body (`text` field is what
        // Twitter v2 expects for plain-text posts).
        var handler = new RoutingJsonHandler();
        // Twitter v2 success body — NyxID forwards 2xx verbatim.
        handler.Add(
            HttpMethod.Post,
            "/api/v1/proxy/s/api-twitter/tweets",
            """{"data":{"id":"1755555555555555555","text":"hello"}}""");

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });

        var services = new ServiceCollection().AddSingleton(nyxClient).BuildServiceProvider();
        var ctx = new RecordingExecutionContext(services);
        ctx.SetItem(LLMRequestMetadataKeys.NyxIdAccessToken, "agent-key-1");

        var module = new TwitterPublishModule();
        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "publish_to_twitter",
                StepType = "twitter_publish",
                RunId = "run-1",
                Input = "Excited to ship #216 today!",
                Parameters =
                {
                    ["publish_provider_slug"] = "api-twitter",
                },
            }),
            ctx,
            CancellationToken.None);

        // Path invariant: must be `/tweets` exactly, never `/2/tweets`.
        var post = handler.Requests.Should()
            .ContainSingle(r => r.Method == HttpMethod.Post)
            .Subject;
        post.Path.Should().Be("/api/v1/proxy/s/api-twitter/tweets");
        post.Path.Should().NotContain("/2/tweets",
            because: "the api-twitter provider already pins https://api.x.com/2 as base_url; doubling /2/ produces 404");

        // Body sanity: Twitter v2 plain-text post requires only `{"text":"..."}`. Pin the
        // shape so we don't accidentally drop the trim or add unsupported fields (#216 v1
        // scope explicitly excludes media / threading / polls).
        post.Body.Should().Contain("\"text\"");
        post.Body.Should().Contain("Excited to ship");

        // Module advances the workflow by emitting StepCompletedEvent { Success = true }
        // with the canonical no-handle URL form.
        var completed = ctx.Published
            .Select(p => p.Event)
            .OfType<StepCompletedEvent>()
            .Single();
        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("https://x.com/i/web/status/1755555555555555555");
    }

    [Fact]
    public async Task HandleAsync_FailsClosed_When_NyxIdAccessTokenMissing()
    {
        // Sanity: if the workflow runtime fails to propagate the api-key into execution
        // items, the module must NOT silently call NyxID with an empty token (would 401 and
        // confuse the user-facing surfacing). Emit a categorized failure code and let
        // on_error: skip carry the workflow forward.
        var handler = new RoutingJsonHandler();
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });

        var services = new ServiceCollection().AddSingleton(nyxClient).BuildServiceProvider();
        var ctx = new RecordingExecutionContext(services);
        // Note: no SetItem(NyxIdAccessToken, ...) — execution items are empty.

        var module = new TwitterPublishModule();
        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "publish_to_twitter",
                StepType = "twitter_publish",
                RunId = "run-1",
                Input = "draft",
            }),
            ctx,
            CancellationToken.None);

        handler.Requests.Should().BeEmpty(because: "no api-key means no NyxID call should fire");

        var completed = ctx.Published
            .Select(p => p.Event)
            .OfType<StepCompletedEvent>()
            .Single();
        completed.Success.Should().BeFalse();
        completed.Error.Should().Contain("twitter_publish_api_key_missing");
    }

    private static EventEnvelope Envelope(IMessage evt) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        Payload = Any.Pack(evt),
        Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
    };

    /// <summary>
    /// Minimal <see cref="IWorkflowExecutionContext"/> + <see cref="IWorkflowExecutionItemsContext"/>
    /// implementation for unit-testing module HandleAsync. Holds Published events and
    /// execution items in-memory; everything else is stubbed.
    /// </summary>
    private sealed class RecordingExecutionContext : IWorkflowExecutionContext, IWorkflowExecutionItemsContext
    {
        private readonly Dictionary<string, object?> _items = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Any> _states = new(StringComparer.Ordinal);

        public RecordingExecutionContext(IServiceProvider services)
        {
            Services = services;
            Logger = NullLogger.Instance;
            InboundEnvelope = new EventEnvelope();
        }

        public List<(IMessage Event, TopologyAudience Direction)> Published { get; } = [];
        public EventEnvelope InboundEnvelope { get; }
        public string AgentId => "test-actor";
        public IServiceProvider Services { get; }
        public Microsoft.Extensions.Logging.ILogger Logger { get; }
        public string RunId => "test-run";

        public void SetItem(string itemKey, object? value) => _items[itemKey] = value;

        public bool TryGetItem<TItem>(string itemKey, out TItem? value)
        {
            if (_items.TryGetValue(itemKey, out var raw) && raw is TItem typed)
            {
                value = typed;
                return true;
            }
            value = default;
            return false;
        }

        public bool RemoveItem(string itemKey) => _items.Remove(itemKey);

        public TState LoadState<TState>(string scopeKey)
            where TState : class, IMessage<TState>, new()
        {
            if (!_states.TryGetValue(scopeKey, out var packed) || !packed.Is(new TState().Descriptor))
                return new TState();
            return packed.Unpack<TState>() ?? new TState();
        }

        public IReadOnlyList<KeyValuePair<string, TState>> LoadStates<TState>(string scopeKeyPrefix = "")
            where TState : class, IMessage<TState>, new() => [];

        public Task SaveStateAsync<TState>(string scopeKey, TState state, CancellationToken ct = default)
            where TState : class, IMessage<TState>
        {
            _states[scopeKey] = Any.Pack(state);
            return Task.CompletedTask;
        }

        public Task ClearStateAsync(string scopeKey, CancellationToken ct = default)
        {
            _states.Remove(scopeKey);
            return Task.CompletedTask;
        }

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience direction = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = options;
            Published.Add((evt, direction));
            return Task.CompletedTask;
        }

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(AgentId, callbackId, 1, RuntimeCallbackBackend.InMemory));

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimerAsync(
            string callbackId,
            TimeSpan dueTime,
            TimeSpan period,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(AgentId, callbackId, 1, RuntimeCallbackBackend.InMemory));

        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class RoutingJsonHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _responses = new(StringComparer.OrdinalIgnoreCase);

        public List<RecordedRequest> Requests { get; } = [];

        public void Add(HttpMethod method, string path, string json) =>
            _responses[$"{method.Method}:{path}"] = json;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.Method, path, body));

            if (_responses.TryGetValue($"{request.Method.Method}:{path}", out var json))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"error":true,"message":"not found"}""", Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, string Path, string? Body);
}
