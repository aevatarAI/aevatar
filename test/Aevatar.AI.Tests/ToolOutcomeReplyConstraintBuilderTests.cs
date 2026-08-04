using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Tools;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class ToolOutcomeReplyConstraintBuilderTests
{
    [Fact]
    public void BuildMutationClaimConstraints_WhenOnlyReadOnlyToolSucceeded_ShouldReturnConstraint()
    {
        var constraints = ToolOutcomeReplyConstraintBuilder.BuildMutationClaimConstraints(
            [Succeeded(new StubTool("read", isReadOnly: true))],
            toolReceipts: null);

        constraints.Should().ContainSingle();
        constraints[0].Role.Should().Be("system");
        constraints[0].Content.Should().Contain("no successful mutating tool execution");
    }

    [Fact]
    public void BuildMutationClaimConstraints_WhenMutatingToolSucceeded_ShouldReturnGroundingConstraint()
    {
        var constraints = ToolOutcomeReplyConstraintBuilder.BuildMutationClaimConstraints(
            [Succeeded(new StubTool("write"))],
            toolReceipts: null);

        constraints.Should().ContainSingle();
        constraints[0].Content.Should().Contain("whose tool, side effect, and subject match that exact action");
        constraints[0].Content.Should().NotContain("no successful mutating tool execution");
    }

    [Fact]
    public void BuildMutationClaimConstraints_WhenReadOnlyAndMutatingSuccessAreMixed_ShouldReturnGroundingConstraint()
    {
        var constraints = ToolOutcomeReplyConstraintBuilder.BuildMutationClaimConstraints(
            [
                Succeeded(new StubTool("read", isReadOnly: true)),
                Succeeded(new StubTool("write")),
            ],
            toolReceipts: null);

        constraints.Should().ContainSingle();
        constraints[0].Content.Should().Contain("different action");
        constraints[0].Content.Should().NotContain("no successful mutating tool execution");
    }

    [Theory]
    [InlineData(AgentToolReceiptStatus.Error)]
    [InlineData(AgentToolReceiptStatus.Denied)]
    [InlineData(AgentToolReceiptStatus.ApprovalRequired)]
    [InlineData(AgentToolReceiptStatus.Unspecified)]
    public void BuildMutationClaimConstraints_WhenMutatingToolDidNotSucceed_ShouldReturnConstraint(
        AgentToolReceiptStatus status)
    {
        var constraints = ToolOutcomeReplyConstraintBuilder.BuildMutationClaimConstraints(
            [
                new ToolOutcomeReplyFact(
                    new StubTool("write"),
                    "{}",
                    Succeeded: false,
                    Receipt(status, isDestructive: true)),
            ],
            toolReceipts: null);

        constraints.Should().ContainSingle();
    }

    [Fact]
    public void BuildMutationClaimConstraints_WhenSuccessfulMutatingReceiptExists_ShouldReturnGroundingConstraint()
    {
        var constraints = ToolOutcomeReplyConstraintBuilder.BuildMutationClaimConstraints(
            toolOutcomes: null,
            toolReceipts:
            [
                Receipt(
                    AgentToolReceiptStatus.Success,
                    sideEffectKind: "definition.update",
                    effect: AgentToolReceiptEffect.Mutating),
            ]);

        constraints.Should().ContainSingle();
        constraints[0].Content.Should().Contain("match that exact action");
        constraints[0].Content.Should().NotContain("no successful mutating tool execution");
    }

    [Fact]
    public void BuildMutationClaimConstraints_WhenLegacyMutatingSuccessHasUnspecifiedEffect_ShouldKeepNoSuccessConstraint()
    {
        var constraints = ToolOutcomeReplyConstraintBuilder.BuildMutationClaimConstraints(
            [
                new ToolOutcomeReplyFact(
                    new StubTool("write"),
                    "{}",
                    Succeeded: true,
                    Receipt(
                        AgentToolReceiptStatus.Success,
                        sideEffectKind: "definition.update")),
            ],
            toolReceipts: null);

        constraints.Should().ContainSingle();
        constraints[0].Content.Should().Contain("no successful mutating tool execution");
    }

    [Fact]
    public void BuildMutationClaimConstraints_WhenOnlyFailedMutatingReceiptsExist_ShouldReturnConstraint()
    {
        var constraints = ToolOutcomeReplyConstraintBuilder.BuildMutationClaimConstraints(
            toolOutcomes: null,
            toolReceipts:
            [
                Receipt(AgentToolReceiptStatus.Error, sideEffectKind: "definition.update"),
                Receipt(AgentToolReceiptStatus.Denied, isDestructive: true),
            ]);

        constraints.Should().ContainSingle();
    }

    [Fact]
    public void IsMutatingTool_ShouldKeepApprovalSeparateFromExternalEffect()
    {
        ToolOutcomeReplyConstraintBuilder.IsMutatingTool(new StubTool("read", isReadOnly: true), "{}")
            .Should().BeFalse();
        ToolOutcomeReplyConstraintBuilder.IsMutatingTool(new StubTool("default-write"), "{}")
            .Should().BeTrue();
        ToolOutcomeReplyConstraintBuilder.IsMutatingTool(new StubTool("destroy", isReadOnly: true, isDestructive: true), "{}")
            .Should().BeTrue();
        ToolOutcomeReplyConstraintBuilder.IsMutatingTool(new StubTool("side-effect", isReadOnly: true, sideEffectKind: "publish"), "{}")
            .Should().BeTrue();
        ToolOutcomeReplyConstraintBuilder.IsMutatingTool(new StubTool("approval", isReadOnly: true, requiresApproval: true), "{}")
            .Should().BeFalse();
        ToolOutcomeReplyConstraintBuilder.IsMutatingTool(
                new StubTool(
                    "dynamic-read",
                    callSafety: new AgentToolCallSafety(false, true, false)),
                "{}")
            .Should().BeFalse();
    }

    private static ToolOutcomeReplyFact Succeeded(IAgentTool tool) =>
        new(
            tool,
            "{}",
            Succeeded: true,
            Receipt(
                AgentToolReceiptStatus.Success,
                effect: ToolOutcomeReplyConstraintBuilder.IsMutatingTool(tool, "{}")
                    ? AgentToolReceiptEffect.Mutating
                    : AgentToolReceiptEffect.ReadOnly));

    private static AgentToolReceipt Receipt(
        AgentToolReceiptStatus status,
        bool isDestructive = false,
        string sideEffectKind = "",
        AgentToolReceiptEffect effect = AgentToolReceiptEffect.Unspecified) =>
        new()
        {
            ToolName = "tool",
            Status = status,
            IsDestructive = isDestructive,
            SideEffectKind = sideEffectKind,
            Effect = effect,
        };

    private sealed class StubTool(
        string name,
        bool isReadOnly = false,
        bool isDestructive = false,
        string sideEffectKind = "",
        bool? requiresApproval = null,
        AgentToolCallSafety? callSafety = null) : IAgentTool
    {
        public string Name => name;
        public string Description => name;
        public string ParametersSchema => "{}";
        public bool IsReadOnly => isReadOnly;
        public bool IsDestructive => isDestructive;
        public string SideEffectKind => sideEffectKind;
        public bool? RequiresApproval(string argumentsJson) => requiresApproval;
        public AgentToolCallSafety GetCallSafety(string argumentsJson) =>
            callSafety ?? new AgentToolCallSafety(requiresApproval, isReadOnly, isDestructive);
        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult("{}");
    }
}
