using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Domain.Studio.Models;
using Aevatar.Studio.Domain.Studio.Services;

namespace Aevatar.Studio.Application.Studio.Services;

public sealed class WorkflowEditorService
{
    private readonly IWorkflowYamlDocumentService _yamlDocumentService;
    private readonly WorkflowDocumentNormalizer _normalizer;
    private readonly WorkflowValidator _validator;
    private readonly WorkflowGraphMapper _graphMapper;
    private readonly TextDiffService _textDiffService;

    public WorkflowEditorService(
        IWorkflowYamlDocumentService yamlDocumentService,
        WorkflowDocumentNormalizer normalizer,
        WorkflowValidator validator,
        WorkflowGraphMapper graphMapper,
        TextDiffService textDiffService)
    {
        _yamlDocumentService = yamlDocumentService;
        _normalizer = normalizer;
        _validator = validator;
        _graphMapper = graphMapper;
        _textDiffService = textDiffService;
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
            ValidateDocument(parse.Document, request.AvailableWorkflowNames));

        return new ParseYamlResponse(
            parse.Document,
            _graphMapper.Map(parse.Document),
            findings);
    }

    public SerializeYamlResponse SerializeYaml(SerializeYamlRequest request)
    {
        var normalized = _normalizer.NormalizeForExport(request.Document);
        var findings = ValidateDocument(normalized, request.AvailableWorkflowNames);
        var yaml = _yamlDocumentService.Serialize(normalized);

        return new SerializeYamlResponse(yaml, normalized, findings);
    }

    public ValidateWorkflowResponse Validate(ValidateWorkflowRequest request) =>
        new(ValidateDocument(request.Document, request.AvailableWorkflowNames));

    public NormalizeWorkflowResponse Normalize(NormalizeWorkflowRequest request)
    {
        var normalized = _normalizer.NormalizeForExport(request.Document);
        var findings = ValidateDocument(normalized, request.AvailableWorkflowNames);
        var yaml = _yamlDocumentService.Serialize(normalized);

        return new NormalizeWorkflowResponse(normalized, yaml, findings);
    }

    public DiffWorkflowResponse Diff(DiffWorkflowRequest request) =>
        new(_textDiffService.BuildLineDiff(request.BeforeYaml, request.AfterYaml));

    internal IReadOnlyList<ValidationFinding> ValidateDocument(
        WorkflowDocument document,
        IReadOnlyCollection<string>? availableWorkflowNames)
    {
        var workflowNames = availableWorkflowNames is null
            ? null
            : availableWorkflowNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return _validator.Validate(
            document,
            new WorkflowValidationOptions
            {
                AvailableWorkflowNames = workflowNames,
            });
    }

    private static IReadOnlyList<ValidationFinding> MergeFindings(
        IReadOnlyList<ValidationFinding> left,
        IReadOnlyList<ValidationFinding> right) =>
        left.Concat(right).ToList();
}
