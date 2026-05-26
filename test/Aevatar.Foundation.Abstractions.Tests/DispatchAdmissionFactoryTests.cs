using Shouldly;

namespace Aevatar.Foundation.Abstractions.Tests;

public sealed class DispatchAdmissionFactoryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithBlankOrMissingEnvelopeId_GeneratesStableCommandId(string? envelopeId)
    {
        var envelope = new EventEnvelope
        {
            Id = envelopeId ?? string.Empty,
        };

        var admission = DispatchAdmissionFactory.Create("actor-1", envelope);

        admission.Accepted.ShouldBeTrue();
        admission.CommandId.ShouldNotBeNullOrWhiteSpace();
        Guid.TryParseExact(admission.CommandId, "N", out _).ShouldBeTrue();
        admission.CorrelationId.ShouldBe(admission.CommandId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithBlankOrMissingCorrelationId_FallsBackToCommandId(string? correlationId)
    {
        var envelope = new EventEnvelope
        {
            Id = "command-1",
            Propagation = new EnvelopePropagation
            {
                CorrelationId = correlationId ?? string.Empty,
            },
        };

        var admission = DispatchAdmissionFactory.Create("actor-1", envelope);

        admission.CommandId.ShouldBe("command-1");
        admission.CorrelationId.ShouldBe("command-1");
    }

    [Fact]
    public void Create_WithWhitespaceAroundInputs_TrimsStableReceiptFields()
    {
        var envelope = new EventEnvelope
        {
            Id = "  command-1  ",
            Propagation = new EnvelopePropagation
            {
                CorrelationId = "  corr-1  ",
            },
        };

        var admission = DispatchAdmissionFactory.Create("  actor-1  ", envelope);

        admission.CommandId.ShouldBe("command-1");
        admission.ActorId.ShouldBe("actor-1");
        admission.CorrelationId.ShouldBe("corr-1");
    }

    [Fact]
    public void Create_WithNullEnvelope_Throws()
    {
        Should.Throw<ArgumentNullException>(() =>
            DispatchAdmissionFactory.Create("actor-1", null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithIllegalActorId_Throws(string? actorId)
    {
        Should.Throw<ArgumentException>(() =>
            DispatchAdmissionFactory.Create(actorId!, new EventEnvelope { Id = "command-1" }));
    }
}
