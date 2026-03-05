using FluentAssertions;
using Aevatar.App.GAgents.Rules;

namespace Aevatar.App.GAgents.Tests;

public sealed class SyncRulesTests
{
    [Fact]
    public void Evaluate_NewEntity_RevisionZero_ReturnsCreated()
    {
        var incoming = new SyncEntity { Revision = 0, ClientId = "test_1" };
        SyncRules.Evaluate(existing: null, incoming).Should().Be(SyncRuleResult.Created);
    }

    [Fact]
    public void Evaluate_ExistingEntity_RevisionMatches_ReturnsUpdated()
    {
        var existing = new SyncEntity { Revision = 3, ClientId = "test_1" };
        var incoming = new SyncEntity { Revision = 3, ClientId = "test_1" };
        SyncRules.Evaluate(existing, incoming).Should().Be(SyncRuleResult.Updated);
    }

    [Fact]
    public void Evaluate_ExistingEntity_RevisionMismatch_ReturnsStale()
    {
        var existing = new SyncEntity { Revision = 5, ClientId = "test_1" };
        var incoming = new SyncEntity { Revision = 3, ClientId = "test_1" };
        SyncRules.Evaluate(existing, incoming).Should().Be(SyncRuleResult.Stale);
    }

    [Fact]
    public void Evaluate_UnknownEntity_RevisionGreaterThanZero_ReturnsStale()
    {
        var incoming = new SyncEntity { Revision = 1, ClientId = "test_1" };
        SyncRules.Evaluate(existing: null, incoming).Should().Be(SyncRuleResult.Stale);
    }

    [Fact]
    public void Evaluate_ExistingEntity_RevisionZero_ReturnsStale()
    {
        var existing = new SyncEntity { Revision = 1, ClientId = "test_1" };
        var incoming = new SyncEntity { Revision = 0, ClientId = "test_1" };
        SyncRules.Evaluate(existing, incoming).Should().Be(SyncRuleResult.Stale);
    }
}
