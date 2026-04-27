using System.Reflection;
using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Core.Commands;
using Aevatar.CQRS.Core.Interactions;
using Aevatar.CQRS.Core.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Application.ScopeGAgents;
using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.GAgentService.Projection.Orchestration;
using Aevatar.GAgentService.Projection.Projectors;
using Aevatar.Presentation.AGUI;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.Projectors;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using AiTextContentEvent = Aevatar.AI.Abstractions.TextMessageContentEvent;
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
        var projectionPort = new StubDraftRunProjectionPort
        {
            Messages =
            {
                new EventEnvelope
                {
                    Payload = Any.Pack(new AiTextEndEvent { Content = "done" }),
                },
            },
        };
        var interactionService = CreateStaticStreamInteractionService(runtime, projectionPort);

        await InvokeStaticStreamAsync(
            http,
            CreateStaticTarget(typeof(StreamTestAgent).AssemblyQualifiedName!, primaryActorId: "actor-1"),
            "hello",
            "actor-1",
            "session-1",
            "scope-a",
            new Dictionary<string, string> { ["trace-id"] = "abc" },
            null,
            interactionService,
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
        var projectionPort = new StubDraftRunProjectionPort
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
        var interactionService = CreateStaticStreamInteractionService(runtime, projectionPort);

        await InvokeStaticStreamAsync(
            http,
            CreateStaticTarget(typeof(StreamTestAgent).AssemblyQualifiedName!, primaryActorId: "actor-1"),
            "hello",
            "actor-1",
            null,
            "scope-a",
            null,
            null,
            interactionService,
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
        var projectionPort = new StubDraftRunProjectionPort
        {
            Messages =
            {
                new EventEnvelope
                {
                    Payload = Any.Pack(new AiTextEndEvent { Content = "done" }),
                },
            },
        };
        var interactionService = CreateStaticStreamInteractionService(runtime, projectionPort);

        await InvokeStaticStreamAsync(
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
            interactionService,
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
        var projectionPort = new StubDraftRunProjectionPort
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
        var interactionService = CreateStaticStreamInteractionService(runtime, projectionPort);

        await InvokeStaticStreamAsync(
            http,
            CreateStaticTarget(typeof(StreamTestAgent).AssemblyQualifiedName!, primaryActorId: "actor-1"),
            "hello",
            "actor-1",
            null,
            "scope-a",
            null,
            null,
            interactionService,
            CancellationToken.None);

        var body = await ReadBodyAsync(http);
        body.Should().Contain("runError");
        body.Should().NotContain("runFinished");
    }

    [Fact]
    public async Task GAgentDraftRunSessionEventProjector_ShouldPublishMappedAguiEvent_ToCommandSession()
    {
        var sessionHub = new RecordingProjectionSessionEventHub();
        var projector = new GAgentDraftRunSessionEventProjector(sessionHub);
        var context = new GAgentDraftRunProjectionContext
        {
            RootActorId = "actor-1",
            SessionId = "cmd-1",
            ProjectionKind = "service-draft-run-session",
        };

        await projector.ProjectAsync(
            context,
            new EventEnvelope
            {
                Propagation = new EnvelopePropagation
                {
                    CorrelationId = "cmd-1",
                },
                Payload = Any.Pack(new AiTextContentEvent
                {
                    SessionId = "msg-1",
                    Delta = "hello",
                }),
            },
            CancellationToken.None);

        var published = sessionHub.Published.Should().ContainSingle().Subject;
        published.ScopeId.Should().Be("actor-1");
        published.SessionId.Should().Be("cmd-1");
        published.Event.TextMessageContent.MessageId.Should().Be("msg-1");
        published.Event.TextMessageContent.Delta.Should().Be("hello");
    }

    [Fact]
    public async Task GAgentDraftRunSessionEventProjector_ShouldIgnoreEnvelope_FromDifferentCommandSession()
    {
        var sessionHub = new RecordingProjectionSessionEventHub();
        var projector = new GAgentDraftRunSessionEventProjector(sessionHub);
        var context = new GAgentDraftRunProjectionContext
        {
            RootActorId = "actor-1",
            SessionId = "cmd-1",
            ProjectionKind = "service-draft-run-session",
        };

        await projector.ProjectAsync(
            context,
            new EventEnvelope
            {
                Propagation = new EnvelopePropagation
                {
                    CorrelationId = "cmd-2",
                },
                Payload = Any.Pack(new AiTextContentEvent
                {
                    SessionId = "msg-1",
                    Delta = "hello",
                }),
            },
            CancellationToken.None);

        sessionHub.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task ScriptExecutionSessionEventProjector_ShouldRouteOnlyMatchingRunSession()
    {
        var sessionHub = new RecordingScriptExecutionSessionEventHub();
        var projector = new ScriptExecutionSessionEventProjector(sessionHub);
        var context = new ScriptExecutionProjectionContext
        {
            RootActorId = "runtime-1",
            SessionId = "run-1",
            ProjectionKind = "script-execution-session",
        };

        var matchingEnvelope = new EventEnvelope
        {
            Id = "evt-1",
            Propagation = new EnvelopePropagation
            {
                CorrelationId = "run-1",
            },
            Payload = Any.Pack(new AiTextContentEvent
            {
                SessionId = "msg-1",
                Delta = "hello",
            }),
        };
        var mismatchedEnvelope = new EventEnvelope
        {
            Id = "evt-2",
            Propagation = new EnvelopePropagation
            {
                CorrelationId = "run-2",
            },
            Payload = Any.Pack(new AiTextContentEvent
            {
                SessionId = "msg-2",
                Delta = "other",
            }),
        };

        await projector.ProjectAsync(context, matchingEnvelope, CancellationToken.None);
        await projector.ProjectAsync(context, mismatchedEnvelope, CancellationToken.None);

        var published = sessionHub.Published.Should().ContainSingle().Subject;
        published.ScopeId.Should().Be("runtime-1");
        published.SessionId.Should().Be("run-1");
        published.Event.Id.Should().Be("evt-1");
    }

    [Fact]
    public async Task HandleGAgentServiceChatStreamAsync_ShouldThrow_WhenAgentTypeCannotBeResolved()
    {
        var act = () => InvokeStaticStreamAsync(
            CreateHttpContext(),
            CreateStaticTarget("Missing.Agent, Missing.Assembly", primaryActorId: "actor-1"),
            "hello",
            "actor-1",
            null,
            "scope-a",
            null,
            null,
            CreateStaticStreamInteractionService(new StubActorRuntime(), new StubDraftRunProjectionPort()),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*could not be resolved*");
    }

    [Fact]
    public async Task HandleScriptingServiceChatStreamAsync_ShouldThrow_WhenPrimaryActorMissing()
    {
        var act = () => InvokeScriptingStreamAsync(
            CreateHttpContext(),
            CreateScriptingTarget(primaryActorId: string.Empty),
            "hello",
            "session-1",
            "scope-a",
            null,
            new StubScriptRuntimeCommandPort(),
            new StubScriptExecutionProjectionPort(),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*runtime actor is not available*");
    }

    [Fact]
    public async Task HandleScriptingServiceChatStreamAsync_ShouldThrow_WhenActorCannotBeResolved()
    {
        var act = () => InvokeScriptingStreamAsync(
            CreateHttpContext(),
            CreateScriptingTarget(primaryActorId: "actor-1"),
            "hello",
            "session-1",
            "scope-a",
            null,
            new ThrowingScriptRuntimeCommandPort(new InvalidOperationException("Script runtime actor 'actor-1' could not be resolved. The service may not be activated.")),
            new StubScriptExecutionProjectionPort(),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*could not be resolved*");
    }

    [Fact]
    public async Task HandleScriptingServiceChatStreamAsync_ShouldEmitSyntheticFinish()
    {
        var http = CreateHttpContext();
        var commandPort = new StubScriptRuntimeCommandPort();
        var projectionPort = new StubScriptExecutionProjectionPort
        {
            Messages =
            {
                new EventEnvelope
                {
                    Payload = Any.Pack(new AiTextEndEvent { Content = "done" }),
                },
            },
        };

        await InvokeScriptingStreamAsync(
            http,
            CreateScriptingTarget(primaryActorId: "actor-1"),
            "hello",
            "session-1",
            "scope-a",
            new Dictionary<string, string> { ["trace-id"] = "abc" },
            commandPort,
            projectionPort,
            CancellationToken.None);

        var request = commandPort.Invocations.Should().ContainSingle().Subject.InputPayload.Unpack<ChatRequestEvent>();
        request.Metadata["trace-id"].Should().Be("abc");
        request.ScopeId.Should().Be("scope-a");
        request.SessionId.Should().Be("session-1");
        projectionPort.EnsureCalls.Should().ContainSingle(call =>
            call.ActorId == "actor-1" &&
            call.RunId == commandPort.Invocations.Single().RunId);

        var body = await ReadBodyAsync(http);
        body.Should().Contain("runStarted");
        body.Should().Contain("textMessageEnd");
        body.Should().Contain("runFinished");
    }

    [Fact]
    public async Task HandleScriptingServiceChatStreamAsync_ShouldPreserveRunErrorWithoutSyntheticFinish()
    {
        var http = CreateHttpContext();
        var commandPort = new StubScriptRuntimeCommandPort();
        var projectionPort = new StubScriptExecutionProjectionPort
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

        await InvokeScriptingStreamAsync(
            http,
            CreateScriptingTarget(primaryActorId: "actor-1"),
            "hello",
            "session-1",
            "scope-a",
            null,
            commandPort,
            projectionPort,
            CancellationToken.None);

        var body = await ReadBodyAsync(http);
        body.Should().Contain("runError");
        body.Should().NotContain("runFinished");
    }

    [Fact]
    public async Task HandleScriptingServiceChatStreamAsync_ShouldAvoidSyntheticDuplicateFinish_WhenRunFinishedArrives()
    {
        var http = CreateHttpContext();
        var commandPort = new StubScriptRuntimeCommandPort();
        var projectionPort = new StubScriptExecutionProjectionPort
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

        await InvokeScriptingStreamAsync(
            http,
            CreateScriptingTarget(primaryActorId: "actor-1"),
            "hello",
            "session-1",
            "scope-a",
            null,
            commandPort,
            projectionPort,
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

    private static ServiceInvocationResolvedTarget CreateScriptingTarget(string primaryActorId)
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
            ImplementationKind = ServiceImplementationKind.Scripting,
            DeploymentPlan = new ServiceDeploymentPlan
            {
                ScriptingPlan = new ScriptingServiceDeploymentPlan
                {
                    Revision = "rev-1",
                    DefinitionActorId = "definition-1",
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

    private static Task InvokeStaticStreamAsync(
        HttpContext http,
        ServiceInvocationResolvedTarget target,
        string prompt,
        string? actorId,
        string? sessionId,
        string scopeId,
        IReadOnlyDictionary<string, string>? headers,
        IReadOnlyList<ScopeServiceEndpoints.StreamContentPartHttpRequest>? inputParts,
        ICommandInteractionService<GAgentDraftRunCommand, GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, AGUIEvent, GAgentDraftRunCompletionStatus> interactionService,
        CancellationToken ct) =>
        InvokePrivateTaskAsync(
            HandleGAgentStreamMethod,
            http,
            target,
            prompt,
            actorId,
            sessionId,
            scopeId,
            "svc-default",
            headers,
            inputParts,
            interactionService,
            new ServiceInvocationRequest(),
            new NoOpServiceRunRegistrationPort(),
            ct);

    private static Task InvokeScriptingStreamAsync(
        HttpContext http,
        ServiceInvocationResolvedTarget target,
        string prompt,
        string? sessionId,
        string scopeId,
        IReadOnlyDictionary<string, string>? headers,
        IScriptRuntimeCommandPort scriptRuntimeCommandPort,
        IScriptExecutionProjectionPort scriptExecutionProjectionPort,
        CancellationToken ct) =>
        InvokePrivateTaskAsync(
            HandleScriptingStreamMethod,
            http,
            target,
            prompt,
            sessionId,
            scopeId,
            "svc-default",
            headers,
            scriptRuntimeCommandPort,
            scriptExecutionProjectionPort,
            new ServiceInvocationRequest(),
            new NoOpServiceRunRegistrationPort(),
            ct);

    private sealed class NoOpServiceRunRegistrationPort : IServiceRunRegistrationPort
    {
        public Task<ServiceRunRegistrationResult> RegisterAsync(ServiceRunRecord record, CancellationToken ct = default) =>
            Task.FromResult(new ServiceRunRegistrationResult($"service-run:{record.RunId}", record.RunId));

        public Task UpdateStatusAsync(string runActorId, string runId, ServiceRunStatus status, CancellationToken ct = default) =>
            Task.CompletedTask;
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

    private static ICommandInteractionService<GAgentDraftRunCommand, GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, AGUIEvent, GAgentDraftRunCompletionStatus> CreateStaticStreamInteractionService(
        StubActorRuntime runtime,
        StubDraftRunProjectionPort projectionPort)
    {
        var pipeline = new DefaultCommandDispatchPipeline<GAgentDraftRunCommand, GAgentDraftRunCommandTarget, GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError>(
            new GAgentDraftRunCommandTargetResolver(
                runtime,
                projectionPort),
            new DefaultCommandContextPolicy(),
            new GAgentDraftRunCommandTargetBinder(projectionPort),
            new GAgentDraftRunCommandEnvelopeFactory(),
            new ActorCommandTargetDispatcher<GAgentDraftRunCommandTarget>(new InlineActorDispatchPort(runtime)),
            new GAgentDraftRunAcceptedReceiptFactory());

        return new DefaultCommandInteractionService<GAgentDraftRunCommand, GAgentDraftRunCommandTarget, GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, AGUIEvent, AGUIEvent, GAgentDraftRunCompletionStatus>(
            pipeline,
            new DefaultEventOutputStream<AGUIEvent, AGUIEvent>(new IdentityEventFrameMapper<AGUIEvent>()),
            new GAgentDraftRunCompletionPolicy(),
            new GAgentDraftRunFinalizeEmitter(),
            new GAgentDraftRunDurableCompletionResolver(),
            NullLogger<DefaultCommandInteractionService<GAgentDraftRunCommand, GAgentDraftRunCommandTarget, GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, AGUIEvent, AGUIEvent, GAgentDraftRunCompletionStatus>>.Instance);
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

    private sealed class InlineActorDispatchPort(StubActorRuntime runtime) : IActorDispatchPort
    {
        public async Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            var actor = await runtime.GetAsync(actorId);
            if (actor == null)
                throw new InvalidOperationException($"Actor '{actorId}' not found.");

            await actor.HandleEventAsync(envelope, ct);
        }
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

    private sealed class StubDraftRunProjectionPort : IGAgentDraftRunProjectionPort
    {
        public List<EventEnvelope> Messages { get; } = [];

        public bool ProjectionEnabled => true;

        public Task<IGAgentDraftRunProjectionLease?> EnsureActorProjectionAsync(
            string actorId,
            string commandId,
            CancellationToken ct = default)
        {
            _ = ct;
            return Task.FromResult<IGAgentDraftRunProjectionLease?>(new StubDraftRunProjectionLease(actorId, commandId));
        }

        public async Task AttachLiveSinkAsync(
            IGAgentDraftRunProjectionLease lease,
            IEventSink<AGUIEvent> sink,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(lease);
            ArgumentNullException.ThrowIfNull(sink);
            _ = ct;

            foreach (var message in Messages)
            {
                var mapped = ScopeGAgentAguiEventMapper.TryMap(message);
                if (mapped == null)
                    continue;

                try
                {
                    await sink.PushAsync(mapped, CancellationToken.None);
                }
                catch (EventSinkCompletedException)
                {
                    break;
                }
            }
        }

        public Task DetachLiveSinkAsync(
            IGAgentDraftRunProjectionLease lease,
            IEventSink<AGUIEvent> sink,
            CancellationToken ct = default)
        {
            _ = lease;
            _ = sink;
            _ = ct;
            return Task.CompletedTask;
        }

        public Task ReleaseActorProjectionAsync(
            IGAgentDraftRunProjectionLease lease,
            CancellationToken ct = default)
        {
            _ = lease;
            _ = ct;
            return Task.CompletedTask;
        }
    }

    private sealed record StubDraftRunProjectionLease(string ActorId, string CommandId) : IGAgentDraftRunProjectionLease;

    private sealed class StubScriptExecutionProjectionPort : IScriptExecutionProjectionPort
    {
        public List<EventEnvelope> Messages { get; } = [];
        public List<(string ActorId, string RunId)> EnsureCalls { get; } = [];

        public bool ProjectionEnabled => true;

        public Task<IScriptExecutionProjectionLease?> EnsureActorProjectionAsync(
            string actorId,
            CancellationToken ct = default)
        {
            return EnsureRunProjectionAsync(actorId, actorId, ct);
        }

        public Task<IScriptExecutionProjectionLease?> EnsureRunProjectionAsync(
            string actorId,
            string runId,
            CancellationToken ct = default)
        {
            _ = ct;
            EnsureCalls.Add((actorId, runId));
            return Task.FromResult<IScriptExecutionProjectionLease?>(new StubScriptExecutionProjectionLease(actorId, runId));
        }

        public async Task AttachLiveSinkAsync(
            IScriptExecutionProjectionLease lease,
            IEventSink<EventEnvelope> sink,
            CancellationToken ct = default)
        {
            _ = lease;
            _ = ct;

            foreach (var message in Messages)
            {
                try
                {
                    await sink.PushAsync(message, CancellationToken.None);
                }
                catch (EventSinkCompletedException)
                {
                    break;
                }
            }
        }

        public Task DetachLiveSinkAsync(
            IScriptExecutionProjectionLease lease,
            IEventSink<EventEnvelope> sink,
            CancellationToken ct = default)
        {
            _ = lease;
            _ = sink;
            _ = ct;
            return Task.CompletedTask;
        }

        public Task ReleaseActorProjectionAsync(
            IScriptExecutionProjectionLease lease,
            CancellationToken ct = default)
        {
            _ = lease;
            _ = ct;
            return Task.CompletedTask;
        }
    }

    private sealed record StubScriptExecutionProjectionLease(string ActorId, string RunId) : IScriptExecutionProjectionLease;

    private sealed class StubScriptRuntimeCommandPort : IScriptRuntimeCommandPort
    {
        public List<ScriptRuntimeInvocation> Invocations { get; } = [];

        public Task RunRuntimeAsync(
            string runtimeActorId,
            string runId,
            Any? inputPayload,
            string scriptRevision,
            string definitionActorId,
            string requestedEventType,
            CancellationToken ct) =>
            RunRuntimeAsync(
                runtimeActorId,
                runId,
                inputPayload,
                scriptRevision,
                definitionActorId,
                requestedEventType,
                scopeId: null,
                ct);

        public Task RunRuntimeAsync(
            string runtimeActorId,
            string runId,
            Any? inputPayload,
            string scriptRevision,
            string definitionActorId,
            string requestedEventType,
            string? scopeId,
            CancellationToken ct)
        {
            _ = ct;
            Invocations.Add(new ScriptRuntimeInvocation(
                runtimeActorId,
                runId,
                inputPayload?.Clone() ?? new Any(),
                scriptRevision,
                definitionActorId,
                requestedEventType,
                scopeId));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingScriptRuntimeCommandPort(Exception exception) : IScriptRuntimeCommandPort
    {
        public Task RunRuntimeAsync(
            string runtimeActorId,
            string runId,
            Any? inputPayload,
            string scriptRevision,
            string definitionActorId,
            string requestedEventType,
            CancellationToken ct) =>
            Task.FromException(exception);

        public Task RunRuntimeAsync(
            string runtimeActorId,
            string runId,
            Any? inputPayload,
            string scriptRevision,
            string definitionActorId,
            string requestedEventType,
            string? scopeId,
            CancellationToken ct) =>
            Task.FromException(exception);
    }

    private sealed record ScriptRuntimeInvocation(
        string RuntimeActorId,
        string RunId,
        Any InputPayload,
        string ScriptRevision,
        string DefinitionActorId,
        string RequestedEventType,
        string? ScopeId);

    private sealed class RecordingProjectionSessionEventHub : Aevatar.CQRS.Projection.Core.Abstractions.IProjectionSessionEventHub<AGUIEvent>
    {
        public List<(string ScopeId, string SessionId, AGUIEvent Event)> Published { get; } = [];

        public Task PublishAsync(string scopeId, string sessionId, AGUIEvent evt, CancellationToken ct = default)
        {
            _ = ct;
            Published.Add((scopeId, sessionId, evt));
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync(string scopeId, string sessionId, Func<AGUIEvent, ValueTask> handler, CancellationToken ct = default)
        {
            _ = scopeId;
            _ = sessionId;
            _ = handler;
            _ = ct;
            return Task.FromResult<IAsyncDisposable>(new NoopAsyncDisposable());
        }
    }

    private sealed class RecordingScriptExecutionSessionEventHub
        : Aevatar.CQRS.Projection.Core.Abstractions.IProjectionSessionEventHub<EventEnvelope>
    {
        public List<(string ScopeId, string SessionId, EventEnvelope Event)> Published { get; } = [];

        public Task PublishAsync(string scopeId, string sessionId, EventEnvelope evt, CancellationToken ct = default)
        {
            _ = ct;
            Published.Add((scopeId, sessionId, evt));
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync(string scopeId, string sessionId, Func<EventEnvelope, ValueTask> handler, CancellationToken ct = default)
        {
            _ = scopeId;
            _ = sessionId;
            _ = handler;
            _ = ct;
            return Task.FromResult<IAsyncDisposable>(new NoopAsyncDisposable());
        }
    }

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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

            return Task.FromResult<IAsyncDisposable>(new NoopAsyncDisposable());
        }
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
