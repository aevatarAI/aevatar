using System.Security.Claims;
using System.Text.Json;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.NyxidChat;
using Aevatar.AGUI.Contracts;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.Tests;

public partial class NyxIdChatEndpointsCoverageTests
{
    [Fact]
    public async Task HandleStreamMessageAsync_ActionContinue_ShouldUseAuthenticatedSubjectAndServerTurn()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", "owner-alpha")],
                authenticationType: "test")),
        };
        context.Request.Headers.Authorization = "Bearer valid-token";
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
        var report = command.Actions.Should().ContainSingle().Which;
        report.ActionRequestId.Should().Be("action-alpha");
        report.OriginTurnId.Should().Be("turn-origin-alpha");
        report.Disposition.Should().Be(NyxIdChatActionDisposition.Completed);
        report.Resource.UserService.UserServiceId.Should().Be("service-alpha");
        (await ReadResponseBodyAsync(context)).Should()
            .Contain("RUN_FINISHED")
            .And.Contain(command.ContinuationTurnId);
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
            CorrelationId: "correlation-alpha");

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
        using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IActorRuntime>(runtime)
            .AddSingleton<IActorDispatchPort>(dispatchPort)
            .AddSingleton<INyxIdChatSessionProjectionPort>(projectionPort)
            .AddNyxIdChat()
            .BuildServiceProvider();
        var interaction = services.GetRequiredService<
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
            "turn-continuation-alpha"));
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
}
