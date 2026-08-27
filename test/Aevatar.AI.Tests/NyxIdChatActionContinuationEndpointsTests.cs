using System.Security.Claims;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgents.NyxidChat;
using Aevatar.AGUI.Contracts;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.Tests;

public partial class NyxIdChatEndpointsCoverageTests
{
    [Fact]
    public async Task HandleStreamMessageAsync_TextExactRetry_ShouldUsePayloadBoundCommandIdentity()
    {
        var interaction = new StubNyxIdChatInteractionService<NyxIdChatCommand>
        {
            Frames =
            {
                new AGUIEvent { RunFinished = new RunFinishedEvent() },
            },
        };
        var request = new NyxIdChatEndpoints.NyxIdChatStreamRequest(
            Prompt: "hello",
            ClientRequestId: "client-chat-alpha",
            Type: "text",
            OriginTurnId: null,
            Actions: []);

        await InvokeTextAsync(request, interaction);
        await InvokeTextAsync(request, interaction);
        await InvokeTextAsync(request with { Prompt = "different input" }, interaction);

        interaction.Commands.Should().HaveCount(3);
        interaction.Commands[0].CommandId.Should().NotBeNullOrWhiteSpace();
        interaction.Commands[1].CommandId.Should().Be(interaction.Commands[0].CommandId);
        interaction.Commands[2].CommandId.Should().NotBe(interaction.Commands[0].CommandId);
    }

    [Fact]
    public async Task NyxIdChatInteraction_ExactRetry_ShouldReplayDurableTerminal()
    {
        const string actorId = "conversation-alpha";
        const string scopeId = "scope-alpha";
        const string turnId = "turn-chat-alpha";
        const string commandId = "chat-command-alpha";
        var actor = new StubActor(actorId);
        var runtime = new StubActorRuntime();
        runtime.Actors[actor.Id] = actor;
        var projectionPort = new StubNyxIdChatSessionProjectionPort
        {
            Messages =
            {
                ExpectedTerminal(
                    turnId,
                    "RUN_FINISHED",
                    RunCompletionStatus.Blocked,
                    null),
            },
        };
        var stateQuery = new FixedNyxIdChatStateQueryPort(
            NyxIdChatConversationStateQueryResult.NotFound());
        var dispatchPort = new StubActorDispatchPort(runtime);
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IActorRuntime>(runtime)
            .AddSingleton<IActorDispatchPort>(dispatchPort)
            .AddSingleton<INyxIdChatSessionProjectionPort>(projectionPort)
            .AddSingleton<INyxIdChatConversationStateQueryPort>(stateQuery);
        using var provider = services
            .AddStreamForwarding(runtime.StreamForwardingRegistry)
            .AddNyxIdChat()
            .BuildServiceProvider();
        var interaction = provider.GetRequiredService<
            ICommandInteractionService<NyxIdChatCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>();
        var command = new NyxIdChatCommand(
            actorId,
            scopeId,
            "hello",
            turnId,
            "access-token",
            null,
            null,
            CommandId: commandId,
            CorrelationId: commandId,
            ClientRequestId: "client-chat-alpha");
        var liveFrames = new List<AGUIEvent>();

        var live = await interaction.ExecuteAsync(
            command,
            (frame, _) =>
            {
                liveFrames.Add(frame);
                return ValueTask.CompletedTask;
            });
        dispatchPort.Dispatches.Should().ContainSingle(dispatch =>
            dispatch.ActorId == actorId &&
            dispatch.Envelope.Payload.Is(NyxIdChatStartTurnCommand.Descriptor));

        projectionPort.Messages.Clear();
        stateQuery.Result = OrdinaryTerminalState(
            actorId,
            scopeId,
            turnId,
            "blocked");
        var replayFrames = new List<AGUIEvent>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var replay = await interaction.ExecuteAsync(
            command,
            (frame, _) =>
            {
                replayFrames.Add(frame);
                return ValueTask.CompletedTask;
            },
            ct: timeout.Token);

        dispatchPort.Dispatches.Should().ContainSingle(dispatch =>
            dispatch.ActorId == actorId &&
            dispatch.Envelope.Payload.Is(NyxIdChatStartTurnCommand.Descriptor),
            "durable exact retry must not enter the actor inbox again");
        replay.Receipt.Should().Be(live.Receipt);
        replay.Receipt!.ScopeId.Should().Be(scopeId);
        replay.Completion.Should().Be(NyxIdChatCompletionStatus.Completed);
        stateQuery.Queries.Should().HaveCount(2).And.OnlyContain(query =>
            query.ScopeId == scopeId &&
            query.ActorId == actorId &&
            query.TurnId == turnId);
        liveFrames.Should().ContainSingle()
            .Which.RunFinished.Status.Should().Be(RunCompletionStatus.Blocked);
        replayFrames.Should().ContainSingle()
            .Which.RunFinished.Status.Should().Be(RunCompletionStatus.Blocked);
    }

    [Theory]
    [InlineData("different-command")]
    [InlineData("")]
    public async Task NyxIdChatDurableCompletionResolver_WithoutMatchingCommandIdentity_ShouldFailClosed(
        string durableCommandId)
    {
        var stateQuery = new FixedNyxIdChatStateQueryPort(OrdinaryTerminalState(
            "conversation-alpha",
            "scope-alpha",
            "turn-chat-alpha",
            "blocked",
            durableCommandId));
        var resolver = new NyxIdChatDurableCompletionResolver(stateQuery);

        var result = await resolver.ResolveAsync(new NyxIdChatAcceptedReceipt(
            "conversation-alpha",
            "chat-command-alpha",
            "chat-command-alpha",
            "turn-chat-alpha",
            "scope-alpha"));

        result.HasTerminalCompletion.Should().BeFalse();
    }

    [Fact]
    public async Task NyxIdChatDurableCompletionResolver_WithRecoverableFailedStep_ShouldReturnBlockedTerminal()
    {
        var stateQuery = new FixedNyxIdChatStateQueryPort(RecoverableFailureState());
        var resolver = new NyxIdChatDurableCompletionResolver(stateQuery);

        var result = await resolver.ResolveAsync(new NyxIdChatAcceptedReceipt(
            "conversation-alpha",
            "chat-command-alpha",
            "chat-command-alpha",
            "turn-chat-alpha",
            "scope-alpha"));

        result.HasTerminalCompletion.Should().BeTrue();
        result.Completion.Should().Be(NyxIdChatCompletionStatus.Completed);
        result.Completion.DurableTerminal.Should().NotBeNull();
        result.Completion.DurableTerminal!.RunFinished.Status.Should()
            .Be(RunCompletionStatus.Blocked);
        stateQuery.Result.Snapshot!.ActiveTask!.Status.Should().Be("active");
        stateQuery.Result.Snapshot.ActiveTask.Steps.Single().AvailableActions!.Retry.Should()
            .BeTrue();
    }

    [Fact]
    public async Task NyxIdChatDurableCompletionResolver_WithHistoricalRecoverableStep_ShouldRemainIncomplete()
    {
        var result = RecoverableFailureState();
        var snapshot = result.Snapshot!;
        var failedStep = snapshot.ActiveTask!.Steps.Single();
        var activeStep = failedStep with
        {
            StepId = "step-beta",
            Status = "running",
            FailureCode = null,
            SafeMessage = null,
            AvailableActions = new NyxIdChatAvailableActionsSnapshot(false, false, false),
            Operation = failedStep.Operation! with
            {
                StepId = "step-beta",
                OperationId = "operation-beta",
                Phase = "running",
                TerminalCode = null,
                SafeMessage = null,
                CompletedAt = null,
            },
        };
        var stateQuery = new FixedNyxIdChatStateQueryPort(
            NyxIdChatConversationStateQueryResult.Current(snapshot with
            {
                ActiveTask = snapshot.ActiveTask with
                {
                    ActiveStepId = activeStep.StepId,
                    FailureCode = null,
                    SafeMessage = null,
                    Steps = [failedStep, activeStep],
                },
                ActiveTurn = snapshot.ActiveTurn! with
                {
                    FailureCode = null,
                    SafeMessage = null,
                },
                LatestTurn = snapshot.LatestTurn! with
                {
                    FailureCode = null,
                    SafeMessage = null,
                },
            }));
        var resolver = new NyxIdChatDurableCompletionResolver(stateQuery);

        var observation = await resolver.ResolveAsync(new NyxIdChatAcceptedReceipt(
            "conversation-alpha",
            "chat-command-alpha",
            "chat-command-alpha",
            "turn-chat-alpha",
            "scope-alpha"));

        observation.HasTerminalCompletion.Should().BeFalse();
    }

    [Fact]
    public async Task HandleStreamMessageAsync_ActionContinueExactRetry_ShouldUsePayloadBoundCommandIdentity()
    {
        var actionInteraction = new StubNyxIdChatInteractionService<NyxIdActionContinuationCommand>
        {
            Frames =
            {
                new AGUIEvent { RunFinished = new RunFinishedEvent() },
            },
        };
        var request = new NyxIdChatEndpoints.NyxIdChatStreamRequest(
            Prompt: null,
            ClientRequestId: "client-action-alpha",
            Type: "action.continue",
            OriginTurnId: null,
            Actions: []);

        await InvokeActionContinuationAsync(request, actionInteraction);
        await InvokeActionContinuationAsync(request, actionInteraction);
        await InvokeActionContinuationAsync(
            request with
            {
                OriginTurnId = "turn-origin-alpha",
                Actions =
                [
                    new NyxIdChatEndpoints.NyxIdChatActionReportDto(
                        "action-alpha",
                        "turn-origin-alpha",
                        "declined",
                        null),
                ],
            },
            actionInteraction);

        actionInteraction.Commands.Should().HaveCount(3);
        actionInteraction.Commands[0].CommandId.Should().NotBeNullOrWhiteSpace();
        actionInteraction.Commands[1].CommandId.Should().Be(
            actionInteraction.Commands[0].CommandId);
        actionInteraction.Commands[2].CommandId.Should().NotBe(
            actionInteraction.Commands[0].CommandId);
    }

    [Theory]
    [InlineData("succeeded", "RUN_FINISHED", RunCompletionStatus.Completed, null)]
    [InlineData("blocked", "RUN_FINISHED", RunCompletionStatus.Blocked, null)]
    [InlineData("stopped", "RUN_FINISHED", RunCompletionStatus.Blocked, null)]
    [InlineData("failed", "RUN_ERROR", RunCompletionStatus.Unspecified, "NYXID_ACTION_CANCELLED")]
    public async Task NyxIdActionContinuationInteraction_ExactRetry_ShouldReplayDurableTerminal(
        string turnStatus,
        string expectedTerminal,
        RunCompletionStatus expectedCompletionStatus,
        string? expectedFailureCode)
    {
        const string actorId = "conversation-alpha";
        const string scopeId = "scope-alpha";
        const string turnId = "turn-continuation-alpha";
        const string commandId = "action-continuation-command-alpha";
        var actor = new StubActor(actorId);
        var runtime = new StubActorRuntime();
        runtime.Actors[actor.Id] = actor;
        var projectionPort = new StubNyxIdChatSessionProjectionPort
        {
            Messages =
            {
                ExpectedTerminal(
                    turnId,
                    expectedTerminal,
                    expectedCompletionStatus,
                    expectedFailureCode),
            },
        };
        var stateQuery = new FixedNyxIdChatStateQueryPort(
            NyxIdChatConversationStateQueryResult.NotFound());
        var services = AddInMemoryStreamForwardingServices(new ServiceCollection())
            .AddLogging()
            .AddSingleton<IActorRuntime>(runtime)
            .AddSingleton<IActorDispatchPort>(new StubActorDispatchPort(runtime))
            .AddSingleton<INyxIdChatSessionProjectionPort>(projectionPort)
            .AddSingleton<INyxIdChatConversationStateQueryPort>(stateQuery);
        using var provider = services
            .AddStreamForwarding(runtime.StreamForwardingRegistry)
            .AddNyxIdChat()
            .BuildServiceProvider();
        var interaction = provider.GetRequiredService<
            ICommandInteractionService<NyxIdActionContinuationCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>();
        var command = new NyxIdActionContinuationCommand(
            actorId,
            scopeId,
            string.Empty,
            turnId,
            "owner-alpha",
            "client-action-alpha",
            [],
            CommandId: commandId,
            CorrelationId: commandId);
        var liveFrames = new List<AGUIEvent>();

        var live = await interaction.ExecuteAsync(
            command,
            (frame, _) =>
            {
                liveFrames.Add(frame);
                return ValueTask.CompletedTask;
            });

        projectionPort.Messages.Clear();
        stateQuery.Result = TerminalState(
            actorId,
            scopeId,
            turnId,
            commandId,
            turnStatus,
            expectedFailureCode);
        var replayFrames = new List<AGUIEvent>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var replay = await interaction.ExecuteAsync(
            command,
            (frame, _) =>
            {
                replayFrames.Add(frame);
                return ValueTask.CompletedTask;
            },
            ct: timeout.Token);

        live.Receipt.Should().Be(replay.Receipt);
        live.Receipt!.TurnId.Should().Be(turnId);
        replay.Completion.Should().Be(expectedTerminal == "RUN_ERROR"
            ? NyxIdChatCompletionStatus.Failed
            : NyxIdChatCompletionStatus.Completed);
        stateQuery.Queries.Should().HaveCount(2).And.OnlyContain(query =>
            query.ScopeId == scopeId &&
            query.ActorId == actorId &&
            query.TurnId == turnId);
        AssertTerminal(liveFrames.Should().ContainSingle().Which);
        AssertTerminal(replayFrames.Should().ContainSingle().Which);

        void AssertTerminal(AGUIEvent terminal)
        {
            if (expectedTerminal == "RUN_FINISHED")
            {
                terminal.EventCase.Should().Be(AGUIEvent.EventOneofCase.RunFinished);
                terminal.RunFinished.RunId.Should().Be(turnId);
                terminal.RunFinished.Status.Should().Be(expectedCompletionStatus);
                return;
            }

            terminal.EventCase.Should().Be(AGUIEvent.EventOneofCase.RunError);
            terminal.RunError.RunId.Should().Be(turnId);
            terminal.RunError.Code.Should().Be(expectedFailureCode);
            terminal.RunError.Message.Should().Be("The action was cancelled.");
        }
    }

    [Fact]
    public async Task HandleStreamMessageAsync_ActionContinue_ShouldUseAuthenticatedSubjectAndServerTurn()
    {
        var visibility = new StubActionContinuationCredentialVisibilityPort(
            new NyxIdActionContinuationCredentialVisibilityResult(
                NyxIdActionContinuationCredentialVisibilityStatus.Visible,
                "service-alpha",
                "visible"));
        using var requestServices = new ServiceCollection()
            .AddSingleton<INyxIdActionContinuationCredentialVisibilityPort>(visibility)
            .BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", "owner-alpha")],
                authenticationType: "test")),
            RequestServices = requestServices,
        };
        context.Request.Headers.Authorization = "Bearer refreshed-token";
        context.Response.Body = new MemoryStream();
        var textInteraction = new StubNyxIdChatInteractionService<NyxIdChatCommand>();
        var actionInteraction = new StubNyxIdChatInteractionService<NyxIdActionContinuationCommand>
        {
            Frames =
            {
                new AGUIEvent { RunFinished = new RunFinishedEvent() },
            },
        };
        var request = new NyxIdChatEndpoints.NyxIdChatStreamRequest(
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

        await InvokeTaskAsync(
            "HandleStreamMessageAsync",
            context,
            "scope-alpha",
            "conversation-alpha",
            request,
            new StubGAgentActorStore(),
            textInteraction,
            actionInteraction,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        textInteraction.Commands.Should().BeEmpty();
        var command = actionInteraction.Commands.Should().ContainSingle().Which;
        command.ActorId.Should().Be("conversation-alpha");
        command.ScopeId.Should().Be("scope-alpha");
        command.OriginTurnId.Should().Be("turn-origin-alpha");
        command.ContinuationTurnId.Should().StartWith("turn-")
            .And.NotBe("turn-origin-alpha");
        command.OwnerSubject.Should().Be("owner-alpha");
        command.ClientRequestId.Should().Be("client-action-alpha");
        command.ToolContext.Should().NotBeNull();
        command.ToolContext!.Credentials.NyxIdAccessToken.Should().Be("refreshed-token");
        command.ToolContext.Credentials.NyxIdCredentialKind.Should().Be(
            AgentToolNyxIdCredentialKindPayload.SourceReadableUserBearer);
        var report = command.Actions.Should().ContainSingle().Which;
        report.ActionRequestId.Should().Be("action-alpha");
        report.OriginTurnId.Should().Be("turn-origin-alpha");
        report.Disposition.Should().Be(NyxIdChatActionDisposition.Completed);
        report.Resource.UserService.UserServiceId.Should().Be("service-alpha");
        visibility.Reads.Should().ContainSingle().Which.Should().Be(
            ("refreshed-token", "service-alpha"));
        (await ReadResponseBodyAsync(context)).Should()
            .Contain("RUN_FINISHED")
            .And.Contain(command.ContinuationTurnId);
    }

    [Fact]
    public async Task HandleStreamMessageAsync_ActionContinue_WhenUserServiceIsNotVisible_ShouldRequireCredentialRefreshBeforeDispatch()
    {
        var visibility = new StubActionContinuationCredentialVisibilityPort(
            new NyxIdActionContinuationCredentialVisibilityResult(
                NyxIdActionContinuationCredentialVisibilityStatus.CredentialRefreshRequired,
                "service-alpha",
                "not-visible"));
        using var requestServices = new ServiceCollection()
            .AddSingleton<INyxIdActionContinuationCredentialVisibilityPort>(visibility)
            .BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", "owner-alpha")],
                authenticationType: "test")),
            RequestServices = requestServices,
        };
        context.Request.Headers.Authorization = "Bearer stale-token";
        context.Response.Body = new MemoryStream();
        var actionInteraction = new StubNyxIdChatInteractionService<NyxIdActionContinuationCommand>();

        await InvokeTaskAsync(
            "HandleStreamMessageAsync",
            context,
            "scope-alpha",
            "conversation-alpha",
            CompletedUserServiceContinuationRequest(),
            new StubGAgentActorStore(),
            new StubNyxIdChatInteractionService<NyxIdChatCommand>(),
            actionInteraction,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        actionInteraction.Commands.Should().BeEmpty();
        visibility.Reads.Should().ContainSingle().Which.Should().Be(
            ("stale-token", "service-alpha"));
        (await ReadResponseBodyAsync(context)).Should().Contain(
            "NYXID_ACTION_CONTINUATION_CREDENTIAL_REFRESH_REQUIRED");
    }

    [Fact]
    public async Task HandleStreamMessageAsync_ActionContinue_WhenVisibilitySourceIsUnavailable_ShouldFailBeforeDispatch()
    {
        var visibility = new StubActionContinuationCredentialVisibilityPort(
            new NyxIdActionContinuationCredentialVisibilityResult(
                NyxIdActionContinuationCredentialVisibilityStatus.SourceUnavailable,
                "service-alpha",
                "catalog-unavailable"));
        using var requestServices = new ServiceCollection()
            .AddSingleton<INyxIdActionContinuationCredentialVisibilityPort>(visibility)
            .BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", "owner-alpha")],
                authenticationType: "test")),
            RequestServices = requestServices,
        };
        context.Request.Headers.Authorization = "Bearer current-token";
        context.Response.Body = new MemoryStream();
        var actionInteraction = new StubNyxIdChatInteractionService<NyxIdActionContinuationCommand>();

        await InvokeTaskAsync(
            "HandleStreamMessageAsync",
            context,
            "scope-alpha",
            "conversation-alpha",
            CompletedUserServiceContinuationRequest(),
            new StubGAgentActorStore(),
            new StubNyxIdChatInteractionService<NyxIdChatCommand>(),
            actionInteraction,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        actionInteraction.Commands.Should().BeEmpty();
        (await ReadResponseBodyAsync(context)).Should().Contain(
            "NYXID_ACTION_CONTINUATION_CATALOG_UNAVAILABLE");
    }

    private static async Task InvokeActionContinuationAsync(
        NyxIdChatEndpoints.NyxIdChatStreamRequest request,
        StubNyxIdChatInteractionService<NyxIdActionContinuationCommand> interaction)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", "owner-alpha")],
                authenticationType: "test")),
        };
        context.Request.Headers.Authorization = "Bearer valid-token";
        context.Response.Body = new MemoryStream();

        await InvokeTaskAsync(
            "HandleStreamMessageAsync",
            context,
            "scope-alpha",
            "conversation-alpha",
            request,
            new StubGAgentActorStore(),
            new StubNyxIdChatInteractionService<NyxIdChatCommand>(),
            interaction,
            NullLoggerFactory.Instance,
            CancellationToken.None);
    }

    private static async Task InvokeTextAsync(
        NyxIdChatEndpoints.NyxIdChatStreamRequest request,
        StubNyxIdChatInteractionService<NyxIdChatCommand> interaction)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", "owner-alpha")],
                authenticationType: "test")),
        };
        context.Request.Headers.Authorization = "Bearer valid-token";
        context.Response.Body = new MemoryStream();

        await InvokeTaskAsync(
            "HandleStreamMessageAsync",
            context,
            "scope-alpha",
            "conversation-alpha",
            request,
            new StubGAgentActorStore(),
            interaction,
            new StubNyxIdChatInteractionService<NyxIdActionContinuationCommand>(),
            NullLoggerFactory.Instance,
            CancellationToken.None);
    }

    private static NyxIdChatEndpoints.NyxIdChatStreamRequest
        CompletedUserServiceContinuationRequest() =>
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

    private static NyxIdChatConversationStateQueryResult OrdinaryTerminalState(
        string actorId,
        string scopeId,
        string turnId,
        string turnStatus,
        string commandId = "chat-command-alpha") =>
        NyxIdChatConversationStateQueryResult.Current(
            new NyxIdChatConversationStateSnapshot(
                actorId,
                scopeId,
                12,
                5,
                new DateTimeOffset(2026, 7, 31, 1, 0, 0, TimeSpan.Zero),
                new NyxIdChatConversationTurnSnapshot(
                    turnId,
                    "task-alpha",
                    turnStatus,
                    null,
                    null,
                    null,
                    new DateTimeOffset(2026, 7, 31, 1, 0, 0, TimeSpan.Zero),
                    commandId),
                new NyxIdChatConversationTurnSnapshot(
                    turnId,
                    "task-alpha",
                    turnStatus,
                    null,
                    null,
                    null,
                    new DateTimeOffset(2026, 7, 31, 1, 0, 0, TimeSpan.Zero),
                    commandId),
                [],
                new NyxIdChatConversationTaskSnapshot(
                    "task-alpha",
                    turnId,
                    turnStatus,
                    null,
                    null,
                    null,
                    null,
                    new DateTimeOffset(2026, 7, 31, 1, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 7, 31, 1, 0, 0, TimeSpan.Zero),
                    []),
                null,
                [],
                null,
                null,
                null));

    private static NyxIdChatConversationStateQueryResult RecoverableFailureState()
    {
        var result = OrdinaryTerminalState(
            "conversation-alpha",
            "scope-alpha",
            "turn-chat-alpha",
            "active");
        var snapshot = result.Snapshot!;
        var failedStep = new NyxIdChatConversationStepSnapshot(
            StepId: "step-alpha",
            Order: 1,
            Kind: "llm",
            Status: "failed",
            Required: true,
            Description: "Generate the assistant response.",
            MayChangeExternalState: false,
            ExternalEffect: "not_applied",
            ApprovalRequestId: null,
            ActionRequestId: null,
            FailureCode: "NYXID_CHAT_OPERATION_EXECUTION_FAILED",
            SafeMessage: "The operation could not be completed.",
            SafeToSkip: false,
            AvailableActions: new NyxIdChatAvailableActionsSnapshot(
                Retry: true,
                Skip: false,
                Stop: false),
            UpdatedAt: snapshot.UpdatedAt,
            Operation: new NyxIdChatConversationOperationSnapshot(
                ConversationActorId: snapshot.ActorId,
                TurnId: "turn-chat-alpha",
                TaskId: "task-alpha",
                StepId: "step-alpha",
                OperationId: "operation-alpha",
                OperationGeneration: 1,
                Kind: "llm",
                Phase: "failed",
                MayChangeExternalState: false,
                Idempotent: false,
                LatestProgressSequence: 0,
                TerminalCode: "NYXID_CHAT_OPERATION_EXECUTION_FAILED",
                SafeMessage: "The operation could not be completed.",
                RequestedAt: snapshot.UpdatedAt,
                DispatchedAt: snapshot.UpdatedAt,
                CompletedAt: snapshot.UpdatedAt));
        return NyxIdChatConversationStateQueryResult.Current(snapshot with
        {
            ActiveTask = snapshot.ActiveTask! with
            {
                ActiveStepId = failedStep.StepId,
                FailureCode = failedStep.FailureCode,
                SafeMessage = failedStep.SafeMessage,
                Steps = [failedStep],
            },
            ActiveTurn = snapshot.ActiveTurn! with
            {
                FailureCode = failedStep.FailureCode,
                SafeMessage = failedStep.SafeMessage,
            },
            LatestTurn = snapshot.LatestTurn! with
            {
                FailureCode = failedStep.FailureCode,
                SafeMessage = failedStep.SafeMessage,
            },
        });
    }

    private static NyxIdChatConversationStateQueryResult TerminalState(
        string actorId,
        string scopeId,
        string turnId,
        string commandId,
        string turnStatus,
        string? failureCode) =>
        NyxIdChatConversationStateQueryResult.Current(
            new NyxIdChatConversationStateSnapshot(
                actorId,
                scopeId,
                12,
                5,
                new DateTimeOffset(2026, 7, 30, 1, 0, 0, TimeSpan.Zero),
                new NyxIdChatConversationTurnSnapshot(
                    turnId,
                    "task-alpha",
                    turnStatus,
                    failureCode,
                    failureCode is null ? null : "The action was cancelled.",
                    null,
                    new DateTimeOffset(2026, 7, 30, 1, 0, 0, TimeSpan.Zero)),
                new NyxIdChatConversationTurnSnapshot(
                    turnId,
                    "task-alpha",
                    turnStatus,
                    failureCode,
                    failureCode is null ? null : "The action was cancelled.",
                    null,
                    new DateTimeOffset(2026, 7, 30, 1, 0, 0, TimeSpan.Zero)),
                [],
                new NyxIdChatConversationTaskSnapshot(
                    "task-alpha",
                    turnId,
                    turnStatus,
                    null,
                    null,
                    failureCode,
                    failureCode is null ? null : "The action was cancelled.",
                    new DateTimeOffset(2026, 7, 30, 1, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 7, 30, 1, 0, 0, TimeSpan.Zero),
                    []),
                null,
                [],
                null,
                null,
                new NyxIdChatContinuationAdmissionSnapshot(
                    "action",
                    commandId,
                    "client-action-alpha",
                    string.Empty,
                    turnId,
                    "accepted",
                    NyxIdChatBrowserActions.ActionContinuationAccepted,
                    null,
                    new DateTimeOffset(2026, 7, 30, 1, 0, 0, TimeSpan.Zero))));

    private static AGUIEvent ExpectedTerminal(
        string turnId,
        string terminal,
        RunCompletionStatus completionStatus,
        string? failureCode) =>
        terminal == "RUN_FINISHED"
            ? new AGUIEvent
            {
                RunFinished = new RunFinishedEvent
                {
                    RunId = turnId,
                    Status = completionStatus,
                },
            }
            : new AGUIEvent
            {
                RunError = new RunErrorEvent
                {
                    RunId = turnId,
                    Code = failureCode,
                    Message = "The action was cancelled.",
                },
            };

    private sealed class FixedNyxIdChatStateQueryPort
        : INyxIdChatConversationStateQueryPort
    {
        public FixedNyxIdChatStateQueryPort(NyxIdChatConversationStateQueryResult result)
        {
            Result = result;
        }

        public NyxIdChatConversationStateQueryResult Result { get; set; }
        public List<NyxIdChatConversationStateQuery> Queries { get; } = [];

        public Task<NyxIdChatConversationStateQueryResult> GetAsync(
            NyxIdChatConversationStateQuery query,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Queries.Add(query);
            return Task.FromResult(Result);
        }

        public Task<IReadOnlyDictionary<string, NyxIdChatConversationAttentionSummary>>
            GetAttentionSummariesAsync(
                string scopeId,
                IReadOnlyCollection<string> actorIds,
                CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, NyxIdChatConversationAttentionSummary>>(
                new Dictionary<string, NyxIdChatConversationAttentionSummary>());
    }

    [Theory]
    [InlineData("id", "\"action-alpha\"")]
    [InlineData("risk", "\"low\"")]
    [InlineData("continuationTurnId", "\"turn-caller-owned\"")]
    [InlineData("accessToken", "\"secret-alpha\"")]
    [InlineData("userCode", "\"secret-alpha\"")]
    public void ActionContinueJson_ShouldRejectUnmappedOrSecretBearingFields(
        string field,
        string valueJson)
    {
        var json = $$"""
            {
              "type": "action.continue",
              "clientRequestId": "client-action-alpha",
              "originTurnId": "turn-origin-alpha",
              "actions": [
                {
                  "actionRequestId": "action-alpha",
                  "originTurnId": "turn-origin-alpha",
                  "disposition": "completed"
                }
              ],
              "{{field}}": {{valueJson}}
            }
            """;

        Action deserialize = () => JsonSerializer.Deserialize<
            NyxIdChatEndpoints.NyxIdChatStreamRequest>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        deserialize.Should().Throw<JsonException>();
    }

    [Fact]
    public void NyxIdActionContinuationEnvelopeFactory_ShouldPreserveDistinctTypedIdentities()
    {
        var factory = new NyxIdActionContinuationCommandEnvelopeFactory();
        var command = new NyxIdActionContinuationCommand(
            "conversation-alpha",
            "scope-alpha",
            "turn-origin-alpha",
            "turn-continuation-alpha",
            "owner-alpha",
            "client-request-alpha",
            [
                new NyxIdChatActionReport
                {
                    ActionRequestId = "action-alpha",
                    OriginTurnId = "turn-origin-alpha",
                    Disposition = NyxIdChatActionDisposition.Completed,
                    Resource = new NyxIdChatSafeResourceRef
                    {
                        UserService = new NyxIdChatUserServiceRef
                        {
                            UserServiceId = "service-alpha",
                        },
                    },
                },
            ],
            CommandId: "command-alpha",
            CorrelationId: "correlation-alpha",
            ToolContext: new AgentToolExecutionContextPayload
            {
                Credentials = new AgentToolCredentialsPayload
                {
                    NyxIdAccessToken = "fresh-token",
                    NyxIdCredentialKind =
                        AgentToolNyxIdCredentialKindPayload.SourceReadableUserBearer,
                },
            });

        var envelope = factory.CreateEnvelope(
            command,
            new CommandContext(
                "conversation-alpha",
                "command-alpha",
                "correlation-alpha",
                new Dictionary<string, string>()));

        envelope.Route.Direct.TargetActorId.Should().Be("conversation-alpha");
        envelope.Propagation.CorrelationId.Should().Be("correlation-alpha");
        var message = envelope.Payload.Unpack<NyxIdChatActionContinueCommand>();
        message.ScopeId.Should().Be("scope-alpha");
        message.ConversationActorId.Should().Be("conversation-alpha");
        message.OriginTurnId.Should().Be("turn-origin-alpha");
        message.ContinuationTurnId.Should().Be("turn-continuation-alpha");
        message.OwnerSubject.Should().Be("owner-alpha");
        message.ClientRequestId.Should().Be("client-request-alpha");
        message.CommandId.Should().Be("command-alpha");
        message.CorrelationId.Should().Be("correlation-alpha");
        message.ToolContext.Credentials.NyxIdAccessToken.Should().Be("fresh-token");
        message.ToolContext.Credentials.NyxIdCredentialKind.Should().Be(
            AgentToolNyxIdCredentialKindPayload.SourceReadableUserBearer);
        message.Actions.Should().ContainSingle().Which.Resource.UserService.UserServiceId
            .Should().Be("service-alpha");
    }

    [Fact]
    public async Task NyxIdActionContinuationInteraction_ShouldUseCanonicalObservationAndDispatchPipeline()
    {
        var actor = new StubActor("conversation-alpha");
        var runtime = new StubActorRuntime();
        runtime.Actors[actor.Id] = actor;
        var projectionPort = new StubNyxIdChatSessionProjectionPort
        {
            Messages =
            {
                new AGUIEvent { RunFinished = new RunFinishedEvent() },
            },
        };
        var dispatchPort = new StubActorDispatchPort(runtime);
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IActorRuntime>(runtime)
            .AddSingleton<IActorDispatchPort>(dispatchPort)
            .AddSingleton<INyxIdChatSessionProjectionPort>(projectionPort);
        using var provider = services
            .AddStreamForwarding(runtime.StreamForwardingRegistry)
            .AddNyxIdChat()
            .BuildServiceProvider();
        var interaction = provider.GetRequiredService<
            ICommandInteractionService<NyxIdActionContinuationCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>();
        var report = new NyxIdChatActionReport
        {
            ActionRequestId = "action-alpha",
            OriginTurnId = "turn-origin-alpha",
            Disposition = NyxIdChatActionDisposition.Completed,
            Resource = new NyxIdChatSafeResourceRef
            {
                UserService = new NyxIdChatUserServiceRef
                {
                    UserServiceId = "service-alpha",
                },
            },
        };

        var result = await interaction.ExecuteAsync(
            new NyxIdActionContinuationCommand(
                actor.Id,
                "scope-alpha",
                "turn-origin-alpha",
                "turn-continuation-alpha",
                "owner-alpha",
                "client-request-alpha",
                [report],
                CommandId: "command-alpha",
                CorrelationId: "correlation-alpha"),
            (_, _) => ValueTask.CompletedTask);

        result.Succeeded.Should().BeTrue();
        result.Receipt.Should().Be(new NyxIdChatAcceptedReceipt(
            actor.Id,
            "command-alpha",
            "correlation-alpha",
            "turn-continuation-alpha",
            "scope-alpha"));
        projectionPort.AttachExistingCalls.Should().ContainSingle(call =>
            call.ActorId == actor.Id &&
            call.SessionId == "turn-continuation-alpha");
        projectionPort.AttachCount.Should().Be(1);
        projectionPort.DetachCount.Should().Be(1);
        projectionPort.ReleaseCount.Should().Be(1);
        var envelope = RequireDispatchedPayload<NyxIdChatActionContinueCommand>(dispatchPort);
        envelope.Route.Direct.TargetActorId.Should().Be(actor.Id);
        envelope.Propagation.CorrelationId.Should().Be("correlation-alpha");
        envelope.Payload.Unpack<NyxIdChatActionContinueCommand>()
            .OwnerSubject.Should().Be("owner-alpha");
    }

    private sealed class StubActionContinuationCredentialVisibilityPort(
        NyxIdActionContinuationCredentialVisibilityResult result)
        : INyxIdActionContinuationCredentialVisibilityPort
    {
        public List<(string BearerToken, string UserServiceId)> Reads { get; } = [];

        public Task<NyxIdActionContinuationCredentialVisibilityResult> InspectUserServiceAsync(
            string bearerToken,
            string userServiceId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Reads.Add((bearerToken, userServiceId));
            return Task.FromResult(result);
        }
    }
}

