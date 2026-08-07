using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgentService.Infrastructure.Adapters;

public sealed class SkillWorkflowMountAdapter : ISkillWorkflowMountPort, ISkillWorkflowConfirmationPort
{
    private const string ConfirmationMismatchCode = "USE_SKILL_MOUNT_CONFIRMATION_MISMATCH";

    private readonly IScopeWorkflowCommandPort _scopeWorkflowCommandPort;
    private readonly IWorkflowDefinitionParser _workflowDefinitionParser;
    private readonly IWorkflowExplicitRequestPreviewService _explicitRequestPreviewService;
    private readonly ILogger<SkillWorkflowMountAdapter> _logger;

    public SkillWorkflowMountAdapter(
        IScopeWorkflowCommandPort scopeWorkflowCommandPort,
        IWorkflowDefinitionParser workflowDefinitionParser,
        IWorkflowExplicitRequestPreviewService explicitRequestPreviewService,
        ILogger<SkillWorkflowMountAdapter>? logger = null)
    {
        _scopeWorkflowCommandPort = scopeWorkflowCommandPort ?? throw new ArgumentNullException(nameof(scopeWorkflowCommandPort));
        _workflowDefinitionParser = workflowDefinitionParser ?? throw new ArgumentNullException(nameof(workflowDefinitionParser));
        _explicitRequestPreviewService = explicitRequestPreviewService ??
                                         throw new ArgumentNullException(nameof(explicitRequestPreviewService));
        _logger = logger ?? NullLogger<SkillWorkflowMountAdapter>.Instance;
    }

    public async Task<SkillWorkflowMountResult> MountAsync(
        SkillWorkflowMountRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Workflows.Count == 0)
        {
            return new SkillWorkflowMountResult(
                Status: "no_workflows",
                Mounted: false,
                Workflows: [],
                Message: "The skill does not expose workflow YAML bundles.");
        }

        if (string.IsNullOrWhiteSpace(request.CallerId))
            throw new InvalidOperationException("Skill workflow mounting requires an authenticated caller identity.");

        try
        {
            return await MountCoreAsync(request, ct);
        }
        catch (WorkflowExternalCapabilityAdmissionException exception)
        {
            _logger.LogWarning(
                "Skill workflow mount admission blocked with code {FailureCode} and schedule-safe code {StableCode}",
                exception.SafeBlockerCode,
                exception.StableCode);
            return new SkillWorkflowMountResult(
                Status: "capability_admission_blocked",
                Mounted: false,
                Workflows: [],
                Message: exception.SafeMessage,
                FailureCode: exception.SafeBlockerCode);
        }
    }

    public async Task<SkillWorkflowConfirmationResult> ConfirmAsync(
        SkillWorkflowConfirmationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Workflows.Count == 0)
        {
            return new SkillWorkflowConfirmationResult(
                Status: "no_workflows",
                Confirmed: false,
                ConfirmationRequests: [],
                Message: "The skill does not expose workflow YAML bundles.");
        }

        if (string.IsNullOrWhiteSpace(request.CallerId))
            throw new InvalidOperationException("Skill workflow confirmation requires an authenticated caller identity.");

        try
        {
            var preparation = await PrepareCoreAsync(request, [], ct);
            return preparation.Result;
        }
        catch (WorkflowExternalCapabilityAdmissionException exception)
        {
            _logger.LogWarning(
                "Skill workflow confirmation admission blocked with code {FailureCode} and schedule-safe code {StableCode}",
                exception.SafeBlockerCode,
                exception.StableCode);
            return new SkillWorkflowConfirmationResult(
                Status: "capability_admission_blocked",
                Confirmed: false,
                ConfirmationRequests: [],
                Message: exception.SafeMessage,
                FailureCode: exception.SafeBlockerCode);
        }
    }

    private async Task<SkillWorkflowMountResult> MountCoreAsync(
        SkillWorkflowMountRequest request,
        CancellationToken ct)
    {
        var preparation = await PrepareCoreAsync(
            new SkillWorkflowConfirmationRequest(
                request.ScopeId,
                request.CallerId,
                request.SourceReadableNyxIdAccessToken,
                request.Workflows,
                ExternalCapabilityExecutionMode.Interactive)
            {
                ConfirmationToken = request.ConfirmationToken,
            },
            request.Confirmations ?? [],
            ct);
        if (!preparation.Result.Confirmed)
        {
            return new SkillWorkflowMountResult(
                preparation.Result.Status,
                Mounted: false,
                Workflows: [],
                preparation.Result.Message,
                preparation.Result.ConfirmationRequests,
                preparation.Result.FailureCode,
                preparation.Result.ConfirmationToken);
        }

        var mounted = new List<MountedSkillWorkflow>(preparation.Prepared.Count);
        foreach (var item in preparation.Prepared)
        {
            var explicitRequestConfirmations = item.Confirmation.ExplicitRequests
                .Select(confirmation => new NyxIdExplicitRequestConfirmation
                {
                    WorkflowId = item.Confirmation.WorkflowId,
                    RevisionId = item.Confirmation.RevisionId,
                    CallSiteId = confirmation.CallSiteId,
                    RequestContractDigest = confirmation.RequestContractDigest,
                    AttestedRisk = confirmation.AttestedRisk,
                })
                .ToArray();
            var upsert = await _scopeWorkflowCommandPort.UpsertAsync(
                new ScopeWorkflowUpsertRequest(
                    request.ScopeId,
                    item.WorkflowId,
                    item.Bundle.EntryWorkflowYaml,
                    WorkflowName: item.Bundle.EntryWorkflowName,
                    DisplayName: item.WorkflowId,
                    InlineWorkflowYamls: item.Bundle.SubWorkflowYamls,
                    RevisionId: item.Confirmation.RevisionId)
                {
                    CapabilityAdmission = new WorkflowCapabilityAdmissionContext(
                        request.CallerId.Trim(),
                        NyxIdCallerCredentialSelection.SourceReadableUserBearerOrNull(
                            request.SourceReadableNyxIdAccessToken),
                        executionMode: ExternalCapabilityExecutionMode.Interactive,
                        explicitRequestConfirmations: explicitRequestConfirmations),
                },
                ct);

            mounted.Add(new MountedSkillWorkflow(
                WorkflowId: item.WorkflowId,
                ServiceId: upsert.WorkflowId,
                EndpointId: "chat",
                RevisionId: upsert.RevisionId));
        }

        return new SkillWorkflowMountResult(
            Status: "mounted",
            Mounted: mounted.Count > 0,
            Workflows: mounted,
            Message: mounted.Count > 0
                ? "Mounted reviewed skill workflows into the current scope."
                : "No skill workflows were mounted.",
            ConfirmationRequests: preparation.Result.ConfirmationRequests,
            ConfirmationToken: preparation.Result.ConfirmationToken);
    }

    private async Task<SkillWorkflowPreparation> PrepareCoreAsync(
        SkillWorkflowConfirmationRequest request,
        IReadOnlyList<SkillWorkflowMountConfirmation> confirmations,
        CancellationToken ct)
    {
        var suppliedToken = request.ConfirmationToken?.Trim() ?? string.Empty;
        var confirmationInput = ValidateConfirmationInput(
            suppliedToken,
            confirmations,
            request.Workflows.Count);
        if (confirmationInput.Error is not null)
            return ConfirmationMismatch(confirmationInput.Error);

        var tokenStage = confirmationInput.TokenStage;
        var legacyConfirmationStage = confirmationInput.LegacyConfirmationStage;
        var confirmationStage = tokenStage || legacyConfirmationStage;
        var confirmationByWorkflowId = confirmationInput.ConfirmationsByWorkflowId;

        var access = new ExternalWorkflowCapabilityAccessContext(
            request.ScopeId,
            request.CallerId.Trim(),
            NyxIdCallerCredentialSelection.SourceReadableUserBearerOrNull(
                request.SourceReadableNyxIdAccessToken));
        var prepared = new List<PreparedWorkflowMount>(request.Workflows.Count);
        var seenWorkflowIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var workflow in request.Workflows)
        {
            var workflowId = workflow.WorkflowId?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(workflowId))
                throw new InvalidOperationException("Skill workflow descriptor must define a workflow ID.");
            if (!seenWorkflowIds.Add(workflowId))
                throw new InvalidOperationException($"Skill workflow '{workflowId}' is duplicated.");

            SkillWorkflowMountConfirmation? suppliedConfirmation = null;
            if (legacyConfirmationStage &&
                !confirmationByWorkflowId.TryGetValue(workflowId, out suppliedConfirmation))
            {
                return ConfirmationMismatch("A workflow confirmation does not match this skill package.");
            }

            var bundle = await ParseBundleAsync(workflow, ct);
            var bundleDigest = ComputeWorkflowBundleDigest(workflow.WorkflowYamls);
            var revisionId = CreateRevisionId(bundleDigest);
            if (legacyConfirmationStage &&
                !string.Equals(suppliedConfirmation!.RevisionId, revisionId, StringComparison.Ordinal))
                return ConfirmationMismatch("Workflow confirmation revision identity is invalid.");

            var preview = await _explicitRequestPreviewService.PreviewAsync(
                new WorkflowExplicitRequestPreviewRequest(
                    access,
                    bundle.EntryWorkflowYaml,
                    bundle.SubWorkflowYamls,
                    request.ExecutionMode,
                    workflowId,
                    revisionId,
                    workflow.WorkflowYamls),
                ct);
            var expectedConfirmation = BuildConfirmation(preview, bundleDigest);
            if (legacyConfirmationStage && !MatchesConfirmation(suppliedConfirmation!, expectedConfirmation))
                return ConfirmationMismatch("Workflow content or external request confirmation changed after review.");

            prepared.Add(new PreparedWorkflowMount(
                workflowId,
                bundle,
                expectedConfirmation,
                BuildPreview(preview, bundleDigest, expectedConfirmation)));
        }

        var expectedToken = ComputeConfirmationToken(
            request.ExecutionMode,
            prepared.Select(static item => item.Confirmation));
        if (tokenStage && !FixedTimeEquals(suppliedToken, expectedToken))
            return ConfirmationMismatch("Workflow content or external request confirmation changed after review.");

        var previews = prepared.Select(static item => item.Preview).ToArray();
        return new SkillWorkflowPreparation(
            new SkillWorkflowConfirmationResult(
                Status: confirmationStage ? "confirmed" : "confirmation_required",
                Confirmed: confirmationStage,
                ConfirmationRequests: previews,
                Message: confirmationStage
                    ? "The reviewed skill workflow confirmation is valid."
                    : "Review every explicit request and submit the exact confirmation token before creating workflow state.",
                ConfirmationToken: expectedToken),
            prepared);
    }

    private static ConfirmationInputValidation ValidateConfirmationInput(
        string suppliedToken,
        IReadOnlyList<SkillWorkflowMountConfirmation> confirmations,
        int workflowCount)
    {
        var tokenStage = !string.IsNullOrWhiteSpace(suppliedToken);
        var legacyConfirmationStage = confirmations.Count > 0;
        if (tokenStage && legacyConfirmationStage)
            return ConfirmationInputValidation.Failed("Use one workflow confirmation contract, not both.");
        if (legacyConfirmationStage && confirmations.Count != workflowCount)
        {
            return ConfirmationInputValidation.Failed(
                "Workflow confirmation count does not match the skill workflow count.");
        }

        var byWorkflowId = new Dictionary<string, SkillWorkflowMountConfirmation>(StringComparer.Ordinal);
        foreach (var confirmation in confirmations)
        {
            if (confirmation is null)
                return ConfirmationInputValidation.Failed("Workflow confirmations cannot contain null values.");
            if (string.IsNullOrWhiteSpace(confirmation.WorkflowId) ||
                !byWorkflowId.TryAdd(confirmation.WorkflowId, confirmation))
            {
                return ConfirmationInputValidation.Failed(
                    "Workflow confirmations must contain distinct workflow identities.");
            }
        }

        return ConfirmationInputValidation.Valid(tokenStage, legacyConfirmationStage, byWorkflowId);
    }

    private async Task<WorkflowYamlBundle> ParseBundleAsync(
        SkillWorkflowDescriptor workflow,
        CancellationToken ct)
    {
        if (workflow.WorkflowYamls.Count == 0)
            throw new InvalidOperationException($"Skill workflow '{workflow.WorkflowId}' does not include any YAML documents.");

        string? entryWorkflowName = null;
        string? entryWorkflowYaml = null;
        var subWorkflowYamls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seenWorkflowNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < workflow.WorkflowYamls.Count; index++)
        {
            var workflowYaml = workflow.WorkflowYamls[index]?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(workflowYaml))
                throw new InvalidOperationException($"Skill workflow '{workflow.WorkflowId}' contains an empty YAML document.");

            var parse = await _workflowDefinitionParser.ParseWorkflowYamlAsync(workflowYaml, ct);
            if (!parse.Succeeded)
                throw new InvalidOperationException(parse.Error);

            var workflowName = parse.WorkflowName?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(workflowName))
                throw new InvalidOperationException($"Skill workflow '{workflow.WorkflowId}' must define a workflow name.");
            if (!seenWorkflowNames.Add(workflowName))
            {
                throw new InvalidOperationException(
                    $"Skill workflow '{workflow.WorkflowId}' contains duplicate workflow name '{workflowName}'.");
            }

            if (index == 0)
            {
                entryWorkflowName = workflowName;
                entryWorkflowYaml = workflowYaml;
            }
            else
            {
                subWorkflowYamls[workflowName] = workflowYaml;
            }
        }

        return new WorkflowYamlBundle(
            entryWorkflowName ?? throw new InvalidOperationException($"Skill workflow '{workflow.WorkflowId}' is missing the root workflow."),
            entryWorkflowYaml ?? throw new InvalidOperationException($"Skill workflow '{workflow.WorkflowId}' is missing the root workflow YAML."),
            subWorkflowYamls);
    }

    private SkillWorkflowPreparation ConfirmationMismatch(string message)
    {
        _logger.LogWarning("Skill workflow mount confirmation rejected with code {FailureCode}", ConfirmationMismatchCode);
        return new SkillWorkflowPreparation(
            new SkillWorkflowConfirmationResult(
                Status: "confirmation_mismatch",
                Confirmed: false,
                ConfirmationRequests: [],
                Message: message,
                FailureCode: ConfirmationMismatchCode),
            []);
    }

    private static SkillWorkflowMountConfirmation BuildConfirmation(
        WorkflowExplicitRequestPreviewResult preview,
        string bundleDigest) =>
        new(
            preview.WorkflowId,
            preview.RevisionId,
            bundleDigest,
            preview.Items.Select(static item => new SkillWorkflowExplicitRequestConfirmation(
                item.CallSiteId,
                item.RequestContractDigest,
                item.EffectiveRisk)).ToArray());

    private static SkillWorkflowMountPreview BuildPreview(
        WorkflowExplicitRequestPreviewResult preview,
        string bundleDigest,
        SkillWorkflowMountConfirmation confirmation) =>
        new(
            preview.WorkflowId,
            preview.RevisionId,
            bundleDigest,
            preview.Items.Select(static item => new SkillWorkflowExplicitRequestPreview(
                item.CallSiteId,
                item.RequestContractDigest,
                item.UserServiceId,
                item.Method,
                item.PathTemplate,
                item.BodyMode,
                item.BodyRequired,
                item.ResponseMode,
                item.EffectiveRisk,
                item.ApprovalRequired,
                item.AllowedExecutionModes)).ToArray(),
            confirmation);

    private static bool MatchesConfirmation(
        SkillWorkflowMountConfirmation supplied,
        SkillWorkflowMountConfirmation expected)
    {
        if (!string.Equals(supplied.WorkflowId, expected.WorkflowId, StringComparison.Ordinal) ||
            !string.Equals(supplied.RevisionId, expected.RevisionId, StringComparison.Ordinal) ||
            !string.Equals(supplied.WorkflowBundleDigest, expected.WorkflowBundleDigest, StringComparison.Ordinal) ||
            supplied.ExplicitRequests is null ||
            supplied.ExplicitRequests.Count != expected.ExplicitRequests.Count ||
            supplied.ExplicitRequests.Any(static item => item is null))
        {
            return false;
        }

        var suppliedByCallSite = new Dictionary<string, SkillWorkflowExplicitRequestConfirmation>(StringComparer.Ordinal);
        foreach (var item in supplied.ExplicitRequests)
        {
            if (string.IsNullOrWhiteSpace(item.CallSiteId) ||
                !suppliedByCallSite.TryAdd(item.CallSiteId, item))
                return false;
        }

        return expected.ExplicitRequests.All(expectedItem =>
            suppliedByCallSite.TryGetValue(expectedItem.CallSiteId, out var suppliedItem) &&
            string.Equals(
                suppliedItem.RequestContractDigest,
                expectedItem.RequestContractDigest,
                StringComparison.Ordinal) &&
            suppliedItem.AttestedRisk == expectedItem.AttestedRisk);
    }

    private static string ComputeWorkflowBundleDigest(IReadOnlyList<string> workflowYamls)
    {
        var canonicalBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(workflowYamls));
        return $"sha256:{Convert.ToHexString(SHA256.HashData(canonicalBytes)).ToLowerInvariant()}";
    }

    private static string ComputeConfirmationToken(
        ExternalCapabilityExecutionMode executionMode,
        IEnumerable<SkillWorkflowMountConfirmation> confirmations)
    {
        var canonical = new
        {
            ExecutionMode = executionMode.ToString(),
            Workflows = confirmations
                .OrderBy(static confirmation => confirmation.WorkflowId, StringComparer.Ordinal)
                .Select(static confirmation => new
                {
                    confirmation.WorkflowId,
                    confirmation.RevisionId,
                    confirmation.WorkflowBundleDigest,
                    ExplicitRequests = confirmation.ExplicitRequests
                        .OrderBy(static item => item.CallSiteId, StringComparer.Ordinal)
                        .Select(static item => new
                        {
                            item.CallSiteId,
                            item.RequestContractDigest,
                            AttestedRisk = item.AttestedRisk.ToString(),
                        })
                        .ToArray(),
                })
                .ToArray(),
        };
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical));
        return $"sha256:{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";
    }

    private static bool FixedTimeEquals(string supplied, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(supplied),
            Encoding.UTF8.GetBytes(expected));

    private static string CreateRevisionId(string bundleDigest)
    {
        var hash = bundleDigest["sha256:".Length..];
        return $"rev-skill-{hash[..32]}";
    }

    private sealed record PreparedWorkflowMount(
        string WorkflowId,
        WorkflowYamlBundle Bundle,
        SkillWorkflowMountConfirmation Confirmation,
        SkillWorkflowMountPreview Preview);

    private sealed record SkillWorkflowPreparation(
        SkillWorkflowConfirmationResult Result,
        IReadOnlyList<PreparedWorkflowMount> Prepared);

    private sealed record ConfirmationInputValidation(
        bool TokenStage,
        bool LegacyConfirmationStage,
        IReadOnlyDictionary<string, SkillWorkflowMountConfirmation> ConfirmationsByWorkflowId,
        string? Error)
    {
        public static ConfirmationInputValidation Valid(
            bool tokenStage,
            bool legacyConfirmationStage,
            IReadOnlyDictionary<string, SkillWorkflowMountConfirmation> confirmationsByWorkflowId) =>
            new(tokenStage, legacyConfirmationStage, confirmationsByWorkflowId, null);

        public static ConfirmationInputValidation Failed(string error) =>
            new(false, false, new Dictionary<string, SkillWorkflowMountConfirmation>(), error);
    }

    private sealed record WorkflowYamlBundle(
        string EntryWorkflowName,
        string EntryWorkflowYaml,
        IReadOnlyDictionary<string, string> SubWorkflowYamls);
}
