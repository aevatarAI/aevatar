using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.Ornn.Publishing;

public sealed class OrnnSkillPublishValidationPipeline
{
    public const string WorkflowYamlAssetKind = "workflow_yaml";
    public const string ScriptAssetKind = "script";

    private readonly IReadOnlyList<IOrnnSkillPublishAssetValidator> _validators;
    private readonly ILogger _logger;

    public OrnnSkillPublishValidationPipeline(
        IEnumerable<IOrnnSkillPublishAssetValidator>? validators = null,
        ILogger<OrnnSkillPublishValidationPipeline>? logger = null)
    {
        _validators = validators?.ToArray() ?? [];
        _logger = logger ?? NullLogger<OrnnSkillPublishValidationPipeline>.Instance;
    }

    public async Task<OrnnSkillPublishValidationResult> ValidateAsync(
        OrnnSkillPublishRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var diagnostics = new List<OrnnSkillPublishDiagnostic>();
        EnsureValidatorPresent(request.HasWorkflowYamls, WorkflowYamlAssetKind, diagnostics);
        EnsureValidatorPresent(request.HasScripts, ScriptAssetKind, diagnostics);

        foreach (var validator in _validators)
        {
            ct.ThrowIfCancellationRequested();
            var validatorDiagnostics = await validator.ValidateAsync(request, ct);
            diagnostics.AddRange(validatorDiagnostics);
        }

        if (diagnostics.Count > 0)
        {
            _logger.LogInformation(
                "Ornn skill publish validation failed for {SkillName} with {DiagnosticCount} diagnostics",
                request.Name,
                diagnostics.Count);
        }

        return diagnostics.Count == 0
            ? OrnnSkillPublishValidationResult.Success
            : new OrnnSkillPublishValidationResult(diagnostics);
    }

    private void EnsureValidatorPresent(
        bool required,
        string assetKind,
        List<OrnnSkillPublishDiagnostic> diagnostics)
    {
        if (!required)
            return;

        if (_validators.Any(x => string.Equals(x.AssetKind, assetKind, StringComparison.Ordinal)))
            return;

        diagnostics.Add(new OrnnSkillPublishDiagnostic(
            "missing_asset_validator",
            $"No Ornn publish validator is registered for {assetKind} assets."));
    }
}
