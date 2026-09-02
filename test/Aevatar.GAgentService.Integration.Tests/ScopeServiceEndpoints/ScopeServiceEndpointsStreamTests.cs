using System.Reflection;
using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Abstractions.ScopeScripts;
using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.GAgentService.Projection.Orchestration;
using Aevatar.GAgentService.Projection.Projectors;
using Aevatar.AGUI.Contracts;
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

[Collection(ScopeServiceEndpointCollection.Name)]
public sealed class ScopeServiceEndpointsStreamTests : ScopeServiceEndpointStreamTestKit
{
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

        interactionPort.Requests.Should().ContainSingle().Which.AgentKind.Should().Be(typeof(StreamTestAgent).AssemblyQualifiedName!);
        var body = await ReadBodyAsync(http);
        body.Should().Contain("runStarted");
        body.Should().Contain("runError");
    }

    [Fact]
    public async Task HandleGAgentServiceChatStreamAsync_ShouldDelegateToStaticInvocationPort_AndStreamFrames()
    {
        var http = CreateHttpContext();
        var invocationPort = new StubStaticGAgentStreamInvocationPort
        {
            ResultFactory = async (request, emitAsync, onAcceptedAsync, ct) =>
            {
                var receipt = new StaticGAgentStreamAcceptedReceipt(
                    new ServiceInvocationAcceptedReceipt
                    {
                        ServiceKey = "svc-key",
                        DeploymentId = "dep-1",
                        TargetActorId = request.Input.PreferredActorId,
                        EndpointId = request.EndpointId,
                        CommandId = "cmd-static-1",
                        CorrelationId = "corr-static-1",
                    },
                    new GAgentDraftRunAcceptedReceipt(
                        request.Input.PreferredActorId ?? "actor-1",
                        typeof(StreamTestAgent).AssemblyQualifiedName!,
                        "cmd-static-1",
                        "corr-static-1",
                        "session-1"));

                if (onAcceptedAsync != null)
                    await onAcceptedAsync(receipt, ct);

                await emitAsync(
                    new AGUIEvent
                    {
                        TextMessageEnd = new Aevatar.AGUI.Contracts.TextMessageEndEvent
                        {
                            MessageId = "msg-1",
                        },
                    },
                    ct);

                return new StaticGAgentStreamInvocationResult(
                    receipt,
                    GAgentDraftRunStartError.None,
                    GAgentDraftRunCompletionStatus.TextMessageCompleted,
                    CompletionObserved: true);
            },
        };

        await InvokeStaticStreamAsync(
            http,
            CreateStaticTarget(typeof(StreamTestAgent).AssemblyQualifiedName!, primaryActorId: "actor-1"),
            "hello",
            "actor-1",
            "session-1",
            "scope-a",
            new Dictionary<string, string> { ["trace-id"] = "abc" },
            null,
            invocationPort,
            CancellationToken.None);

        invocationPort.Requests.Should().ContainSingle();
        var request = invocationPort.Requests[0];
        request.EndpointId.Should().Be("chat");
        request.Input.Prompt.Should().Be("hello");
        request.Input.PreferredActorId.Should().Be("actor-1");
        request.Input.SessionId.Should().Be("session-1");
        request.Input.Headers.Should().ContainKey("trace-id").WhoseValue.Should().Be("abc");

        var body = await ReadBodyAsync(http);
        body.Should().Contain("runStarted");
        body.Should().Contain("textMessageEnd");
    }

    [Fact]
    public async Task HandleGAgentServiceChatStreamAsync_ShouldMapAllInputPartKinds()
    {
        var http = CreateHttpContext();
        var invocationPort = new StubStaticGAgentStreamInvocationPort();

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
            invocationPort,
            CancellationToken.None);

        invocationPort.Requests.Should().ContainSingle();
        invocationPort.Requests[0].Input.InputParts.Should().NotBeNull();
        invocationPort.Requests[0].Input.InputParts!.Select(part => part.Kind).Should().Equal(
            GAgentDraftRunInputPartKind.Image,
            GAgentDraftRunInputPartKind.Audio,
            GAgentDraftRunInputPartKind.Video,
            GAgentDraftRunInputPartKind.Text,
            GAgentDraftRunInputPartKind.Unspecified);

        var body = await ReadBodyAsync(http);
        body.Should().Contain("runStarted");
    }

    [Fact]
    public async Task HandleGAgentServiceChatStreamAsync_ShouldPreserveRunErrorWithoutSyntheticFinish()
    {
        var http = CreateHttpContext();
        var invocationPort = new StubStaticGAgentStreamInvocationPort
        {
            ResultFactory = async (request, emitAsync, onAcceptedAsync, ct) =>
            {
                var receipt = new StaticGAgentStreamAcceptedReceipt(
                    new ServiceInvocationAcceptedReceipt
                    {
                        ServiceKey = "svc-key",
                        DeploymentId = "dep-1",
                        TargetActorId = request.Input.PreferredActorId,
                        EndpointId = request.EndpointId,
                        CommandId = "cmd-static-1",
                        CorrelationId = "corr-static-1",
                    },
                    new GAgentDraftRunAcceptedReceipt(
                        request.Input.PreferredActorId ?? "actor-1",
                        typeof(StreamTestAgent).AssemblyQualifiedName!,
                        "cmd-static-1",
                        "corr-static-1"));

                if (onAcceptedAsync != null)
                    await onAcceptedAsync(receipt, ct);

                await emitAsync(
                    new AGUIEvent
                    {
                        RunError = new RunErrorEvent
                        {
                            Message = "failed",
                        },
                    },
                    ct);

                return new StaticGAgentStreamInvocationResult(
                    receipt,
                    GAgentDraftRunStartError.None,
                    GAgentDraftRunCompletionStatus.Failed,
                    CompletionObserved: true);
            },
        };

        await InvokeStaticStreamAsync(
            http,
            CreateStaticTarget(typeof(StreamTestAgent).AssemblyQualifiedName!, primaryActorId: "actor-1"),
            "hello",
            "actor-1",
            null,
            "scope-a",
            null,
            null,
            invocationPort,
            CancellationToken.None);

        var body = await ReadBodyAsync(http);
        body.Should().Contain("runError");
        body.Should().NotContain("runFinished");
    }

    [Fact]
    public async Task HandleGAgentServiceChatStreamAsync_ShouldThrow_WhenAgentTypeCannotBeResolved()
    {
        var invocationPort = new StubStaticGAgentStreamInvocationPort
        {
            ResultFactory = (request, emitAsync, onAcceptedAsync, ct) =>
                Task.FromResult(new StaticGAgentStreamInvocationResult(
                    null,
                    GAgentDraftRunStartError.UnknownAgentKind,
                    GAgentDraftRunCompletionStatus.Unknown,
                    CompletionObserved: false)),
        };
        var act = () => InvokeStaticStreamAsync(
            CreateHttpContext(),
            CreateStaticTarget("Missing.Agent, Missing.Assembly", primaryActorId: "actor-1"),
            "hello",
            "actor-1",
            null,
            "scope-a",
            null,
            null,
            invocationPort,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*could not be resolved*");
    }

    [Fact]
    public async Task GAgentDraftRunSessionEventProjector_ShouldIgnoreRawTextFrame_WithoutCommittedCompletion()
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

        sessionHub.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task GAgentDraftRunSessionEventProjector_ShouldPublishExplicitAguiObservation_ToCommandSession()
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
                Payload = Any.Pack(new AGUIEvent
                {
                    TextMessageContent = new Aevatar.AGUI.Contracts.TextMessageContentEvent
                    {
                        MessageId = "msg-1",
                        Delta = "hello",
                    },
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
                    Outcome = RoleChatSessionOutcome.Unspecified,
                },
                correlationId: "cmd-1"),
            CancellationToken.None);

        var published = sessionHub.Published.Should().ContainSingle().Subject;
        published.ScopeId.Should().Be("actor-1");
        published.SessionId.Should().Be("cmd-1");
        published.Event.RunError.Should().NotBeNull();
        published.Event.RunError!.Message.Should().Be("NyxID authentication required for provider 'nyxid'. Please sign in.");
    }

    [Theory]
    [InlineData(RoleChatSessionOutcome.Failed, "SESSION_ORPHANED", "The interrupted session cannot be resumed.", "The interrupted session cannot be resumed.")]
    [InlineData(RoleChatSessionOutcome.OutcomeUncertain, "SESSION_OUTCOME_UNCERTAIN", " ", "SESSION_OUTCOME_UNCERTAIN")]
    public async Task GAgentDraftRunSessionEventProjector_ShouldPublishRunError_FromTypedFailureWithEmptyContent(
        RoleChatSessionOutcome outcome,
        string failureCode,
        string safeMessage,
        string expectedMessage)
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
                    Content = string.Empty,
                    Outcome = outcome,
                    FailureCode = failureCode,
                    SafeMessage = safeMessage,
                },
                correlationId: "cmd-1"),
            CancellationToken.None);

        var published = sessionHub.Published.Should().ContainSingle().Subject;
        published.Event.RunError.Should().NotBeNull();
        published.Event.RunError!.Message.Should().Be(expectedMessage);
        published.Event.RunError.Code.Should().Be(failureCode);
    }

    [Fact]
    public async Task GAgentDraftRunSessionEventProjector_ShouldNotInterpretLegacyFailureMarker_WhenOutcomeIsCompleted()
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
                    Content = "[[AEVATAR_LLM_ERROR]] preserved assistant text",
                    ContentEmitted = true,
                    Outcome = RoleChatSessionOutcome.Completed,
                },
                correlationId: "cmd-1"),
            CancellationToken.None);

        sessionHub.Published.Should().HaveCount(3);
        sessionHub.Published.Should().NotContain(entry =>
            entry.Event.EventCase == AGUIEvent.EventOneofCase.RunError);
        sessionHub.Published[^1].Event.EventCase.Should().Be(AGUIEvent.EventOneofCase.RunFinished);
    }

    [Fact]
    public async Task GAgentDraftRunSessionEventProjector_ShouldPublishContentFrames_FromCommittedTerminalSuccess_WhenActorEmittedContent()
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
                    Usage = new Aevatar.AI.Abstractions.TokenUsagePayload
                    {
                        PromptTokens = 11,
                        CompletionTokens = 7,
                        TotalTokens = 18,
                    },
                    Model = "nyxid-model",
                },
                correlationId: "cmd-1"),
            CancellationToken.None);

        sessionHub.Published.Should().HaveCount(3);
        sessionHub.Published[0].Event.Usage.Should().NotBeNull();
        sessionHub.Published[0].Event.Usage.Available.Should().BeTrue();
        sessionHub.Published[0].Event.Usage.PromptTokens.Should().Be(11);
        sessionHub.Published[0].Event.Usage.CompletionTokens.Should().Be(7);
        sessionHub.Published[0].Event.Usage.TotalTokens.Should().Be(18);
        sessionHub.Published[0].Event.Usage.Model.Should().Be("nyxid-model");
        sessionHub.Published[1].Event.TextMessageEnd.Should().NotBeNull();
        sessionHub.Published[1].Event.TextMessageEnd!.MessageId.Should().Be("cmd-1");
        sessionHub.Published[2].Event.RunFinished.Should().NotBeNull();
        sessionHub.Published[2].Event.RunFinished!.ThreadId.Should().Be("actor-1");
        sessionHub.Published[2].Event.RunFinished.RunId.Should().Be("cmd-1");
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

        sessionHub.Published.Should().HaveCount(5);
        sessionHub.Published[0].Event.TextMessageStart.Should().NotBeNull();
        sessionHub.Published[0].Event.TextMessageStart!.MessageId.Should().Be("cmd-1");
        sessionHub.Published[0].Event.TextMessageStart.Role.Should().Be("assistant");
        sessionHub.Published[1].Event.TextMessageContent.Should().NotBeNull();
        sessionHub.Published[1].Event.TextMessageContent!.MessageId.Should().Be("cmd-1");
        sessionHub.Published[1].Event.TextMessageContent.Delta.Should().Be("pong");
        sessionHub.Published[2].Event.Usage.Should().NotBeNull();
        sessionHub.Published[2].Event.Usage.Available.Should().BeFalse();
        sessionHub.Published[3].Event.TextMessageEnd.Should().NotBeNull();
        sessionHub.Published[3].Event.TextMessageEnd!.MessageId.Should().Be("cmd-1");
        sessionHub.Published[4].Event.RunFinished.Should().NotBeNull();
        sessionHub.Published[4].Event.RunFinished!.ThreadId.Should().Be("actor-1");
        sessionHub.Published[4].Event.RunFinished.RunId.Should().Be("cmd-1");
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
    public async Task GAgentDraftRunSessionEventProjector_ShouldIgnoreRawTextMessageEnd_WithoutCommittedCompletion()
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

        sessionHub.Published.Should().BeEmpty();
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

        sessionHub.Published.Should().HaveCount(3);
        sessionHub.Published[0].Event.Usage.Should().NotBeNull();
        sessionHub.Published[0].Event.Usage.Available.Should().BeFalse();
        sessionHub.Published[1].Event.TextMessageEnd.Should().NotBeNull();
        sessionHub.Published[1].Event.TextMessageEnd!.MessageId.Should().Be("cmd-1");
        sessionHub.Published[2].Event.RunFinished.Should().NotBeNull();
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

        sessionHub.Published.Should().HaveCount(3);
        sessionHub.Published[0].Event.TextMessageEnd.MessageId.Should().Be("msg-1");
        sessionHub.Published[1].Event.Usage.Should().NotBeNull();
        sessionHub.Published[1].Event.Usage.Available.Should().BeFalse();
        sessionHub.Published[2].Event.RunFinished.ThreadId.Should().Be("runtime-1");
        sessionHub.Published[2].Event.RunFinished.RunId.Should().Be("run-1");
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
                    TextMessageEnd = new Aevatar.AGUI.Contracts.TextMessageEndEvent { MessageId = "msg-1" },
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
    public async Task HandleGAgentServiceChatStreamAsync_ShouldReturnServiceUnavailableJson_WhenProjectionUnavailableBeforeSseStarts()
    {
        var http = CreateHttpContext();
        var invocationPort = new StubStaticGAgentStreamInvocationPort
        {
            ResultFactory = (_, _, _, _) => Task.FromResult(new StaticGAgentStreamInvocationResult(
                Accepted: null,
                StartError: GAgentDraftRunStartError.ProjectionUnavailable,
                CompletionStatus: GAgentDraftRunCompletionStatus.Unknown,
                CompletionObserved: false)),
        };

        await InvokeStaticStreamAsync(
            http,
            CreateStaticTarget(typeof(StreamTestAgent).AssemblyQualifiedName!, primaryActorId: "actor-1"),
            "hello",
            "actor-1",
            null,
            "scope-a",
            null,
            null,
            invocationPort,
            CancellationToken.None);

        http.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        http.Response.ContentType.Should().StartWith("application/json");
        var body = await ReadBodyAsync(http);
        body.Should().Contain("GAGENT_PROJECTION_UNAVAILABLE");
        body.Should().NotContain("runStarted");
    }

    [Fact]
    public async Task HandleGAgentServiceChatStreamAsync_ShouldWriteRunError_WhenProjectionUnavailableAfterSseStarts()
    {
        var http = CreateHttpContext();
        var invocationPort = new StubStaticGAgentStreamInvocationPort
        {
            ResultFactory = async (request, _, onAcceptedAsync, ct) =>
            {
                var receipt = new StaticGAgentStreamAcceptedReceipt(
                    new ServiceInvocationAcceptedReceipt
                    {
                        ServiceKey = "svc-key",
                        DeploymentId = "dep-1",
                        TargetActorId = request.Input.PreferredActorId,
                        EndpointId = request.EndpointId,
                        CommandId = "cmd-static-1",
                        CorrelationId = "corr-static-1",
                    },
                    new GAgentDraftRunAcceptedReceipt(
                        request.Input.PreferredActorId ?? "actor-1",
                        typeof(StreamTestAgent).AssemblyQualifiedName!,
                        "cmd-static-1",
                        "corr-static-1"));

                if (onAcceptedAsync != null)
                    await onAcceptedAsync(receipt, ct);

                return new StaticGAgentStreamInvocationResult(
                    receipt,
                    GAgentDraftRunStartError.ProjectionUnavailable,
                    GAgentDraftRunCompletionStatus.Unknown,
                    CompletionObserved: false);
            },
        };

        await InvokeStaticStreamAsync(
            http,
            CreateStaticTarget(typeof(StreamTestAgent).AssemblyQualifiedName!, primaryActorId: "actor-1"),
            "hello",
            "actor-1",
            null,
            "scope-a",
            null,
            null,
            invocationPort,
            CancellationToken.None);

        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        var body = await ReadBodyAsync(http);
        body.Should().Contain("runStarted");
        body.Should().Contain("runError");
        body.Should().Contain("GAGENT_PROJECTION_UNAVAILABLE");
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
}
