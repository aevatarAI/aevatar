using Aevatar.AI.ToolProviders.Ornn.Publishing;
using Aevatar.Bootstrap.Extensions.AI.OrnnPublishing;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.TypeSystem;
using FluentAssertions;

namespace Aevatar.Bootstrap.Tests;

public sealed class WorkflowOrnnSkillPublishAssetValidatorTests
{
    [Fact]
    public async Task OrnnPublishValidateAsync_ShouldRejectMalformedYaml()
    {
        var validator = new WorkflowOrnnSkillPublishAssetValidator();

        var diagnostics = await validator.ValidateAsync(RequestWithWorkflow("not: [valid"));

        diagnostics.Should().Contain(x => x.Code == "invalid_workflow_yaml");
    }

    [Fact]
    public async Task OrnnPublishValidateAsync_ShouldRejectUnresolvedAgentKindWhenRegistryIsAvailable()
    {
        var validator = new WorkflowOrnnSkillPublishAssetValidator(new EmptyAgentKindRegistry());

        var diagnostics = await validator.ValidateAsync(RequestWithWorkflow("""
            name: approval-flow
            roles:
              - id: reviewer
                name: Reviewer
                agent_kind: missing-kind
            steps:
              - id: review
                type: llm_call
                target_role: reviewer
            """));

        diagnostics.Should().Contain(x =>
            x.Code == "unresolved_agent_kind" &&
            x.Message.Contains("missing-kind", StringComparison.Ordinal));
    }

    private static OrnnSkillPublishRequest RequestWithWorkflow(string content) => new()
    {
        Name = "workflow-skill",
        Description = "Workflow skill",
        Version = "1.0",
        Category = "plain",
        InstructionsMarkdown = "Run workflow.",
        WorkflowYamls =
        [
            new OrnnSkillPublishWorkflowYaml
            {
                WorkflowId = "approval-flow",
                Content = content,
            }
        ],
    };

    private sealed class EmptyAgentKindRegistry : IAgentKindRegistry
    {
        public AgentImplementation Resolve(string kind) => throw new UnknownAgentKindException(kind);

        public bool TryResolve(string kind, out AgentImplementation implementation)
        {
            implementation = null!;
            return false;
        }

        public bool TryGetKindForAgentType(Type agentType, out string kind)
        {
            kind = string.Empty;
            return false;
        }

        public bool TryGetKind(AgentImplementation implementation, out string kind)
        {
            kind = string.Empty;
            return false;
        }
    }
}
