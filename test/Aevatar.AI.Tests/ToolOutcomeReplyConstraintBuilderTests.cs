using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Tools;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class ToolOutcomeReplyConstraintBuilderTests
{
    [Fact]
    public void BuildFinalNoToolsConstraints_WhenOnlyReadOnlyToolSucceeded_ShouldReturnConstraint()
    {
        var constraints = ToolOutcomeReplyConstraintBuilder.BuildFinalNoToolsConstraints(
            [Succeeded(new StubTool("read", isReadOnly: true))],
            toolReceipts: null);

        constraints.Should().ContainSingle();
        constraints[0].Role.Should().Be("system");
        constraints[0].Content.Should().Contain("no successful mutating tool execution");
    }

    [Fact]
    public void BuildFinalNoToolsConstraints_WhenMutatingToolSucceeded_ShouldReturnNone()
    {
        var constraints = ToolOutcomeReplyConstraintBuilder.BuildFinalNoToolsConstraints(
            [Succeeded(new StubTool("write"))],
            toolReceipts: null);

        constraints.Should().BeEmpty();
    }

    [Fact]
    public void BuildFinalNoToolsConstraints_WhenReadOnlyAndMutatingSuccessAreMixed_ShouldReturnNone()
    {
        var constraints = ToolOutcomeReplyConstraintBuilder.BuildFinalNoToolsConstraints(
            [
                Succeeded(new StubTool("read", isReadOnly: true)),
                Succeeded(new StubTool("write")),
            ],
            toolReceipts: null);

        constraints.Should().BeEmpty();
    }

    [Theory]
    [InlineData(AgentToolReceiptStatus.Error)]
    [InlineData(AgentToolReceiptStatus.Denied)]
    [InlineData(AgentToolReceiptStatus.ApprovalRequired)]
    [InlineData(AgentToolReceiptStatus.Unspecified)]
    public void BuildFinalNoToolsConstraints_WhenMutatingToolDidNotSucceed_ShouldReturnConstraint(
        AgentToolReceiptStatus status)
    {
        var constraints = ToolOutcomeReplyConstraintBuilder.BuildFinalNoToolsConstraints(
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
    public void BuildFinalNoToolsConstraints_WhenSuccessfulMutatingReceiptExists_ShouldReturnNone()
    {
        var constraints = ToolOutcomeReplyConstraintBuilder.BuildFinalNoToolsConstraints(
            toolOutcomes: null,
            toolReceipts:
            [
                Receipt(AgentToolReceiptStatus.Success, sideEffectKind: "definition.update"),
            ]);

        constraints.Should().BeEmpty();
    }

    [Fact]
    public void BuildFinalNoToolsConstraints_WhenOnlyFailedMutatingReceiptsExist_ShouldReturnConstraint()
    {
        var constraints = ToolOutcomeReplyConstraintBuilder.BuildFinalNoToolsConstraints(
            toolOutcomes: null,
            toolReceipts:
            [
                Receipt(AgentToolReceiptStatus.Error, sideEffectKind: "definition.update"),
                Receipt(AgentToolReceiptStatus.Denied, isDestructive: true),
            ]);

        constraints.Should().ContainSingle();
    }

    [Fact]
    public void IsMutatingTool_ShouldUseReadOnlyDestructiveSideEffectAndApprovalPredicate()
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
            .Should().BeTrue();
        ToolOutcomeReplyConstraintBuilder.IsMutatingTool(
                new StubTool(
                    "dynamic-read",
                    callSafety: new AgentToolCallSafety(false, true, false)),
                "{}")
            .Should().BeFalse();
    }

    private static ToolOutcomeReplyFact Succeeded(IAgentTool tool) =>
        new(tool, "{}", Succeeded: true, Receipt(AgentToolReceiptStatus.Success));

    private static AgentToolReceipt Receipt(
        AgentToolReceiptStatus status,
        bool isDestructive = false,
        string sideEffectKind = "") =>
        new()
        {
            ToolName = "tool",
            Status = status,
            IsDestructive = isDestructive,
            SideEffectKind = sideEffectKind,
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
