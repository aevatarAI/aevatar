using Aevatar.Studio.Application;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Studio.Domain.Studio.Compatibility;
using Aevatar.Studio.Domain.Studio.Models;
using Aevatar.Studio.Domain.Studio.Services;
using Aevatar.Studio.Infrastructure.Serialization;
using Aevatar.Studio.Tests.Shared;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;
using RuntimeWorkflowValidator = Aevatar.Workflow.Core.Validation.WorkflowValidator;

namespace Aevatar.Studio.Tests;

public sealed class WorkflowStarterTemplateAuthoringContractTests
{
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

    [Theory]
    [MemberData(nameof(StarterTemplates))]
    public async Task StarterTemplate_ShouldRoundTripThroughEditorAndScopedDraftServices(
        string templateName)
    {
        var filePath = Path.Combine(TemplateDirectory(), $"{templateName}.yaml");
        var yaml = File.ReadAllText(filePath);

        var profile = WorkflowCompatibilityProfile.AevatarV1;
        var yamlService = new YamlWorkflowDocumentService(profile);
        var editor = new WorkflowEditorService(
            yamlService,
            new WorkflowDocumentNormalizer(profile),
            new WorkflowValidator(profile),
            new WorkflowGraphMapper(profile),
            new TextDiffService());

        var parsed = editor.ParseYaml(new ParseYamlRequest(yaml));
        parsed.Document.Should().NotBeNull();
        parsed.Document!.Name.Should().Be(templateName);
        parsed.Graph.Should().NotBeNull();
        parsed.Findings.Should().BeEmpty(
            "starter YAML must satisfy the same Studio and runtime checks as the vNext editor");

        var serialized = editor.SerializeYaml(new SerializeYamlRequest(parsed.Document!));
        serialized.Findings.Should().BeEmpty();
        var roundTrip = editor.ParseYaml(new ParseYamlRequest(serialized.Yaml));
        roundTrip.Document.Should().NotBeNull();
        roundTrip.Graph.Should().NotBeNull();
        roundTrip.Findings.Should().BeEmpty();

        const string scopeId = "scope-template-contract";
        var workflowId = $"wf-{templateName.Replace('_', '-')}";
        var workspacePort = new RecordingStudioWorkspacePorts();
        var draftService = new AppScopedWorkflowService(
            yamlService,
            new StarterWorkflowDefinitionParser(),
            workspacePort,
            workspacePort);

        var accepted = await draftService.SaveDraftAsync(
            scopeId,
            workflowId,
            new SaveWorkflowDraftRequest(
                $"scope:{scopeId}",
                templateName,
                $"{templateName}.yaml",
                serialized.Yaml));
        var reopened = await draftService.GetDraftAsync(scopeId, workflowId);

        accepted.Accepted.Should().BeTrue();
        accepted.WorkflowId.Should().Be(workflowId);
        accepted.AckStage.Should().Be("accepted");
        accepted.Readiness.Readable.Should().BeFalse();
        accepted.Readiness.Stage.Should().Be("projection_pending");
        var saved = workspacePort.SavedDrafts.Should().ContainSingle().Which;
        saved.ScopeId.Should().Be(scopeId);
        saved.WorkflowId.Should().Be(workflowId);
        saved.WorkflowName.Should().Be(templateName);
        reopened.Should().NotBeNull();
        reopened!.WorkflowId.Should().Be(workflowId);
        reopened.Name.Should().Be(templateName);
        reopened.FileName.Should().Be($"{templateName}.yaml");
        reopened.DirectoryId.Should().Be($"scope:{scopeId}");
        reopened.Yaml.Should().Be(serialized.Yaml.Trim());
        var reopenedInEditor = editor.ParseYaml(new ParseYamlRequest(reopened.Yaml));
        reopenedInEditor.Document.Should().NotBeNull();
        reopenedInEditor.Document!.Name.Should().Be(templateName);
        reopenedInEditor.Findings.Should().BeEmpty(
            "the in-memory scoped draft service must reopen the exact saved workflow document");
    }

    private sealed class StarterWorkflowDefinitionParser : IWorkflowDefinitionParser
    {
        private static readonly ISet<string> KnownStepTypes = new WorkflowCoreModulePack().Modules
            .SelectMany(static module => module.Names)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        private readonly WorkflowParser _parser = new();

        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(
            string workflowYaml,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var workflow = _parser.Parse(workflowYaml);
                var errors = RuntimeWorkflowValidator.Validate(
                    workflow,
                    new RuntimeWorkflowValidator.WorkflowValidationOptions
                    {
                        RequireKnownStepTypes = true,
                        KnownStepTypes = KnownStepTypes,
                    },
                    availableWorkflowNames: null);
                if (errors.Count > 0)
                    return Task.FromResult(WorkflowYamlParseResult.Invalid(string.Join("; ", errors)));

                return Task.FromResult(WorkflowYamlParseResult.Success(
                    workflow.Name,
                    WorkflowAuthorizationDependencyEvaluator.Evaluate(workflow)));
            }
            catch (Exception exception)
            {
                return Task.FromResult(WorkflowYamlParseResult.Invalid(exception.Message));
            }
        }

        public Task<WorkflowInlineYamlBundleParseResult> ParseInlineWorkflowBundleAsync(
            IReadOnlyList<WorkflowChatInlineYamlDocument> inlineWorkflowDocuments,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
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
