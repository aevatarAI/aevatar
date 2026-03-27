using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Domain.Studio.Models;
using Aevatar.Studio.Domain.Studio.Services;
using RuntimeWorkflowPrimitiveCatalog = Aevatar.Workflow.Core.Primitives.WorkflowPrimitiveCatalog;
using RuntimeWorkflowParser = Aevatar.Workflow.Core.Primitives.WorkflowParser;
using RuntimeWorkflowValidator = Aevatar.Workflow.Core.Validation.WorkflowValidator;

namespace Aevatar.Studio.Application.Studio.Services;

public sealed class WorkflowEditorService
{
    private readonly IWorkflowYamlDocumentService _yamlDocumentService;
    private readonly WorkflowDocumentNormalizer _normalizer;
    private readonly WorkflowValidator _validator;
    private readonly WorkflowGraphMapper _graphMapper;
    private readonly TextDiffService _textDiffService;
    private readonly RuntimeWorkflowParser _runtimeWorkflowParser;

    public WorkflowEditorService(
        IWorkflowYamlDocumentService yamlDocumentService,
        WorkflowDocumentNormalizer normalizer,
        WorkflowValidator validator,
        WorkflowGraphMapper graphMapper,
        TextDiffService textDiffService,
        RuntimeWorkflowParser? runtimeWorkflowParser = null)
    {
        _yamlDocumentService = yamlDocumentService;
        _normalizer = normalizer;
        _validator = validator;
        _graphMapper = graphMapper;
        _textDiffService = textDiffService;
        _runtimeWorkflowParser = runtimeWorkflowParser ?? new RuntimeWorkflowParser();
    }

    public ParseYamlResponse ParseYaml(ParseYamlRequest request)
    {
        var parse = _yamlDocumentService.Parse(request.Yaml);
        if (parse.Document is null)
        {
            return new ParseYamlResponse(null, null, parse.Findings);
        }

        var findings = MergeFindings(
            parse.Findings,
            ValidateDocument(
                parse.Document,
                request.AvailableWorkflowNames,
                request.AvailableStepTypes),
            ValidateRuntimeYaml(
                request.Yaml,
                request.AvailableStepTypes));

        return new ParseYamlResponse(
            parse.Document,
            _graphMapper.Map(parse.Document),
            findings);
    }

    public SerializeYamlResponse SerializeYaml(SerializeYamlRequest request)
    {
        var normalized = _normalizer.NormalizeForExport(request.Document);
        var findings = ValidateDocument(
            normalized,
            request.AvailableWorkflowNames,
            request.AvailableStepTypes);
        var yaml = _yamlDocumentService.Serialize(normalized);
        findings = MergeFindings(
            findings,
            ValidateRuntimeYaml(yaml, request.AvailableStepTypes));

        return new SerializeYamlResponse(yaml, normalized, findings);
    }

    public ValidateWorkflowResponse Validate(ValidateWorkflowRequest request)
    {
        var normalized = _normalizer.NormalizeForExport(request.Document);
        var findings = ValidateDocument(
            normalized,
            request.AvailableWorkflowNames,
            request.AvailableStepTypes);
        var yaml = _yamlDocumentService.Serialize(normalized);
        return new(MergeFindings(
            findings,
            ValidateRuntimeYaml(yaml, request.AvailableStepTypes)));
    }

    public NormalizeWorkflowResponse Normalize(NormalizeWorkflowRequest request)
    {
        var normalized = _normalizer.NormalizeForExport(request.Document);
        var findings = ValidateDocument(
            normalized,
            request.AvailableWorkflowNames,
            request.AvailableStepTypes);
        var yaml = _yamlDocumentService.Serialize(normalized);
        findings = MergeFindings(
            findings,
            ValidateRuntimeYaml(yaml, request.AvailableStepTypes));

        return new NormalizeWorkflowResponse(normalized, yaml, findings);
    }

    public DiffWorkflowResponse Diff(DiffWorkflowRequest request) =>
        new(_textDiffService.BuildLineDiff(request.BeforeYaml, request.AfterYaml));

    internal IReadOnlyList<ValidationFinding> ValidateDocument(
        WorkflowDocument document,
        IReadOnlyCollection<string>? availableWorkflowNames,
        IReadOnlyCollection<string>? availableStepTypes = null)
    {
        var workflowNames = availableWorkflowNames is null
            ? null
            : availableWorkflowNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stepTypes = availableStepTypes is null
            ? null
            : availableStepTypes.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return _validator.Validate(
            document,
            new WorkflowValidationOptions
            {
                AvailableWorkflowNames = workflowNames,
                AvailableStepTypes = stepTypes,
            });
    }

    private static IReadOnlyList<ValidationFinding> MergeFindings(
        IReadOnlyList<ValidationFinding> left,
        IReadOnlyList<ValidationFinding> right,
        IReadOnlyList<ValidationFinding>? third = null)
    {
        var merged = left.Concat(right);
        if (third is { Count: > 0 })
            merged = merged.Concat(third);

        return merged
            .Distinct()
            .ToList();
    }

    private IReadOnlyList<ValidationFinding> ValidateRuntimeYaml(
        string yaml,
        IReadOnlyCollection<string>? availableStepTypes)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            return [];

        try
        {
            var workflow = _runtimeWorkflowParser.Parse(yaml);
            var knownStepTypes = RuntimeWorkflowPrimitiveCatalog.BuildCanonicalStepTypeSet(
                availableStepTypes);
            var errors = RuntimeWorkflowValidator.Validate(
                workflow,
                new RuntimeWorkflowValidator.WorkflowValidationOptions
                {
                    RequireKnownStepTypes = knownStepTypes.Count > 0,
                    KnownStepTypes = knownStepTypes.Count > 0 ? knownStepTypes : null,
                },
                availableWorkflowNames: null);

            return errors
                .Select(static error => ValidationFinding.Error(
                    "/",
                    error,
                    "Runtime validation rejected this YAML.",
                    code: "runtime_validation"))
                .ToList();
        }
        catch (Exception ex)
        {
            return
            [
                ValidationFinding.Error(
                    "/",
                    ex.Message,
                    "Runtime validation rejected this YAML.",
                    code: "runtime_validation"),
            ];
        }
    }
}
