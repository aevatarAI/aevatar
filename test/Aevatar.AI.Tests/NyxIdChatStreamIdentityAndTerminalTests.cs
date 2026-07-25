using Aevatar.AGUI.Contracts;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
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
            new NyxIdChatEndpoints.NyxIdChatStreamRequest("first prompt", SessionId: "legacy-conversation-session"),
            new StubGAgentActorStore(),
            interactionService,
            NullLoggerFactory.Instance,
            CancellationToken.None);
        await InvokeTaskAsync(
            "HandleStreamMessageAsync",
            secondContext,
            "scope-a",
            "actor-1",
            new NyxIdChatEndpoints.NyxIdChatStreamRequest("second prompt", SessionId: "legacy-conversation-session"),
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
                ClientRequestId: "client-request-1"),
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
                ClientRequestId: "client-request-1"),
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
                new NyxIdChatEndpoints.NyxIdChatStreamRequest("hello"),
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
            new NyxIdChatEndpoints.NyxIdChatStreamRequest("hello"),
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
    public async Task HandleStreamMessageAsync_WhenProjectionNeverTerminates_ShouldWriteRunErrorAndStopHeartbeat()
    {
        var previousInterval = NyxIdChatEndpoints.StreamKeepAliveInterval;
        var previousTimeout = NyxIdChatEndpoints.StreamTerminalTimeout;
        try
        {
            NyxIdChatEndpoints.StreamKeepAliveInterval = TimeSpan.FromMilliseconds(10);
            NyxIdChatEndpoints.StreamTerminalTimeout = TimeSpan.FromMilliseconds(40);
            var context = CreateAuthorizedStreamContext();
            var neverCompletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var interactionService = new StubNyxIdChatInteractionService<NyxIdChatCommand>
            {
                BeforeEmitAsync = ct => neverCompletes.Task.WaitAsync(ct),
            };

            await InvokeTaskAsync(
                    "HandleStreamMessageAsync",
                    context,
                    "scope-a",
                    "actor-1",
                    new NyxIdChatEndpoints.NyxIdChatStreamRequest("hello"),
                    new StubGAgentActorStore(),
                    interactionService,
                    NullLoggerFactory.Instance,
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));

            var body = await ReadResponseBodyAsync(context);
            var frames = ParseSseFrames(body);
            frames.Where(static frame =>
                    frame.GetProperty("type").GetString() is "RUN_FINISHED" or "RUN_ERROR")
                .Should()
                .ContainSingle()
                .Which.GetProperty("runError").GetProperty("code").GetString()
                .Should().Be("STREAM_TIMEOUT");
            var frameCount = frames.Count;
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
