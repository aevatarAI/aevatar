using System.Reflection;
using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.Presentation.AGUI;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using AiTextEndEvent = Aevatar.AI.Abstractions.TextMessageEndEvent;

namespace Aevatar.GAgentService.Integration.Tests;

public sealed class ScopeServiceEndpointsStreamTests
{
    private static readonly MethodInfo HandleGAgentStreamMethod = typeof(ScopeServiceEndpoints)
        .GetMethod("HandleStaticGAgentChatStreamAsync", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("HandleStaticGAgentChatStreamAsync not found.");

    private static readonly MethodInfo HandleScriptingStreamMethod = typeof(ScopeServiceEndpoints)
        .GetMethod("HandleScriptingServiceChatStreamAsync", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("HandleScriptingServiceChatStreamAsync not found.");

    [Theory]
    [InlineData(AGUIEvent.EventOneofCase.TextMessageEnd, true)]
    [InlineData(AGUIEvent.EventOneofCase.RunError, false)]
    [InlineData(AGUIEvent.EventOneofCase.RunFinished, false)]
    public void ShouldEmitSyntheticRunFinished_ShouldRespectTerminalEvent(
        AGUIEvent.EventOneofCase terminalEventCase,
        bool expected)
    {
        ScopeServiceEndpoints.ShouldEmitSyntheticRunFinished(terminalEventCase)
            .Should()
            .Be(expected);
    }

    [Fact]
    public async Task HandleGAgentServiceChatStreamAsync_ShouldCreateActor_AndEmitSyntheticFinish()
    {
        var http = CreateHttpContext();
        var runtime = new StubActorRuntime();
        var subscriptions = new StubSubscriptionProvider
        {
            Messages =
            {
                new EventEnvelope
                {
                    Payload = Any.Pack(new AiTextEndEvent { Content = "done" }),
                },
            },
        };

        await InvokePrivateTaskAsync(
            HandleGAgentStreamMethod,
            http,
            CreateStaticTarget(typeof(StreamTestAgent).AssemblyQualifiedName!, primaryActorId: "actor-1"),
            "hello",
            "actor-1",
            "session-1",
            "scope-a",
            new Dictionary<string, string> { ["trace-id"] = "abc" },
            null,
            runtime,
            subscriptions,
            CancellationToken.None);

        runtime.CreateCalls.Should().ContainSingle(call => call.Id == "actor-1");
        var actor = runtime.Actors["actor-1"].Should().BeOfType<StubActor>().Subject;
        var request = actor.HandledEnvelopes.Should().ContainSingle().Subject.Payload.Unpack<ChatRequestEvent>();
        request.Prompt.Should().Be("hello");
        request.SessionId.Should().Be("session-1");
        request.ScopeId.Should().Be("scope-a");
        request.Metadata["trace-id"].Should().Be("abc");

        var body = await ReadBodyAsync(http);
        body.Should().Contain("runStarted");
        body.Should().Contain("textMessageEnd");
        body.Should().Contain("runFinished");
    }

    [Fact]
    public async Task HandleGAgentServiceChatStreamAsync_ShouldReuseExistingActor_AndAvoidSyntheticDuplicateFinish()
    {
        var http = CreateHttpContext();
        var runtime = new StubActorRuntime();
        runtime.Actors["actor-1"] = new StubActor("actor-1");
        var subscriptions = new StubSubscriptionProvider
        {
            Messages =
            {
                new EventEnvelope
                {
                    Payload = Any.Pack(new AGUIEvent
                    {
                        RunFinished = new RunFinishedEvent
                        {
                            ThreadId = "actor-1",
                            RunId = "run-1",
                        },
                    }),
                },
            },
        };

        await InvokePrivateTaskAsync(
            HandleGAgentStreamMethod,
            http,
            CreateStaticTarget(typeof(StreamTestAgent).AssemblyQualifiedName!, primaryActorId: "actor-1"),
            "hello",
            "actor-1",
            null,
            "scope-a",
            null,
            null,
            runtime,
            subscriptions,
            CancellationToken.None);

        runtime.CreateCalls.Should().BeEmpty();
        var body = await ReadBodyAsync(http);
        body.Split("\"runFinished\"", StringSplitOptions.None).Length.Should().Be(2);
    }

    [Fact]
    public async Task HandleGAgentServiceChatStreamAsync_ShouldMapAllInputPartKinds_WhenCreatingAnonymousActor()
    {
        var http = CreateHttpContext();
        var runtime = new StubActorRuntime();
        var subscriptions = new StubSubscriptionProvider
        {
            Messages =
            {
                new EventEnvelope
                {
                    Payload = Any.Pack(new AiTextEndEvent { Content = "done" }),
                },
            },
        };

        await InvokePrivateTaskAsync(
            HandleGAgentStreamMethod,
            http,
            CreateStaticTarget(typeof(StreamTestAgent).AssemblyQualifiedName!, primaryActorId: "actor-1"),
            "hello",
            null,
            null,
            "scope-a",
            null,
            new List<ScopeServiceEndpoints.StreamContentPartHttpRequest>
            {
                new("image", null, null, "image/png", "https://example.com/image.png", "image-1"),
                new("audio", null, "ZGF0YQ==", "audio/mpeg", null, "audio-1"),
                new("video", null, null, "video/mp4", "https://example.com/video.mp4", "video-1"),
                new("text", "hello text"),
                new("custom", "unknown"),
            },
            runtime,
            subscriptions,
            CancellationToken.None);

        runtime.CreateCalls.Should().ContainSingle(call => call.Id == null);
        var actor = runtime.Actors.Values.Should().ContainSingle().Subject.Should().BeOfType<StubActor>().Subject;
        var request = actor.HandledEnvelopes.Should().ContainSingle().Subject.Payload.Unpack<ChatRequestEvent>();
        request.SessionId.Should().BeEmpty();
        request.InputParts.Select(part => part.Kind).Should().Equal(
            ChatContentPartKind.Image,
            ChatContentPartKind.Audio,
            ChatContentPartKind.Video,
            ChatContentPartKind.Text,
            ChatContentPartKind.Unspecified);

        var body = await ReadBodyAsync(http);
        body.Should().Contain("textMessageEnd");
        body.Should().Contain("runFinished");
    }

    [Fact]
    public async Task HandleGAgentServiceChatStreamAsync_ShouldPreserveRunErrorWithoutSyntheticFinish()
    {
        var http = CreateHttpContext();
        var runtime = new StubActorRuntime();
        runtime.Actors["actor-1"] = new StubActor("actor-1");
        var subscriptions = new StubSubscriptionProvider
        {
            Messages =
            {
                new EventEnvelope
                {
                    Payload = Any.Pack(new AGUIEvent
                    {
                        RunError = new RunErrorEvent
                        {
                            Message = "failed",
                        },
                    }),
                },
            },
        };

        await InvokePrivateTaskAsync(
            HandleGAgentStreamMethod,
            http,
            CreateStaticTarget(typeof(StreamTestAgent).AssemblyQualifiedName!, primaryActorId: "actor-1"),
            "hello",
            "actor-1",
            null,
            "scope-a",
            null,
            null,
            runtime,
            subscriptions,
            CancellationToken.None);

        var body = await ReadBodyAsync(http);
        body.Should().Contain("runError");
        body.Should().NotContain("runFinished");
    }

    [Fact]
    public async Task HandleGAgentServiceChatStreamAsync_ShouldThrow_WhenAgentTypeCannotBeResolved()
    {
        var act = () => InvokePrivateTaskAsync(
            HandleGAgentStreamMethod,
            CreateHttpContext(),
            CreateStaticTarget("Missing.Agent, Missing.Assembly", primaryActorId: "actor-1"),
            "hello",
            "actor-1",
            null,
            "scope-a",
            null,
            null,
            new StubActorRuntime(),
            new StubSubscriptionProvider(),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*could not be resolved*");
    }

    [Fact]
    public async Task HandleScriptingServiceChatStreamAsync_ShouldThrow_WhenPrimaryActorMissing()
    {
        var act = () => InvokePrivateTaskAsync(
            HandleScriptingStreamMethod,
            CreateHttpContext(),
            CreateStaticTarget(typeof(StreamTestAgent).AssemblyQualifiedName!, primaryActorId: string.Empty),
            "hello",
            "session-1",
            "scope-a",
            null,
            new StubActorRuntime(),
            new StubSubscriptionProvider(),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*runtime actor is not available*");
    }

    [Fact]
    public async Task HandleScriptingServiceChatStreamAsync_ShouldThrow_WhenActorCannotBeResolved()
    {
        var act = () => InvokePrivateTaskAsync(
            HandleScriptingStreamMethod,
            CreateHttpContext(),
            CreateStaticTarget(typeof(StreamTestAgent).AssemblyQualifiedName!, primaryActorId: "actor-1"),
            "hello",
            "session-1",
            "scope-a",
            null,
            new StubActorRuntime(),
            new StubSubscriptionProvider(),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*could not be resolved*");
    }

    [Fact]
    public async Task HandleScriptingServiceChatStreamAsync_ShouldEmitSyntheticFinish()
    {
        var http = CreateHttpContext();
        var runtime = new StubActorRuntime();
        runtime.Actors["actor-1"] = new StubActor("actor-1");
        var subscriptions = new StubSubscriptionProvider
        {
            Messages =
            {
                new EventEnvelope
                {
                    Payload = Any.Pack(new AiTextEndEvent { Content = "done" }),
                },
            },
        };

        await InvokePrivateTaskAsync(
            HandleScriptingStreamMethod,
            http,
            CreateStaticTarget(typeof(StreamTestAgent).AssemblyQualifiedName!, primaryActorId: "actor-1"),
            "hello",
            "session-1",
            "scope-a",
            new Dictionary<string, string> { ["trace-id"] = "abc" },
            runtime,
            subscriptions,
            CancellationToken.None);

        var actor = runtime.Actors["actor-1"].Should().BeOfType<StubActor>().Subject;
        var request = actor.HandledEnvelopes.Should().ContainSingle().Subject.Payload.Unpack<ChatRequestEvent>();
        request.Metadata["trace-id"].Should().Be("abc");

        var body = await ReadBodyAsync(http);
        body.Should().Contain("runStarted");
        body.Should().Contain("textMessageEnd");
        body.Should().Contain("runFinished");
    }

    [Fact]
    public async Task HandleScriptingServiceChatStreamAsync_ShouldPreserveRunErrorWithoutSyntheticFinish()
    {
        var http = CreateHttpContext();
        var runtime = new StubActorRuntime();
        runtime.Actors["actor-1"] = new StubActor("actor-1");
        var subscriptions = new StubSubscriptionProvider
        {
            Messages =
            {
                new EventEnvelope
                {
                    Payload = Any.Pack(new AGUIEvent
                    {
                        RunError = new RunErrorEvent
                        {
                            Message = "failed",
                        },
                    }),
                },
            },
        };

        await InvokePrivateTaskAsync(
            HandleScriptingStreamMethod,
            http,
            CreateStaticTarget(typeof(StreamTestAgent).AssemblyQualifiedName!, primaryActorId: "actor-1"),
            "hello",
            "session-1",
            "scope-a",
            null,
            runtime,
            subscriptions,
            CancellationToken.None);

        var body = await ReadBodyAsync(http);
        body.Should().Contain("runError");
        body.Should().NotContain("runFinished");
    }

    [Fact]
    public async Task HandleScriptingServiceChatStreamAsync_ShouldAvoidSyntheticDuplicateFinish_WhenRunFinishedArrives()
    {
        var http = CreateHttpContext();
        var runtime = new StubActorRuntime();
        runtime.Actors["actor-1"] = new StubActor("actor-1");
        var subscriptions = new StubSubscriptionProvider
        {
            Messages =
            {
                new EventEnvelope
                {
                    Payload = Any.Pack(new AGUIEvent
                    {
                        RunFinished = new RunFinishedEvent
                        {
                            ThreadId = "actor-1",
                            RunId = "run-1",
                        },
                    }),
                },
            },
        };

        await InvokePrivateTaskAsync(
            HandleScriptingStreamMethod,
            http,
            CreateStaticTarget(typeof(StreamTestAgent).AssemblyQualifiedName!, primaryActorId: "actor-1"),
            "hello",
            "session-1",
            "scope-a",
            null,
            runtime,
            subscriptions,
            CancellationToken.None);

        var body = await ReadBodyAsync(http);
        body.Split("\"runFinished\"", StringSplitOptions.None).Length.Should().Be(2);
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();
        return http;
    }

    private static ServiceInvocationResolvedTarget CreateStaticTarget(string actorTypeName, string primaryActorId)
    {
        var identity = new ServiceIdentity
        {
            TenantId = "tenant",
            AppId = "app",
            Namespace = "default",
            ServiceId = "svc",
        };

        var artifact = new PreparedServiceRevisionArtifact
        {
            Identity = identity.Clone(),
            RevisionId = "rev-1",
            ImplementationKind = ServiceImplementationKind.Static,
            DeploymentPlan = new ServiceDeploymentPlan
            {
                StaticPlan = new StaticServiceDeploymentPlan
                {
                    ActorTypeName = actorTypeName,
                    PreferredActorId = primaryActorId,
                },
            },
        };
        artifact.Endpoints.Add(new ServiceEndpointDescriptor
        {
            EndpointId = "chat",
            DisplayName = "chat",
            Kind = ServiceEndpointKind.Chat,
            RequestTypeUrl = "type.googleapis.com/aevatar.ai.ChatRequestEvent",
        });

        return new ServiceInvocationResolvedTarget(
            new ServiceInvocationResolvedService(
                "svc-key",
                "rev-1",
                "dep-1",
                primaryActorId,
                "Active",
                []),
            artifact,
            artifact.Endpoints[0]);
    }

    private static async Task InvokePrivateTaskAsync(MethodInfo method, params object?[] args)
    {
        var result = method.Invoke(null, args);
        switch (result)
        {
            case Task task:
                await task;
                return;
            case ValueTask valueTask:
                await valueTask;
                return;
            default:
                throw new InvalidOperationException($"Unexpected return type: {result?.GetType().FullName}");
        }
    }

    private static async Task<string> ReadBodyAsync(DefaultHttpContext http)
    {
        http.Response.Body.Position = 0;
        return await new StreamReader(http.Response.Body).ReadToEndAsync();
    }

    private sealed class StubActorRuntime : IActorRuntime
    {
        public Dictionary<string, IActor> Actors { get; } = [];
        public List<(System.Type Type, string? Id)> CreateCalls { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent => CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default)
        {
            var actor = new StubActor(id ?? Guid.NewGuid().ToString("N"));
            Actors[actor.Id] = actor;
            CreateCalls.Add((agentType, id));
            return Task.FromResult<IActor>(actor);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IActor?> GetAsync(string id) => Task.FromResult(Actors.GetValueOrDefault(id));
        public Task<bool> ExistsAsync(string id) => Task.FromResult(Actors.ContainsKey(id));
        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = new StreamTestAgent();
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

    private sealed class StubSubscriptionProvider : IActorEventSubscriptionProvider
    {
        public List<EventEnvelope> Messages { get; } = [];

        public Task<IAsyncDisposable> SubscribeAsync<TMessage>(
            string actorId,
            Func<TMessage, Task> handler,
            CancellationToken ct = default)
            where TMessage : class, IMessage, new()
        {
            _ = actorId;
            _ = ct;

            if (typeof(TMessage) == typeof(EventEnvelope))
            {
                foreach (var message in Messages)
                    handler((TMessage)(object)message).GetAwaiter().GetResult();
            }

            return Task.FromResult<IAsyncDisposable>(new NoopDisposable());
        }
    }

    private sealed class NoopDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StreamTestAgent : IAgent
    {
        public string Id => "stream-test-agent";
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("stream-test-agent");
        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
