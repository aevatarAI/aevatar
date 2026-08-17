using System.Security.Cryptography;
using System.Text;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgents.WorkflowDelivery;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Options;
using DeliveryConnectionStatus = Aevatar.Studio.Application.Studio.Abstractions.WorkflowDeliveryConnectionStatus;
using DeliveryLifecycleStatus = Aevatar.Studio.Application.Studio.Abstractions.WorkflowDeliveryLifecycleStatus;
using DeliveryTriggerIntent = Aevatar.Studio.Application.Studio.Abstractions.WorkflowDeliveryTriggerIntent;
using DeliveryTriggerKind = Aevatar.Studio.Application.Studio.Abstractions.WorkflowDeliveryTriggerKind;
using InstallationStatus = Aevatar.Studio.Application.Studio.Abstractions.WorkflowInstallationStatus;
using InstallationArtifactKind = Aevatar.Studio.Application.Studio.Abstractions.WorkflowInstallationArtifactKind;
using InstallationArtifactVerificationStatus = Aevatar.Studio.Application.Studio.Abstractions.WorkflowInstallationArtifactVerificationStatus;
using PackageSnapshot = Aevatar.Studio.Application.Studio.Abstractions.WorkflowDeliveryPackageSnapshot;
using VariableKind = Aevatar.Studio.Application.Studio.Abstractions.WorkflowDeliveryVariableKind;
using ContractConfirmationReference = Aevatar.Studio.Application.Studio.Abstractions.WorkflowDeliveryConfirmationReference;
using ContractVariableDefinition = Aevatar.Studio.Application.Studio.Abstractions.WorkflowDeliveryVariableDefinition;
using ContractConnectionSlotDefinition = Aevatar.Studio.Application.Studio.Abstractions.WorkflowDeliveryConnectionSlotDefinition;
using ContractAcceptanceMode = Aevatar.Studio.Application.Studio.Abstractions.WorkflowDeliveryAcceptanceMode;
using ContractAcceptancePolicy = Aevatar.Studio.Application.Studio.Abstractions.WorkflowDeliveryAcceptancePolicy;
using ActorAcceptanceMode = Aevatar.GAgents.WorkflowDelivery.WorkflowDeliveryAcceptanceMode;
using ActorAcceptancePolicy = Aevatar.GAgents.WorkflowDelivery.WorkflowDeliveryAcceptancePolicy;

namespace Aevatar.Studio.Application.Delivery;

public sealed class WorkflowDeliveryService : IWorkflowDeliveryService
{
    private static readonly IReadOnlyList<WorkflowDeliveryTriggerOptionView> AutomaticAcceptanceTriggerIntents =
    [
        new("one_shot", "Run once and verify"),
        new("cron", "Run on a recurring cron and verify"),
        new("none", "Publish only (automatic acceptance unavailable)"),
    ];

    private static readonly IReadOnlyList<WorkflowDeliveryTriggerOptionView> ManualAcceptanceTriggerIntents =
    [
        new("none", "Publish only (automatic acceptance unavailable)"),
    ];

    private readonly IWorkflowDeliveryPackageCatalog _packages;
    private readonly IWorkflowDeliveryConfigurationRenderer _renderer;
    private readonly IWorkflowDeliveryCommandPort _commands;
    private readonly IWorkflowDeliveryQueryPort _queries;
    private readonly INyxIdConnectLinkPort _connectLinks;
    private readonly IWorkflowExplicitRequestPreviewService _preview;
    private readonly IStudioWorkflowProvisioningService _provisioning;
    private readonly IOptions<WorkflowDeliveryOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly Uri? _consoleWebBaseUri;

    public WorkflowDeliveryService(
        IWorkflowDeliveryPackageCatalog packages,
        IWorkflowDeliveryConfigurationRenderer renderer,
        IWorkflowDeliveryCommandPort commands,
        IWorkflowDeliveryQueryPort queries,
        INyxIdConnectLinkPort connectLinks,
        IWorkflowExplicitRequestPreviewService preview,
        IStudioWorkflowProvisioningService provisioning,
        IOptions<WorkflowDeliveryOptions> options,
        TimeProvider timeProvider)
    {
        _packages = packages ?? throw new ArgumentNullException(nameof(packages));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _queries = queries ?? throw new ArgumentNullException(nameof(queries));
        _connectLinks = connectLinks ?? throw new ArgumentNullException(nameof(connectLinks));
        _preview = preview ?? throw new ArgumentNullException(nameof(preview));
        _provisioning = provisioning ?? throw new ArgumentNullException(nameof(provisioning));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        var configuredConsoleWebBaseUrl = _options.Value.ConsoleWebBaseUrl;
        if (string.IsNullOrWhiteSpace(configuredConsoleWebBaseUrl))
            _consoleWebBaseUri = null;
        else if (WorkflowDeliveryConsoleLink.TryNormalizeBaseUrl(configuredConsoleWebBaseUrl, out var consoleWebBaseUri))
            _consoleWebBaseUri = consoleWebBaseUri;
        else
            throw new InvalidOperationException(
                $"{WorkflowDeliveryOptions.SectionName}:{nameof(WorkflowDeliveryOptions.ConsoleWebBaseUrl)} " +
                "must be an absolute HTTPS origin (or a loopback HTTP origin) without userinfo, query, or fragment.");
    }

    public async Task<WorkflowDeliveryPackageListResponse> ListPackagesAsync(
        string principalId,
        CancellationToken ct = default) =>
        new((await _packages.ListAsync(principalId, ct)).Select(ToPackageView).ToArray());

    public async Task<WorkflowDeliveryPackageView> GetPackageAsync(
        string workflowName,
        string principalId,
        CancellationToken ct = default) =>
        ToPackageView(await _packages.GetAsync(workflowName, principalId, ct));

    public async Task<WorkflowDeliveryAcceptedResponse> CreateAsync(
        string principalId,
        WorkflowDeliveryCreateRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = _timeProvider.GetUtcNow();
        var package = await _packages.GetAsync(request.WorkflowName, principalId, ct);
        if (!string.Equals(package.PackageVersionId, NormalizeRequired(request.PackageVersionId, "packageVersionId"), StringComparison.Ordinal))
            throw new WorkflowDeliveryException("PACKAGE_VERSION_CHANGED", "The allowlisted workflow package changed. Refresh and review the current version.");
        var scopeId = NormalizeRequired(request.TargetScopeId, "targetScopeId");
        var idempotencyKey = NormalizeIdempotencyKey(request.IdempotencyKey);
        var expiresAtDefaulted = request.ExpiresAt is null;
        var expiresAt = request.ExpiresAt ?? now.AddHours(_options.Value.DefaultExpiryHours);
        if (expiresAt <= now || expiresAt > now.AddHours(_options.Value.MaximumExpiryHours))
            throw new WorkflowDeliveryException("INVALID_DELIVERY_EXPIRY", "Delivery expiry is outside the configured range.");
        var deliveryId = BuildDeliveryId(principalId, idempotencyKey);
        await _commands.CreateAsync(new CreateWorkflowDeliveryMutation(
            deliveryId,
            ToContract(package),
            scopeId,
            expiresAt,
            expiresAtDefaulted,
            NormalizeOptional(request.Note),
            NormalizeRequired(principalId, nameof(principalId)),
            now), ct);
        return new WorkflowDeliveryAcceptedResponse(
            deliveryId,
            "accepted",
            $"/delivery#/customer/{Uri.EscapeDataString(deliveryId)}");
    }

    public async Task<WorkflowDeliveryListResponse> ListAdminAsync(CancellationToken ct = default)
    {
        var result = await _queries.ListAsync(new WorkflowDeliveryListQuery(PageSize: 200), ct);
        return new WorkflowDeliveryListResponse(result.Items.Select(ToDeliveryView).ToArray(), result.NextPageToken);
    }

    public async Task<WorkflowDeliveryListResponse> ListCustomerAsync(string scopeId, CancellationToken ct = default)
    {
        var normalizedScope = NormalizeRequired(scopeId, nameof(scopeId));
        var result = await _queries.ListAsync(new WorkflowDeliveryListQuery(normalizedScope, 200), ct);
        return new WorkflowDeliveryListResponse(result.Items.Select(ToDeliveryView).ToArray(), result.NextPageToken);
    }

    public async Task<WorkflowDeliveryView?> GetAdminAsync(string deliveryId, CancellationToken ct = default)
    {
        var snapshot = await _queries.GetAsync(NormalizeRequired(deliveryId, nameof(deliveryId)), ct);
        return snapshot == null ? null : ToDeliveryView(snapshot);
    }

    public async Task<WorkflowDeliveryView?> GetCustomerAsync(
        string deliveryId,
        string scopeId,
        CancellationToken ct = default)
    {
        var snapshot = await _queries.GetForScopeAsync(
            NormalizeRequired(deliveryId, nameof(deliveryId)),
            NormalizeRequired(scopeId, nameof(scopeId)),
            ct);
        return snapshot == null ? null : ToDeliveryView(snapshot);
    }

    public async Task<WorkflowDeliveryView> ValidateAccessAsync(
        string deliveryId,
        string scopeId,
        CancellationToken ct = default)
    {
        var snapshot = await GetRequiredCustomerSnapshotAsync(deliveryId, scopeId, ct);
        EnsureAvailable(snapshot);
        await _commands.RecordAccessAsync(new RecordWorkflowDeliveryAccessMutation(
            snapshot.DeliveryId,
            snapshot.TargetScopeId,
            _timeProvider.GetUtcNow()), ct);
        return ToDeliveryView(snapshot);
    }

    public async Task RevokeAsync(string deliveryId, string principalId, CancellationToken ct = default)
    {
        var snapshot = await _queries.GetAsync(NormalizeRequired(deliveryId, nameof(deliveryId)), ct)
            ?? throw new WorkflowDeliveryException("DELIVERY_NOT_FOUND", "Workflow delivery was not found.");
        await _commands.RevokeAsync(new RevokeWorkflowDeliveryMutation(
            snapshot.DeliveryId,
            NormalizeRequired(principalId, nameof(principalId)),
            _timeProvider.GetUtcNow()), ct);
    }

    public async Task<WorkflowDeliveryConnectLinkResponse> CreateConnectLinkAsync(
        string deliveryId,
        string scopeId,
        string slotKey,
        string bearerToken,
        Uri callbackUrl,
        CancellationToken ct = default)
    {
        var snapshot = await GetRequiredCustomerSnapshotAsync(deliveryId, scopeId, ct);
        EnsureAvailable(snapshot);
        var slot = snapshot.Package.ConnectionSlots.SingleOrDefault(x =>
            string.Equals(x.Key, slotKey?.Trim(), StringComparison.Ordinal))
            ?? throw new WorkflowDeliveryException("CONNECTION_SLOT_NOT_FOUND", "Workflow delivery connection slot was not found.");
        var created = await _connectLinks.CreateAsync(
            NormalizeRequired(bearerToken, nameof(bearerToken)),
            new NyxIdConnectLinkCreateRequest(
                slot.ServiceSlug,
                $"{snapshot.Package.DisplayName} delivery",
                snapshot.TargetScopeId,
                callbackUrl,
                900),
            ct);
        await _commands.BeginConnectionAsync(new BeginWorkflowDeliveryConnectionMutation(
            snapshot.DeliveryId,
            snapshot.TargetScopeId,
            slot.Key,
            slot.ServiceSlug,
            created.ConnectLinkId,
            _timeProvider.GetUtcNow()), ct);
        return new WorkflowDeliveryConnectLinkResponse(slot.Key, "pending", created.ConnectUrl, created.ExpiresAt);
    }

    public async Task<WorkflowDeliveryConnectStatusResponse> GetConnectStatusAsync(
        string deliveryId,
        string scopeId,
        string slotKey,
        CancellationToken ct = default)
    {
        var delivery = await GetRequiredCustomerSnapshotAsync(deliveryId, scopeId, ct);
        var connection = delivery.Connections.SingleOrDefault(x =>
            string.Equals(x.SlotKey, slotKey?.Trim(), StringComparison.Ordinal))
            ?? throw new WorkflowDeliveryException("CONNECTION_NOT_STARTED", "Connection has not been started for this slot.");
        return new WorkflowDeliveryConnectStatusResponse(
            connection.SlotKey,
            ConnectionStatusName(connection.Status),
            connection.UserServiceId,
            connection.UpdatedAtUtc);
    }

    public async Task<WorkflowDeliveryConnectionRefreshAcceptedResponse> RefreshConnectStatusAsync(
        string deliveryId,
        string scopeId,
        string slotKey,
        string bearerToken,
        CancellationToken ct = default)
    {
        var delivery = await GetRequiredCustomerSnapshotAsync(deliveryId, scopeId, ct);
        EnsureAvailable(delivery);
        var connection = delivery.Connections.SingleOrDefault(x =>
            string.Equals(x.SlotKey, slotKey?.Trim(), StringComparison.Ordinal))
            ?? throw new WorkflowDeliveryException("CONNECTION_NOT_STARTED", "Connection has not been started for this slot.");
        var status = await _connectLinks.GetAsync(
            NormalizeRequired(bearerToken, nameof(bearerToken)),
            connection.LinkId,
            ct);
        if (!string.Equals(status.ConnectLinkId, connection.LinkId, StringComparison.Ordinal) ||
            !string.Equals(status.ServiceSlug, connection.ServiceSlug, StringComparison.Ordinal))
            throw new WorkflowDeliveryException("CONNECTION_CONTRACT_MISMATCH", "NyxID returned a different connection identity.");
        var mapped = status.Status switch
        {
            NyxIdConnectLinkStatus.Pending => DeliveryConnectionStatus.Pending,
            NyxIdConnectLinkStatus.Completed => DeliveryConnectionStatus.Completed,
            NyxIdConnectLinkStatus.Expired => DeliveryConnectionStatus.Expired,
            NyxIdConnectLinkStatus.Cancelled => DeliveryConnectionStatus.Failed,
            _ => DeliveryConnectionStatus.Failed,
        };
        await _commands.UpdateConnectionAsync(new UpdateWorkflowDeliveryConnectionMutation(
            delivery.DeliveryId,
            delivery.TargetScopeId,
            connection.SlotKey,
            connection.LinkId,
            mapped,
            status.UserServiceId,
            _timeProvider.GetUtcNow()), ct);
        return new WorkflowDeliveryConnectionRefreshAcceptedResponse(
            connection.SlotKey,
            "refresh_accepted",
            $"/api/scopes/{Uri.EscapeDataString(delivery.TargetScopeId)}/delivery-requests/{Uri.EscapeDataString(delivery.DeliveryId)}/connections/{Uri.EscapeDataString(connection.SlotKey)}");
    }

    public async Task<WorkflowDeliveryConfigurationValidationResponse> ValidateConfigurationAsync(
        string deliveryId,
        string scopeId,
        WorkflowDeliveryValidateConfigurationRequest request,
        WorkflowCapabilityAdmissionContext capabilityContext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var delivery = await GetRequiredCustomerSnapshotAsync(deliveryId, scopeId, ct);
        EnsureAvailable(delivery);
        EnsureNoClientConnectionReferences(request.ConnectionReferences);
        _ = NormalizeRequired(request.TeamId, "teamId");
        var trigger = ParseTrigger(request.TriggerIntent);
        WorkflowDeliveryAcceptancePolicies.EnsureTriggerSupported(delivery.Package, trigger.Kind);
        var render = _renderer.Render(
            ToActorPackage(delivery.Package),
            request.CustomerConfig,
            ResolveConnectionReferences(delivery));
        var preview = await PreviewAsync(delivery, render, trigger, capabilityContext, ct);
        return new WorkflowDeliveryConfigurationValidationResponse(true, render.ResolvedHash, [], preview);
    }

    public async Task<WorkflowInstallationAcceptedResponse> PublishAsync(
        string deliveryId,
        string scopeId,
        WorkflowDeliveryPublishRequest request,
        WorkflowDeliveryCallerContext caller,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);
        var delivery = await GetRequiredCustomerSnapshotAsync(deliveryId, scopeId, ct);
        EnsureAvailable(delivery);
        EnsureNoClientConnectionReferences(request.ConnectionReferences);
        var teamId = NormalizeRequired(request.TeamId, "teamId");
        var idempotencyKey = NormalizeIdempotencyKey(request.IdempotencyKey);
        var trigger = ParseTrigger(request.TriggerIntent);
        WorkflowDeliveryAcceptancePolicies.EnsureTriggerSupported(delivery.Package, trigger.Kind);
        var render = _renderer.Render(
            ToActorPackage(delivery.Package),
            request.CustomerConfig,
            ResolveConnectionReferences(delivery));
        var preview = await PreviewAsync(delivery, render, trigger, caller.CapabilityAdmission, ct);
        var confirmations = ValidateConfirmations(preview, request.Confirmations);
        var installationId = WorkflowDeliveryConventions.BuildInstallationId(delivery.DeliveryId, delivery.TargetScopeId);
        var attempt = 1;
        var operationId = BuildProvisionOperationId(installationId, attempt);
        var provisionRequest = BuildProvisionWorkflowRequest(
            delivery,
            installationId,
            teamId,
            idempotencyKey,
            render.ResolvedYaml,
            trigger,
            confirmations,
            caller,
            operationId);
        var preparation = await _provisioning.PrepareAsync(
            delivery.TargetScopeId,
            caller.CallerCredential,
            provisionRequest,
            ct);
        await _commands.StartInstallationAsync(new StartWorkflowInstallationMutation(
            delivery.DeliveryId,
            installationId,
            idempotencyKey,
            delivery.TargetScopeId,
            teamId,
            render.ConfigurationValues,
            render.ConnectionReferences,
            trigger,
            render.SourceHash,
            render.ResolvedHash,
            render.ResolvedYaml,
            confirmations.Select(static item => new ContractConfirmationReference(item.CallSiteId, item.RequestContractDigest)).ToArray(),
            preparation.CapabilityAdmissionPlan,
            caller.AuthenticatedOwner,
            operationId,
            _timeProvider.GetUtcNow()), ct);

        return new WorkflowInstallationAcceptedResponse(
            installationId,
            "accepted",
            BuildInstallationStatusUrl(delivery.TargetScopeId, installationId),
            null);
    }

    public async Task<WorkflowInstallationView?> GetInstallationAsync(
        string scopeId,
        string installationId,
        CancellationToken ct = default)
    {
        var delivery = await _queries.FindByInstallationAsync(
            NormalizeRequired(scopeId, nameof(scopeId)),
            NormalizeRequired(installationId, nameof(installationId)),
            ct);
        return delivery?.Installation == null ? null : ToInstallationView(delivery.Installation);
    }

    public async Task<WorkflowInstallationAcceptedResponse> RetryAsync(
        string scopeId,
        string installationId,
        WorkflowDeliveryCallerContext caller,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var delivery = await _queries.FindByInstallationAsync(
            NormalizeRequired(scopeId, nameof(scopeId)),
            NormalizeRequired(installationId, nameof(installationId)),
            ct) ?? throw new WorkflowDeliveryException("INSTALLATION_NOT_FOUND", "Workflow installation was not found.");
        var installation = delivery.Installation
            ?? throw new WorkflowDeliveryException("INSTALLATION_NOT_FOUND", "Workflow installation was not found.");
        if (installation.Status != InstallationStatus.Failed)
            return new WorkflowInstallationAcceptedResponse(
                installation.InstallationId,
                InstallationStatusName(installation.Status),
                BuildInstallationStatusUrl(delivery.TargetScopeId, installation.InstallationId),
                BuildConsoleUrl(installation));

        var attempt = checked(installation.Attempt + 1);
        var operationId = BuildProvisionOperationId(installation.InstallationId, attempt);
        await _commands.RetryInstallationAsync(new RetryWorkflowInstallationMutation(
            delivery.DeliveryId,
            installation.InstallationId,
            installation.IdempotencyKey,
            operationId,
            _timeProvider.GetUtcNow()), ct);
        return new WorkflowInstallationAcceptedResponse(
            installation.InstallationId,
            "accepted",
            BuildInstallationStatusUrl(delivery.TargetScopeId, installation.InstallationId),
            null);
    }

    private async Task<WorkflowDeliverySnapshot> GetRequiredCustomerSnapshotAsync(
        string deliveryId,
        string scopeId,
        CancellationToken ct) =>
        await _queries.GetForScopeAsync(
            NormalizeRequired(deliveryId, nameof(deliveryId)),
            NormalizeRequired(scopeId, nameof(scopeId)),
            ct) ?? throw new WorkflowDeliveryException("DELIVERY_NOT_FOUND", "Workflow delivery was not found for this scope.");

    private void EnsureAvailable(WorkflowDeliverySnapshot delivery)
    {
        if (delivery.LifecycleStatus == DeliveryLifecycleStatus.Revoked)
            throw new WorkflowDeliveryException("DELIVERY_REVOKED", "Workflow delivery has been revoked.");
        if (delivery.ExpiresAtUtc <= _timeProvider.GetUtcNow())
            throw new WorkflowDeliveryException("DELIVERY_EXPIRED", "Workflow delivery has expired.");
    }

    private async Task<IReadOnlyList<WorkflowDeliveryConfirmationChallenge>> PreviewAsync(
        WorkflowDeliverySnapshot delivery,
        WorkflowDeliveryRenderResult render,
        DeliveryTriggerIntent trigger,
        WorkflowCapabilityAdmissionContext capabilityContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(capabilityContext);
        var mode = trigger.Kind == DeliveryTriggerKind.None
            ? ExternalCapabilityExecutionMode.Interactive
            : ExternalCapabilityExecutionMode.Durable;
        var result = await _preview.PreviewAsync(new WorkflowExplicitRequestPreviewRequest(
            new ExternalWorkflowCapabilityAccessContext(
                delivery.TargetScopeId,
                capabilityContext.CallerId,
                capabilityContext.NyxIdCallerCredential,
                capabilityContext.NyxIdOrganizationBearerToken),
            render.ResolvedYaml,
            null,
            mode,
            $"delivery-{delivery.DeliveryId}",
            render.ResolvedHash), ct);
        return result.Items
            .OrderBy(static item => item.CallSiteId, StringComparer.Ordinal)
            .Select(item => new WorkflowDeliveryConfirmationChallenge(
                item.CallSiteId,
                item.RequestContractDigest,
                RiskName(item.EffectiveRisk),
                item.Method.ToString().ToUpperInvariant(),
                item.PathTemplate,
                item.ApprovalRequired,
                $"{item.Method.ToString().ToUpperInvariant()} {item.PathTemplate}"))
            .ToArray();
    }

    internal static ProvisionWorkflowRequest BuildProvisionWorkflowRequest(
        WorkflowDeliverySnapshot delivery,
        string installationId,
        string teamId,
        string idempotencyKey,
        string resolvedYaml,
        DeliveryTriggerIntent trigger,
        IReadOnlyList<WorkflowDeliveryConfirmationInput> confirmations,
        WorkflowDeliveryCallerContext caller,
        string operationId)
    {
        var confirmationInputs = confirmations.Select(static item => new NyxIdExplicitRequestConfirmationInput(
            item.CallSiteId,
            item.RequestContractDigest,
            item.AttestedRisk)).ToArray();
        var executionMode = trigger.Kind == DeliveryTriggerKind.None
            ? ExternalCapabilityExecutionMode.Interactive
            : ExternalCapabilityExecutionMode.Durable;
        var capabilityAdmission = new WorkflowCapabilityAdmissionContext(
            caller.CapabilityAdmission.CallerId,
            caller.CapabilityAdmission.NyxIdCallerCredential,
            caller.CapabilityAdmission.NyxIdOrganizationBearerToken,
            executionMode,
            explicitRequestConfirmations: NyxIdExplicitRequestConfirmationInputs.ToConfirmations(confirmationInputs));
        var request = new ProvisionWorkflowRequest(
            $"{delivery.Package.DisplayName} [{delivery.DeliveryId}]",
            resolvedYaml,
            Prompt: string.Empty,
            RunImmediately: trigger.Kind == DeliveryTriggerKind.OneShot,
            Cron: trigger.Kind == DeliveryTriggerKind.Cron ? trigger.Cron : null,
            Timezone: trigger.TimeZone,
            Caller: caller.CallerCredential)
        {
            TeamId = teamId,
            ExplicitRequestConfirmations = confirmationInputs,
            CapabilityAdmission = capabilityAdmission,
            AuthenticatedOwner = trigger.Kind == DeliveryTriggerKind.None ? null : caller.AuthenticatedOwner,
            ProvisioningBearerToken = trigger.Kind == DeliveryTriggerKind.None ? null : caller.ProvisioningBearerToken,
            ScheduleOperationId = operationId,
            ScheduleIdempotencyKey = idempotencyKey,
        };
        return request;
    }

    private static IReadOnlyList<WorkflowDeliveryConfirmationInput> ValidateConfirmations(
        IReadOnlyList<WorkflowDeliveryConfirmationChallenge> required,
        IReadOnlyList<WorkflowDeliveryConfirmationInput>? supplied)
    {
        var ordered = (supplied ?? [])
            .OrderBy(static item => item.CallSiteId, StringComparer.Ordinal)
            .ToArray();
        if (ordered.Length != required.Count)
            throw ConfirmationMismatch();
        for (var i = 0; i < required.Count; i++)
        {
            if (!string.Equals(ordered[i].CallSiteId, required[i].CallSiteId, StringComparison.Ordinal) ||
                !string.Equals(ordered[i].RequestContractDigest, required[i].RequestContractDigest, StringComparison.Ordinal) ||
                !string.Equals(ordered[i].AttestedRisk, required[i].AttestedRisk, StringComparison.Ordinal))
                throw ConfirmationMismatch();
        }
        return ordered;
    }

    private static IReadOnlyList<WorkflowDeliveryConfirmationInput> ValidateStoredConfirmations(
        IReadOnlyList<WorkflowDeliveryConfirmationChallenge> required,
        IReadOnlyList<ContractConfirmationReference> stored)
    {
        var risks = required.ToDictionary(static item => item.CallSiteId, static item => item.AttestedRisk, StringComparer.Ordinal);
        var supplied = stored.Select(item => new WorkflowDeliveryConfirmationInput(
            item.CallSiteId,
            item.RequestContractDigest,
            risks.GetValueOrDefault(item.CallSiteId) ?? string.Empty)).ToArray();
        return ValidateConfirmations(required, supplied);
    }

    private static WorkflowDeliveryException ConfirmationMismatch() =>
        new("CONFIRMATION_DIGEST_MISMATCH", "Workflow capability confirmations no longer match the server preview.");

    private static IReadOnlyDictionary<string, string> ResolveConnectionReferences(WorkflowDeliverySnapshot delivery)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var slot in delivery.Package.ConnectionSlots)
        {
            var connection = delivery.Connections.SingleOrDefault(x =>
                string.Equals(x.SlotKey, slot.Key, StringComparison.Ordinal) &&
                x.Status == DeliveryConnectionStatus.Completed &&
                !string.IsNullOrWhiteSpace(x.UserServiceId));
            if (connection != null)
                result.Add(slot.Key, connection.UserServiceId!);
        }
        return result;
    }

    private static void EnsureNoClientConnectionReferences(IReadOnlyDictionary<string, string>? values)
    {
        if (values is { Count: > 0 })
        {
            throw new WorkflowDeliveryException(
                "CLIENT_CONNECTION_REFERENCE_FORBIDDEN",
                "Connection references are resolved by the server from completed NyxID connect links.");
        }
    }

    private static DeliveryTriggerIntent ParseTrigger(WorkflowDeliveryTriggerRequest? request)
    {
        var kind = request?.Kind?.Trim().ToLowerInvariant() ?? "none";
        return kind switch
        {
            "none" => new DeliveryTriggerIntent(DeliveryTriggerKind.None, null, null, false),
            "one_shot" => new DeliveryTriggerIntent(DeliveryTriggerKind.OneShot, null, NormalizeOptional(request?.TimeZone), true),
            "cron" => new DeliveryTriggerIntent(
                DeliveryTriggerKind.Cron,
                NormalizeRequired(request?.Cron, "triggerIntent.cron"),
                NormalizeRequired(request?.TimeZone, "triggerIntent.timeZone"),
                false),
            _ => throw new WorkflowDeliveryException("INVALID_TRIGGER_INTENT", "Trigger kind must be none, one_shot, or cron."),
        };
    }

    private static string BuildDeliveryId(string principalId, string idempotencyKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{NormalizeRequired(principalId, nameof(principalId))}\n{idempotencyKey}"));
        return "delivery-" + Convert.ToHexStringLower(bytes)[..24];
    }

    internal static string BuildProvisionOperationId(string installationId, int attempt)
    {
        var normalizedInstallationId = NormalizeRequired(installationId, nameof(installationId));
        if (attempt <= 0)
            throw new ArgumentOutOfRangeException(nameof(attempt));
        return $"{normalizedInstallationId}:provision:a{attempt}";
    }

    private static string BuildInstallationStatusUrl(string scopeId, string installationId) =>
        $"/api/scopes/{Uri.EscapeDataString(scopeId)}/installations/{Uri.EscapeDataString(installationId)}";

    private static string NormalizeIdempotencyKey(string? value)
    {
        var normalized = NormalizeRequired(value, "idempotencyKey");
        if (normalized.Length > 128 || normalized.Any(char.IsControl))
            throw new WorkflowDeliveryException("INVALID_IDEMPOTENCY_KEY", "Idempotency key is invalid or too long.");
        return normalized;
    }

    private static string NormalizeRequired(string? value, string name)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length == 0
            ? throw new WorkflowDeliveryException("INVALID_DELIVERY_REQUEST", $"{name} is required.")
            : normalized;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static PackageSnapshot ToContract(WorkflowPackageVersionSnapshot package) =>
        new(
            package.PackageId,
            package.PackageVersionId,
            package.WorkflowName,
            package.Version,
            package.DisplayName,
            package.Description,
            package.SourceYaml,
            package.SourceHash,
            package.PackageHash,
            package.VariableSchema.Select(static item => new ContractVariableDefinition(
                item.Key,
                item.Label,
                item.Description,
                (VariableKind)(int)item.Kind,
                item.Required,
                item.YamlPointer,
                NormalizeOptional(item.JsonPointer),
                NormalizeOptional(item.DefaultValue))).ToArray(),
            package.ConnectionSlots.Select(static item => new ContractConnectionSlotDefinition(
                item.Key,
                item.Label,
                item.ServiceSlug,
                item.Required)).ToArray(),
            package.Capabilities.ToArray(),
            package.RiskSummary,
            package.ParserDiagnostics.ToArray(),
            ToContractAcceptancePolicy(package.AcceptancePolicy),
            package.CreatedBy,
            package.CreatedAtUtc.ToDateTimeOffset());

    private static WorkflowPackageVersionSnapshot ToActorPackage(PackageSnapshot package)
    {
        var result = new WorkflowPackageVersionSnapshot
        {
            PackageId = package.PackageId,
            PackageVersionId = package.PackageVersionId,
            WorkflowName = package.WorkflowName,
            Version = package.Version,
            DisplayName = package.DisplayName,
            Description = package.Description,
            SourceYaml = package.SourceYaml,
            SourceHash = package.SourceHash,
            PackageHash = package.PackageHash,
            RiskSummary = package.RiskSummary,
            AcceptancePolicy = ToActorAcceptancePolicy(package.AcceptancePolicy),
            CreatedBy = package.CreatedBy,
            CreatedAtUtc = Timestamp.FromDateTimeOffset(package.CreatedAtUtc),
        };
        result.VariableSchema.Add(package.VariableSchema.Select(static item => new Aevatar.GAgents.WorkflowDelivery.WorkflowDeliveryVariableDefinition
        {
            Key = item.Key,
            Label = item.Label,
            Description = item.Description,
            Kind = (Aevatar.GAgents.WorkflowDelivery.WorkflowDeliveryVariableKind)(int)item.Kind,
            Required = item.Required,
            YamlPointer = item.YamlPointer,
            JsonPointer = item.JsonPointer ?? string.Empty,
            DefaultValue = item.DefaultValue ?? string.Empty,
        }));
        result.ConnectionSlots.Add(package.ConnectionSlots.Select(static item => new Aevatar.GAgents.WorkflowDelivery.WorkflowDeliveryConnectionSlotDefinition
        {
            Key = item.Key,
            Label = item.Label,
            ServiceSlug = item.ServiceSlug,
            Required = item.Required,
        }));
        result.Capabilities.Add(package.Capabilities);
        result.ParserDiagnostics.Add(package.ParserDiagnostics);
        return result;
    }

    private static WorkflowDeliveryPackageView ToPackageView(WorkflowPackageVersionSnapshot package) =>
        ToPackageView(ToContract(package));

    private static WorkflowDeliveryPackageView ToPackageView(PackageSnapshot package) =>
        new(
            package.PackageVersionId,
            package.PackageId,
            package.WorkflowName,
            package.Version,
            package.DisplayName,
            package.Description,
            package.SourceHash,
            package.PackageHash,
            package.VariableSchema.Select(static item => new WorkflowDeliveryVariableView(
                item.Key,
                item.Label,
                VariableKindName(item.Kind),
                item.Required,
                item.Description,
                ParseDefaultValue(item))).ToArray(),
            package.ConnectionSlots.Select(static item => new WorkflowDeliveryConnectionSlotView(
                item.Key,
                item.Label,
                item.ServiceSlug,
                item.Required)).ToArray(),
            package.RiskSummary,
            package.Capabilities,
            package.ParserDiagnostics,
            ToAcceptancePolicyView(package.AcceptancePolicy));

    private WorkflowDeliveryView ToDeliveryView(WorkflowDeliverySnapshot delivery) =>
        new(
            delivery.DeliveryId,
            delivery.TargetScopeId,
            delivery.ExpiresAtUtc,
            delivery.Note,
            DeliveryStatusName(delivery),
            delivery.CreatedAtUtc,
            delivery.AccessedAtUtc,
            ToPackageView(delivery.Package),
            delivery.Connections.Select(static item => new WorkflowDeliveryConnectionView(
                item.SlotKey,
                item.ServiceSlug,
                ConnectionStatusName(item.Status),
                item.UserServiceId,
                item.UpdatedAtUtc)).ToArray(),
            AvailableTriggerIntentsFor(delivery.Package.AcceptancePolicy),
            delivery.Installation == null ? null : ToInstallationView(delivery.Installation));

    private static IReadOnlyList<WorkflowDeliveryTriggerOptionView> AvailableTriggerIntentsFor(
        ContractAcceptancePolicy policy) =>
        policy.Mode == ContractAcceptanceMode.AutomaticPreview
            ? AutomaticAcceptanceTriggerIntents
            : ManualAcceptanceTriggerIntents;

    private static ContractAcceptancePolicy ToContractAcceptancePolicy(ActorAcceptancePolicy? policy)
    {
        if (policy == null)
            throw new WorkflowDeliveryException("INVALID_PACKAGE_ACCEPTANCE_POLICY", "Workflow package acceptance policy is missing.");
        return new ContractAcceptancePolicy(
            policy.Mode switch
            {
                ActorAcceptanceMode.AutomaticPreview => ContractAcceptanceMode.AutomaticPreview,
                ActorAcceptanceMode.Manual => ContractAcceptanceMode.Manual,
                _ => ContractAcceptanceMode.Unspecified,
            },
            NormalizeOptional(policy.Limitation));
    }

    private static ActorAcceptancePolicy ToActorAcceptancePolicy(ContractAcceptancePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new ActorAcceptancePolicy
        {
            Mode = policy.Mode switch
            {
                ContractAcceptanceMode.AutomaticPreview => ActorAcceptanceMode.AutomaticPreview,
                ContractAcceptanceMode.Manual => ActorAcceptanceMode.Manual,
                _ => ActorAcceptanceMode.Unspecified,
            },
            Limitation = policy.Limitation ?? string.Empty,
        };
    }

    private static WorkflowDeliveryPackageAcceptancePolicyView ToAcceptancePolicyView(
        ContractAcceptancePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.Mode == ContractAcceptanceMode.Unspecified)
            throw new WorkflowDeliveryException("INVALID_PACKAGE_ACCEPTANCE_POLICY", "Workflow package acceptance policy is unspecified.");
        return new WorkflowDeliveryPackageAcceptancePolicyView(
            policy.Mode == ContractAcceptanceMode.AutomaticPreview,
            policy.Limitation);
    }

    private WorkflowInstallationView ToInstallationView(WorkflowInstallationSnapshot installation) =>
        new(
            installation.InstallationId,
            installation.ScopeId,
            installation.TeamId,
            TriggerKindName(installation.TriggerIntent.Kind),
            installation.Stage,
            InstallationStatusName(installation.Status),
            installation.ErrorMessage,
            installation.WorkflowId,
            installation.MemberId,
            installation.PublishedServiceId,
            installation.RevisionId,
            installation.ScheduleId,
            installation.AcceptanceRunId,
            installation.ReadinessEvidence?.Artifacts.Select(static evidence =>
                new WorkflowInstallationEvidenceView(
                    ArtifactKindName(evidence.Kind),
                    evidence.ArtifactId,
                    ArtifactVerificationStatusName(evidence.VerificationStatus),
                    evidence.VerificationReference,
                    evidence.ContentDigest)).ToArray() ?? [],
            installation.UpdatedAtUtc,
            BuildConsoleUrl(installation),
            BuildChannelRunCommand(installation));

    /// <summary>
    /// Absolute product-console URL, or <see langword="null"/> when the console-web origin
    /// is unconfigured. Never a same-origin path: this API host does not serve the console.
    /// </summary>
    private string? BuildConsoleUrl(WorkflowInstallationSnapshot installation) =>
        WorkflowDeliveryConsoleLink.BuildMemberWorkflowUrl(
            _consoleWebBaseUri,
            installation.ScopeId,
            installation.TeamId,
            installation.MemberId);

    /// <summary>
    /// The exact channel slash command that runs this installed workflow from a bound
    /// messaging channel. It is only meaningful once the published workflow identity
    /// exists, so it stays null until then.
    /// <para>
    /// The command is not universally runnable: the channel admission path resolves the
    /// workflow in the BOT REGISTRATION's scope, so it only reaches this installation when
    /// the bot is registered in this installation's own scope. Delivery does not create that
    /// registration and cannot assert it, so any surface rendering this command must state
    /// the precondition rather than present it as a ready-to-use entry point.
    /// </para>
    /// </summary>
    private static string? BuildChannelRunCommand(WorkflowInstallationSnapshot installation) =>
        string.IsNullOrWhiteSpace(installation.WorkflowId)
            ? null
            : $"/workflow run {installation.WorkflowId}";

    private string DeliveryStatusName(WorkflowDeliverySnapshot delivery) =>
        delivery.LifecycleStatus == DeliveryLifecycleStatus.Revoked
            ? "revoked"
            : delivery.ExpiresAtUtc <= _timeProvider.GetUtcNow()
                ? "expired"
                : "active";

    private static string InstallationStatusName(InstallationStatus status) => status switch
    {
        InstallationStatus.Accepted => "accepted",
        InstallationStatus.ProvisioningAccepted => "provisioning_accepted",
        InstallationStatus.Failed => "failed",
        InstallationStatus.Ready => "ready",
        _ => "unknown",
    };

    private static string ArtifactKindName(InstallationArtifactKind kind) => kind switch
    {
        InstallationArtifactKind.RunOutput => "run_output",
        InstallationArtifactKind.SideEffectReceipt => "side_effect_receipt",
        InstallationArtifactKind.ApprovalResult => "approval_result",
        InstallationArtifactKind.ExternalResource => "external_resource",
        _ => "unknown",
    };

    private static string ArtifactVerificationStatusName(
        InstallationArtifactVerificationStatus status) => status switch
        {
            InstallationArtifactVerificationStatus.Verified => "verified",
            InstallationArtifactVerificationStatus.Rejected => "rejected",
            _ => "unknown",
        };

    private static string TriggerKindName(DeliveryTriggerKind kind) => kind switch
    {
        DeliveryTriggerKind.None => "none",
        DeliveryTriggerKind.OneShot => "one_shot",
        DeliveryTriggerKind.Cron => "cron",
        _ => "unknown",
    };

    private static string ConnectionStatusName(DeliveryConnectionStatus status) => status switch
    {
        DeliveryConnectionStatus.Pending => "pending",
        DeliveryConnectionStatus.Completed => "completed",
        DeliveryConnectionStatus.Failed => "failed",
        DeliveryConnectionStatus.Expired => "expired",
        _ => "unknown",
    };

    private static string VariableKindName(VariableKind kind) => kind switch
    {
        VariableKind.String => "string",
        VariableKind.Integer => "integer",
        VariableKind.Number => "number",
        VariableKind.Boolean => "boolean",
        _ => "string",
    };

    private static object? ParseDefaultValue(ContractVariableDefinition definition) =>
        definition.Kind switch
        {
            VariableKind.Integer when long.TryParse(definition.DefaultValue, out var integer) => integer,
            VariableKind.Number when double.TryParse(definition.DefaultValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) => number,
            VariableKind.Boolean when bool.TryParse(definition.DefaultValue, out var boolean) => boolean,
            _ => definition.DefaultValue,
        };

    private static string RiskName(NyxIdOperationRisk risk) => risk switch
    {
        NyxIdOperationRisk.ReadOnly => "read_only",
        NyxIdOperationRisk.Write => "write",
        NyxIdOperationRisk.Destructive => "destructive",
        _ => throw new WorkflowDeliveryException("INVALID_CAPABILITY_PREVIEW", "Capability preview returned an unspecified risk."),
    };
}

internal static class WorkflowDeliveryAcceptancePolicies
{
    internal const string BudgetVarianceMonitor = "fin_budget_variance_monitor";
    internal const string InvoicePrecheckApproval = "fin_invoice_precheck_approval";
    internal const string AttendanceFillReminder = "hr_attendance_fill_reminder";
    internal const string MonthlyAttendanceApproval = "hr_monthly_attendance_approval";
    internal const string OnboardingEmailApproval = "hr_onboarding_email_approval";

    private const string InvoiceAttachmentReason =
        "Automatic acceptance is unavailable because invoice input_file_refs attachments are required and Workflow Delivery does not transport run attachments.";

    private const string UnknownPackageReason =
        "Automatic acceptance is unavailable because this package has no registered acceptance input contract.";

    private static readonly ContractAcceptancePolicy AutomaticPreview =
        new(ContractAcceptanceMode.AutomaticPreview, null);
    private static readonly ContractAcceptancePolicy InvoiceManualOnly =
        new(ContractAcceptanceMode.Manual, InvoiceAttachmentReason);
    private static readonly ContractAcceptancePolicy Unknown =
        new(ContractAcceptanceMode.Unspecified, UnknownPackageReason);

    internal static ContractAcceptancePolicy Resolve(string? workflowName) => workflowName switch
    {
        BudgetVarianceMonitor or AttendanceFillReminder or MonthlyAttendanceApproval or OnboardingEmailApproval =>
            AutomaticPreview,
        InvoicePrecheckApproval => InvoiceManualOnly,
        _ => Unknown,
    };

    internal static void EnsureTriggerSupported(
        PackageSnapshot package,
        DeliveryTriggerKind triggerKind)
    {
        ArgumentNullException.ThrowIfNull(package);
        var policy = package.AcceptancePolicy;
        if (policy == null || policy.Mode == ContractAcceptanceMode.Unspecified)
        {
            throw new WorkflowDeliveryException(
                "UNSUPPORTED_DELIVERY_PACKAGE",
                "The workflow package has no registered acceptance input contract.");
        }

        if (triggerKind != DeliveryTriggerKind.None && policy.Mode != ContractAcceptanceMode.AutomaticPreview)
        {
            throw new WorkflowDeliveryException(
                "AUTOMATIC_ACCEPTANCE_UNSUPPORTED",
                policy.Limitation ?? "Automatic acceptance is unavailable for this workflow package.");
        }
    }
}
