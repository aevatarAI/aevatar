using Aevatar.Foundation.Abstractions.Interactions;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Validation;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Validation;

public sealed class WorkflowValidatorTests
{
    [Fact]
    public void Validate_WhenLeaseAcquireUsesDefaults_ShouldAccept()
    {
        var errors = WorkflowValidator.Validate(WorkflowWith(Step("acquire", "lease", new()
        {
            ["key"] = "shared",
        })));

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_WhenLeaseRenewOrReleaseMissingCredential_ShouldReject()
    {
        var renewErrors = WorkflowValidator.Validate(WorkflowWith(Step("renew", "lease", new()
        {
            ["action"] = "renew",
            ["key"] = "shared",
            ["holder_token"] = "token",
        })));
        var releaseErrors = WorkflowValidator.Validate(WorkflowWith(Step("release", "lease", new()
        {
            ["action"] = "release",
            ["key"] = "shared",
            ["generation"] = "1",
        })));

        renewErrors.Should().Contain(x => x.Contains("generation", StringComparison.Ordinal));
        releaseErrors.Should().Contain(x => x.Contains("holder_token", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("action", "invalid")]
    [InlineData("ttl_ms", "999")]
    [InlineData("ttl_ms", "3600001")]
    [InlineData("ttl_ms", "not-int")]
    [InlineData("wait_timeout_ms", "999")]
    [InlineData("on_conflict", "block")]
    public void Validate_WhenLeaseParametersInvalid_ShouldReject(string key, string value)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["key"] = "shared",
            [key] = value,
        };

        var errors = WorkflowValidator.Validate(WorkflowWith(Step("lease-1", "lease", parameters)));

        errors.Should().NotBeEmpty();
    }

    [Fact]
    public void Validate_WhenLeaseAcquireDeclaresCredential_ShouldReject()
    {
        var errors = WorkflowValidator.Validate(WorkflowWith(Step("acquire", "lease", new()
        {
            ["key"] = "shared",
            ["holder_token"] = "token",
            ["generation"] = "1",
        })));

        errors.Should().Contain(x => x.Contains("acquire", StringComparison.Ordinal) &&
                                     x.Contains("holder_token", StringComparison.Ordinal));
        errors.Should().Contain(x => x.Contains("acquire", StringComparison.Ordinal) &&
                                     x.Contains("generation", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WhenLeaseKeyMissing_ShouldReject()
    {
        var errors = WorkflowValidator.Validate(WorkflowWith(Step("lease-1", "lease", new())));

        errors.Should().Contain(x => x.Contains("key", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WhenNonNotifyStepCarriesInteractionTemplateSpec_ShouldReject()
    {
        var step = Step("transform-1", "transform", new());
        step = new StepDefinition
        {
            Id = step.Id,
            Type = step.Type,
            Parameters = step.Parameters,
            Presentation = new StepPresentation
            {
                InteractionTemplateSpec = new InteractionTemplateSpec { TemplateId = "tpl-1" },
            },
        };

        var errors = WorkflowValidator.Validate(WorkflowWith(step));

        errors.Should().Contain(x => x.Contains("interaction_template_spec", StringComparison.Ordinal) &&
                                     x.Contains("notify", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WhenCompensationTargetExistsInSameWorkflow_ShouldAccept()
    {
        var errors = WorkflowValidator.Validate(new WorkflowDefinition
        {
            Name = "saga_validation",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "create_order",
                    Type = "tool_call",
                    Compensation = "cancel_order",
                },
                Step("cancel_order", "tool_call", new()),
            ],
        });

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_WhenCompensationTargetIsMissing_ShouldReject()
    {
        var errors = WorkflowValidator.Validate(new WorkflowDefinition
        {
            Name = "saga_validation",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "create_order",
                    Type = "tool_call",
                    Compensation = "cancel_order",
                },
            ],
        });

        errors.Should().ContainSingle(error =>
            error.Contains("create_order", StringComparison.Ordinal) &&
            error.Contains("compensation", StringComparison.Ordinal) &&
            error.Contains("cancel_order", StringComparison.Ordinal));
    }

    private static WorkflowDefinition WorkflowWith(StepDefinition step) =>
        new()
        {
            Name = "lease_validation",
            Roles = [],
            Steps = [step],
        };

    private static StepDefinition Step(
        string id,
        string type,
        Dictionary<string, string> parameters) =>
        new()
        {
            Id = id,
            Type = type,
            Parameters = parameters,
        };
}
