using Aevatar.ChatRouting.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using ProtoValue = Google.Protobuf.WellKnownTypes.Value;

namespace Aevatar.ChatRouting.Core.Tests;

public sealed class ChatRoutePolicyMigratorTests
{
    [Fact]
    public void MigrateLegacyActions_EmptySnapshot_ReturnsEmptyMigratedSnapshot()
    {
        var snapshot = new ChatRoutePolicySnapshot(NewForwardToModel("default-model"), []);

        var migrated = ChatRoutePolicyMigrator.MigrateLegacyActions(snapshot);

        migrated.Rules.Should().BeEmpty();
        migrated.DefaultTarget.Should().Be(snapshot.DefaultTarget);
    }

    [Fact]
    public void MigrateLegacyActions_NewShapeOnly_ReturnsEquivalentSnapshot()
    {
        var defaultTarget = NewForwardToModel("default-model", includeToolHint: true);
        var newRule = new ChatRouteRule
        {
            RuleId = "new-rule",
            Priority = 10,
            Match = new ChatRouteMatch { Channel = "lark" },
            Action = NewForwardToModel("routed-model", includeToolHint: true),
            Description = "already canonical",
        };
        var snapshot = new ChatRoutePolicySnapshot(defaultTarget, [newRule]);

        var migrated = ChatRoutePolicyMigrator.MigrateLegacyActions(snapshot);

        migrated.DefaultTarget.Should().Be(defaultTarget);
        migrated.Rules.Should().ContainSingle().Which.Should().Be(newRule);
    }

    [Fact]
    public void MigrateLegacyActions_MixedSnapshot_RewritesOnlyWorkflowLegacyAction()
    {
        var defaultTarget = NewForwardToModel("default-model", includeToolHint: true);
        var newRuleAction = NewForwardToModel("new-model", includeToolHint: true);
        var snapshot = new ChatRoutePolicySnapshot(
            defaultTarget,
            [
                new ChatRouteRule
                {
                    RuleId = "new-model",
                    Priority = 10,
                    Match = new ChatRouteMatch { CommandName = "/model" },
                    Action = newRuleAction,
                    Description = "new route",
                },
                new ChatRouteRule
                {
                    RuleId = "legacy-workflow",
                    Priority = 5,
                    Match = new ChatRouteMatch { CommandName = "/workflow" },
                    Action = new ChatRouteAction
                    {
                        ForwardToWorkflow = new ForwardToWorkflow { WorkflowId = "workflow-1" },
                    },
                    Description = "workflow route",
                },
            ]);

        var migrated = ChatRoutePolicyMigrator.MigrateLegacyActions(snapshot);

        migrated.DefaultTarget.Should().Be(defaultTarget);

        migrated.Rules.Select(static rule => rule.RuleId)
            .Should()
            .ContainInOrder("new-model", "legacy-workflow");
        migrated.Rules[0].Action.Should().Be(newRuleAction);
        AssertForwardToModelTool(
            migrated.Rules[1].Action,
            expectedToolName: "aevatar_start_workflow",
            expectedArguments: new Dictionary<string, string>
            {
                ["workflow_id"] = "workflow-1",
            });
    }

    [Fact]
    public void MigrateLegacyActions_RejectRules_PassThroughUnchanged()
    {
        var rejectDefault = new ChatRouteAction { Reject = new Reject { Reason = "default blocked" } };
        var rejectRule = new ChatRouteRule
        {
            RuleId = "reject",
            Priority = 10,
            Match = new ChatRouteMatch { CommandName = "/deny" },
            Action = new ChatRouteAction { Reject = new Reject { Reason = "blocked" } },
        };
        var snapshot = new ChatRoutePolicySnapshot(rejectDefault, [rejectRule]);

        var migrated = ChatRoutePolicyMigrator.MigrateLegacyActions(snapshot);

        migrated.DefaultTarget.Should().Be(rejectDefault);
        migrated.Rules.Should().ContainSingle().Which.Should().Be(rejectRule);
    }

    private static ChatRouteAction NewForwardToModel(string modelName, bool includeToolHint = false)
    {
        var forward = new ForwardToModel { ModelName = modelName };
        if (includeToolHint)
        {
            forward.ToolSetRef = new ChatRouteToolSetRef { Name = "lark.self_notify" };
            forward.ToolChoiceHint = new ChatRouteToolChoiceHint
            {
                ToolName = "notify_self",
                PrefilledArguments = new Struct
                {
                    Fields =
                    {
                        ["recipient"] = ProtoValue.ForString("me"),
                    },
                },
            };
        }

        return new ChatRouteAction { ForwardToModel = forward };
    }

    private static void AssertForwardToModelTool(
        ChatRouteAction action,
        string expectedToolName,
        IReadOnlyDictionary<string, string> expectedArguments)
    {
        action.ActionCase.Should().Be(ChatRouteAction.ActionOneofCase.ForwardToModel);
        action.ForwardToModel.ModelName.Should().BeEmpty();
        action.ForwardToModel.ToolSetRef.Name.Should().Be("workspace.default");
        action.ForwardToModel.ToolChoiceHint.ToolName.Should().Be(expectedToolName);
        action.ForwardToModel.ToolChoiceHint.PrefilledArguments.Fields
            .Should()
            .HaveCount(expectedArguments.Count);

        foreach (var (key, value) in expectedArguments)
        {
            action.ForwardToModel.ToolChoiceHint.PrefilledArguments.Fields[key].StringValue
                .Should()
                .Be(value);
        }
    }
}
