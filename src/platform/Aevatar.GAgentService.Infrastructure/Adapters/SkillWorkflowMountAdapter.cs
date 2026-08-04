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

public sealed class SkillWorkflowMountAdapter : ISkillWorkflowMountPort
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
                "Skill workflow mount admission blocked with code {FailureCode}",
                exception.StableCode);
            return new SkillWorkflowMountResult(
                Status: "capability_admission_blocked",
                Mounted: false,
                Workflows: [],
                Message: exception.SafeMessage,
                FailureCode: exception.StableCode);
        }
    }

    private async Task<SkillWorkflowMountResult> MountCoreAsync(
        SkillWorkflowMountRequest request,
        CancellationToken ct)
    {
        var confirmations = request.Confirmations ?? [];
        var confirmationStage = confirmations.Count > 0;
        if (confirmationStage && confirmations.Count != request.Workflows.Count)
            return ConfirmationMismatch("Workflow confirmation count does not match the skill workflow count.");
        if (confirmations.Any(static confirmation => confirmation is null))
            return ConfirmationMismatch("Workflow confirmations cannot contain null values.");

        var confirmationByWorkflowId = new Dictionary<string, SkillWorkflowMountConfirmation>(StringComparer.Ordinal);
        foreach (var confirmation in confirmations)
        {
            if (string.IsNullOrWhiteSpace(confirmation.WorkflowId) ||
                !confirmationByWorkflowId.TryAdd(confirmation.WorkflowId, confirmation))
            {
                return ConfirmationMismatch("Workflow confirmations must contain distinct workflow identities.");
            }
        }

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
            if (confirmationStage &&
                !confirmationByWorkflowId.TryGetValue(workflowId, out suppliedConfirmation))
            {
                return ConfirmationMismatch("A workflow confirmation does not match this skill package.");
            }

            var bundle = await ParseBundleAsync(workflow, ct);
            var bundleDigest = ComputeWorkflowBundleDigest(workflow.WorkflowYamls);
            var revisionId = CreateRevisionId(bundleDigest);
            if (confirmationStage &&
                !string.Equals(suppliedConfirmation!.RevisionId, revisionId, StringComparison.Ordinal))
                return ConfirmationMismatch("Workflow confirmation revision identity is invalid.");

            var preview = await _explicitRequestPreviewService.PreviewAsync(
                new WorkflowExplicitRequestPreviewRequest(
                    access,
                    bundle.EntryWorkflowYaml,
                    bundle.SubWorkflowYamls,
                    ExternalCapabilityExecutionMode.Interactive,
                    workflowId,
                    revisionId,
                    workflow.WorkflowYamls),
                ct);
            var expectedConfirmation = BuildConfirmation(preview, bundleDigest);
            if (confirmationStage && !MatchesConfirmation(suppliedConfirmation!, expectedConfirmation))
                return ConfirmationMismatch("Workflow content or external request confirmation changed after review.");

            prepared.Add(new PreparedWorkflowMount(
                workflowId,
                bundle,
                expectedConfirmation,
                BuildPreview(preview, bundleDigest, expectedConfirmation)));
        }

        if (!confirmationStage)
        {
            return new SkillWorkflowMountResult(
                Status: "confirmation_required",
                Mounted: false,
                Workflows: [],
                Message: "Review every confirmation request, then call use_skill again with the same skill, mount_workflows=true, and workflow_mount_confirmations set to each confirmation_requests[].confirmation. The second call requires durable approval before any workflow is changed.",
                ConfirmationRequests: prepared.Select(static item => item.Preview).ToArray());
        }

        var mounted = new List<MountedSkillWorkflow>(prepared.Count);
        foreach (var item in prepared)
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
                : "No skill workflows were mounted.");
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

    private SkillWorkflowMountResult ConfirmationMismatch(string message)
    {
        _logger.LogWarning("Skill workflow mount confirmation rejected with code {FailureCode}", ConfirmationMismatchCode);
        return new SkillWorkflowMountResult(
            Status: "confirmation_mismatch",
            Mounted: false,
            Workflows: [],
            Message: message,
            FailureCode: ConfirmationMismatchCode);
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

    private sealed record WorkflowYamlBundle(
        string EntryWorkflowName,
        string EntryWorkflowYaml,
        IReadOnlyDictionary<string, string> SubWorkflowYamls);
}
