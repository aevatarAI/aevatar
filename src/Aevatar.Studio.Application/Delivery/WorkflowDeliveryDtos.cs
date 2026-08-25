using System.Text.Json;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Application.Delivery;

public sealed record WorkflowDeliveryPackageRequest(string WorkflowName);

public sealed record WorkflowDeliveryCreateRequest(
    string WorkflowName,
    string PackageVersionId,
    string TargetScopeId,
    string IdempotencyKey,
    DateTimeOffset? ExpiresAt = null,
    string? Note = null);

public sealed record WorkflowDeliveryTriggerRequest(
    string Kind,
    string? Cron = null,
    string? TimeZone = null);

public sealed record WorkflowDeliveryValidateConfigurationRequest(
    string TeamId,
    IReadOnlyDictionary<string, JsonElement>? CustomerConfig = null,
    IReadOnlyDictionary<string, string>? ConnectionReferences = null,
    WorkflowDeliveryTriggerRequest? TriggerIntent = null);

public sealed record WorkflowDeliveryConfirmationInput(
    string CallSiteId,
    string RequestContractDigest,
    string AttestedRisk);

public sealed record WorkflowDeliveryPublishRequest(
    string TeamId,
    string IdempotencyKey,
    IReadOnlyDictionary<string, JsonElement>? CustomerConfig = null,
    IReadOnlyDictionary<string, string>? ConnectionReferences = null,
    WorkflowDeliveryTriggerRequest? TriggerIntent = null,
    IReadOnlyList<WorkflowDeliveryConfirmationInput>? Confirmations = null);

public sealed record WorkflowDeliveryVariableView(
    string Key,
    string Label,
    string Type,
    bool Required,
    string Description,
    object? DefaultValue);

public sealed record WorkflowDeliveryConnectionSlotView(
    string Key,
    string Label,
    string ServiceSlug,
    bool Required);

public sealed record WorkflowDeliveryPackageAcceptancePolicyView(
    bool AutomaticAcceptanceSupported,
    string? Limitation,
    bool InputDeclared);

public sealed record WorkflowDeliveryPackageView(
    string PackageVersionId,
    string PackageId,
    string WorkflowName,
    string Version,
    string DisplayName,
    string Description,
    string SourceHash,
    string PackageHash,
    IReadOnlyList<WorkflowDeliveryVariableView> VariableSchema,
    IReadOnlyList<WorkflowDeliveryConnectionSlotView> ConnectionSlots,
    string RiskSummary,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> ParserDiagnostics,
    WorkflowDeliveryPackageAcceptancePolicyView AcceptancePolicy);

public sealed record WorkflowDeliveryPackageListResponse(
    IReadOnlyList<WorkflowDeliveryPackageView> Items);

public sealed record WorkflowDeliveryConnectionView(
    string SlotKey,
    string ServiceSlug,
    string Status,
    string? UserServiceId,
    DateTimeOffset UpdatedAt);

public sealed record WorkflowDeliveryTriggerOptionView(
    string Kind,
    string Label);

public sealed record WorkflowInstallationEvidenceView(
    string Kind,
    string ArtifactId,
    string VerificationStatus,
    string? VerificationReference,
    string? ContentDigest);

public sealed record WorkflowInstallationView(
    string InstallationId,
    string ScopeId,
    string TeamId,
    string TriggerKind,
    string Stage,
    string Status,
    string? Error,
    string? WorkflowId,
    string? MemberId,
    string? PublishedServiceId,
    string? RevisionId,
    string? ScheduleId,
    string? AcceptanceRunId,
    IReadOnlyList<WorkflowInstallationEvidenceView> Evidence,
    DateTimeOffset UpdatedAt,
    string? ConsoleUrl,
    string? ChannelRunCommand,
    long DeliveryStateVersion);

public sealed record WorkflowDeliveryView(
    string DeliveryId,
    string TargetScopeId,
    DateTimeOffset ExpiresAt,
    string? Note,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? AccessedAt,
    WorkflowDeliveryPackageView Package,
    IReadOnlyList<WorkflowDeliveryConnectionView> Connections,
    IReadOnlyList<WorkflowDeliveryTriggerOptionView> AvailableTriggerIntents,
    WorkflowInstallationView? Installation,
    long StateVersion);

public sealed record WorkflowDeliveryListResponse(
    IReadOnlyList<WorkflowDeliveryView> Items,
    string? NextPageToken = null);

public sealed record WorkflowDeliveryPageRequest(
    int? PageSize = null,
    string? PageToken = null);

public sealed record WorkflowDeliveryAcceptedResponse(
    string DeliveryId,
    string Status,
    string CustomerUrl);

public sealed record WorkflowDeliveryConfirmationChallenge(
    string CallSiteId,
    string RequestContractDigest,
    string AttestedRisk,
    string Method,
    string PathTemplate,
    bool ApprovalRequired,
    string Summary);

public sealed record WorkflowDeliveryConfigurationValidationResponse(
    bool Valid,
    string ResolvedHash,
    IReadOnlyList<string> Findings,
    IReadOnlyList<WorkflowDeliveryConfirmationChallenge> RequiredConfirmations);

public sealed record WorkflowInstallationAcceptedResponse(
    string InstallationId,
    string Status,
    string StatusUrl,
    string? ConsoleUrl);

public sealed record WorkflowDeliveryConnectLinkResponse(
    string SlotKey,
    string Status,
    string ConnectLinkId,
    string ConnectUrl,
    string StatusUrl,
    DateTimeOffset ExpiresAt);

public sealed record WorkflowDeliveryExistingConnectionView(
    string UserServiceId,
    string ServiceSlug,
    string Label);

public sealed record WorkflowDeliveryExistingConnectionListResponse(
    string SlotKey,
    IReadOnlyList<WorkflowDeliveryExistingConnectionView> Items);

public sealed record WorkflowDeliveryAttachConnectionRequest(
    string UserServiceId);

public sealed record WorkflowDeliveryAttachedConnectionResponse(
    string SlotKey,
    string Status,
    string UserServiceId);

public sealed record WorkflowDeliveryConnectStatusResponse(
    string SlotKey,
    string Status,
    string ConnectLinkId,
    string? UserServiceId,
    DateTimeOffset UpdatedAt);

public sealed record WorkflowDeliveryConnectionRefreshAcceptedResponse(
    string SlotKey,
    string Status,
    string StatusUrl);

public sealed record WorkflowDeliveryCallerContext(
    ProvisionWorkflowCallerCredential CallerCredential,
    WorkflowCapabilityAdmissionContext CapabilityAdmission,
    AuthenticatedAuthorizationOwnerContext? AuthenticatedOwner,
    string? ProvisioningBearerToken);

public interface IWorkflowDeliveryService
{
    Task<WorkflowDeliveryPackageListResponse> ListPackagesAsync(string principalId, CancellationToken ct = default);
    Task<WorkflowDeliveryPackageView> GetPackageAsync(string workflowName, string principalId, CancellationToken ct = default);
    Task<WorkflowDeliveryAcceptedResponse> CreateAsync(string principalId, WorkflowDeliveryCreateRequest request, CancellationToken ct = default);
    Task<WorkflowDeliveryListResponse> ListAdminAsync(
        WorkflowDeliveryPageRequest? page = null,
        CancellationToken ct = default);
    Task<WorkflowDeliveryListResponse> ListCustomerAsync(
        string scopeId,
        WorkflowDeliveryPageRequest? page = null,
        CancellationToken ct = default);
    Task<WorkflowDeliveryView?> GetAdminAsync(string deliveryId, CancellationToken ct = default);
    Task<WorkflowDeliveryView?> GetCustomerAsync(string deliveryId, string scopeId, CancellationToken ct = default);
    Task<WorkflowDeliveryView> ValidateAccessAsync(string deliveryId, string scopeId, CancellationToken ct = default);
    Task RevokeAsync(string deliveryId, string principalId, CancellationToken ct = default);
    Task<WorkflowDeliveryConnectLinkResponse> CreateConnectLinkAsync(string deliveryId, string scopeId, string slotKey, string bearerToken, CancellationToken ct = default);
    Task<WorkflowDeliveryExistingConnectionListResponse> ListExistingConnectionsAsync(string deliveryId, string scopeId, string slotKey, string bearerToken, CancellationToken ct = default);
    Task<WorkflowDeliveryAttachedConnectionResponse> AttachExistingConnectionAsync(string deliveryId, string scopeId, string slotKey, WorkflowDeliveryAttachConnectionRequest request, string bearerToken, CancellationToken ct = default);
    Task<WorkflowDeliveryConnectStatusResponse> GetConnectStatusAsync(string deliveryId, string scopeId, string slotKey, CancellationToken ct = default);
    Task<WorkflowDeliveryConnectionRefreshAcceptedResponse> RefreshConnectStatusAsync(string deliveryId, string scopeId, string slotKey, string bearerToken, CancellationToken ct = default);
    Task<WorkflowDeliveryConfigurationValidationResponse> ValidateConfigurationAsync(string deliveryId, string scopeId, WorkflowDeliveryValidateConfigurationRequest request, WorkflowCapabilityAdmissionContext capabilityContext, CancellationToken ct = default);
    Task<WorkflowInstallationAcceptedResponse> PublishAsync(string deliveryId, string scopeId, WorkflowDeliveryPublishRequest request, WorkflowDeliveryCallerContext caller, CancellationToken ct = default);
    Task<WorkflowInstallationView?> GetInstallationAsync(string scopeId, string installationId, CancellationToken ct = default);
    Task<WorkflowInstallationAcceptedResponse> RetryAsync(string scopeId, string installationId, WorkflowDeliveryCallerContext caller, CancellationToken ct = default);
}

public sealed class WorkflowDeliveryException(string code, string safeMessage) : InvalidOperationException(safeMessage)
{
    public string Code { get; } = code;
}
