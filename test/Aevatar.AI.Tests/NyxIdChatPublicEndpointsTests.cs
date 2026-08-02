using System.Security.Claims;
using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AGUI.Contracts;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgents.NyxidChat;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChatPublicEndpointsTests
{
    [Fact]
    public async Task FirstText_ShouldUseAuthenticatedScopeAndBodyIdempotencyIdentity()
    {
        var chat = new RecordingInteraction<NyxIdChatCommand>();
        var context = CreateContext("scope-alpha", services => services
            .AddSingleton<ICommandInteractionService<NyxIdChatCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>(chat)
            .AddSingleton<ICommandInteractionService<NyxIdActionContinuationCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>(new RecordingInteraction<NyxIdActionContinuationCommand>())
            .AddSingleton<IScopeResourceAdmissionPort>(new RecordingAdmissionPort()));
        context.Request.Headers.Authorization = "Bearer delegated-token";
        context.Request.Headers["Idempotency-Key"] = "header-request";
        context.Response.Body = new MemoryStream();

        await NyxIdChatEndpoints.HandlePublicChatAsync(context, Parse("""
            {
              "type": "text",
              "clientRequestId": "body-request",
              "prompt": "Summarize my bill",
              "agentProfile": {
                "ownerKind": "caller",
                "profileSlug": "research-assistant"
              }
            }
            """));

        var command = chat.Commands.Should().ContainSingle().Which;
        command.ScopeId.Should().Be("scope-alpha");
        command.OwnerSubject.Should().Be("user-alpha");
        command.ClientRequestId.Should().Be("body-request");
        command.CreateIfMissing.Should().BeTrue();
        command.AgentProfileReference.Should().BeEquivalentTo(new AgentProfileReference
        {
            OwnerKind = AgentProfileReferenceOwnerKind.Caller,
            ProfileSlug = "research-assistant",
        });
        command.ActorId.Should().Be(NyxIdChatPublicIdentity.CreateConversationActorId(
            "scope-alpha",
            "body-request"));
        command.TurnId.Should().Be(NyxIdChatPublicIdentity.CreateTurnId(
            command.ActorId,
            "body-request"));

        var envelope = new NyxIdChatCommandEnvelopeFactory().CreateEnvelope(
            command,
            new CommandContext(
                command.ActorId,
                "command-alpha",
                "correlation-alpha",
                new Dictionary<string, string>()));
        var create = envelope.Payload.Unpack<NyxIdChatConversationCreateCommand>();
        create.AgentProfileReference.Should().BeEquivalentTo(command.AgentProfileReference);
        var start = create.FirstTurn;
        start.ToolContext.Caller.ScopeId.Should().Be("scope-alpha");
        start.ToolContext.Caller.OwnerScopeId.Should().Be("scope-alpha");
        start.ToolContext.Caller.OwnerSubject.Should().Be("user-alpha");
        start.ToolContext.Caller.ResponseId.Should().Be(command.TurnId);
        start.ToolContext.NyxIdAuthority.Platform.Should().Be("nyxid");
        start.ToolContext.NyxIdAuthority.ExternalUserId.Should().Be("user-alpha");
        start.ToolContext.NyxIdAuthority.Scope.Should().Be("proxy");
        AgentToolSourceReadableNyxIdCredential.ResolveBearerToken(
                AgentToolExecutionContextMapper.FromPayload(start.ToolContext).Credentials)
            .Should().Be("delegated-token");

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.Should().Contain(command.ActorId).And.Contain(command.TurnId);
    }

    [Fact]
    public async Task FirstText_WithProxyDelegation_ShouldPreserveCredentialKind()
    {
        var chat = new RecordingInteraction<NyxIdChatCommand>();
        var context = CreateContext("scope-alpha", services => services
            .AddSingleton<ICommandInteractionService<NyxIdChatCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>(chat)
            .AddSingleton<ICommandInteractionService<NyxIdActionContinuationCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>(new RecordingInteraction<NyxIdActionContinuationCommand>())
            .AddSingleton<IScopeResourceAdmissionPort>(new RecordingAdmissionPort()));
        context.Request.Headers["X-NyxID-Delegation-Token"] = "proxy-delegation";
        context.Response.Body = new MemoryStream();

        await NyxIdChatEndpoints.HandlePublicChatAsync(context, Parse("""
            {
              "type": "text",
              "clientRequestId": "proxy-request",
              "prompt": "hello"
            }
            """));

        var command = chat.Commands.Should().ContainSingle().Which;
        var envelope = new NyxIdChatCommandEnvelopeFactory().CreateEnvelope(
            command,
            new CommandContext(
                command.ActorId,
                "command-alpha",
                "correlation-alpha",
                new Dictionary<string, string>()));
        var credentials = AgentToolExecutionContextMapper.FromPayload(
            envelope.Payload.Unpack<NyxIdChatConversationCreateCommand>().FirstTurn.ToolContext).Credentials;
        credentials.NyxIdAccessToken.Should().Be("proxy-delegation");
        credentials.NyxIdCredentialKind.Should().Be(AgentToolNyxIdCredentialKind.ProxyDelegation);
        AgentToolSourceReadableNyxIdCredential.ResolveBearerToken(credentials).Should().BeNull();
    }

    [Fact]
    public async Task ContinuedText_ShouldReuseConversationAndAuthorizeIt()
    {
        var chat = new RecordingInteraction<NyxIdChatCommand>();
        var admission = new RecordingAdmissionPort();
        var context = CreateContext("scope-alpha", services => services
            .AddSingleton<ICommandInteractionService<NyxIdChatCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>(chat)
            .AddSingleton<ICommandInteractionService<NyxIdActionContinuationCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>(new RecordingInteraction<NyxIdActionContinuationCommand>())
            .AddSingleton<IScopeResourceAdmissionPort>(admission));
        context.Request.Headers.Authorization = "Bearer delegated-token";
        context.Response.Body = new MemoryStream();

        await NyxIdChatEndpoints.HandlePublicChatAsync(context, Parse("""
            {
              "type": "text",
              "conversationId": "conversation-alpha",
              "clientRequestId": "request-beta",
              "prompt": "Only July"
            }
            """));

        chat.Commands.Should().ContainSingle().Which.Should().Match<NyxIdChatCommand>(command =>
            command.ActorId == "conversation-alpha" && !command.CreateIfMissing);
        admission.Targets.Should().ContainSingle().Which.Should().Match<ScopeResourceTarget>(target =>
            target.ScopeId == "scope-alpha" &&
            target.ActorId == "conversation-alpha" &&
            target.Operation == ScopeResourceOperation.Stream);
    }

    [Fact]
    public async Task ContinuedText_ShouldRejectAgentProfileSwitch()
    {
        var chat = new RecordingInteraction<NyxIdChatCommand>();
        var admission = new RecordingAdmissionPort();
        var context = CreateContext("scope-alpha", services => services
            .AddSingleton<ICommandInteractionService<NyxIdChatCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>(chat)
            .AddSingleton<ICommandInteractionService<NyxIdActionContinuationCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>(new RecordingInteraction<NyxIdActionContinuationCommand>())
            .AddSingleton<IScopeResourceAdmissionPort>(admission));
        context.Request.Headers.Authorization = "Bearer delegated-token";
        context.Response.Body = new MemoryStream();

        await NyxIdChatEndpoints.HandlePublicChatAsync(context, Parse("""
            {
              "type": "text",
              "conversationId": "conversation-alpha",
              "clientRequestId": "request-beta",
              "prompt": "Only July",
              "agentProfile": {
                "ownerKind": "system",
                "profileSlug": "research-assistant"
              }
            }
            """));

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        chat.Commands.Should().BeEmpty();
        admission.Targets.Should().BeEmpty();
    }

    [Fact]
    public async Task FirstText_ShouldRejectInvalidAgentProfileOwnerKind()
    {
        var chat = new RecordingInteraction<NyxIdChatCommand>();
        var context = CreateContext("scope-alpha", services => services
            .AddSingleton<ICommandInteractionService<NyxIdChatCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>(chat)
            .AddSingleton<ICommandInteractionService<NyxIdActionContinuationCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>(new RecordingInteraction<NyxIdActionContinuationCommand>())
            .AddSingleton<IScopeResourceAdmissionPort>(new RecordingAdmissionPort()));
        context.Request.Headers.Authorization = "Bearer delegated-token";
        context.Response.Body = new MemoryStream();

        await NyxIdChatEndpoints.HandlePublicChatAsync(context, Parse("""
            {
              "type": "text",
              "clientRequestId": "request-alpha",
              "prompt": "hello",
              "agentProfile": {
                "ownerKind": "scope",
                "profileSlug": "research-assistant"
              }
            }
            """));

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        chat.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task ActionContinueWithoutConversation_ShouldFailClosed()
    {
        var action = new RecordingInteraction<NyxIdActionContinuationCommand>();
        var context = CreateContext("scope-alpha", services => services
            .AddSingleton<ICommandInteractionService<NyxIdChatCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>(new RecordingInteraction<NyxIdChatCommand>())
            .AddSingleton<ICommandInteractionService<NyxIdActionContinuationCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>(action)
            .AddSingleton<IScopeResourceAdmissionPort>(new RecordingAdmissionPort()));
        context.Request.Headers.Authorization = "Bearer delegated-token";
        context.Response.Body = new MemoryStream();

        await NyxIdChatEndpoints.HandlePublicChatAsync(context, Parse("""
            {
              "type": " action.continue ",
              "clientRequestId": "request-action",
              "originTurnId": "turn-alpha",
              "actions": [{
                "actionRequestId": "action-alpha",
                "originTurnId": "turn-alpha",
                "disposition": "completed",
                "resource": { "userService": { "userServiceId": "service-alpha" } }
              }]
            }
            """));

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        action.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task ActionContinue_ShouldReuseConversationAndStartDistinctContinuationTurn()
    {
        var action = new RecordingInteraction<NyxIdActionContinuationCommand>();
        var admission = new RecordingAdmissionPort();
        var context = CreateContext("scope-alpha", services => services
            .AddSingleton<ICommandInteractionService<NyxIdChatCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>(new RecordingInteraction<NyxIdChatCommand>())
            .AddSingleton<ICommandInteractionService<NyxIdActionContinuationCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>(action)
            .AddSingleton<IScopeResourceAdmissionPort>(admission));
        context.Request.Headers.Authorization = "Bearer delegated-token";
        context.Response.Body = new MemoryStream();

        await NyxIdChatEndpoints.HandlePublicChatAsync(context, Parse("""
            {
              "type": "action.continue",
              "conversationId": "conversation-alpha",
              "clientRequestId": "request-action",
              "originTurnId": "turn-alpha",
              "actions": [{
                "actionRequestId": "action-alpha",
                "originTurnId": "turn-alpha",
                "disposition": "completed",
                "resource": { "userService": { "userServiceId": "service-alpha" } }
              }]
            }
            """));

        var command = action.Commands.Should().ContainSingle().Which;
        command.ActorId.Should().Be("conversation-alpha");
        command.OriginTurnId.Should().Be("turn-alpha");
        command.ContinuationTurnId.Should().Be(
            NyxIdChatPublicIdentity.CreateTurnId("conversation-alpha", "request-action"));
        command.ContinuationTurnId.Should().NotBe(command.OriginTurnId);
        admission.Targets.Should().ContainSingle().Which.Operation.Should().Be(ScopeResourceOperation.Stream);
    }

    [Fact]
    public async Task ActionContinueEmptyActions_ShouldWakePendingActionsWithoutClaimingCompletion()
    {
        var action = new RecordingInteraction<NyxIdActionContinuationCommand>();
        var context = CreateContext("scope-alpha", services => services
            .AddSingleton<ICommandInteractionService<NyxIdChatCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>(new RecordingInteraction<NyxIdChatCommand>())
            .AddSingleton<ICommandInteractionService<NyxIdActionContinuationCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>(action)
            .AddSingleton<IScopeResourceAdmissionPort>(new RecordingAdmissionPort()));
        context.Request.Headers.Authorization = "Bearer delegated-token";
        context.Response.Body = new MemoryStream();

        await NyxIdChatEndpoints.HandlePublicChatAsync(context, Parse("""
            {
              "type": "action.continue",
              "conversationId": "conversation-alpha",
              "clientRequestId": "request-wake",
              "actions": []
            }
            """));

        var command = action.Commands.Should().ContainSingle().Which;
        command.OriginTurnId.Should().BeEmpty();
        command.Actions.Should().BeEmpty();
        command.ContinuationTurnId.Should().Be(
            NyxIdChatPublicIdentity.CreateTurnId("conversation-alpha", "request-wake"));
    }

    [Fact]
    public async Task Stop_ShouldUseHeaderIdempotencyIdentity_WhenBodyOmitsIt()
    {
        var dispatch = new RecordingDispatchPort();
        var context = CreateContext("scope-alpha", services => services
            .AddSingleton<IScopeResourceAdmissionPort>(new RecordingAdmissionPort())
            .AddSingleton<IActorDispatchPort>(dispatch)
            .AddSingleton<INyxIdChatControlCommandPort, NyxIdChatControlCommandPort>());
        context.Request.Path = "/api/chat";
        context.Request.Headers["Idempotency-Key"] = "header-stop";
        context.Response.Body = new MemoryStream();

        await NyxIdChatEndpoints.HandlePublicChatAsync(context, Parse("""
            {
              "type": "task.stop",
              "conversationId": "conversation-alpha",
              "turnId": "turn-alpha",
              "stopRequestId": "stop-alpha",
              "expectedStateVersion": 7
            }
            """));

        context.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        var command = dispatch.Dispatches.Should().ContainSingle().Which.Envelope.Payload
            .Unpack<NyxIdChatStopCommand>();
        command.ClientRequestId.Should().Be("header-stop");
        context.Response.Headers.Location.ToString().Should().Be(
            "/api/chat/conversations/conversation-alpha/state");
    }

    public static TheoryData<string, string, string> ControlCases => new()
    {
        { "task.stop", "stop", "\"turnId\":\"turn-alpha\",\"stopRequestId\":\"stop-alpha\"" },
        { "task.steer", "steer", "\"turnId\":\"turn-alpha\",\"steeringId\":\"steer-alpha\",\"instruction\":\"focus\"" },
        { "step.retry", "retry", "\"turnId\":\"turn-alpha\",\"taskId\":\"task-alpha\",\"stepId\":\"step-alpha\",\"retryRequestId\":\"retry-alpha\",\"expectedOperationGeneration\":2" },
        { "step.skip", "skip", "\"turnId\":\"turn-alpha\",\"taskId\":\"task-alpha\",\"stepId\":\"step-alpha\",\"skipRequestId\":\"skip-alpha\",\"expectedOperationGeneration\":2" },
    };

    [Theory]
    [MemberData(nameof(ControlCases))]
    public async Task Controls_ShouldDispatchTypedCommandsWithBodyIdempotencyIdentity(
        string type,
        string expectedCommand,
        string fields)
    {
        var dispatch = new RecordingDispatchPort();
        var context = CreateContext("scope-alpha", services => services
            .AddSingleton<IScopeResourceAdmissionPort>(new RecordingAdmissionPort())
            .AddSingleton<IActorDispatchPort>(dispatch)
            .AddSingleton<INyxIdChatControlCommandPort, NyxIdChatControlCommandPort>());
        context.Request.Path = "/api/chat";
        context.Request.Headers.Authorization = "Bearer delegated-token";
        context.Request.Headers["Idempotency-Key"] = "header-control";
        context.Response.Body = new MemoryStream();

        await NyxIdChatEndpoints.HandlePublicChatAsync(context, Parse(
            $"{{\"type\":\"{type}\",\"conversationId\":\"conversation-alpha\"," +
            $"\"clientRequestId\":\"body-control\",{fields},\"expectedStateVersion\":7}}"));

        context.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        var payload = dispatch.Dispatches.Should().ContainSingle().Which.Envelope.Payload;
        var clientRequestId = expectedCommand switch
        {
            "stop" => payload.Unpack<NyxIdChatStopCommand>().ClientRequestId,
            "steer" => payload.Unpack<NyxIdChatSteeringCommand>().ClientRequestId,
            "retry" => payload.Unpack<NyxIdChatRetryStepCommand>().ClientRequestId,
            "skip" => payload.Unpack<NyxIdChatSkipStepCommand>().ClientRequestId,
            _ => throw new InvalidOperationException($"Unexpected command kind: {expectedCommand}"),
        };
        clientRequestId.Should().Be("body-control");
        context.Response.Headers.Location.ToString().Should().Be(
            "/api/chat/conversations/conversation-alpha/state");
    }

    [Fact]
    public async Task Approval_ShouldDispatchTypedCommandWithHeaderIdempotencyIdentity()
    {
        var dispatch = new RecordingDispatchPort();
        var context = CreateContext("scope-alpha", services => services
            .AddSingleton<IScopeResourceAdmissionPort>(new RecordingAdmissionPort())
            .AddSingleton<IActorDispatchPort>(dispatch)
            .AddSingleton<INyxIdChatControlCommandPort, NyxIdChatControlCommandPort>());
        context.Request.Path = "/api/chat";
        context.Request.Headers.Authorization = "Bearer delegated-token";
        context.Request.Headers["Idempotency-Key"] = "header-approval";
        context.Response.Body = new MemoryStream();

        await NyxIdChatEndpoints.HandlePublicChatAsync(context, Parse("""
            {
              "type": "approval.resolve",
              "conversationId": "conversation-alpha",
              "requestId": "approval-alpha",
              "approved": true,
              "reason": "Proceed",
              "expectedStateVersion": 17
            }
            """));

        context.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        var command = dispatch.Dispatches.Should().ContainSingle().Which.Envelope.Payload
            .Unpack<NyxIdChatApprovalResolveCommand>();
        command.ScopeId.Should().Be("scope-alpha");
        command.ConversationActorId.Should().Be("conversation-alpha");
        command.RequestId.Should().Be("approval-alpha");
        command.ClientRequestId.Should().Be("header-approval");
        command.Approved.Should().BeTrue();
        command.Reason.Should().Be("Proceed");
        command.ExpectedStateVersion.Should().Be(17);
        context.Response.Headers.Location.ToString().Should().Be(
            "/api/chat/conversations/conversation-alpha/state");
    }

    [Fact]
    public async Task Input_ShouldDispatchTypedCommandWithBodyIdempotencyIdentity()
    {
        var dispatch = new RecordingDispatchPort();
        var context = CreateContext("scope-alpha", services => services
            .AddSingleton<IScopeResourceAdmissionPort>(new RecordingAdmissionPort())
            .AddSingleton<IActorDispatchPort>(dispatch)
            .AddSingleton<INyxIdChatControlCommandPort, NyxIdChatControlCommandPort>());
        context.Request.Headers.Authorization = "Bearer delegated-token";
        context.Request.Headers["Idempotency-Key"] = "header-input";
        context.Response.Body = new MemoryStream();

        await NyxIdChatEndpoints.HandlePublicChatAsync(context, Parse("""
            {
              "type": "input.resolve",
              "conversationId": "conversation-alpha",
              "requestId": "input-alpha",
              "clientRequestId": "body-input",
              "answer": {"selectedOptionIds": ["option-a", "option-b"]},
              "expectedStateVersion": 19
            }
            """));

        context.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        var command = dispatch.Dispatches.Should().ContainSingle().Which.Envelope.Payload
            .Unpack<NyxIdChatInputResolveCommand>();
        command.ScopeId.Should().Be("scope-alpha");
        command.ConversationActorId.Should().Be("conversation-alpha");
        command.RequestId.Should().Be("input-alpha");
        command.ClientRequestId.Should().Be("body-input");
        command.Answer.AnswerCase.Should().Be(NyxIdChatInputAnswer.AnswerOneofCase.Selection);
        command.Answer.Selection.OptionIds.Should().Equal("option-a", "option-b");
        command.ToolContext.Credentials.NyxIdAccessToken.Should().Be("delegated-token");
        command.ExpectedStateVersion.Should().Be(19);
    }

    [Fact]
    public async Task Approval_ShouldRejectMissingExplicitDecision()
    {
        var dispatch = new RecordingDispatchPort();
        var context = CreateContext("scope-alpha", services => services
            .AddSingleton<IScopeResourceAdmissionPort>(new RecordingAdmissionPort())
            .AddSingleton<IActorDispatchPort>(dispatch)
            .AddSingleton<INyxIdChatControlCommandPort, NyxIdChatControlCommandPort>());
        context.Request.Headers.Authorization = "Bearer delegated-token";
        context.Request.Headers["Idempotency-Key"] = "header-approval";
        context.Response.Body = new MemoryStream();

        await NyxIdChatEndpoints.HandlePublicChatAsync(context, Parse("""
            {
              "type": "approval.resolve",
              "conversationId": "conversation-alpha",
              "requestId": "approval-alpha"
            }
            """));

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        dispatch.Dispatches.Should().BeEmpty();
    }

    [Theory]
    [InlineData("input.resolve", "\"answer\":\"Option A\"")]
    [InlineData("approval.resolve", "\"approved\":true")]
    public async Task NeedsYouResolution_ShouldRequireObservedActorVersion(
        string type,
        string decisionField)
    {
        var dispatch = new RecordingDispatchPort();
        var context = CreateContext("scope-alpha", services => services
            .AddSingleton<IScopeResourceAdmissionPort>(new RecordingAdmissionPort())
            .AddSingleton<IActorDispatchPort>(dispatch)
            .AddSingleton<INyxIdChatControlCommandPort, NyxIdChatControlCommandPort>());
        context.Request.Headers["Idempotency-Key"] = "header-needs-you";
        context.Response.Body = new MemoryStream();

        await NyxIdChatEndpoints.HandlePublicChatAsync(context, Parse(
            $"{{\"type\":\"{type}\",\"conversationId\":\"conversation-alpha\"," +
            $"\"requestId\":\"request-alpha\",{decisionField}}}"));

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        dispatch.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task PublicRequest_ShouldRejectCallerSuppliedScopeId()
    {
        var chat = new RecordingInteraction<NyxIdChatCommand>();
        var context = CreateContext("scope-alpha", services => services
            .AddSingleton<ICommandInteractionService<NyxIdChatCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>(chat)
            .AddSingleton<ICommandInteractionService<NyxIdActionContinuationCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>(new RecordingInteraction<NyxIdActionContinuationCommand>())
            .AddSingleton<IScopeResourceAdmissionPort>(new RecordingAdmissionPort()));
        context.Response.Body = new MemoryStream();

        await NyxIdChatEndpoints.HandlePublicChatAsync(context, Parse("""
            {
              "type": "text",
              "scopeId": "scope-other",
              "clientRequestId": "request-alpha",
              "prompt": "hello"
            }
            """));

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        chat.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task ListConversations_ShouldFilterNyxIdHistoryAndRejectAmbiguousScope()
    {
        var history = new RecordingHistoryPort();
        var state = new RecordingStateQueryPort
        {
            AttentionSummaries = new Dictionary<string, NyxIdChatConversationAttentionSummary>
            {
                ["conversation-alpha"] = new(
                    "conversation-alpha",
                    "active",
                    "input",
                    DateTimeOffset.Parse("2026-08-01T12:00:00Z"),
                    "Choose a deployment region.",
                    23),
            },
        };
        var context = CreateContext("scope-alpha", services => services
            .AddSingleton<IChatHistoryQueryPort>(history)
            .AddSingleton<INyxIdChatConversationStateQueryPort>(state));
        var response = await ExecutePublicRouteAsync(
            context,
            HttpMethods.Get,
            "/api/chat/conversations",
            queryString: "?pageSize=50&cursor=cursor-alpha");

        history.Requests.Should().ContainSingle().Which.Should().Be(
            new ChatHistoryIndexPageRequest("scope-alpha", 50, "cursor-alpha"));
        using var body = JsonDocument.Parse(response.Body);
        var conversation = body.RootElement.GetProperty("conversations").EnumerateArray()
            .Should().ContainSingle().Subject;
        conversation.GetProperty("id").GetString().Should().Be("conversation-alpha");
        conversation.GetProperty("taskStatus").GetString().Should().Be("active");
        conversation.GetProperty("attentionKind").GetString().Should().Be("input");
        conversation.GetProperty("activeStepSummary").GetString().Should()
            .Be("Choose a deployment region.");
        conversation.GetProperty("stateVersion").GetInt64().Should().Be(23);
        state.AttentionRequests.Should().ContainSingle().Which.Should().BeEquivalentTo(
            ("scope-alpha", (IReadOnlyCollection<string>)["conversation-alpha"]));

        var ambiguous = CreateContext("scope-alpha", services => services
            .AddSingleton<IChatHistoryQueryPort>(history)
            .AddSingleton<INyxIdChatConversationStateQueryPort>(state));
        ((ClaimsIdentity)ambiguous.User.Identity!).AddClaim(new Claim("workflow.scope_id", "scope-beta"));
        var denied = await ExecutePublicRouteAsync(
            ambiguous,
            HttpMethods.Get,
            "/api/chat/conversations");
        denied.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        history.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task TranscriptRoute_ShouldAuthorizeClaimOwnedConversation()
    {
        var history = new RecordingHistoryPort
        {
            MessagesResult = ChatHistoryConversationMessagesResult.Found(
                [new StoredChatMessage("message-alpha", "assistant", "done", 1, "completed")],
                9),
        };
        var admission = new RecordingAdmissionPort();
        var context = CreateContext("scope-alpha", services => services
            .AddSingleton<IChatHistoryQueryPort>(history)
            .AddSingleton<IScopeResourceAdmissionPort>(admission));

        var response = await ExecutePublicRouteAsync(
            context,
            HttpMethods.Get,
            "/api/chat/conversations/{conversationId}",
            new RouteValueDictionary { ["conversationId"] = "conversation-alpha" });

        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        history.MessageRequests.Should().ContainSingle().Which.Should().Be(("scope-alpha", "conversation-alpha"));
        admission.Targets.Should().ContainSingle().Which.Operation.Should().Be(ScopeResourceOperation.Use);
        using var body = JsonDocument.Parse(response.Body);
        body.RootElement.GetProperty("stateVersion").GetInt64().Should().Be(9);
    }

    [Fact]
    public async Task StateRoute_ShouldForwardConditionalCursorUnderClaimScope()
    {
        var registry = RecordingRegistryQueryPort.OwningConversation();
        var state = new RecordingStateQueryPort();
        var context = CreateContext("scope-alpha", services => services
            .AddSingleton<IGAgentActorRegistryQueryPort>(registry)
            .AddSingleton<INyxIdChatConversationStateQueryPort>(state));

        var response = await ExecutePublicRouteAsync(
            context,
            HttpMethods.Get,
            "/api/chat/conversations/{conversationId}/state",
            new RouteValueDictionary { ["conversationId"] = "conversation-alpha" },
            "?afterStateVersion=8&turnId=turn-alpha");

        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        state.Queries.Should().ContainSingle().Which.Should().Be(
            new NyxIdChatConversationStateQuery("scope-alpha", "conversation-alpha", 8, "turn-alpha"));
    }

    [Fact]
    public async Task DeleteRoute_ShouldReturnHonestAcceptedPublicRecoveryUrl()
    {
        var delete = new RecordingDispatchService<
            NyxIdChatConversationDeleteCommand,
            NyxIdChatLifecycleCommandReceipt,
            NyxIdChatLifecycleCommandStartError>(command =>
                CommandDispatchResult<NyxIdChatLifecycleCommandReceipt, NyxIdChatLifecycleCommandStartError>.Success(
                    new NyxIdChatLifecycleCommandReceipt(command.ActorId, "command-alpha", "correlation-alpha")));
        var create = new RecordingDispatchService<
            NyxIdChatConversationCreateCommand,
            NyxIdChatLifecycleCommandReceipt,
            NyxIdChatLifecycleCommandStartError>(_ => throw new InvalidOperationException("Create is not used by delete."));
        var facade = new NyxIdChatLifecycleFacade(create, delete);
        var context = CreateContext("scope-alpha", services => services.AddSingleton(facade));

        var response = await ExecutePublicRouteAsync(
            context,
            HttpMethods.Delete,
            "/api/chat/conversations/{conversationId}",
            new RouteValueDictionary { ["conversationId"] = "conversation-alpha" });

        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        response.Location.Should().Be("/api/chat/conversations/conversation-alpha/state");
        delete.Commands.Should().ContainSingle().Which.Should().Match<NyxIdChatConversationDeleteCommand>(command =>
            command.ScopeId == "scope-alpha" && command.ActorId == "conversation-alpha");
    }

    [Fact]
    public async Task PublicRoute_ShouldRequireScopeClaim_WhenDevelopmentAuthenticationIsDisabled()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Aevatar:Authentication:Enabled"] = "false",
                })
            .Build())
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment { EnvironmentName = Environments.Development })
            .AddSingleton<IChatHistoryQueryPort>(new RecordingHistoryPort())
            .AddSingleton<INyxIdChatConversationStateQueryPort>(new RecordingStateQueryPort())
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };

        var response = await ExecutePublicRouteAsync(context, HttpMethods.Get, "/api/chat/conversations");

        response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static DefaultHttpContext CreateContext(
        string scopeId,
        Func<IServiceCollection, IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Aevatar:Authentication:Enabled"] = "true",
                })
                .Build())
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        configure?.Invoke(services);
        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("scope_id", scopeId), new Claim("sub", "user-alpha")],
                "test")),
            RequestServices = services.BuildServiceProvider(),
        };
    }

    private static async Task<(int StatusCode, string Body, string? Location)> ExecutePublicRouteAsync(
        DefaultHttpContext context,
        string method,
        string routePattern,
        RouteValueDictionary? routeValues = null,
        string? queryString = null)
    {
        context.Request.Method = method;
        context.Request.Path = routePattern.Replace("{conversationId}", "conversation-alpha", StringComparison.Ordinal);
        context.Request.RouteValues = routeValues ?? new RouteValueDictionary();
        if (queryString is not null)
            context.Request.QueryString = new QueryString(queryString);
        context.Response.Body = new MemoryStream();

        await BuildPublicRouteEndpoint(routePattern, method).RequestDelegate!(context);
        context.Response.Body.Position = 0;
        return (
            context.Response.StatusCode,
            await new StreamReader(context.Response.Body).ReadToEndAsync(),
            context.Response.Headers.Location.ToString());
    }

    private static RouteEndpoint BuildPublicRouteEndpoint(string routePattern, string method)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        var app = builder.Build();
        app.MapNyxIdChatPublicEndpoints();
        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(endpoint =>
                string.Equals(endpoint.RoutePattern.RawText, routePattern, StringComparison.Ordinal) &&
                endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method));
    }

    private sealed class RecordingInteraction<TCommand>
        : ICommandInteractionService<TCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>
    {
        public List<TCommand> Commands { get; } = [];

        public async Task<CommandInteractionResult<NyxIdChatAcceptedReceipt, NyxIdChatStartError, NyxIdChatCompletionStatus>> ExecuteAsync(
            TCommand command,
            Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
            Func<NyxIdChatAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            Commands.Add(command);
            var (actorId, turnId) = command switch
            {
                NyxIdChatCommand chat => (chat.ActorId, chat.TurnId),
                NyxIdActionContinuationCommand continuation =>
                    (continuation.ActorId, continuation.ContinuationTurnId),
                _ => ("conversation", "turn"),
            };
            var receipt = new NyxIdChatAcceptedReceipt(actorId, "command-alpha", "correlation-alpha", turnId);
            if (onAcceptedAsync is not null)
                await onAcceptedAsync(receipt, ct);
            await emitAsync(new AGUIEvent
            {
                RunFinished = new RunFinishedEvent { RunId = turnId, ThreadId = actorId },
            }, ct);
            return CommandInteractionResult<NyxIdChatAcceptedReceipt, NyxIdChatStartError, NyxIdChatCompletionStatus>.Success(
                receipt,
                new CommandInteractionFinalizeResult<NyxIdChatCompletionStatus>(
                    NyxIdChatCompletionStatus.Completed,
                    true));
        }

        async Task<RealtimeSessionResult<NyxIdChatAcceptedReceipt, NyxIdChatStartError, NyxIdChatCompletionStatus>>
            IRealtimeSession<TCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>.ExecuteAsync(
                TCommand inbound,
                Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
                Func<NyxIdChatAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync,
                CancellationToken ct) =>
            await ExecuteAsync(inbound, emitAsync, onAcceptedAsync, ct);
    }

    private sealed class RecordingAdmissionPort : IScopeResourceAdmissionPort
    {
        public List<ScopeResourceTarget> Targets { get; } = [];

        public Task<ScopeResourceAdmissionResult> AuthorizeTargetAsync(
            ScopeResourceTarget target,
            CancellationToken cancellationToken = default)
        {
            Targets.Add(target);
            return Task.FromResult(ScopeResourceAdmissionResult.Allowed());
        }
    }

    private sealed class RecordingDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Dispatches { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Dispatches.Add((actorId, envelope.Clone()));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class RecordingHistoryPort : IChatHistoryQueryPort
    {
        public List<ChatHistoryIndexPageRequest> Requests { get; } = [];
        public List<(string ScopeId, string ConversationId)> MessageRequests { get; } = [];
        public ChatHistoryConversationMessagesResult MessagesResult { get; init; } =
            ChatHistoryConversationMessagesResult.NotFound();

        public Task<ChatHistoryIndexPage> GetIndexAsync(
            ChatHistoryIndexPageRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ChatHistoryIndexPage(
                [
                    new ConversationMeta("conversation-alpha", "Assistant", "conversation-alpha", NyxIdChatServiceDefaults.GAgentKind, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1),
                    new ConversationMeta("workflow-alpha", "Workflow", "workflow-alpha", "workflow", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1),
                ],
                "cursor-beta"));
        }

        public Task<ChatHistoryConversationMessagesResult> GetMessagesAsync(
            string scopeId,
            string conversationId,
            CancellationToken ct = default)
        {
            MessageRequests.Add((scopeId, conversationId));
            return Task.FromResult(MessagesResult);
        }

        public Task<ChatHistoryCreateRecoveryResult> GetCreateRecoveryAsync(
            string scopeId,
            string commandId,
            CancellationToken ct = default) =>
            Task.FromResult(ChatHistoryCreateRecoveryResult.NotFound(scopeId, commandId));
    }

    private sealed class RecordingRegistryQueryPort : IGAgentActorRegistryQueryPort
    {
        public required GAgentActorRegistrySnapshot Snapshot { get; init; }

        public Task<GAgentActorRegistrySnapshot> ListActorsAsync(
            string scopeId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public static RecordingRegistryQueryPort OwningConversation() => new()
        {
            Snapshot = new GAgentActorRegistrySnapshot(
                "scope-alpha",
                [new GAgentActorGroup(NyxIdChatServiceDefaults.GAgentKind, ["conversation-alpha"])],
                9,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch),
        };
    }

    private sealed class RecordingStateQueryPort : INyxIdChatConversationStateQueryPort
    {
        public List<NyxIdChatConversationStateQuery> Queries { get; } = [];
        public List<(string ScopeId, IReadOnlyCollection<string> ActorIds)> AttentionRequests { get; } = [];
        public IReadOnlyDictionary<string, NyxIdChatConversationAttentionSummary> AttentionSummaries { get; init; } =
            new Dictionary<string, NyxIdChatConversationAttentionSummary>();

        public Task<NyxIdChatConversationStateQueryResult> GetAsync(
            NyxIdChatConversationStateQuery query,
            CancellationToken ct = default)
        {
            Queries.Add(query);
            return Task.FromResult(NyxIdChatConversationStateQueryResult.NotModified(9, query.TurnId));
        }

        public Task<IReadOnlyDictionary<string, NyxIdChatConversationAttentionSummary>>
            GetAttentionSummariesAsync(
                string scopeId,
                IReadOnlyCollection<string> actorIds,
                CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            AttentionRequests.Add((scopeId, actorIds.ToArray()));
            return Task.FromResult(AttentionSummaries);
        }
    }

    private sealed class RecordingDispatchService<TCommand, TReceipt, TError>(
        Func<TCommand, CommandDispatchResult<TReceipt, TError>> dispatch)
        : ICommandDispatchService<TCommand, TReceipt, TError>
    {
        public List<TCommand> Commands { get; } = [];

        public Task<CommandDispatchResult<TReceipt, TError>> DispatchAsync(
            TCommand command,
            CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.FromResult(dispatch(command));
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Aevatar.AI.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
