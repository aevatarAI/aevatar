using Aevatar.AI.Abstractions;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.AI.Tests;

public sealed class RoleChatIncompleteSessionFinalizationContractTests
{
    [Fact]
    public void Request_ShouldPreserveTypedReconciliationKeyAcrossProtobufRoundTrip()
    {
        var request = new RoleChatIncompleteSessionFinalizationRequested
        {
            SessionId = "session-1",
            ExpectedLastProgressSequence = 7,
        };

        var parsed = RoleChatIncompleteSessionFinalizationRequested.Parser.ParseFrom(request.ToByteArray());

        parsed.Should().Be(request);
        RoleChatIncompleteSessionFinalizationRequested.Descriptor.Fields
            .InFieldNumberOrder()
            .Select(static field => (field.FieldNumber, field.Name))
            .Should().Equal(
                (1, "session_id"),
                (2, "expected_last_progress_sequence"));
    }
}
