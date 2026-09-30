using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Audit.Abstractions.Ports;
using FluentAssertions;

namespace Aevatar.AI.Core.Tests.Tools;

public sealed partial class AdmittedAgentToolExecutorTests
{
    [Fact]
    public async Task SenderBearerSelection_ShouldPreserveOriginalContinuationAuthorizationWithoutRegistrationCredential()
    {
        var appender = new RecordingAuditTrailAppender((record, _) => AuditTrailAppendResult.Appended(record.AuditId));
        var tool = new RecordingTool(new AgentToolCallSafety(false, false, false),
            credentialRequirement: AgentToolNyxIdCredentialRequirement.SenderBearer);
        var context = CreateTestExecutionContext() with
        {
            Request = new AgentToolRequestIdentity("request-callback", "call-callback"),
            CredentialSource = AgentToolCredentialSource.ChannelRegistration,
            Credentials = new AgentToolCredentials("registration-key", null, "sender-token", AgentToolNyxIdCredentialKind.AgentKey),
            Caller = new AgentToolCallerContext("registration-scope", "registration-scope", "source-message", "sender-owner"),
            NyxIdAuthority = new AgentToolNyxIdAuthorityContext("lark", "tenant-a", "bound-external-sender"),
            SenderBinding = new AgentToolSenderBindingContext("binding-a", "sender-nyx-user", "tenant-a"),
            Channel = AgentToolChannelContext.Empty with
            {
                SenderId = "routing-sender", BotRegistrationId = "registration-a",
                Continuation = new AgentToolChannelContinuationContext
                {
                    ConversationActorId = "original-conversation", ChannelRegistrationId = "registration-a",
                    CanonicalConversationKey = "original-thread", OriginalActivityId = "original-activity",
                },
            },
        };

        var result = await CreateExecutor(appender).ExecuteAsync(CreateRequest(tool) with { ExecutionContext = context });

        result.TerminalInvoked.Should().BeTrue();
        var executed = tool.ExecutionContexts.Should().ContainSingle().Subject;
        executed.DurableNyxIdCredential.Should().BeNull();
        var authorization = executed.Channel.Continuation!.OriginalSenderAuthorization;
        authorization.BindingId.Should().Be("binding-a");
        authorization.OwnerScopeId.Should().Be("sender-owner");
        authorization.ExternalUserId.Should().Be("bound-external-sender");
        authorization.NyxUserId.Should().Be("sender-nyx-user");
        var durable = AgentToolExecutionContextMapper.FromPayload(executed.ToPayload());
        durable.Channel.Continuation!.CanonicalConversationKey.Should().Be("original-thread");
        durable.Channel.Continuation.OriginalSenderAuthorization.Should().BeEquivalentTo(authorization);
    }
}
