using Aevatar.AGUI.Contracts;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.Tests;

public partial class NyxIdChatEndpointsCoverageTests
{
    [Fact]
    public async Task HandleStreamMessageAsync_ReusedLegacySessionId_ShouldCreateDistinctServerTurnIds()
    {
        var interactionService = new StubNyxIdChatInteractionService<NyxIdChatCommand>
        {
            Frames =
            {
                new AGUIEvent { RunFinished = new RunFinishedEvent() },
            },
        };
        var firstContext = CreateAuthorizedStreamContext();
        var secondContext = CreateAuthorizedStreamContext();

        await InvokeTaskAsync(
            "HandleStreamMessageAsync",
            firstContext,
            "scope-a",
            "actor-1",
            new NyxIdChatEndpoints.NyxIdChatStreamRequest(
                "first prompt",
                SessionId: "legacy-conversation-session",
                Type: "text"),
            new StubGAgentActorStore(),
            interactionService,
            NullLoggerFactory.Instance,
            CancellationToken.None);
        await InvokeTaskAsync(
            "HandleStreamMessageAsync",
            secondContext,
            "scope-a",
            "actor-1",
            new NyxIdChatEndpoints.NyxIdChatStreamRequest(
                "second prompt",
                SessionId: "legacy-conversation-session",
                Type: "text"),
            new StubGAgentActorStore(),
            interactionService,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var commands = interactionService.Commands.Cast<NyxIdChatCommand>().ToArray();
        commands.Should().HaveCount(2);
        commands.Select(static command => command.TurnId).Should().OnlyHaveUniqueItems();
        commands.Should().OnlyContain(static command => command.TurnId != "legacy-conversation-session");
        (await ReadResponseBodyAsync(firstContext)).Should()
            .Contain("RUN_FINISHED")
            .And.Contain(commands[0].TurnId);
        (await ReadResponseBodyAsync(secondContext)).Should()
            .Contain("RUN_FINISHED")
            .And.Contain(commands[1].TurnId);
    }

    [Fact]
    public async Task HandleStreamMessageAsync_ReusedClientRequestId_ShouldDeriveSameServerTurnId()
    {
        var interactionService = new StubNyxIdChatInteractionService<NyxIdChatCommand>
        {
            Frames =
            {
                new AGUIEvent { RunFinished = new RunFinishedEvent() },
            },
        };
        var firstContext = CreateAuthorizedStreamContext();
        var secondContext = CreateAuthorizedStreamContext();

        await InvokeTaskAsync(
            "HandleStreamMessageAsync",
            firstContext,
            "scope-a",
            "actor-1",
            new NyxIdChatEndpoints.NyxIdChatStreamRequest(
                "same prompt",
                SessionId: "ignored-legacy-a",
                ClientRequestId: "client-request-1",
                Type: "text"),
            new StubGAgentActorStore(),
            interactionService,
            NullLoggerFactory.Instance,
            CancellationToken.None);
        await InvokeTaskAsync(
            "HandleStreamMessageAsync",
            secondContext,
            "scope-a",
            "actor-1",
            new NyxIdChatEndpoints.NyxIdChatStreamRequest(
                "same prompt",
                SessionId: "ignored-legacy-b",
                ClientRequestId: "client-request-1",
                Type: "text"),
            new StubGAgentActorStore(),
            interactionService,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var commands = interactionService.Commands.Cast<NyxIdChatCommand>().ToArray();
        commands.Should().HaveCount(2);
        commands[0].TurnId.Should().Be(commands[1].TurnId);
        commands[0].TurnId.Should().StartWith("turn-");
        var firstBody = await ReadResponseBodyAsync(firstContext);
        var secondBody = await ReadResponseBodyAsync(secondContext);
        firstBody.Should().Contain("RUN_FINISHED").And.Contain(commands[0].TurnId);
        secondBody.Should().Contain("RUN_FINISHED").And.Contain(commands[1].TurnId);
    }

    [Fact]
    public async Task HandleStreamMessageAsync_ShouldWriteRunError_WhenFailureOccursAfterWriterStarts()
    {
        var previousInterval = NyxIdChatEndpoints.StreamKeepAliveInterval;
        var bodyStream = new SignalingWriteStream("aevatar.nyxid_chat.keepalive");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            NyxIdChatEndpoints.StreamKeepAliveInterval = TimeSpan.FromMilliseconds(10);
            var context = CreateAuthorizedStreamContext();
            context.Response.Body = bodyStream;
            var interactionService = new StubNyxIdChatInteractionService<NyxIdChatCommand>
            {
                BeforeEmitAsync = bodyStream.WaitForSignalAsync,
                AfterBeforeEmitException = new InvalidOperationException("subscription failed bearer-secret"),
            };

            await InvokeTaskAsync(
                "HandleStreamMessageAsync",
                context,
                "scope-a",
                "actor-1",
                new NyxIdChatEndpoints.NyxIdChatStreamRequest("hello", Type: "text"),
                new StubGAgentActorStore(),
                interactionService,
                NullLoggerFactory.Instance,
                timeout.Token);

            context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
            var command = interactionService.Commands.Should().ContainSingle().Which;
            var body = bodyStream.GetText();
            var frames = ParseSseFrames(body);
            var frameTypes = frames.Select(frame => frame.GetProperty("type").GetString()).ToArray();
            frameTypes[0].Should().Be("RUN_STARTED");
            frameTypes[^1].Should().Be("RUN_ERROR");
            frameTypes.Skip(1).SkipLast(1).Should().OnlyContain(type => type == "CUSTOM");
            frames.Skip(1).SkipLast(1)
                .Select(frame => frame.GetProperty("custom").GetProperty("name").GetString())
                .Should()
                .NotBeEmpty()
                .And.OnlyContain(name => name == "aevatar.nyxid_chat.keepalive");
            var terminal = frames[^1];
            terminal.GetProperty("turnId").GetString().Should().Be(command.TurnId);
            terminal.GetProperty("runError").GetProperty("runId").GetString().Should().Be(command.TurnId);
            terminal.GetProperty("runError").GetProperty("code").GetString().Should().Be("STREAM_FAILURE");
            body.Should().Contain("The chat request failed. Please try again.");
            body.Should().NotContain("bearer-secret");
        }
        finally
        {
            NyxIdChatEndpoints.StreamKeepAliveInterval = previousInterval;
        }
    }

    [Fact]
    public async Task HandleStreamMessageAsync_WhenInteractionThrowsTimeout_ShouldWriteStreamFailure()
    {
        var context = CreateAuthorizedStreamContext();
        var interactionService = new StubNyxIdChatInteractionService<NyxIdChatCommand>
        {
            Exception = new TimeoutException("provider timeout secret"),
        };

        await InvokeTaskAsync(
            "HandleStreamMessageAsync",
            context,
            "scope-a",
            "actor-1",
            new NyxIdChatEndpoints.NyxIdChatStreamRequest("hello", Type: "text"),
            new StubGAgentActorStore(),
            interactionService,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var body = await ReadResponseBodyAsync(context);
        var terminal = ParseSseFrames(body)
            .Where(static frame =>
                frame.GetProperty("type").GetString() is "RUN_FINISHED" or "RUN_ERROR")
            .Should().ContainSingle().Which;
        terminal.GetProperty("runError").GetProperty("code").GetString()
            .Should().Be("STREAM_FAILURE");
        body.Should().NotContain("provider timeout secret");
    }

    [Fact]
    public async Task HandleApproveAsync_WhenInteractionThrowsTimeout_ShouldWriteStreamFailure()
    {
        var context = CreateAuthorizedStreamContext();
        var interactionService = new StubNyxIdChatInteractionService<NyxIdApprovalCommand>
        {
            Exception = new TimeoutException("approval timeout secret"),
        };

        await InvokeTaskAsync(
            "HandleApproveAsync",
            context,
            "scope-a",
            "actor-1",
            new NyxIdChatEndpoints.NyxIdApprovalRequest("request-alpha"),
            new StubGAgentActorStore(),
            interactionService,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var body = await ReadResponseBodyAsync(context);
        var terminal = ParseSseFrames(body)
            .Where(static frame =>
                frame.GetProperty("type").GetString() is "RUN_FINISHED" or "RUN_ERROR")
            .Should().ContainSingle().Which;
        terminal.GetProperty("runError").GetProperty("code").GetString()
            .Should().Be("STREAM_FAILURE");
        body.Should().NotContain("approval timeout secret");
    }

    [Fact]
    public async Task HandleStreamMessageAsync_ShouldNotWriteSecondTerminal_WhenCleanupFailsAfterCompletion()
    {
        var context = CreateAuthorizedStreamContext();
        var interactionService = new StubNyxIdChatInteractionService<NyxIdChatCommand>
        {
            Frames =
            {
                new AGUIEvent { RunFinished = new RunFinishedEvent() },
            },
            AfterEmitException = new InvalidOperationException("cleanup failed bearer-secret"),
        };

        await InvokeTaskAsync(
            "HandleStreamMessageAsync",
            context,
            "scope-a",
            "actor-1",
            new NyxIdChatEndpoints.NyxIdChatStreamRequest("hello", Type: "text"),
            new StubGAgentActorStore(),
            interactionService,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var frames = ParseSseFrames(await ReadResponseBodyAsync(context));
        frames.Where(static frame =>
                frame.GetProperty("type").GetString() is "RUN_FINISHED" or "RUN_ERROR")
            .Should()
            .ContainSingle()
            .Which.GetProperty("type").GetString()
            .Should().Be("RUN_FINISHED");
        (await ReadResponseBodyAsync(context)).Should().NotContain("bearer-secret");
    }

    [Fact]
    public async Task HandleStreamMessageAsync_WhenInteractionIgnoresCancellation_ShouldReturnOneTimeoutAndDropLateFrames()
    {
        var previousInterval = NyxIdChatEndpoints.StreamKeepAliveInterval;
        var previousTimeout = NyxIdChatEndpoints.StreamTerminalTimeout;
        try
        {
            NyxIdChatEndpoints.StreamKeepAliveInterval = TimeSpan.FromMilliseconds(10);
            NyxIdChatEndpoints.StreamTerminalTimeout = TimeSpan.FromMilliseconds(40);
            var context = CreateAuthorizedStreamContext();
            var interactionService = new StubbornNyxIdChatInteractionService<NyxIdChatCommand>();

            var endpoint = InvokeTaskAsync(
                "HandleStreamMessageAsync",
                context,
                "scope-a",
                "actor-1",
                new NyxIdChatEndpoints.NyxIdChatStreamRequest("hello", Type: "text"),
                new StubGAgentActorStore(),
                interactionService,
                NullLoggerFactory.Instance,
                CancellationToken.None);
            await interactionService.WaitUntilStartedAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await endpoint.WaitAsync(TimeSpan.FromSeconds(2));

            var body = await ReadResponseBodyAsync(context);
            var frames = ParseSseFrames(body);
            frames.Where(static frame =>
                    frame.GetProperty("type").GetString() is "RUN_FINISHED" or "RUN_ERROR")
                .Should()
                .ContainSingle()
                .Which.GetProperty("runError").GetProperty("code").GetString()
                .Should().Be("STREAM_TIMEOUT");
            var frameCount = frames.Count;
            await interactionService.EmitCapturedAsync(new AGUIEvent
            {
                TextMessageContent = new TextMessageContentEvent
                {
                    MessageId = "late-message",
                    Delta = "late content",
                },
            });
            await interactionService.EmitCapturedAsync(new AGUIEvent
            {
                RunFinished = new RunFinishedEvent(),
            });
            await Task.Yield();
            ParseSseFrames(await ReadResponseBodyAsync(context)).Should().HaveCount(frameCount);
        }
        finally
        {
            NyxIdChatEndpoints.StreamKeepAliveInterval = previousInterval;
            NyxIdChatEndpoints.StreamTerminalTimeout = previousTimeout;
        }
    }

    [Fact]
    public async Task HandleStreamMessageAsync_ActionContinueStubbornInteraction_ShouldReturnOneTimeoutAndDropLateFrames()
    {
        var previousInterval = NyxIdChatEndpoints.StreamKeepAliveInterval;
        var previousTimeout = NyxIdChatEndpoints.StreamTerminalTimeout;
        try
        {
            NyxIdChatEndpoints.StreamKeepAliveInterval = TimeSpan.FromMilliseconds(10);
            NyxIdChatEndpoints.StreamTerminalTimeout = TimeSpan.FromMilliseconds(40);
            var context = CreateActionContinuationStreamContext();
            var textInteraction = new StubNyxIdChatInteractionService<NyxIdChatCommand>();
            var actionInteraction = new StubbornNyxIdChatInteractionService<
                NyxIdActionContinuationCommand>();

            var endpoint = InvokeTaskAsync(
                "HandleStreamMessageAsync",
                context,
                "scope-a",
                "actor-1",
                CreateActionContinuationStreamRequest(),
                new StubGAgentActorStore(),
                textInteraction,
                actionInteraction,
                NullLoggerFactory.Instance,
                CancellationToken.None);
            await actionInteraction.WaitUntilStartedAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await endpoint.WaitAsync(TimeSpan.FromSeconds(2));

            var frames = ParseSseFrames(await ReadResponseBodyAsync(context));
            frames.Where(static frame =>
                    frame.GetProperty("type").GetString() is "RUN_FINISHED" or "RUN_ERROR")
                .Should().ContainSingle()
                .Which.GetProperty("runError").GetProperty("code").GetString()
                .Should().Be("STREAM_TIMEOUT");
            var frameCount = frames.Count;
            await actionInteraction.EmitCapturedAsync(new AGUIEvent
            {
                TextMessageContent = new TextMessageContentEvent { Delta = "late action content" },
            });
            await actionInteraction.EmitCapturedAsync(new AGUIEvent
            {
                RunFinished = new RunFinishedEvent(),
            });
            ParseSseFrames(await ReadResponseBodyAsync(context)).Should().HaveCount(frameCount);
            textInteraction.Commands.Should().BeEmpty();
        }
        finally
        {
            NyxIdChatEndpoints.StreamKeepAliveInterval = previousInterval;
            NyxIdChatEndpoints.StreamTerminalTimeout = previousTimeout;
        }
    }

    [Fact]
    public async Task HandleApproveAsync_WhenInteractionIgnoresCancellation_ShouldReturnOneTimeoutAndDropLateFrames()
    {
        var previousInterval = NyxIdChatEndpoints.StreamKeepAliveInterval;
        var previousTimeout = NyxIdChatEndpoints.StreamTerminalTimeout;
        try
        {
            NyxIdChatEndpoints.StreamKeepAliveInterval = TimeSpan.FromMilliseconds(10);
            NyxIdChatEndpoints.StreamTerminalTimeout = TimeSpan.FromMilliseconds(40);
            var context = CreateAuthorizedStreamContext();
            var interactionService = new StubbornNyxIdChatInteractionService<NyxIdApprovalCommand>();

            var endpoint = InvokeTaskAsync(
                "HandleApproveAsync",
                context,
                "scope-a",
                "actor-1",
                new NyxIdChatEndpoints.NyxIdApprovalRequest("request-alpha"),
                new StubGAgentActorStore(),
                interactionService,
                NullLoggerFactory.Instance,
                CancellationToken.None);
            await interactionService.WaitUntilStartedAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await endpoint.WaitAsync(TimeSpan.FromSeconds(2));

            var frames = ParseSseFrames(await ReadResponseBodyAsync(context));
            frames.Where(static frame =>
                    frame.GetProperty("type").GetString() is "RUN_FINISHED" or "RUN_ERROR")
                .Should().ContainSingle()
                .Which.GetProperty("runError").GetProperty("code").GetString()
                .Should().Be("STREAM_TIMEOUT");
            var frameCount = frames.Count;
            await interactionService.EmitCapturedAsync(new AGUIEvent
            {
                TextMessageContent = new TextMessageContentEvent { Delta = "late approval content" },
            });
            await interactionService.EmitCapturedAsync(new AGUIEvent
            {
                RunError = new RunErrorEvent { Message = "late approval terminal" },
            });
            ParseSseFrames(await ReadResponseBodyAsync(context)).Should().HaveCount(frameCount);
        }
        finally
        {
            NyxIdChatEndpoints.StreamKeepAliveInterval = previousInterval;
            NyxIdChatEndpoints.StreamTerminalTimeout = previousTimeout;
        }
    }

    [Fact]
    public async Task HandleStreamMessageAsync_WhenRequestIsCancelled_ShouldCloseWithoutSyntheticTerminalOrLateFrames()
    {
        var previousInterval = NyxIdChatEndpoints.StreamKeepAliveInterval;
        var previousTimeout = NyxIdChatEndpoints.StreamTerminalTimeout;
        using var requestCancellation = new CancellationTokenSource();
        try
        {
            NyxIdChatEndpoints.StreamKeepAliveInterval = TimeSpan.FromHours(1);
            NyxIdChatEndpoints.StreamTerminalTimeout = TimeSpan.FromMinutes(5);
            var context = CreateAuthorizedStreamContext();
            var interactionService = new StubbornNyxIdChatInteractionService<NyxIdChatCommand>();

            var endpoint = InvokeTaskAsync(
                "HandleStreamMessageAsync",
                context,
                "scope-a",
                "actor-1",
                new NyxIdChatEndpoints.NyxIdChatStreamRequest("hello", Type: "text"),
                new StubGAgentActorStore(),
                interactionService,
                NullLoggerFactory.Instance,
                requestCancellation.Token);
            await interactionService.WaitUntilStartedAsync().WaitAsync(TimeSpan.FromSeconds(2));
            requestCancellation.Cancel();
            await endpoint.WaitAsync(TimeSpan.FromSeconds(2));

            var frames = ParseSseFrames(await ReadResponseBodyAsync(context));
            frames.Where(static frame =>
                    frame.GetProperty("type").GetString() is "RUN_FINISHED" or "RUN_ERROR")
                .Should().BeEmpty("a disconnected request cannot receive a synthetic terminal");
            var frameCount = frames.Count;
            await interactionService.EmitCapturedAsync(new AGUIEvent
            {
                RunFinished = new RunFinishedEvent(),
            });
            ParseSseFrames(await ReadResponseBodyAsync(context)).Should().HaveCount(frameCount);
        }
        finally
        {
            NyxIdChatEndpoints.StreamKeepAliveInterval = previousInterval;
            NyxIdChatEndpoints.StreamTerminalTimeout = previousTimeout;
        }
    }

    private static DefaultHttpContext CreateActionContinuationStreamContext()
    {
        var context = CreateAuthorizedStreamContext();
        context.RequestServices = new ServiceCollection()
            .AddSingleton<INyxIdActionContinuationCredentialVisibilityPort>(
                new VisibleActionContinuationCredentialVisibilityPort())
            .BuildServiceProvider();
        context.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim("sub", "owner-alpha")],
                authenticationType: "test"));
        return context;
    }

    private sealed class VisibleActionContinuationCredentialVisibilityPort
        : INyxIdActionContinuationCredentialVisibilityPort
    {
        public Task<NyxIdActionContinuationCredentialVisibilityResult> InspectUserServiceAsync(
            string bearerToken,
            string userServiceId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new NyxIdActionContinuationCredentialVisibilityResult(
                NyxIdActionContinuationCredentialVisibilityStatus.Visible,
                userServiceId,
                "visible"));
        }
    }

    private static NyxIdChatEndpoints.NyxIdChatStreamRequest
        CreateActionContinuationStreamRequest() =>
        new(
            Prompt: null,
            ClientRequestId: "client-action-alpha",
            Type: "action.continue",
            OriginTurnId: "turn-origin-alpha",
            Actions:
            [
                new NyxIdChatEndpoints.NyxIdChatActionReportDto(
                    "action-alpha",
                    "turn-origin-alpha",
                    "completed",
                    new NyxIdChatEndpoints.NyxIdChatActionResourceDto(
                        UserService: new NyxIdChatEndpoints.NyxIdChatUserServiceRefDto(
                            "service-alpha"))),
            ]);

    private sealed class StubbornNyxIdChatInteractionService<TCommand>
        : ICommandInteractionService<
            TCommand,
            NyxIdChatAcceptedReceipt,
            NyxIdChatStartError,
            AGUIEvent,
            NyxIdChatCompletionStatus>
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<CommandInteractionResult<
            NyxIdChatAcceptedReceipt,
            NyxIdChatStartError,
            NyxIdChatCompletionStatus>> _neverCompletes =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Func<AGUIEvent, CancellationToken, ValueTask>? _emitAsync;

        public Task WaitUntilStartedAsync() => _started.Task;

        public ValueTask EmitCapturedAsync(
            AGUIEvent frame,
            CancellationToken ct = default) =>
            (_emitAsync ?? throw new InvalidOperationException(
                "The interaction emit callback has not been captured."))(frame, ct);

        public Task<CommandInteractionResult<
            NyxIdChatAcceptedReceipt,
            NyxIdChatStartError,
            NyxIdChatCompletionStatus>> ExecuteAsync(
                TCommand command,
                Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
                Func<NyxIdChatAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
                CancellationToken ct = default)
        {
            _emitAsync = emitAsync;
            _started.TrySetResult();
            return _neverCompletes.Task;
        }

        async Task<RealtimeSessionResult<
            NyxIdChatAcceptedReceipt,
            NyxIdChatStartError,
            NyxIdChatCompletionStatus>> IRealtimeSession<
                TCommand,
                NyxIdChatAcceptedReceipt,
                NyxIdChatStartError,
                AGUIEvent,
                NyxIdChatCompletionStatus>.ExecuteAsync(
                    TCommand inbound,
                    Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
                    Func<NyxIdChatAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync,
                    CancellationToken ct) =>
            await ExecuteAsync(inbound, emitAsync, onAcceptedAsync, ct);
    }

    [Fact]
    public async Task HandleApproveAsync_ShouldCreateServerContinuationTurn_AndIgnoreLegacySessionId()
    {
        var context = CreateAuthorizedStreamContext();
        var interactionService = new StubNyxIdChatInteractionService<NyxIdApprovalCommand>
        {
            Frames =
            {
                new AGUIEvent { RunFinished = new RunFinishedEvent() },
            },
        };

        await InvokeTaskAsync(
            "HandleApproveAsync",
            context,
            "scope-a",
            "actor-1",
            new NyxIdChatEndpoints.NyxIdApprovalRequest(
                "pending-request-1",
                SessionId: "legacy-approval-session"),
            new StubGAgentActorStore(),
            interactionService,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var command = interactionService.Commands.Should().ContainSingle().Which.Should()
            .BeOfType<NyxIdApprovalCommand>().Subject;
        command.RequestId.Should().Be("pending-request-1");
        command.TurnId.Should().StartWith("turn-").And.NotBe("legacy-approval-session");
        (await ReadResponseBodyAsync(context)).Should()
            .Contain("RUN_FINISHED")
            .And.Contain(command.TurnId);
    }

    [Fact]
    public void NyxIdChatInteractionContracts_ShouldNamePerSubmissionIdentityTurnId()
    {
        typeof(NyxIdChatCommand).GetProperty("TurnId").Should().NotBeNull();
        typeof(NyxIdApprovalCommand).GetProperty("TurnId").Should().NotBeNull();
        typeof(NyxIdChatAcceptedReceipt).GetProperty("TurnId").Should().NotBeNull();

        typeof(NyxIdChatCommand).GetProperty("SessionId").Should().BeNull();
        typeof(NyxIdApprovalCommand).GetProperty("SessionId").Should().BeNull();
        typeof(NyxIdChatAcceptedReceipt).GetProperty("SessionId").Should().BeNull();
    }
}
