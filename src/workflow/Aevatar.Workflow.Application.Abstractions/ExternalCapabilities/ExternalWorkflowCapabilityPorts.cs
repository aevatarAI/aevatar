using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;

/// <summary>
/// Per-request authority used for live capability reads. Credentials are transient and are
/// deliberately kept out of Protobuf contracts, persistence, receipts, and generated string output.
/// </summary>
public sealed class ExternalWorkflowCapabilityAccessContext
{
    public ExternalWorkflowCapabilityAccessContext(
        string scopeId,
        string callerId,
        NyxIdCallerCredentialSelection? nyxIdCallerCredential = null,
        string? nyxIdOrganizationBearerToken = null)
    {
        ScopeId = Normalize(scopeId);
        CallerId = Normalize(callerId);
        NyxIdCallerCredential = nyxIdCallerCredential;
        NyxIdOrganizationBearerToken = NormalizeOptional(nyxIdOrganizationBearerToken);
    }

    public string ScopeId { get; }

    public string CallerId { get; }

    public NyxIdCallerCredentialSelection? NyxIdCallerCredential { get; }

    public string? NyxIdOrganizationBearerToken { get; }

    public override string ToString() =>
        $"{nameof(ExternalWorkflowCapabilityAccessContext)} {{ ScopeId = {ScopeId}, CallerId = {CallerId}, Credentials = [REDACTED] }}";

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record ListExternalWorkflowCapabilitiesRequest(
    ExternalWorkflowCapabilityAccessContext Access);

public sealed record InspectExternalWorkflowCapabilityReadinessRequest(
    ExternalWorkflowCapabilityAccessContext Access,
    ExternalWorkflowCapabilitySelector Selector,
    ExternalCapabilityExecutionMode ExecutionMode);

public sealed class WorkflowExternalCapabilityAdmissionRequest
{
    public WorkflowExternalCapabilityAdmissionRequest(
        ExternalWorkflowCapabilityAccessContext access,
        string workflowYaml,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls,
        string sourceKind,
        ExternalCapabilityExecutionMode executionMode,
        IEnumerable<NyxIdExplicitRequestConfirmation>? explicitRequestConfirmations = null,
        string? workflowId = null,
        string? revisionId = null)
    {
        Access = access ?? throw new ArgumentNullException(nameof(access));
        WorkflowYaml = workflowYaml ?? string.Empty;
        InlineWorkflowYamls = inlineWorkflowYamls ?? new Dictionary<string, string>();
        SourceKind = sourceKind?.Trim() ?? string.Empty;
        ExecutionMode = executionMode;
        ExplicitRequestConfirmations = CloneConfirmations(explicitRequestConfirmations);
        WorkflowId = NormalizeOptional(workflowId);
        RevisionId = NormalizeOptional(revisionId);
    }

    public ExternalWorkflowCapabilityAccessContext Access { get; }

    public string WorkflowYaml { get; }

    public IReadOnlyDictionary<string, string> InlineWorkflowYamls { get; }

    public string SourceKind { get; }

    public ExternalCapabilityExecutionMode ExecutionMode { get; }

    public IReadOnlyList<NyxIdExplicitRequestConfirmation> ExplicitRequestConfirmations { get; }

    public string? WorkflowId { get; }

    public string? RevisionId { get; }

    public IReadOnlyList<string>? WorkflowYamls { get; private init; }

    public static WorkflowExternalCapabilityAdmissionRequest FromWorkflowYamls(
        ExternalWorkflowCapabilityAccessContext access,
        IReadOnlyList<string> workflowYamls,
        string sourceKind,
        ExternalCapabilityExecutionMode executionMode,
        IEnumerable<NyxIdExplicitRequestConfirmation>? explicitRequestConfirmations = null,
        string? workflowId = null,
        string? revisionId = null)
    {
        ArgumentNullException.ThrowIfNull(workflowYamls);
        if (workflowYamls.Count == 0)
            throw new InvalidOperationException("At least one workflow YAML is required.");

        return new WorkflowExternalCapabilityAdmissionRequest(
            access,
            string.Empty,
            new Dictionary<string, string>(),
            sourceKind,
            executionMode,
            explicitRequestConfirmations,
            workflowId,
            revisionId)
        {
            WorkflowYamls = workflowYamls.ToArray(),
        };
    }

    public override string ToString() =>
        $"{nameof(WorkflowExternalCapabilityAdmissionRequest)} {{ Access = {Access}, SourceKind = {SourceKind}, ExecutionMode = {ExecutionMode}, Definition = [REDACTED] }}";

    private static IReadOnlyList<NyxIdExplicitRequestConfirmation> CloneConfirmations(
        IEnumerable<NyxIdExplicitRequestConfirmation>? confirmations) =>
        confirmations?.Select(static confirmation =>
                confirmation?.Clone() ?? throw new ArgumentException(
                    "Explicit request confirmations cannot contain null values.",
                    nameof(confirmations)))
            .ToArray() ?? [];

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class PersistedWorkflowCapabilityAdmissionRequest
{
    public PersistedWorkflowCapabilityAdmissionRequest(
        WorkflowCapabilityAdmissionPlan plan,
        string workflowYaml,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls,
        string sourceKind,
        ExternalCapabilityExecutionMode expectedExecutionMode,
        string? workflowId = null,
        string? revisionId = null)
    {
        Plan = plan?.Clone() ?? throw new ArgumentNullException(nameof(plan));
        if (expectedExecutionMode == ExternalCapabilityExecutionMode.Unspecified)
            throw new InvalidOperationException("Expected external capability execution mode is required.");

        WorkflowYaml = workflowYaml ?? string.Empty;
        InlineWorkflowYamls = inlineWorkflowYamls ?? new Dictionary<string, string>();
        SourceKind = sourceKind?.Trim() ?? string.Empty;
        ExpectedExecutionMode = expectedExecutionMode;
        WorkflowId = NormalizeOptional(workflowId);
        RevisionId = NormalizeOptional(revisionId);
    }

    public WorkflowCapabilityAdmissionPlan Plan { get; }

    public string WorkflowYaml { get; }

    public IReadOnlyDictionary<string, string> InlineWorkflowYamls { get; }

    public string SourceKind { get; }

    public ExternalCapabilityExecutionMode ExpectedExecutionMode { get; }

    public string? WorkflowId { get; }

    public string? RevisionId { get; }

    public IReadOnlyList<string>? WorkflowYamls { get; private init; }

    public static PersistedWorkflowCapabilityAdmissionRequest FromWorkflowYamls(
        WorkflowCapabilityAdmissionPlan plan,
        IReadOnlyList<string> workflowYamls,
        string sourceKind,
        ExternalCapabilityExecutionMode expectedExecutionMode,
        string? workflowId = null,
        string? revisionId = null)
    {
        ArgumentNullException.ThrowIfNull(workflowYamls);
        if (workflowYamls.Count == 0)
            throw new InvalidOperationException("At least one workflow YAML is required.");

        return new PersistedWorkflowCapabilityAdmissionRequest(
            plan,
            string.Empty,
            new Dictionary<string, string>(),
            sourceKind,
            expectedExecutionMode,
            workflowId,
            revisionId)
        {
            WorkflowYamls = workflowYamls.ToArray(),
        };
    }

    public override string ToString() =>
        $"{nameof(PersistedWorkflowCapabilityAdmissionRequest)} {{ SourceKind = {SourceKind}, ExpectedExecutionMode = {ExpectedExecutionMode}, Definition = [REDACTED], Plan = [REDACTED] }}";

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public interface IExternalWorkflowCapabilitySource
{
    ExternalWorkflowCapabilitySelector.SelectorOneofCase SelectorKind { get; }

    Task<ExternalWorkflowCapabilityDiscoveryResult> ListAsync(
        ExternalWorkflowCapabilityAccessContext access,
        CancellationToken cancellationToken = default);

    Task<ExternalCapabilityReadiness> InspectAsync(
        ExternalWorkflowCapabilityAccessContext access,
        ExternalWorkflowCapabilitySelector selector,
        ExternalCapabilityExecutionMode executionMode,
        CancellationToken cancellationToken = default);
}

public interface IExternalWorkflowCapabilityListPort
{
    Task<ExternalWorkflowCapabilityDiscoveryResult> ListAsync(
        ListExternalWorkflowCapabilitiesRequest request,
        CancellationToken cancellationToken = default);
}

public interface IExternalWorkflowCapabilityReadinessPort
{
    Task<ExternalCapabilityReadiness> InspectAsync(
        InspectExternalWorkflowCapabilityReadinessRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record WorkflowArtifactCompatibilityRequest(
    string WorkflowYaml,
    IReadOnlyDictionary<string, string> InlineWorkflowYamls,
    WorkflowCapabilityAdmissionPlan? CapabilityAdmissionPlan,
    ExternalCapabilityExecutionMode ExpectedExecutionMode,
    string WorkflowId = "",
    string RevisionId = "");

public interface IWorkflowArtifactCompatibilityPreflight
{
    Task ValidateAsync(
        WorkflowArtifactCompatibilityRequest request,
        CancellationToken ct = default);
}

public interface IWorkflowExternalCapabilityAdmissionService
{
    Task<WorkflowCapabilityAdmissionPlan> AdmitAsync(
        WorkflowExternalCapabilityAdmissionRequest request,
        CancellationToken cancellationToken = default);

    Task<WorkflowCapabilityAdmissionPlan> RevalidatePersistedAsync(
        PersistedWorkflowCapabilityAdmissionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class WorkflowExternalCapabilityAdmissionException : InvalidOperationException
{
    private const string AdmissionRejectedCode = "WORKFLOW_ADMISSION_REJECTED";
    private const string AdmissionRejectedMessage = "Workflow admission was rejected.";

    public WorkflowExternalCapabilityAdmissionException(ExternalCapabilityReadiness readiness)
        : base(BuildSafeMessage(readiness))
    {
        Readiness = readiness?.Clone() ?? throw new ArgumentNullException(nameof(readiness));
        var blocker = Readiness.Blockers.FirstOrDefault(static item => !string.IsNullOrWhiteSpace(item.Code));
        StableCode = NormalizeStableCode(blocker?.Code);
        SafeMessage = string.IsNullOrWhiteSpace(blocker?.SafeMessage)
            ? AdmissionRejectedMessage
            : blocker.SafeMessage.Trim();
    }

    public ExternalCapabilityReadiness Readiness { get; }
    public string StableCode { get; }
    public string SafeMessage { get; }

    private static string NormalizeStableCode(string? code) =>
        code?.Trim() switch
        {
            "WORKFLOW_DEFINITION_INVALID" => "WORKFLOW_DEFINITION_INVALID",
            "NYXID_OPERATION_AUTHORING_MIGRATION_REQUIRED" => "NYXID_OPERATION_AUTHORING_MIGRATION_REQUIRED",
            "CAPABILITY_ADMISSION_REBIND_REQUIRED" => "CAPABILITY_ADMISSION_REBIND_REQUIRED",
            _ => AdmissionRejectedCode,
        };

    private static string BuildSafeMessage(ExternalCapabilityReadiness? readiness)
    {
        if (readiness is null)
            return "External workflow capability admission failed.";

        var blocker = readiness.Blockers.FirstOrDefault();
        return string.IsNullOrWhiteSpace(blocker?.SafeMessage)
            ? $"External workflow capability admission failed: {readiness.Status}."
            : $"External workflow capability admission failed: {blocker.Code}: {blocker.SafeMessage}";
    }
}

/// <summary>Length-prefixed SHA-256 for stable capability and source contract fingerprints.</summary>
public static class ExternalWorkflowCapabilityContractDigest
{
    public static string Compute(params string?[] components) => Compute((IEnumerable<string?>)components);

    public static string Compute(IEnumerable<string?> components)
    {
        ArgumentNullException.ThrowIfNull(components);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> lengthBuffer = stackalloc byte[sizeof(int)];
        foreach (var component in components)
        {
            if (component is null)
            {
                BinaryPrimitives.WriteInt32BigEndian(lengthBuffer, -1);
                hash.AppendData(lengthBuffer);
                continue;
            }

            var bytes = Encoding.UTF8.GetBytes(component);
            BinaryPrimitives.WriteInt32BigEndian(lengthBuffer, bytes.Length);
            hash.AppendData(lengthBuffer);
            hash.AppendData(bytes);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
