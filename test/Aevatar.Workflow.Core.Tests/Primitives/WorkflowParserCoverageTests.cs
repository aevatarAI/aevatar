using Aevatar.Workflow.Core.Primitives;
using Aevatar.Foundation.Abstractions.Interactions;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Primitives;

public sealed class WorkflowParserCoverageTests
{
    [Fact]
    public void Parse_WhenYamlIsEmptyOrNameMissing_ShouldThrow()
    {
        var parser = new WorkflowParser();

        Action emptyYaml = () => parser.Parse(string.Empty);
        Action missingName = () => parser.Parse(
            """
            roles: []
            steps: []
            """);

        emptyYaml.Should().Throw<InvalidOperationException>()
            .WithMessage("*YAML 为空*");
        missingName.Should().Throw<InvalidOperationException>()
            .WithMessage("*缺少 name*");
    }

    [Theory]
    [InlineData("wait_signal", true)]
    [InlineData("connector_call", true)]
    [InlineData("secure_connector_call", true)]
    [InlineData("llm_call", true)]
    [InlineData("human_input", true)]
    [InlineData("secure_input", true)]
    [InlineData("human_approval", true)]
    [InlineData("assign", false)]
    public void Parse_WhenRootTimeoutMsIsPresent_ShouldOnlyLiftItForSupportedPrimitiveTypes(string stepType, bool shouldLift)
    {
        var workflow = new WorkflowParser().Parse(
            $$"""
              name: timeout_lift
              roles: []
              steps:
                - id: step_1
                  type: {{stepType}}
                  timeout_ms: 250
              """);

        if (shouldLift)
            workflow.Steps[0].Parameters["timeout_ms"].Should().Be("250");
        else
            workflow.Steps[0].Parameters.Should().NotContainKey("timeout_ms");
    }

    [Fact]
    public void Parse_WhenBranchesUseDictionaryAndListForms_ShouldNormalizeTargets()
    {
        var workflow = new WorkflowParser().Parse(
            """
            name: branches
            roles: []
            steps:
              - id: dict_step
                type: conditional
                branches:
                  true:
                    next: done
                  false:
                    target: fallback
              - id: list_step
                type: switch
                branches:
                  - condition: success
                    next: done
                  - when: retry
                    to: fallback
                  - if: ignored
              - id: fallback
                type: assign
                parameters:
                  target: result
                  value: retry
              - id: done
                type: assign
                parameters:
                  target: result
                  value: ok
            """);

        workflow.Steps[0].Branches.Should().BeEquivalentTo(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["true"] = "done",
            ["false"] = "fallback",
        });
        workflow.Steps[1].Branches.Should().BeEquivalentTo(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["success"] = "done",
            ["retry"] = "fallback",
        });
    }

    [Fact]
    public void Parse_WhenParametersContainScalarsAndCollections_ShouldSerializeInvariantValues()
    {
        var workflow = new WorkflowParser().Parse(
            """
            name: serialization
            roles: []
            steps:
              - id: step_1
                type: transform
                parameters:
                  enabled: true
                  ratio: 1.5
                  tags:
                    - alpha
                    - 2
                  config:
                    enabled: false
                    retries: 3
            """);

        workflow.Steps[0].Parameters["enabled"].Should().Be("true");
        workflow.Steps[0].Parameters["ratio"].Should().Be("1.5");
        workflow.Steps[0].Parameters["tags"].Should().Be("""["alpha","2"]""");
        workflow.Steps[0].Parameters["config"].Should().Be("""{"enabled":"false","retries":"3"}""");
    }

    [Fact]
    public void Parse_WhenInteractionSpecIsPresent_ShouldLiftTypedPresentation()
    {
        var workflow = new WorkflowParser().Parse(
            """
            name: interaction
            roles: []
            steps:
              - id: approve
                type: human_approval
                presentation:
                  interaction_spec:
                    title: "Approve ${input}"
                    body: "Review release"
                    disposition: ephemeral
                    actions:
                      - action_id: approve
                        label: Approve
                        style: primary
                        approval_decision: approve
                      - action_id: reject
                        label: Reject
                        style: danger
                        approval_decision: reject
                    fields:
                      - title: Environment
                        text: prod
                        is_short: true
            """);

        var spec = workflow.Steps[0].Presentation?.InteractionSpec;

        spec.Should().NotBeNull();
        spec!.Title.Should().Be("Approve ${input}");
        spec.Disposition.Should().Be(InteractionDisposition.Ephemeral);
        spec.Actions.Should().HaveCount(2);
        spec.Actions[0].Kind.Should().Be(InteractionActionKind.Button);
        spec.Actions[0].Style.Should().Be(InteractionActionStyle.Primary);
        spec.Actions[0].ApprovalDecision.Should().Be(InteractionApprovalDecision.Approve);
        spec.Actions[1].Style.Should().Be(InteractionActionStyle.Danger);
        spec.Actions[1].ApprovalDecision.Should().Be(InteractionApprovalDecision.Reject);
        spec.Fields.Should().ContainSingle(x => x.Title == "Environment" && x.IsShort);
        workflow.Steps[0].Parameters.Should().NotContainKey("interaction_spec");
    }

    [Fact]
    public void Parse_WhenInteractionSpecIsUnderParameters_ShouldPromoteAndRemoveBagEntry()
    {
        var workflow = new WorkflowParser().Parse(
            """
            name: interaction_parameter
            roles: []
            steps:
              - id: approve
                type: human_approval
                parameters:
                  prompt: "Approve?"
                  interaction_spec:
                    title: "Approval"
                    actions:
                      - action_id: approve
                        label: Approve
            """);

        var step = workflow.Steps[0];

        step.Presentation?.InteractionSpec.Should().NotBeNull();
        step.Presentation!.InteractionSpec!.Title.Should().Be("Approval");
        step.Presentation.InteractionSpec.Actions.Should().ContainSingle(x => x.ActionId == "approve");
        step.Parameters.Should().ContainKey("prompt");
        step.Parameters.Should().NotContainKey("interaction_spec");
    }

    [Fact]
    public void Parse_WhenInteractionSpecIsAtStepRoot_ShouldLiftTypedPresentation()
    {
        var workflow = new WorkflowParser().Parse(
            """
            name: interaction_root
            roles: []
            steps:
              - id: approve
                type: human_approval
                interaction_spec:
                  title: "Root approval"
                  body: "Choose a route"
                  actions:
                    - kind: select
                      action_id: route
                      label: Route
                      options:
                        - label: Canary
                          value: canary
                  cards:
                    - block_id: summary
                      title: Summary
                      fields:
                        - title: Release
                          text: v1
            """);

        var step = workflow.Steps[0];
        var spec = step.Presentation?.InteractionSpec;

        spec.Should().NotBeNull();
        spec!.Title.Should().Be("Root approval");
        spec.Body.Should().Be("Choose a route");
        spec.Actions.Should().ContainSingle();
        spec.Actions[0].Kind.Should().Be(InteractionActionKind.Select);
        spec.Actions[0].Options.Should().ContainSingle(x => x.Label == "Canary" && x.Value == "canary");
        spec.Cards.Should().ContainSingle();
        spec.Cards[0].BlockId.Should().Be("summary");
        spec.Cards[0].Fields.Should().ContainSingle(x => x.Title == "Release" && x.Text == "v1");
        step.Parameters.Should().NotContainKey("interaction_spec");
    }

    [Fact]
    public void Parse_WhenInteractionTemplateSpecIsPresent_ShouldLiftTypedPresentation()
    {
        var workflow = new WorkflowParser().Parse(
            """
            name: notify_template
            roles: []
            steps:
              - id: notify
                type: notify
                parameters:
                  delivery_target_id: agent-1
                  interaction_template_spec:
                    template_id: tpl-${input}
                    template_variable:
                      title: "Deploy"
                      run: run-1
            """);

        var step = workflow.Steps[0];
        var spec = step.Presentation?.InteractionTemplateSpec;

        spec.Should().NotBeNull();
        spec!.TemplateId.Should().Be("tpl-${input}");
        spec.TemplateVariable["title"].Should().Be("Deploy");
        spec.TemplateVariable["run"].Should().Be("run-1");
        step.Presentation!.DeliveryTargetId.Should().Be("agent-1");
        step.Parameters.Should().NotContainKey("delivery_target_id");
        step.Parameters.Should().NotContainKey("interaction_template_spec");
    }

    [Fact]
    public void Parse_WhenNonNotifyStepHasInteractionTemplateSpec_ShouldKeepOrdinaryParameter()
    {
        var workflow = new WorkflowParser().Parse(
            """
            name: transform_template
            roles: []
            steps:
              - id: transform
                type: transform
                parameters:
                  interaction_template_spec:
                    template_id: tpl-1
                    template_variable:
                      title: "Deploy"
            """);

        var step = workflow.Steps[0];

        step.Presentation?.InteractionTemplateSpec.Should().BeNull();
        step.Parameters.Should().ContainKey("interaction_template_spec");
        step.Parameters["interaction_template_spec"].Should().Contain("tpl-1");
    }

    [Fact]
    public void Parse_WhenNotifyUsesCamelCaseDeliveryTargetAlias_ShouldLeaveItAsOrdinaryParameter()
    {
        var workflow = new WorkflowParser().Parse(
            """
            name: notify_template
            roles: []
            steps:
              - id: notify
                type: notify
                parameters:
                  deliveryTargetId: agent-1
                  interaction_template_spec:
                    template_id: tpl-1
            """);

        var step = workflow.Steps[0];

        step.Presentation?.DeliveryTargetId.Should().BeNull();
        step.Parameters.Should().ContainKey("deliveryTargetId");
        step.Parameters.Should().NotContainKey("delivery_target_id");
    }

    [Fact]
    public void Parse_WhenPresentationContainsInlineSpec_ShouldLiftTypedPresentation()
    {
        var workflow = new WorkflowParser().Parse(
            """
            name: interaction_inline_presentation
            roles: []
            steps:
              - id: approve
                type: human_approval
                presentation:
                  title: Inline approval
                  body: Pick an action
                  disposition: pinned
                  actions:
                    - action_id: approve
                      label: Approve
                      style: primary
                  fields:
                    - title: Environment
                      text: prod
                      is_short: true
                  cards:
                    - kind: actions
                      title: Escalation
                      actions:
                        - action_id: escalate
                          label: Escalate
                          style: danger
            """);

        var spec = workflow.Steps[0].Presentation?.InteractionSpec;

        spec.Should().NotBeNull();
        spec!.Title.Should().Be("Inline approval");
        spec.Body.Should().Be("Pick an action");
        spec.Disposition.Should().Be(InteractionDisposition.Pinned);
        spec.Actions.Should().ContainSingle(x => x.ActionId == "approve" && x.Style == InteractionActionStyle.Primary);
        spec.Fields.Should().ContainSingle(x => x.Title == "Environment" && x.IsShort);
        spec.Cards.Should().ContainSingle();
        spec.Cards[0].Kind.Should().Be(InteractionCardKind.Actions);
        spec.Cards[0].Actions.Should().ContainSingle(x => x.ActionId == "escalate" && x.Style == InteractionActionStyle.Danger);
    }

    [Fact]
    public void Parse_WhenRoleAgentKindIsPresent_ShouldMapTrimmedAgentKind()
    {
        var workflow = new WorkflowParser().Parse(
            """
            name: role_agent_kind
            roles:
              - id: assistant
                name: Assistant
                agent_kind: " workflow.role-agent "
            steps:
              - id: step_1
                type: llm_call
                target_role: assistant
            """);

        workflow.Roles.Should().ContainSingle();
        workflow.Roles[0].AgentKind.Should().Be("workflow.role-agent");
    }

    [Theory]
    [InlineData("")]
    [InlineData("agent_kind: \" \"")]
    public void Parse_WhenRoleAgentKindIsMissingOrBlank_ShouldDefaultToPublicRoleAgentKind(string agentKindLine)
    {
        var workflow = new WorkflowParser().Parse(
            $$"""
              name: role_agent_kind_default
              roles:
                - id: assistant
                  name: Assistant
                  {{agentKindLine}}
              steps:
                - id: step_1
                  type: llm_call
                  target_role: assistant
              """);

        workflow.Roles.Should().ContainSingle();
        workflow.Roles[0].AgentKind.Should().Be(WorkflowRoleConventions.DefaultAgentKind);
    }

    [Fact]
    public void Parse_WhenRetryAndOnErrorUseDefaultsAndFallbackAlias_ShouldNormalizePolicies()
    {
        var workflow = new WorkflowParser().Parse(
            """
            name: retry_defaults
            roles: []
            steps:
              - id: step_1
                type: transform
                retry: {}
                on_error:
                  strategy: continue
                  fallback: fallback_step
                  default_output: recovered
              - id: fallback_step
                type: assign
                parameters:
                  target: result
                  value: ok
            """);

        var step = workflow.Steps[0];
        step.Retry.Should().NotBeNull();
        step.Retry!.MaxAttempts.Should().Be(3);
        step.Retry.Backoff.Should().Be("fixed");
        step.Retry.DelayMs.Should().Be(1000);
        step.OnError.Should().NotBeNull();
        step.OnError!.Strategy.Should().Be("continue");
        step.OnError.FallbackStep.Should().Be("fallback_step");
        step.OnError.DefaultOutput.Should().Be("recovered");
    }

    [Fact]
    public void Parse_WhenRunOnFailureIsPresent_ShouldMapRunLevelPolicy()
    {
        var workflow = new WorkflowParser().Parse(
            """
            name: run_on_failure
            on_failure:
              action: fork_from_failed_step
              max_attempts: 3
            roles: []
            steps:
              - id: step_1
                type: transform
            """);

        workflow.OnFailure.Should().NotBeNull();
        workflow.OnFailure!.Action.Should().Be(WorkflowRunFailureActions.ForkFromFailedStep);
        workflow.OnFailure.MaxAttempts.Should().Be(3);
        workflow.Steps[0].Retry.Should().BeNull();
        workflow.Steps[0].OnError.Should().BeNull();
    }

    [Fact]
    public void Parse_WhenRunOnFailureIsAbsent_ShouldLeavePolicyNull()
    {
        var workflow = new WorkflowParser().Parse(
            """
            name: no_run_on_failure
            roles: []
            steps:
              - id: step_1
                type: transform
            """);

        workflow.OnFailure.Should().BeNull();
    }

    [Fact]
    public void Parse_WhenStepIdIsMissing_ShouldThrow()
    {
        Action act = () => new WorkflowParser().Parse(
            """
            name: missing_step_id
            roles: []
            steps:
              - type: transform
            """);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*step 缺 id*");
    }
}
