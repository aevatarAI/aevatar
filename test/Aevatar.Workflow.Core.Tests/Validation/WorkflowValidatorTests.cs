using Aevatar.Foundation.Abstractions.Interactions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Validation;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Validation;

public sealed class WorkflowValidatorTests
{
    [Fact]
    public void Validate_WhenLegacyWorkflowLlmScopeIsMissing_ShouldPreserveV0Compatibility()
    {
        var errors = WorkflowValidator.Validate(WorkflowWith(Step("reply", "llm_call", new())));

        errors.Should().NotContain(error => error.Contains("allowed_tools", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WhenCurrentWorkflowLlmScopeIsMissing_ShouldReject()
    {
        var errors = WorkflowValidator.Validate(
            WorkflowWith(Step("reply", "llm_call", new())),
            CurrentToolCatalogValidationOptions(),
            availableWorkflowNames: null);

        errors.Should().ContainSingle(error =>
            error.Contains("must declare an explicit allowed_tools scope", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WhenCurrentWorkflowDeclaresEmptyAllowedTools_ShouldAcceptRestrictedEmptyCatalog()
    {
        var step = Step("reply", "llm_call", new());
        step = new StepDefinition
        {
            Id = step.Id,
            Type = step.Type,
            Parameters = step.Parameters,
            AgentToolScope = new WorkflowAgentToolScopeDefinition
            {
                RestrictAllowedToolNames = true,
                AllowedToolNames = [],
            },
        };

        var errors = WorkflowValidator.Validate(
            WorkflowWith(step),
            CurrentToolCatalogValidationOptions(),
            availableWorkflowNames: null);

        errors.Should().NotContain(error => error.Contains("allowed_tools", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WhenCurrentWorkflowAllowedToolsExceedOptimizationTarget_ShouldAccept()
    {
        var allowedTools = Enumerable.Range(0, WorkflowToolCatalogPolicies.MaximumWorkflowToolCount + 1)
            .Select(index => $"tool_{index}")
            .ToList();
        var step = new StepDefinition
        {
            Id = "reply",
            Type = "llm_call",
            AgentToolScope = new WorkflowAgentToolScopeDefinition
            {
                RestrictAllowedToolNames = true,
                AllowedToolNames = allowedTools,
            },
        };

        var errors = WorkflowValidator.Validate(
            WorkflowWith(step),
            CurrentToolCatalogValidationOptions(),
            availableWorkflowNames: null);

        errors.Should().NotContain(error => error.Contains("allowed_tools", StringComparison.Ordinal));
    }

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

    [Fact]
    public void Validate_WhenCompensationPointsToSameStep_ShouldReject()
    {
        var errors = WorkflowValidator.Validate(WorkflowWith(
            Step("create_order", "tool_call", new(), compensation: "create_order")));

        errors.Should().Contain("步骤 'create_order' 的 compensation 不能指向自身");
    }

    [Fact]
    public void Validate_WhenCompensationChainHasTwoStepCycle_ShouldReject()
    {
        var errors = WorkflowValidator.Validate(WorkflowWith(
            Step("create_order", "tool_call", new(), compensation: "cancel_order"),
            Step("cancel_order", "tool_call", new(), compensation: "create_order")));

        errors.Should().Contain(
            "步骤 'create_order' 的 compensation 链构成环：create_order -> cancel_order -> create_order");
    }

    [Fact]
    public void Validate_WhenCompensationChainHasThreeStepCycle_ShouldReject()
    {
        var errors = WorkflowValidator.Validate(WorkflowWith(
            Step("reserve_inventory", "tool_call", new(), compensation: "release_inventory"),
            Step("release_inventory", "tool_call", new(), compensation: "audit_release"),
            Step("audit_release", "tool_call", new(), compensation: "reserve_inventory")));

        errors.Should().Contain(
            "步骤 'reserve_inventory' 的 compensation 链构成环：reserve_inventory -> release_inventory -> audit_release -> reserve_inventory");
    }

    [Fact]
    public void Validate_WhenCompensationTargetAppearsInNextPath_ShouldReject()
    {
        var errors = WorkflowValidator.Validate(WorkflowWith(
            Step("create_order", "tool_call", new(), next: "cancel_order", compensation: "cancel_order"),
            Step("cancel_order", "tool_call", new())));

        errors.Should().Contain(
            "步骤 'cancel_order' 既是 compensation 目标又出现在正向路径（next/branches），会被双重执行");
    }

    [Fact]
    public void Validate_WhenCompensationTargetAppearsInBranchPath_ShouldReject()
    {
        var errors = WorkflowValidator.Validate(WorkflowWith(
            Step("create_order", "tool_call", new(), compensation: "cancel_order"),
            Step(
                "check_payment",
                "conditional",
                new(),
                branches: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["true"] = "ship_order",
                    ["false"] = "cancel_order",
                }),
            Step("ship_order", "tool_call", new()),
            Step("cancel_order", "tool_call", new())));

        errors.Should().Contain(
            "步骤 'cancel_order' 既是 compensation 目标又出现在正向路径（next/branches），会被双重执行");
    }

    [Fact]
    public void Validate_WhenCompensationTargetDeclaresCompensation_ShouldReject()
    {
        var errors = WorkflowValidator.Validate(WorkflowWith(
            Step("create_order", "tool_call", new(), compensation: "cancel_order"),
            Step("cancel_order", "tool_call", new(), compensation: "audit_cancel"),
            Step("audit_cancel", "tool_call", new())));

        errors.Should().Contain(
            "步骤 'cancel_order' 是 compensation 步骤，不允许再声明 compensation（不会在反向 walk 生效）");
    }

    [Fact]
    public void Validate_WhenSagaCompensationGraphIsValid_ShouldNotReportCompensationGraphErrors()
    {
        var errors = WorkflowValidator.Validate(WorkflowWith(
            Step("create_order", "tool_call", new(), next: "charge_payment", compensation: "cancel_order"),
            Step("charge_payment", "tool_call", new(), next: "ship_order", compensation: "refund_payment"),
            Step("ship_order", "tool_call", new()),
            Step("refund_payment", "tool_call", new()),
            Step("cancel_order", "tool_call", new())));

        errors.Should().NotContain("步骤 'create_order' 的 compensation 不能指向自身");
        errors.Should().NotContain(error => error.Contains("compensation 链构成环", StringComparison.Ordinal));
        errors.Should().NotContain(error => error.Contains("既是 compensation 目标又出现在正向路径", StringComparison.Ordinal));
        errors.Should().NotContain(error => error.Contains("是 compensation 步骤，不允许再声明 compensation", StringComparison.Ordinal));
    }

    private static WorkflowDefinition WorkflowWith(StepDefinition step) =>
        WorkflowWith([step]);

    private static WorkflowValidator.WorkflowValidationOptions CurrentToolCatalogValidationOptions() =>
        new()
        {
            RequireExplicitLlmAgentToolScopes = true,
        };

    private static WorkflowDefinition WorkflowWith(params StepDefinition[] steps) =>
        new()
        {
            Name = "lease_validation",
            Roles = [],
            Steps = [.. steps],
        };

    private static StepDefinition Step(
        string id,
        string type,
        Dictionary<string, string> parameters,
        string? next = null,
        string? compensation = null,
        Dictionary<string, string>? branches = null) =>
        new()
        {
            Id = id,
            Type = type,
            Parameters = parameters,
            Next = next,
            Compensation = compensation,
            Branches = branches,
        };
}
