using Aevatar.AI.Abstractions;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class AgentToolExecutionContextPayloadContractTests
{
    [Fact]
    public void AgentToolExecutionContextPayload_ShouldExposeCredentialSourceScheduleAndChatAsTypedFields()
    {
        ((int)AgentToolCredentialSourcePayload.Unspecified).Should().Be(0);
        ((int)AgentToolCredentialSourcePayload.NyxidAssertion).Should().Be(1);
        ((int)AgentToolCredentialSourcePayload.BearerToken).Should().Be(2);
        ((int)AgentToolCredentialSourcePayload.ChannelRegistration).Should().Be(3);
        ((int)AgentToolCredentialSourcePayload.ScheduledRun).Should().Be(4);
        ((int)AgentToolCredentialSourcePayload.System).Should().Be(5);
        ((int)AgentToolCredentialSourcePayload.ServiceAccount).Should().Be(6);
        ((int)AgentToolNyxIdCredentialAuthorityPayload.Unspecified).Should().Be(0);
        ((int)AgentToolNyxIdCredentialAuthorityPayload.ToolExecutionContext).Should().Be(1);

        AgentToolExecutionContextPayload.Descriptor.Fields.InFieldNumberOrder()
            .Select(field => (field.FieldNumber, field.Name))
            .Should()
            .Contain((12, "credential_source"))
            .And.Contain((13, "schedule"))
            .And.Contain((17, "chat"))
            .And.Contain((20, "durable_nyx_id_credential"));

        AgentToolScheduleContextPayload.Descriptor.Fields.InFieldNumberOrder()
            .Select(field => (field.FieldNumber, field.Name))
            .Should()
            .Equal((1, "schedule_id"));

        AgentToolCredentialsPayload.Descriptor.Fields.InFieldNumberOrder()
            .Select(field => (field.FieldNumber, field.Name))
            .Should()
            .Contain((6, "nyx_id_credential_authority"));

        AgentToolRecoveryContextPayload.Descriptor.Fields.InFieldNumberOrder()
            .Select(field => (field.FieldNumber, field.Name))
            .Should()
            .Contain((22, "nyx_id_credential_authority"));

        AgentChatInvocationContextPayload.Descriptor.Fields.InFieldNumberOrder()
            .Select(static field => field.Name)
            .Should()
            .Equal("surface", "conversation_id", "turn_id", "task_id", "step_id", "action_request_id");
    }
}
