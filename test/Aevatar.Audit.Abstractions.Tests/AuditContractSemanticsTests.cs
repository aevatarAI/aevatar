using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Models;
using Shouldly;

namespace Aevatar.Audit.Abstractions.Tests;

public sealed class AuditContractSemanticsTests
{
    [Theory]
    [InlineData(AuditOutcome.Accepted, AuditLifecyclePhase.Accepted, AuditTerminalOutcome.Unspecified)]
    [InlineData(AuditOutcome.Success, AuditLifecyclePhase.Terminal, AuditTerminalOutcome.Succeeded)]
    [InlineData(AuditOutcome.Denied, AuditLifecyclePhase.Terminal, AuditTerminalOutcome.Failed)]
    [InlineData(AuditOutcome.Error, AuditLifecyclePhase.Terminal, AuditTerminalOutcome.Failed)]
    [InlineData(AuditOutcome.Cancelled, AuditLifecyclePhase.Terminal, AuditTerminalOutcome.Cancelled)]
    [InlineData(AuditOutcome.Unspecified, AuditLifecyclePhase.Unspecified, AuditTerminalOutcome.Unspecified)]
    public void LegacyRecords_MapOnlyUnambiguousLifecycleFacts(
        AuditOutcome outcome,
        AuditLifecyclePhase expectedPhase,
        AuditTerminalOutcome expectedTerminalOutcome)
    {
        var record = new AuditRecord { Outcome = outcome };

        AuditContractSemantics.ResolveLifecyclePhase(record).ShouldBe(expectedPhase);
        AuditContractSemantics.ResolveTerminalOutcome(record).ShouldBe(expectedTerminalOutcome);
    }

    [Fact]
    public void ExplicitLifecycleFields_RemainAuthoritative()
    {
        var record = new AuditRecord
        {
            Outcome = AuditOutcome.Success,
            LifecyclePhase = AuditLifecyclePhase.WaitingApproval,
            TerminalOutcome = AuditTerminalOutcome.Unspecified,
        };

        AuditContractSemantics.ResolveLifecyclePhase(record).ShouldBe(AuditLifecyclePhase.WaitingApproval);
        AuditContractSemantics.ResolveTerminalOutcome(record).ShouldBe(AuditTerminalOutcome.Unspecified);
    }

    [Fact]
    public void CurrentRecordWithoutApplicableLifecycle_DoesNotInferLegacySemantics()
    {
        var record = new AuditRecord
        {
            SchemaVersion = AuditContractSemantics.CurrentSchemaVersion,
            Outcome = AuditOutcome.Success,
        };

        AuditContractSemantics.ResolveLifecyclePhase(record).ShouldBe(AuditLifecyclePhase.Unspecified);
        AuditContractSemantics.ResolveTerminalOutcome(record).ShouldBe(AuditTerminalOutcome.Unspecified);
    }
}
