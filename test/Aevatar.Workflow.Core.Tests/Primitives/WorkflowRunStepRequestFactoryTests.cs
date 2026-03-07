using Aevatar.Workflow.Core.Expressions;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Primitives;

public class WorkflowRunStepRequestFactoryTests
{
    private readonly WorkflowRunStepRequestFactory _factory = new(new WorkflowExpressionEvaluator());

    [Fact]
    public void BuildStepRequest_ShouldResolveExpressions_CanonicalizeStepTypeParameters_AndPropagateAllowedConnectors()
    {
        var state = new WorkflowRunState();
        state.Variables["name"] = "Auric";

        var workflow = new WorkflowDefinition
        {
            Name = "demo",
            Roles =
            [
                new RoleDefinition
                {
                    Id = "writer",
                    Name = "Writer",
                    Connectors = ["search", "crm"],
                },
            ],
            Steps = [],
        };

        var step = new StepDefinition
        {
            Id = "s1",
            Type = "connector_call",
            TargetRole = "writer",
            Parameters = new Dictionary<string, string>
            {
                ["prompt"] = "Hello ${name}",
                ["step"] = "vote_consensus",
            },
            Branches = new Dictionary<string, string>
            {
                ["approved"] = "s2",
            },
        };

        var request = _factory.BuildStepRequest(step, "fallback", "run-1", state, workflow);

        request.StepType.Should().Be("connector_call");
        request.Parameters["prompt"].Should().Be("Hello Auric");
        request.Parameters["step"].Should().Be("vote");
        request.Parameters["branch.approved"].Should().Be("s2");
        request.Parameters["allowed_connectors"].Should().Be("search,crm");
    }

    [Fact]
    public void BuildStepRequest_ShouldPreserveWhileConditionAndSubParametersForDeferredEvaluation()
    {
        var state = new WorkflowRunState();
        state.Variables["limit"] = "3";

        var step = new StepDefinition
        {
            Id = "loop_1",
            Type = "while",
            Parameters = new Dictionary<string, string>
            {
                ["condition"] = "${lt(iteration, limit)}",
                ["sub_param_prompt"] = "Round ${iteration}",
                ["sub_param_role"] = "${input}",
            },
        };

        var request = _factory.BuildStepRequest(step, "ignored", "run-1", state, compiledWorkflow: null);

        request.Parameters["condition"].Should().Be("${lt(iteration, limit)}");
        request.Parameters["sub_param_prompt"].Should().Be("Round ${iteration}");
        request.Parameters["sub_param_role"].Should().Be("${input}");
    }

    [Fact]
    public void EvaluateWhileCondition_ShouldUseIterationVariables()
    {
        var state = new WorkflowWhileState
        {
            StepId = "loop_1",
            ConditionExpression = "${lt(iteration, max_iterations)}",
            MaxIterations = 3,
        };

        _factory.EvaluateWhileCondition(state, "draft", nextIteration: 1).Should().BeTrue();
        _factory.EvaluateWhileCondition(state, "draft", nextIteration: 3).Should().BeFalse();
    }
}
