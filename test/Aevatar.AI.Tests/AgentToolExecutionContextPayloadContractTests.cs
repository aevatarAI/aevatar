using Aevatar.AI.Abstractions;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class AgentToolExecutionContextPayloadContractTests
{
    [Fact]
    public void AgentToolExecutionContextPayload_ShouldExposeCredentialSourceAndScheduleAsTypedFields()
    {
        ((int)AgentToolCredentialSourcePayload.Unspecified).Should().Be(0);
        ((int)AgentToolCredentialSourcePayload.NyxidAssertion).Should().Be(1);
        ((int)AgentToolCredentialSourcePayload.BearerToken).Should().Be(2);
        ((int)AgentToolCredentialSourcePayload.ChannelRegistration).Should().Be(3);
        ((int)AgentToolCredentialSourcePayload.ScheduledRun).Should().Be(4);
        ((int)AgentToolCredentialSourcePayload.System).Should().Be(5);
        ((int)AgentToolCredentialSourcePayload.ServiceAccount).Should().Be(6);

        AgentToolExecutionContextPayload.Descriptor.Fields.InFieldNumberOrder()
            .Select(field => (field.FieldNumber, field.Name))
            .Should()
            .Contain((12, "credential_source"))
            .And.Contain((13, "schedule"));

        AgentToolScheduleContextPayload.Descriptor.Fields.InFieldNumberOrder()
            .Select(field => (field.FieldNumber, field.Name))
            .Should()
            .Equal((1, "schedule_id"));
    }
}
