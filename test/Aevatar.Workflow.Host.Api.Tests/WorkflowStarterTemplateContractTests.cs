using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Validation;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowStarterTemplateContractTests
{
    private static readonly string[] PortableStepTypes =
    [
        "assign",
        "emit",
        "human_approval",
        "llm_call",
        "wait_signal",
    ];

    private static readonly string[] ExpectedTemplateNames =
    [
        "approval_gated_action",
        "enterprise_knowledge_assistant",
        "invoice_review_approval",
        "long_running_task_handoff",
        "meeting_follow_up",
        "research_report",
        "resume_screening_review",
        "scheduled_monitor",
        "security_alert_triage",
        "support_triage",
    ];

    public static TheoryData<string> StarterTemplates()
    {
        var data = new TheoryData<string>();
        foreach (var templateName in ExpectedTemplateNames)
            data.Add(templateName);
        return data;
    }

    [Fact]
    public void StarterTemplateAssets_ShouldContainTheExpectedPortableSet()
    {
        var directory = TemplateDirectory();

        Directory.Exists(directory).Should().BeTrue();
        Directory.EnumerateFiles(directory, "*.yaml", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .Should()
            .Contain(ExpectedTemplateNames);
    }

    [Theory]
    [MemberData(nameof(StarterTemplates))]
    public void StarterTemplate_ShouldSatisfyProductionAuthoringContracts(string templateName)
    {
        var filePath = Path.Combine(TemplateDirectory(), $"{templateName}.yaml");
        var yaml = File.ReadAllText(filePath);

        var definition = new WorkflowParser().Parse(yaml);
        var allSteps = EnumerateSteps(definition.Steps).ToList();
        var knownStepTypes = new WorkflowCoreModulePack().Modules
            .SelectMany(static module => module.Names)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        definition.Name.Should().Be(templateName);
        definition.Description.Should().NotBeNullOrWhiteSpace();
        definition.WhenToUse.Should().BeNull(
            "workflow-activity-vnext accepts only Studio-authorable root fields");
        definition.Steps.Should().NotBeEmpty();
        allSteps.Select(static step => step.Id).Should().OnlyHaveUniqueItems();
        allSteps.Select(static step => step.Type).Should().BeSubsetOf(PortableStepTypes);
        definition.Roles.Should().OnlyContain(static role =>
            role.AgentToolScope != null &&
            role.AgentToolScope.RestrictAllowedToolNames &&
            role.AgentToolScope.AllowedToolNames.Count == 0);

        WorkflowValidator.Validate(
                definition,
                new WorkflowValidator.WorkflowValidationOptions
                {
                    RequireKnownStepTypes = true,
                    KnownStepTypes = knownStepTypes,
                    DisallowDynamicWorkflowStep = true,
                },
                availableWorkflowNames: null)
            .Should()
            .BeEmpty();
        WorkflowAuthorizationDependencyEvaluator.Evaluate(definition).ExternalCapabilities
            .Should()
            .BeEmpty("starter templates must not embed tenant-owned external capabilities");
    }

    [Theory]
    [InlineData("approval_gated_action", "prepare_action_plan")]
    [InlineData("invoice_review_approval", "review_invoice")]
    [InlineData("resume_screening_review", "screen_resume")]
    [InlineData("security_alert_triage", "triage_alert")]
    public void ApprovalStarterTemplate_ShouldExposeExplicitApproveAndRejectBranches(
        string templateName,
        string reviewedStepId)
    {
        var definition = ParseTemplate(templateName);
        var approvalStep = EnumerateSteps(definition.Steps)
            .Should()
            .ContainSingle(static step => step.Type == "human_approval")
            .Which;

        approvalStep.Parameters.Should().Contain("on_reject", "skip");
        approvalStep.Branches.Should().NotBeNull();
        approvalStep.Branches!.Should().ContainKeys("true", "false");
        approvalStep.Branches.Values.Should().OnlyContain(static target => !string.IsNullOrWhiteSpace(target));

        var branchSteps = approvalStep.Branches.Values
            .Select(definition.GetStep)
            .ToList();
        branchSteps.Should().NotContainNulls();
        branchSteps.Should().OnlyContain(static step => !string.IsNullOrWhiteSpace(step!.Next));
        foreach (var branchStep in branchSteps)
        {
            branchStep!.Parameters.Should().ContainKey("value");
            branchStep.Parameters["value"].Should().Contain("${input}",
                "approval results must preserve edited content or rejection feedback");
        }
        var approvedStep = definition.GetStep(approvalStep.Branches["true"]);
        approvedStep!.Parameters["value"].Should().Contain($"${{steps.{reviewedStepId}.output}}",
            "an optional approval note must not replace the reviewed artifact");
        var completionStepId = branchSteps
            .Select(static step => step!.Next)
            .Distinct(StringComparer.Ordinal)
            .Should()
            .ContainSingle()
            .Which;
        var completionStep = definition.GetStep(completionStepId!);
        completionStep.Should().NotBeNull();
        completionStep!.Type.Should().Be("assign");
        completionStep.Parameters.Should().Contain("value", "$input");
        definition.GetNextStep(completionStep.Id).Should().BeNull();
    }

    [Fact]
    public void LongRunningTaskHandoff_ShouldEmitAndWaitForTaskCompletedSignal()
    {
        var definition = ParseTemplate("long_running_task_handoff");
        var allSteps = EnumerateSteps(definition.Steps).ToList();

        definition.Steps[0].Id.Should().Be("capture_handoff_request");
        definition.Steps[0].Type.Should().Be("assign");
        definition.Steps[0].Parameters.Should().Contain("target", "handoff_request");
        definition.Steps[0].Parameters.Should().Contain("value", "$input");
        var emitStep = allSteps.Should()
            .ContainSingle(static step => step.Type == "emit")
            .Which;
        emitStep.Parameters.Should().Contain("payload", "${handoff_request}",
            "emit does not implement AssignModule's $input pass-through convention");
        var waitStep = allSteps.Should()
            .ContainSingle(static step => step.Type == "wait_signal")
            .Which;
        var missingCallbackStep = definition.GetStep(emitStep.Next!);
        missingCallbackStep.Should().NotBeNull();
        missingCallbackStep!.Type.Should().Be("assign");
        missingCallbackStep.Parameters["value"].Should().Contain("NO CALLBACK PAYLOAD RECEIVED");
        missingCallbackStep.Next.Should().Be(waitStep.Id);
        waitStep.Parameters.Should().Contain("signal_name", "task_completed");
        var reviewStep = definition.GetStep("review_callback");
        reviewStep.Should().NotBeNull();
        reviewStep!.Parameters["prompt_prefix"].Should().Contain("${handoff_request}",
            "callback review must compare the result with the original request");
        reviewStep.Parameters["prompt_prefix"].Should().Contain("NO CALLBACK PAYLOAD RECEIVED",
            "an empty callback must not be mistaken for a successful worker result");
    }

    [Fact]
    public void ApprovalGatedAction_ShouldNotDescribeTimeoutAsHumanRejection()
    {
        var definition = ParseTemplate("approval_gated_action");
        var approvalStep = definition.GetStep("approve_action_plan")!;
        var notApprovedStep = definition.GetStep(approvalStep.Branches!["false"])!;

        notApprovedStep.Parameters["value"].Should().Contain("NOT APPROVED");
        notApprovedStep.Parameters["value"].Should().NotContain("REJECTED");
    }

    [Fact]
    public void ScheduledMonitor_ShouldNotSelfReschedule()
    {
        var definition = ParseTemplate("scheduled_monitor");

        EnumerateSteps(definition.Steps)
            .Select(static step => step.Type)
            .Should()
            .NotContain("self_reschedule");
    }

    [Theory]
    [InlineData("meeting_follow_up", "capture_meeting_source", "meeting_source", "audit_follow_up")]
    [InlineData("research_report", "capture_research_source", "research_source", "review_evidence")]
    public void EvidenceReviewStarterTemplate_ShouldPreserveSourceInputForReview(
        string templateName,
        string captureStepId,
        string sourceVariable,
        string reviewStepId)
    {
        var definition = ParseTemplate(templateName);

        definition.Steps[0].Id.Should().Be(captureStepId);
        definition.Steps[0].Type.Should().Be("assign");
        definition.Steps[0].Parameters.Should().Contain("target", sourceVariable);
        definition.Steps[0].Parameters.Should().Contain("value", "$input");

        var reviewStep = definition.GetStep(reviewStepId);
        reviewStep.Should().NotBeNull();
        reviewStep!.Parameters.Should().ContainKey("prompt_prefix");
        reviewStep.Parameters["prompt_prefix"].Should().Contain($"${{{sourceVariable}}}");
    }

    private static WorkflowDefinition ParseTemplate(string templateName)
    {
        var filePath = Path.Combine(TemplateDirectory(), $"{templateName}.yaml");
        return new WorkflowParser().Parse(File.ReadAllText(filePath));
    }

    private static IEnumerable<StepDefinition> EnumerateSteps(IEnumerable<StepDefinition> steps)
    {
        foreach (var step in steps)
        {
            yield return step;

            if (step.Children is null)
                continue;

            foreach (var child in EnumerateSteps(step.Children))
                yield return child;
        }
    }

    private static string TemplateDirectory() =>
        Path.Combine(FindRepositoryRoot(), "workflow-templates");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "aevatar.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
