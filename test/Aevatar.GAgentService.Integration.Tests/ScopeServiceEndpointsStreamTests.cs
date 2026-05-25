using System.Reflection;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Core;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Core.Commands;
using Aevatar.CQRS.Core.Interactions;
using Aevatar.CQRS.Core.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Abstractions.ScopeScripts;
using Aevatar.GAgentService.Application.ScopeGAgents;
using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.GAgentService.Projection.Orchestration;
using Aevatar.GAgentService.Projection.Projectors;
using Aevatar.Presentation.AGUI;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.Projectors;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

    private static readonly MethodInfo HandleDraftRunMethod = typeof(ScopeGAgentEndpoints)
        .GetMethod("HandleDraftRunAsync", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("HandleDraftRunAsync not found.");

    [Fact]
    public void ScopeServiceEndpoints_ShouldNotContainHostAguiMappingPump()
    {
        var methods = typeof(ScopeServiceEndpoints)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
            .Select(method => method.Name)
            .ToArray();

        methods.Should().NotContain("PumpScriptEventsAsync");
        methods.Should().NotContain("ShouldEmitSyntheticRunFinished");

        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../src/platform/Aevatar.GAgentService.Hosting/Endpoints/ScopeServiceEndpoints.cs"));
        source.Should().NotContain("IScriptRuntimeCommandPort");
        source.Should().NotContain("IScriptServiceAguiProjectionPort");
        source.Should().NotContain("EnsureRunProjectionAsync");
        source.Should().NotContain("EnsureAndAttachLeaseAsync");
        source.Should().NotContain("RunRuntimeAsync");
        source.Should().NotContain("private const string DefaultChatWorkflowYaml");
        source.Should().NotContain("name: default_chat");
        source.Should().NotContain("HasServiceAsync(identity");
    }

    [Fact]
    public async Task HandleDraftRunAsync_ShouldMapInteractionPortFailure_WhenFailureOccursAfterAcceptedFrame()
    {
        var http = CreateHttpContext();
        var interactionPort = new FailingAfterAcceptedDraftRunInteractionPort();

        await InvokeDraftRunAsync(
            http,
            "scope-a",
            new ScopeGAgentEndpoints.GAgentDraftRunHttpRequest(
                typeof(StreamTestAgent).AssemblyQualifiedName!,
                "hello"),
            interactionPort,
            CancellationToken.None);

        interactionPort.Requests.Should().ContainSingle().Which.ActorTypeName.Should().Be(typeof(StreamTestAgent).AssemblyQualifiedName!);
        var body = await ReadBodyAsync(http);
        body.Should().Contain("runStarted");
        body.Should().Contain("runError");
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

        runtime.CreateCalls.Should().ContainSingle(call => !string.IsNullOrWhiteSpace(call.Id));
        var actor = runtime.Actors.Values.Should().ContainSingle().Subject.Should().BeOfType<StubActor>().Subject;
        var envelope = actor.HandledEnvelopes.Should().ContainSingle().Subject;
        var request = envelope.Payload.Unpack<ChatRequestEvent>();
        request.SessionId.Should().Be(envelope.Propagation.CorrelationId);
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
    public async Task HandleGAgentServiceChatStreamAsync_WithMockProvider_ShouldStreamRoleContentThroughDraftRunPipeline()
    {
        var http = CreateHttpContext();
        var provider = new StreamingMockLlmProviderFactory(
            "refund request ",
            "classified as billing_support");
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<ILLMProviderFactory>(provider)
            .AddAevatarRuntime()
            .BuildServiceProvider();
        var runtime = services.GetRequiredService<IActorRuntime>();
        var streamProvider = services.GetRequiredService<IStreamProvider>();
        var actorId = $"role-draft-run-{Guid.NewGuid():N}";
        await SeedRoleInitializationAsync(
            services.GetRequiredService<IEventStore>(),
            actorId,
            provider.Name);

        var projectionPort = new StreamBackedDraftRunProjectionPort(streamProvider);
        var interactionService = CreateStaticStreamInteractionService(runtime, projectionPort);

        await InvokeStaticStreamAsync(
            http,
            CreateStaticTarget(typeof(RoleGAgent).AssemblyQualifiedName!, primaryActorId: actorId),
            "Classify this refund request.",
            actorId,
            null,
            "scope-a",
            null,
            null,
            interactionService,
            CancellationToken.None);

        var body = await ReadBodyAsync(http);
        body.Should().Contain("runStarted");
        body.Should().Contain("textMessageStart");
        body.Should().Contain("textMessageContent");
        body.Should().Contain("refund request ");
        body.Should().Contain("classified as billing_support");
        body.Should().Contain("textMessageEnd");
        body.Should().Contain("runFinished");
        body.Should().NotContain("runError");
        provider.StreamCallCount.Should().Be(1);
        provider.StreamRequests.Should().ContainSingle(x => x.RequestId == ExtractCorrelationId(body));
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
    public async Task GAgentDraftRunSessionEventProjector_ShouldPublishRunError_FromCommittedTerminalFailure()
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
            WrapCommittedCompletion(
                new RoleChatSessionCompletedEvent
                {
                    SessionId = "cmd-1",
                    Content = "[[AEVATAR_LLM_ERROR]] NyxID authentication required for provider 'nyxid'. Please sign in.",
                },
                correlationId: "cmd-1"),
            CancellationToken.None);

        var published = sessionHub.Published.Should().ContainSingle().Subject;
        published.ScopeId.Should().Be("actor-1");
        published.SessionId.Should().Be("cmd-1");
        published.Event.RunError.Should().NotBeNull();
        published.Event.RunError!.Message.Should().Be("NyxID authentication required for provider 'nyxid'. Please sign in.");
    }

    [Fact]
    public async Task GAgentDraftRunSessionEventProjector_ShouldPublishTerminalFrames_FromCommittedTerminalSuccess_WhenActorEmittedContent()
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
            WrapCommittedCompletion(
                new RoleChatSessionCompletedEvent
                {
                    SessionId = "cmd-1",
                    Content = "pong",
                    ContentEmitted = true,
                },
                correlationId: "cmd-1"),
            CancellationToken.None);

        sessionHub.Published.Should().HaveCount(2);
        sessionHub.Published[0].Event.TextMessageEnd.Should().NotBeNull();
        sessionHub.Published[0].Event.TextMessageEnd!.MessageId.Should().Be("cmd-1");
        sessionHub.Published[1].Event.RunFinished.Should().NotBeNull();
        sessionHub.Published[1].Event.RunFinished!.ThreadId.Should().Be("actor-1");
        sessionHub.Published[1].Event.RunFinished.RunId.Should().Be("cmd-1");
    }

    [Fact]
    public async Task GAgentDraftRunSessionEventProjector_ShouldPublishContentFrames_FromCommittedTerminalSuccess_WhenContentWasNotEmitted()
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
            WrapCommittedCompletion(
                new RoleChatSessionCompletedEvent
                {
                    SessionId = "cmd-1",
                    Content = "pong",
                    ContentEmitted = false,
                },
                correlationId: "cmd-1"),
            CancellationToken.None);

        sessionHub.Published.Should().HaveCount(4);
        sessionHub.Published[0].Event.TextMessageStart.Should().NotBeNull();
        sessionHub.Published[0].Event.TextMessageStart!.MessageId.Should().Be("cmd-1");
        sessionHub.Published[0].Event.TextMessageStart.Role.Should().Be("assistant");
        sessionHub.Published[1].Event.TextMessageContent.Should().NotBeNull();
        sessionHub.Published[1].Event.TextMessageContent!.MessageId.Should().Be("cmd-1");
        sessionHub.Published[1].Event.TextMessageContent.Delta.Should().Be("pong");
        sessionHub.Published[2].Event.TextMessageEnd.Should().NotBeNull();
        sessionHub.Published[2].Event.TextMessageEnd!.MessageId.Should().Be("cmd-1");
        sessionHub.Published[3].Event.RunFinished.Should().NotBeNull();
        sessionHub.Published[3].Event.RunFinished!.ThreadId.Should().Be("actor-1");
        sessionHub.Published[3].Event.RunFinished.RunId.Should().Be("cmd-1");
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
    public async Task GAgentDraftRunSessionEventProjector_ShouldIgnoreEnvelope_WhenContextSessionIsMissing()
    {
        var sessionHub = new RecordingProjectionSessionEventHub();
        var projector = new GAgentDraftRunSessionEventProjector(sessionHub);

        await projector.ProjectAsync(
            new GAgentDraftRunProjectionContext
            {
                RootActorId = "actor-1",
                SessionId = " ",
                ProjectionKind = "service-draft-run-session",
            },
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

        sessionHub.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task GAgentDraftRunSessionEventProjector_ShouldIgnoreUnmappedEnvelope_ForMatchingSession()
    {
        var sessionHub = new RecordingProjectionSessionEventHub();
        var projector = new GAgentDraftRunSessionEventProjector(sessionHub);

        await projector.ProjectAsync(
            new GAgentDraftRunProjectionContext
            {
                RootActorId = "actor-1",
                SessionId = "cmd-1",
                ProjectionKind = "service-draft-run-session",
            },
            new EventEnvelope
            {
                Propagation = new EnvelopePropagation
                {
                    CorrelationId = "cmd-1",
                },
                Payload = Any.Pack(new StringValue { Value = "ignored" }),
            },
            CancellationToken.None);

        sessionHub.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task GAgentDraftRunSessionEventProjector_ShouldAppendRunFinished_ForLiveTextMessageEnd()
    {
        var sessionHub = new RecordingProjectionSessionEventHub();
        var projector = new GAgentDraftRunSessionEventProjector(sessionHub);

        await projector.ProjectAsync(
            new GAgentDraftRunProjectionContext
            {
                RootActorId = "actor-1",
                SessionId = "cmd-1",
                ProjectionKind = "service-draft-run-session",
            },
            new EventEnvelope
            {
                Propagation = new EnvelopePropagation
                {
                    CorrelationId = "cmd-1",
                },
                Payload = Any.Pack(new AiTextEndEvent
                {
                    SessionId = "msg-1",
                    Content = "done",
                }),
            },
            CancellationToken.None);

        sessionHub.Published.Should().HaveCount(2);
        sessionHub.Published[0].Event.TextMessageEnd.Should().NotBeNull();
        sessionHub.Published[0].Event.TextMessageEnd!.MessageId.Should().Be("msg-1");
        sessionHub.Published[1].Event.RunFinished.Should().NotBeNull();
        sessionHub.Published[1].Event.RunFinished!.ThreadId.Should().Be("actor-1");
        sessionHub.Published[1].Event.RunFinished.RunId.Should().Be("cmd-1");
    }

    [Fact]
    public async Task GAgentDraftRunSessionEventProjector_ShouldCompleteRunFinishedFrame_WhenIdsAreMissing()
    {
        var sessionHub = new RecordingProjectionSessionEventHub();
        var projector = new GAgentDraftRunSessionEventProjector(sessionHub);

        await projector.ProjectAsync(
            new GAgentDraftRunProjectionContext
            {
                RootActorId = "actor-1",
                SessionId = "cmd-1",
                ProjectionKind = "service-draft-run-session",
            },
            new EventEnvelope
            {
                Propagation = new EnvelopePropagation
                {
                    CorrelationId = "cmd-1",
                },
                Payload = Any.Pack(new AGUIEvent
                {
                    RunFinished = new RunFinishedEvent(),
                }),
            },
            CancellationToken.None);

        var published = sessionHub.Published.Should().ContainSingle().Subject;
        published.Event.RunFinished.Should().NotBeNull();
        published.Event.RunFinished!.ThreadId.Should().Be("actor-1");
        published.Event.RunFinished.RunId.Should().Be("cmd-1");
    }

    [Fact]
    public async Task GAgentDraftRunSessionEventProjector_ShouldPreserveRunFinishedFrameIds_WhenPresent()
    {
        var sessionHub = new RecordingProjectionSessionEventHub();
        var projector = new GAgentDraftRunSessionEventProjector(sessionHub);

        await projector.ProjectAsync(
            new GAgentDraftRunProjectionContext
            {
                RootActorId = "actor-1",
                SessionId = "cmd-1",
                ProjectionKind = "service-draft-run-session",
            },
            new EventEnvelope
            {
                Propagation = new EnvelopePropagation
                {
                    CorrelationId = "cmd-1",
                },
                Payload = Any.Pack(new AGUIEvent
                {
                    RunFinished = new RunFinishedEvent
                    {
                        ThreadId = "thread-existing",
                        RunId = "run-existing",
                    },
                }),
            },
            CancellationToken.None);

        var published = sessionHub.Published.Should().ContainSingle().Subject;
        published.Event.RunFinished.Should().NotBeNull();
        published.Event.RunFinished!.ThreadId.Should().Be("thread-existing");
        published.Event.RunFinished.RunId.Should().Be("run-existing");
    }

    [Fact]
    public async Task GAgentDraftRunSessionEventProjector_ShouldPublishTerminalFrames_FromCommittedEmptyCompletion()
    {
        var sessionHub = new RecordingProjectionSessionEventHub();
        var projector = new GAgentDraftRunSessionEventProjector(sessionHub);

        await projector.ProjectAsync(
            new GAgentDraftRunProjectionContext
            {
                RootActorId = "actor-1",
                SessionId = "cmd-1",
                ProjectionKind = "service-draft-run-session",
            },
            WrapCommittedCompletion(
                new RoleChatSessionCompletedEvent
                {
                    SessionId = " ",
                    Content = string.Empty,
                },
                correlationId: "cmd-1"),
            CancellationToken.None);

        sessionHub.Published.Should().HaveCount(2);
        sessionHub.Published[0].Event.TextMessageEnd.Should().NotBeNull();
        sessionHub.Published[0].Event.TextMessageEnd!.MessageId.Should().Be("cmd-1");
        sessionHub.Published[1].Event.RunFinished.Should().NotBeNull();
    }

    [Fact]
    public async Task GAgentDraftRunSessionEventProjector_ShouldPublishTypedRunFinishedResultWithoutSyntheticTextContent_WhenCompletionWasAlreadyEmitted()
    {
        var sessionHub = new RecordingProjectionSessionEventHub();
        var projector = new GAgentDraftRunSessionEventProjector(sessionHub);

        await projector.ProjectAsync(
            new GAgentDraftRunProjectionContext
            {
                RootActorId = "actor-1",
                SessionId = "cmd-1",
                ProjectionKind = "service-draft-run-session",
            },
            WrapCommittedCompletion(
                new RoleChatSessionCompletedEvent
                {
                    SessionId = "cmd-1",
                    Content = "committed final",
                    ContentEmitted = true,
                },
                correlationId: "cmd-1"),
            CancellationToken.None);

        sessionHub.Published.Should().HaveCount(2);
        sessionHub.Published.Should().NotContain(entry =>
            entry.Event.EventCase == AGUIEvent.EventOneofCase.TextMessageContent);
        var runFinished = sessionHub.Published[1].Event.RunFinished;
        runFinished.Should().NotBeNull();
        runFinished!.Result.Unpack<GAgentDraftRunResultPayload>().Output.Should().Be("committed final");
    }

    [Fact]
    public async Task GAgentDraftRunSessionEventProjector_ShouldPublishRunError_FromCommittedLlmRequestFailure()
    {
        var sessionHub = new RecordingProjectionSessionEventHub();
        var projector = new GAgentDraftRunSessionEventProjector(sessionHub);

        await projector.ProjectAsync(
            new GAgentDraftRunProjectionContext
            {
                RootActorId = "actor-1",
                SessionId = "cmd-1",
                ProjectionKind = "service-draft-run-session",
            },
            WrapCommittedCompletion(
                new RoleChatSessionCompletedEvent
                {
                    SessionId = "cmd-1",
                    Content = "LLM request failed [tools=none]: upstream",
                },
                correlationId: "cmd-1"),
            CancellationToken.None);

        var published = sessionHub.Published.Should().ContainSingle().Subject;
        published.Event.RunError.Should().NotBeNull();
        published.Event.RunError!.Message.Should().Be("upstream");
    }

    private static EventEnvelope WrapCommittedCompletion(
        RoleChatSessionCompletedEvent evt,
        string correlationId) =>
        new()
        {
            Id = "outer-evt-1",
            Route = EnvelopeRouteSemantics.CreateObserverPublication("actor-1"),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = correlationId,
            },
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = "evt-1",
                    Version = 1,
                    Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-05-20T00:00:00+00:00")),
                    EventData = Any.Pack(evt),
                },
                StateRoot = Any.Pack(new RoleGAgentState()),
            }),
        };

    [Fact]
    public async Task ScriptServiceAguiSessionEventProjector_ShouldPublishMappedAguiEvent_ToMatchingRunSession()
    {
        var sessionHub = new RecordingScriptExecutionSessionEventHub();
        var projector = new ScriptServiceAguiSessionEventProjector(sessionHub);
        var context = new ScriptServiceAguiProjectionContext
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
        published.Event.TextMessageContent.MessageId.Should().Be("msg-1");
        published.Event.TextMessageContent.Delta.Should().Be("hello");
    }

    [Fact]
    public async Task ScriptServiceAguiSessionEventProjector_ShouldPublishTerminalRunFinished_ForTextCompletion()
    {
        var sessionHub = new RecordingScriptExecutionSessionEventHub();
        var projector = new ScriptServiceAguiSessionEventProjector(sessionHub);
        var context = new ScriptServiceAguiProjectionContext
        {
            RootActorId = "runtime-1",
            SessionId = "run-1",
            ProjectionKind = "script-execution-session",
        };

        await projector.ProjectAsync(
            context,
            new EventEnvelope
            {
                Propagation = new EnvelopePropagation
                {
                    CorrelationId = "run-1",
                },
                Payload = Any.Pack(new AiTextEndEvent
                {
                    SessionId = "msg-1",
                    Content = "done",
                }),
            },
            CancellationToken.None);

        sessionHub.Published.Should().HaveCount(2);
        sessionHub.Published[0].Event.TextMessageEnd.MessageId.Should().Be("msg-1");
        sessionHub.Published[1].Event.RunFinished.ThreadId.Should().Be("runtime-1");
        sessionHub.Published[1].Event.RunFinished.RunId.Should().Be("run-1");
    }

    [Fact]
    public async Task HandleScriptingServiceChatStreamAsync_ShouldThrow_WhenPrimaryActorMissing()
    {
        var interactionService = new StubScriptServiceRunInteractionService
        {
            StartError = ScriptServiceRunStartError.RuntimeActorUnavailable(
                "Script runtime actor is not available. The service may not be activated."),
        };
        var act = () => InvokeScriptingStreamAsync(
            CreateHttpContext(),
            CreateScriptingTarget(primaryActorId: string.Empty),
            "hello",
            "session-1",
            "scope-a",
            null,
            interactionService,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*runtime actor is not available*");
    }

    [Fact]
    public async Task HandleScriptingServiceChatStreamAsync_ShouldThrow_WhenActorCannotBeResolved()
    {
        var interactionService = new StubScriptServiceRunInteractionService
        {
            StartError = ScriptServiceRunStartError.RuntimeActorUnavailable(
                "Script runtime actor 'actor-1' could not be resolved. The service may not be activated."),
        };
        var act = () => InvokeScriptingStreamAsync(
            CreateHttpContext(),
            CreateScriptingTarget(primaryActorId: "actor-1"),
            "hello",
            "session-1",
            "scope-a",
            null,
            interactionService,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*could not be resolved*");
    }

    [Fact]
    public async Task HandleScriptingServiceChatStreamAsync_ShouldWriteProjectionTerminalFrame()
    {
        var http = CreateHttpContext();
        var interactionService = new StubScriptServiceRunInteractionService
        {
            Messages =
            {
                new AGUIEvent
                {
                    TextMessageEnd = new Aevatar.Presentation.AGUI.TextMessageEndEvent { MessageId = "msg-1" },
                },
                new AGUIEvent
                {
                    RunFinished = new RunFinishedEvent { ThreadId = "actor-1", RunId = "run-1" },
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
            interactionService,
            CancellationToken.None);

        var command = interactionService.Commands.Should().ContainSingle().Subject;
        command.RuntimeActorId.Should().Be("actor-1");
        command.Headers.Should().Contain("trace-id", "abc");
        command.ScopeId.Should().Be("scope-a");
        command.SessionId.Should().Be("session-1");
        command.RunId.Should().NotBeNullOrWhiteSpace();
        command.CommandId.Should().NotBeNullOrWhiteSpace();
        command.CorrelationId.Should().NotBeNullOrWhiteSpace();
        command.CommandId.Should().NotBe(command.RunId);
        command.CorrelationId.Should().NotBe(command.RunId);
        command.CorrelationId.Should().NotBe(command.CommandId);

        var body = await ReadBodyAsync(http);
        body.Should().Contain("runStarted");
        body.Should().Contain(command.RunId);
        body.Should().NotContain(command.CommandId);
        body.Should().NotContain(command.CorrelationId);
        body.Should().Contain("textMessageEnd");
        body.Should().Contain("runFinished");
    }

    [Fact]
    public async Task HandleScriptingServiceChatStreamAsync_ShouldPreserveRunErrorWithoutSyntheticFinish()
    {
        var http = CreateHttpContext();
        var interactionService = new StubScriptServiceRunInteractionService
        {
            Messages =
            {
                new AGUIEvent
                {
                    RunError = new RunErrorEvent
                    {
                        Message = "failed",
                    },
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
            interactionService,
            CancellationToken.None);

        var body = await ReadBodyAsync(http);
        body.Should().Contain("runError");
        body.Should().NotContain("runFinished");
    }

    [Fact]
    public async Task HandleScriptingServiceChatStreamAsync_ShouldWriteRunError_WhenInteractionThrowsAfterAccepted()
    {
        var http = CreateHttpContext();
        var interactionService = new StubScriptServiceRunInteractionService
        {
            ThrowAfterAccepted = new InvalidOperationException("runtime dispatch failed"),
        };

        await InvokeScriptingStreamAsync(
            http,
            CreateScriptingTarget(primaryActorId: "actor-1"),
            "hello",
            "session-1",
            "scope-a",
            null,
            interactionService,
            CancellationToken.None);

        var body = await ReadBodyAsync(http);
        body.Should().Contain("runStarted");
        body.Should().Contain("runError");
        body.Should().Contain("runtime dispatch failed");
    }

    [Fact]
    public async Task HandleScriptingServiceChatStreamAsync_ShouldAvoidSyntheticDuplicateFinish_WhenRunFinishedArrives()
    {
        var http = CreateHttpContext();
        var interactionService = new StubScriptServiceRunInteractionService
        {
            Messages =
            {
                new AGUIEvent
                {
                    RunFinished = new RunFinishedEvent
                    {
                        ThreadId = "actor-1",
                        RunId = "run-1",
                    },
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
            interactionService,
            CancellationToken.None);

        var body = await ReadBodyAsync(http);
        body.Split("\"runFinished\"", StringSplitOptions.None).Length.Should().Be(2);
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();
        http.RequestServices = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Aevatar:Authentication:Enabled"] = "false",
                })
                .Build())
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment())
            .BuildServiceProvider();
        return http;
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Aevatar.GAgentService.Integration.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
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

    private static Task InvokeDraftRunAsync(
        HttpContext http,
        string scopeId,
        ScopeGAgentEndpoints.GAgentDraftRunHttpRequest request,
        IGAgentDraftRunInteractionPort interactionPort,
        CancellationToken ct) =>
        InvokePrivateTaskAsync(
            HandleDraftRunMethod,
            http,
            scopeId,
            request,
            interactionPort,
            NullLoggerFactory.Instance,
            ct);

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
            prompt,
            actorId,
            sessionId,
            headers,
            inputParts,
            target.Artifact.RevisionId,
            new ServiceInvocationRequest
            {
                Identity = target.Artifact.Identity.Clone(),
                EndpointId = target.Endpoint.EndpointId,
            },
            new TestStaticGAgentStreamInvocationPort(
                target.Artifact.DeploymentPlan.StaticPlan.ActorTypeName,
                target.Artifact.DeploymentPlan.StaticPlan.PreferredActorId,
                scopeId,
                interactionService),
            ct);

    private static Task InvokeScriptingStreamAsync(
        HttpContext http,
        ServiceInvocationResolvedTarget target,
        string prompt,
        string? sessionId,
        string scopeId,
        IReadOnlyDictionary<string, string>? headers,
        ICommandInteractionService<ScriptServiceRunCommand, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, AGUIEvent, ScriptServiceRunCompletionStatus> interactionService,
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
            interactionService,
            new ServiceInvocationRequest(),
            ct);

    private sealed class TestStaticGAgentStreamInvocationPort(
        string actorTypeName,
        string? defaultActorId,
        string scopeId,
        ICommandInteractionService<GAgentDraftRunCommand, GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, AGUIEvent, GAgentDraftRunCompletionStatus> interactionService)
        : IStaticGAgentStreamInvocationPort<AGUIEvent>
    {
        public async Task<StaticGAgentStreamInvocationResult> InvokeAsync(
            StaticGAgentStreamInvocationRequest request,
            Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
            Func<StaticGAgentStreamAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            var command = new GAgentDraftRunCommand(
                ScopeId: scopeId,
                ActorTypeName: actorTypeName,
                Prompt: request.Input.Prompt,
                PreferredActorId: request.Input.PreferredActorId ?? defaultActorId,
                SessionId: request.Input.SessionId,
                NyxIdAccessToken: null,
                ModelOverride: null,
                PreferredLlmRoute: null,
                Headers: request.Input.Headers,
                InputParts: request.Input.InputParts);

            StaticGAgentStreamAcceptedReceipt? accepted = null;
            var result = await interactionService.ExecuteAsync(
                command,
                emitAsync,
                async (receipt, token) =>
                {
                    accepted = new StaticGAgentStreamAcceptedReceipt(
                        new ServiceInvocationAcceptedReceipt
                        {
                            RequestId = receipt.CommandId,
                            TargetActorId = receipt.ActorId,
                            EndpointId = request.EndpointId,
                            CommandId = receipt.CommandId,
                            CorrelationId = receipt.CorrelationId,
                            RunId = receipt.CommandId,
                        },
                        receipt);

                    if (onAcceptedAsync != null)
                        await onAcceptedAsync(accepted, token);
                },
                ct);

            return new StaticGAgentStreamInvocationResult(
                accepted,
                result.Error,
                result.FinalizeResult?.Completion ?? GAgentDraftRunCompletionStatus.Unknown,
                result.FinalizeResult?.Completed ?? false);
        }
    }

    private sealed class FailingAfterAcceptedDraftRunInteractionPort : IGAgentDraftRunInteractionPort
    {
        public List<GAgentDraftRunInteractionRequest> Requests { get; } = [];

        public async Task<CommandInteractionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus>> ExecuteAsync(
            GAgentDraftRunInteractionRequest request,
            Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
            Func<GAgentDraftRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(emitAsync);
            Requests.Add(request);

            if (onAcceptedAsync != null)
            {
                await onAcceptedAsync(
                    new GAgentDraftRunAcceptedReceipt(
                        request.PreferredActorId ?? "actor-1",
                        request.ActorTypeName,
                        "cmd-1",
                        "corr-1"),
                    ct);
            }

            throw new InvalidOperationException("dispatch failed");
        }
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

    private static async Task SeedRoleInitializationAsync(
        IEventStore store,
        string actorId,
        string providerName)
    {
        var initialize = new InitializeRoleAgentEvent
        {
            RoleId = "role-refund-classifier",
            RoleName = "refund-classifier",
            ProviderName = providerName,
            SystemPrompt = "Classify refund requests.",
            MaxToolRounds = 1,
        };

        await store.AppendAsync(
            actorId,
            [
                new StateEvent
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                    Version = 1,
                    EventType = InitializeRoleAgentEvent.Descriptor.FullName,
                    EventData = Any.Pack(initialize),
                    AgentId = actorId,
                },
            ],
            expectedVersion: 0);
    }

    private static string ExtractCorrelationId(string sseBody)
    {
        using var document = ParseFirstEvent(sseBody, "runStarted");
        return document.RootElement
            .GetProperty("runStarted")
            .GetProperty("runId")
            .GetString()
            ?? throw new InvalidOperationException("runStarted.runId is missing.");
    }

    private static JsonDocument ParseFirstEvent(string sseBody, string eventProperty)
    {
        foreach (var line in sseBody.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
                continue;

            var document = JsonDocument.Parse(line["data: ".Length..]);
            if (document.RootElement.TryGetProperty(eventProperty, out _))
                return document;

            document.Dispose();
        }

        throw new InvalidOperationException($"SSE event '{eventProperty}' was not found.");
    }

    private static ICommandInteractionService<GAgentDraftRunCommand, GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, AGUIEvent, GAgentDraftRunCompletionStatus> CreateStaticStreamInteractionService(
        IActorRuntime runtime,
        StubDraftRunProjectionPort projectionPort)
    {
        return CreateStaticStreamInteractionService(
            runtime,
            projectionPort,
            new StubGAgentRunTerminalProjectionPort());
    }

    private static ICommandInteractionService<GAgentDraftRunCommand, GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, AGUIEvent, GAgentDraftRunCompletionStatus> CreateStaticStreamInteractionService(
        IActorRuntime runtime,
        IGAgentDraftRunProjectionPort projectionPort)
    {
        return CreateStaticStreamInteractionService(
            runtime,
            projectionPort,
            new StubGAgentRunTerminalProjectionPort());
    }

    private static ICommandInteractionService<GAgentDraftRunCommand, GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, AGUIEvent, GAgentDraftRunCompletionStatus> CreateStaticStreamInteractionService(
        IActorRuntime runtime,
        IGAgentDraftRunProjectionPort projectionPort,
        IGAgentRunTerminalProjectionPort terminalProjectionPort)
    {
        var pipeline = new DefaultCommandDispatchPipeline<GAgentDraftRunCommand, GAgentDraftRunCommandTarget, GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError>(
            new GAgentDraftRunCommandTargetResolver(
                runtime,
                projectionPort,
                terminalProjectionPort),
            new DefaultCommandContextPolicy(),
            new GAgentDraftRunCommandEnvelopeFactory(),
            new ActorCommandTargetDispatcher<GAgentDraftRunCommandTarget>(new RuntimeActorDispatchPort(runtime)),
            new GAgentDraftRunAcceptedReceiptFactory());

        return new DefaultCommandInteractionService<GAgentDraftRunCommand, GAgentDraftRunCommandTarget, GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, AGUIEvent, AGUIEvent, GAgentDraftRunCompletionStatus>(
            pipeline,
            new DefaultEventOutputStream<AGUIEvent, AGUIEvent>(new IdentityEventFrameMapper<AGUIEvent>()),
            new GAgentDraftRunCompletionPolicy(),
            new GAgentDraftRunFinalizeEmitter(),
            new GAgentDraftRunDurableCompletionResolver(new StubGAgentRunTerminalQueryPort()),
            NullLogger<DefaultCommandInteractionService<GAgentDraftRunCommand, GAgentDraftRunCommandTarget, GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, AGUIEvent, AGUIEvent, GAgentDraftRunCompletionStatus>>.Instance,
            new GAgentDraftRunObservationLifecycle(projectionPort, terminalProjectionPort),
            new GAgentDraftRunAcceptedReceiptFactory());
    }

    private sealed class StreamingMockLlmProviderFactory(params string[] chunks) : ILLMProviderFactory, ILLMProvider
    {
        public int StreamCallCount { get; private set; }
        public List<LLMRequest> StreamRequests { get; } = [];
        public string Name => "mock";

        public ILLMProvider GetProvider(string name)
        {
            _ = name;
            return this;
        }

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public Task<LLMResponse> ChatAsync(LLMRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new LLMResponse
            {
                Content = string.Concat(chunks),
                FinishReason = "stop",
                Usage = new TokenUsage(1, 1, 2),
            });
        }

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            StreamCallCount++;
            StreamRequests.Add(request);
            foreach (var chunk in chunks)
            {
                ct.ThrowIfCancellationRequested();
                yield return new LLMStreamChunk { DeltaContent = chunk };
                await Task.Yield();
            }

            yield return new LLMStreamChunk
            {
                IsLast = true,
                Usage = new TokenUsage(1, 1, 2),
            };
        }
    }

    private sealed class StreamBackedDraftRunProjectionPort(IStreamProvider streamProvider) : IGAgentDraftRunProjectionPort
    {
        private readonly IStreamProvider _streamProvider = streamProvider;

        public bool ProjectionEnabled => true;

        public async Task<EventSinkProjectionAttachment<IGAgentDraftRunProjectionLease>?> AttachExistingActorProjectionAsync(
            string actorId,
            string commandId,
            IEventSink<AGUIEvent> sink,
            CancellationToken ct = default)
        {
            var lease = new StubDraftRunProjectionLease(actorId, commandId);
            var liveSinkLease = await AttachLiveSinkAsync(lease, sink, ct);
            return new EventSinkProjectionAttachment<IGAgentDraftRunProjectionLease>(
                lease,
                liveSinkLease);
        }

        public async Task<IAsyncDisposable?> AttachLiveSinkAsync(
            IGAgentDraftRunProjectionLease lease,
            IEventSink<AGUIEvent> sink,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(lease);
            ArgumentNullException.ThrowIfNull(sink);

            if (lease is not StubDraftRunProjectionLease draftRunLease)
                throw new InvalidOperationException("Unsupported draft-run projection lease.");

            return await _streamProvider
                .GetStream(draftRunLease.ActorId)
                .SubscribeAsync<EventEnvelope>(async envelope =>
                {
                    if (!string.Equals(
                            envelope.Propagation?.CorrelationId,
                            draftRunLease.CommandId,
                            StringComparison.Ordinal))
                    {
                        return;
                    }

                    var mapped = ScopeGAgentAguiEventMapper.TryMap(envelope);
                    if (mapped == null)
                        return;

                    try
                    {
                        await sink.PushAsync(mapped, CancellationToken.None);
                    }
                    catch (EventSinkCompletedException)
                    {
                    }
                }, ct);
        }

        public async Task DetachLiveSinkAsync(
            IAsyncDisposable? liveSinkLease,
            CancellationToken ct = default)
        {
            _ = ct;
            if (liveSinkLease != null)
                await liveSinkLease.DisposeAsync();
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

    private sealed class RuntimeActorDispatchPort(IActorRuntime runtime) : IActorDispatchPort
    {
        public async Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            var actor = await runtime.GetAsync(actorId);
            if (actor == null)
                throw new InvalidOperationException($"Actor '{actorId}' not found.");

            await actor.HandleEventAsync(envelope, ct);
            return DispatchAdmissionFactory.Create(actorId, envelope);
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

        public async Task<EventSinkProjectionAttachment<IGAgentDraftRunProjectionLease>?> AttachExistingActorProjectionAsync(
            string actorId,
            string commandId,
            IEventSink<AGUIEvent> sink,
            CancellationToken ct = default)
        {
            var lease = new StubDraftRunProjectionLease(actorId, commandId);
            var liveSinkLease = await AttachLiveSinkAsync(lease, sink, ct);
            return new EventSinkProjectionAttachment<IGAgentDraftRunProjectionLease>(
                lease,
                liveSinkLease);
        }

        public async Task<IAsyncDisposable?> AttachLiveSinkAsync(
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

            return null;
        }

        public Task DetachLiveSinkAsync(
            IAsyncDisposable? liveSinkLease,
            CancellationToken ct = default)
        {
            _ = liveSinkLease;
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

    private sealed class StubGAgentRunTerminalProjectionPort : IGAgentRunTerminalProjectionPort
    {
        public Task<IGAgentRunTerminalProjectionLease?> AttachExistingProjectionAsync(
            string actorId,
            string correlationId,
            GAgentRunTerminalInteractionKind interactionKind,
            CancellationToken ct = default)
        {
            _ = ct;
            return Task.FromResult<IGAgentRunTerminalProjectionLease?>(
                new StubGAgentRunTerminalProjectionLease(actorId, correlationId, interactionKind));
        }

        public Task ReleaseProjectionAsync(
            IGAgentRunTerminalProjectionLease lease,
            CancellationToken ct = default)
        {
            _ = lease;
            _ = ct;
            return Task.CompletedTask;
        }
    }

    private sealed record StubGAgentRunTerminalProjectionLease(
        string ActorId,
        string CorrelationId,
        GAgentRunTerminalInteractionKind InteractionKind) : IGAgentRunTerminalProjectionLease;

    private sealed class StubGAgentRunTerminalQueryPort : IGAgentRunTerminalQueryPort
    {
        public Task<GAgentRunTerminalSnapshot?> GetByCorrelationIdAsync(
            string actorId,
            string correlationId,
            CancellationToken ct = default)
        {
            _ = actorId;
            _ = correlationId;
            _ = ct;
            return Task.FromResult<GAgentRunTerminalSnapshot?>(null);
        }

        public Task<GAgentRunTerminalSnapshot?> GetBySessionIdAsync(
            string actorId,
            string sessionId,
            CancellationToken ct = default)
        {
            _ = actorId;
            _ = sessionId;
            _ = ct;
            return Task.FromResult<GAgentRunTerminalSnapshot?>(null);
        }
    }

    private sealed class StubScriptServiceAguiProjectionPort : IScriptServiceAguiProjectionPort
    {
        public List<AGUIEvent> Messages { get; } = [];

        public bool ProjectionEnabled => true;

        public async Task<EventSinkProjectionAttachment<IScriptServiceAguiProjectionLease>?> AttachExistingRunProjectionAsync(
            string actorId,
            string runId,
            IEventSink<AGUIEvent> sink,
            CancellationToken ct = default)
        {
            var lease = new StubScriptServiceAguiProjectionLease(actorId, runId);
            var liveSinkLease = await AttachLiveSinkAsync(lease, sink, ct);
            return new EventSinkProjectionAttachment<IScriptServiceAguiProjectionLease>(lease, liveSinkLease);
        }

        public async Task<IAsyncDisposable?> AttachLiveSinkAsync(
            IScriptServiceAguiProjectionLease lease,
            IEventSink<AGUIEvent> sink,
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

            return null;
        }

        public Task DetachLiveSinkAsync(
            IAsyncDisposable? liveSinkLease,
            CancellationToken ct = default)
        {
            _ = liveSinkLease;
            _ = ct;
            return Task.CompletedTask;
        }

        public Task ReleaseActorProjectionAsync(
            IScriptServiceAguiProjectionLease lease,
            CancellationToken ct = default)
        {
            _ = lease;
            _ = ct;
            return Task.CompletedTask;
        }
    }

    private sealed record StubScriptServiceAguiProjectionLease(string ActorId, string RunId) : IScriptServiceAguiProjectionLease;

    private sealed class StubScriptServiceRunInteractionService
        : ICommandInteractionService<ScriptServiceRunCommand, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, AGUIEvent, ScriptServiceRunCompletionStatus>
    {
        public List<ScriptServiceRunCommand> Commands { get; } = [];

        public List<AGUIEvent> Messages { get; } = [];

        public ScriptServiceRunStartError? StartError { get; init; }

        public Exception? ThrowAfterAccepted { get; init; }

        public async Task<CommandInteractionResult<ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, ScriptServiceRunCompletionStatus>> ExecuteAsync(
            ScriptServiceRunCommand command,
            Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
            Func<ScriptServiceRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            Commands.Add(command);
            if (StartError != null)
                return CommandInteractionResult<ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, ScriptServiceRunCompletionStatus>.Failure(StartError);

            var receipt = new ScriptServiceRunAcceptedReceipt(
                command.RuntimeActorId,
                command.RunId,
                command.CommandId,
                command.CorrelationId);
            if (onAcceptedAsync != null)
                await onAcceptedAsync(receipt, ct);

            if (ThrowAfterAccepted != null)
                throw ThrowAfterAccepted;

            var completion = ScriptServiceRunCompletionStatus.Incomplete;
            var completed = false;
            foreach (var message in Messages)
            {
                await emitAsync(message, ct);
                if (message.EventCase == AGUIEvent.EventOneofCase.RunFinished)
                {
                    completion = ScriptServiceRunCompletionStatus.RunFinished;
                    completed = true;
                    break;
                }

                if (message.EventCase == AGUIEvent.EventOneofCase.RunError)
                {
                    completion = ScriptServiceRunCompletionStatus.RunError;
                    completed = true;
                    break;
                }
            }

            return CommandInteractionResult<ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, ScriptServiceRunCompletionStatus>.Success(
                receipt,
                new CommandInteractionFinalizeResult<ScriptServiceRunCompletionStatus>(completion, completed));
        }
    }

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
        : Aevatar.CQRS.Projection.Core.Abstractions.IProjectionSessionEventHub<AGUIEvent>
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
