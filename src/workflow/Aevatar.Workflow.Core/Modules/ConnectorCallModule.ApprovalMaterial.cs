using System.Security.Cryptography;
using System.Text;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Workflow.Core.Execution;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Core.Modules;

public sealed partial class ConnectorCallModule
{
    private const string ExternalActionPlanSchema = "workflow-external-action.v1";
    private const string ProtectedMaterialSchema = "connector-call-protected-material.v1";
    private const string ProtectedCompletionSchema = "connector-call-protected-completion.v1";
    private const string ExternalActionToolName = "workflow_connector_external_action";
    private const string ProtectedMaterialAuditReason = "workflow.connector-external-action-material";
    private const string ProtectedCompletionAuditReason = "workflow.connector-external-action-completion";

    private sealed record ConnectorApprovalMaterialBundle(
        ConnectorCallProtectedMaterial Material,
        WorkflowExternalActionPlan Plan);

    private static bool RequiresConnectorApproval(StepRequestEvent request) =>
        request.StepParameters?.ConnectorApproval?.Policy == WorkflowExternalActionApprovalPolicy.Required;

    private async Task<ConnectorApprovalMaterialBundle> BuildApprovalMaterialAsync(
        StepRequestEvent request,
        string runId,
        string connectorName,
        string operation,
        IConnector connector,
        int attempts,
        int timeoutMs,
        bool onErrorContinue,
        bool isSecureStep,
        WorkflowExternalActionPlan? existingPlan,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var options = request.StepParameters?.ConnectorApproval
            ?? throw new InvalidOperationException("Connector approval options are required.");
        ValidateApprovalConfiguration(request, runId, operation, options, connector, ctx);

        var createdAt = existingPlan?.CreatedAt?.ToDateTimeOffset() ?? ctx.UtcNow;
        var expiresAt = createdAt.AddSeconds(options.ExpirationSeconds);
        var actionId = BuildApprovalActionId(
            runId,
            request.StepId,
            request.ExecutionId,
            request.IdempotencyKey);
        var authority = ctx.CallerNyxIdAuthority!;
        var plan = new WorkflowExternalActionPlan
        {
            SchemaVersion = ExternalActionPlanSchema,
            ActionId = actionId,
            IdempotencyKey = request.IdempotencyKey,
            Provenance = new WorkflowExternalActionProvenance
            {
                ScopeId = ctx.ScopeId.Trim(),
                TeamId = Normalize(options.TeamId),
                MemberId = Normalize(options.MemberId),
                WorkflowId = Normalize(options.WorkflowId),
                PublishedServiceId = Normalize(options.PublishedServiceId),
                WorkflowActorId = ctx.AgentId,
                RunId = runId,
                StepId = request.StepId.Trim(),
                ExecutionId = Normalize(request.ExecutionId),
                RequesterPlatform = authority.Platform.Trim(),
                RequesterTenant = Normalize(authority.Tenant),
                RequesterExternalUserId = authority.ExternalUserId.Trim(),
                PrincipalSubject = authority.ExternalUserId.Trim(),
                RequesterCapabilityScope = authority.Scope.Trim(),
            },
            ServiceRef = options.ServiceRef.Trim(),
            NodeId = options.NodeId.Trim(),
            ConnectorName = connectorName,
            ConnectorType = connector.Type,
            Operation = operation.Trim(),
            HttpVerb = options.HttpVerb.Trim().ToUpperInvariant(),
            Resource = options.Resource.Trim(),
            PermissionScope = options.PermissionScope.Trim(),
            CreatedAt = Timestamp.FromDateTimeOffset(createdAt),
            ExpiresAt = Timestamp.FromDateTimeOffset(expiresAt),
            DigestAlgorithm = "sha256",
            Summary = BuildApprovalSummary(options.HttpVerb, options.Resource, options.ServiceRef, options.NodeId),
            ApprovalPolicy = WorkflowExternalActionApprovalPolicy.Required,
            PolicyReason = string.IsNullOrWhiteSpace(options.PolicyReason)
                ? "workflow-step-required-approval"
                : options.PolicyReason.Trim(),
            Destructive = options.Destructive,
        };

        var material = new ConnectorCallProtectedMaterial
        {
            SchemaVersion = ProtectedMaterialSchema,
            ActionId = actionId,
            IdempotencyKey = request.IdempotencyKey,
            RunId = runId,
            StepId = request.StepId.Trim(),
            ExecutionId = Normalize(request.ExecutionId),
            ConnectorName = connectorName,
            ConnectorType = connector.Type,
            Operation = operation.Trim(),
            Payload = await ResolvePayloadAsync(request, isSecureStep, ctx, ct) ?? string.Empty,
            Input = request.Input ?? string.Empty,
            SecureStep = isSecureStep,
            Attempts = attempts,
            TimeoutMs = timeoutMs,
            OnErrorContinue = onErrorContinue,
            ApprovalPlan = plan.Clone(),
        };
        material.Parameters.Add(request.Parameters
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => new ConnectorCallProtectedParameter
            {
                Key = pair.Key,
                Value = pair.Value,
            }));

        plan.MaterialDigestSha256 = ComputeMaterialDigest(material);
        return new ConnectorApprovalMaterialBundle(material, plan);
    }

    private static async Task<RuntimeSecretReference> StoreApprovalMaterialAsync(
        ConnectorApprovalMaterialBundle bundle,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var runtimeSecretStore = WorkflowRunExecutionContextStateAccess.ResolveRuntimeSecretStore(ctx)
            ?? throw new InvalidOperationException("Workflow connector approval runtime secret store is unavailable.");
        var plan = bundle.Plan;
        var material = bundle.Material;
        var approvalWindow = plan.ExpiresAt.ToDateTimeOffset() - plan.CreatedAt.ToDateTimeOffset();
        var executionWindow = TimeSpan.FromMilliseconds((long)material.Attempts * material.TimeoutMs);
        var stored = await runtimeSecretStore.PutAsync(
            new StoreRuntimeSecretRequest(
                CredentialSecretPurposes.WorkflowConnectorExternalActionMaterial,
                material.RunId,
                material.StepId,
                Convert.ToBase64String(material.ToByteArray()),
                approvalWindow + executionWindow,
                ConsumeOnce: false,
                AuditReason: ProtectedMaterialAuditReason),
            ct);
        return stored.Reference.Clone();
    }

    private static void ValidateApprovalConfiguration(
        StepRequestEvent request,
        string runId,
        string operation,
        WorkflowConnectorApprovalOptions options,
        IConnector connector,
        IWorkflowExecutionContext ctx)
    {
        if (string.IsNullOrWhiteSpace(runId) ||
            string.IsNullOrWhiteSpace(request.StepId) ||
            string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            throw new InvalidOperationException(
                "Connector approval requires run_id, step_id, and a stable idempotency_key.");
        }

        if (string.IsNullOrWhiteSpace(ctx.ScopeId) ||
            string.IsNullOrWhiteSpace(ctx.AgentId) ||
            ctx.CallerNyxIdAuthority == null)
        {
            throw new InvalidOperationException(
                "Connector approval requires workflow scope, actor, and caller NyxID authority.");
        }

        if (string.IsNullOrWhiteSpace(operation) ||
            string.IsNullOrWhiteSpace(options.ServiceRef) ||
            string.IsNullOrWhiteSpace(options.NodeId) ||
            string.IsNullOrWhiteSpace(options.HttpVerb) ||
            string.IsNullOrWhiteSpace(options.Resource) ||
            string.IsNullOrWhiteSpace(options.PermissionScope))
        {
            throw new InvalidOperationException(
                "Connector approval requires operation, service_ref, node_id, http_verb, resource, and permission_scope.");
        }

        if (options.ExpirationSeconds <= 0 ||
            options.StatusCheckIntervalSeconds <= 0 ||
            options.StatusCheckIntervalSeconds > options.ExpirationSeconds)
        {
            throw new InvalidOperationException(
                "Connector approval requires a positive expiration and a status-check interval within that expiration.");
        }

        ValidateHttpApprovalBinding(request, operation, options, connector);
    }

    private static void ValidateHttpApprovalBinding(
        StepRequestEvent request,
        string operation,
        WorkflowConnectorApprovalOptions options,
        IConnector connector)
    {
        if (!string.Equals(connector.Type, "http", StringComparison.OrdinalIgnoreCase))
            return;

        var actualVerb = request.Parameters.TryGetValue("method", out var method) &&
                         !string.IsNullOrWhiteSpace(method)
            ? method.Trim().ToUpperInvariant()
            : "POST";
        var rawResource = request.Parameters.TryGetValue("path", out var path) &&
                          !string.IsNullOrWhiteSpace(path)
            ? path
            : operation;
        var actualResource = NormalizeHttpResource(rawResource);
        if (!string.Equals(options.HttpVerb.Trim(), actualVerb, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(NormalizeHttpResource(options.Resource), actualResource, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Connector approval HTTP verb and resource must match the concrete connector request.");
        }
    }

    private static string NormalizeHttpResource(string? resource)
    {
        var normalized = string.IsNullOrWhiteSpace(resource) ? "/" : resource.Trim();
        return normalized.StartsWith('/') ? normalized : "/" + normalized;
    }

    private static string BuildApprovalActionId(
        string runId,
        string stepId,
        string executionId,
        string idempotencyKey)
    {
        var identity = string.Join('\n', runId, stepId, executionId, idempotencyKey);
        return "workflow-connector-action:" + ComputeSha256Hex(Encoding.UTF8.GetBytes(identity));
    }

    private static string BuildApprovalSummary(
        string httpVerb,
        string resource,
        string serviceRef,
        string nodeId) =>
        $"{httpVerb.Trim().ToUpperInvariant()} {resource.Trim()} via {serviceRef.Trim()} on node {nodeId.Trim()}";

    private static string ComputeMaterialDigest(ConnectorCallProtectedMaterial material) =>
        ComputeSha256Hex(material.ToByteArray());

    private static string ComputeSha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool FixedTimeDigestEquals(string left, string right)
    {
        if (left.Length != right.Length)
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left),
            Encoding.ASCII.GetBytes(right));
    }

    private async Task<(ConnectorCallProtectedMaterial? Material, string ReasonCode)> ResolveAndVerifyMaterialAsync(
        ConnectorApprovalCoordinationState coordination,
        IWorkflowExecutionContext ctx,
        CancellationToken ct,
        bool requireUnexpired)
    {
        var snapshot = coordination.Snapshot;
        var plan = snapshot?.Plan;
        var reference = coordination.MaterialReference;
        if (plan == null || reference == null || string.IsNullOrWhiteSpace(reference.Ref))
            return (null, "approval_material_unavailable");

        var runtimeSecretStore = WorkflowRunExecutionContextStateAccess.ResolveRuntimeSecretStore(ctx);
        if (runtimeSecretStore == null)
            return (null, "approval_material_store_unavailable");

        var resolved = await runtimeSecretStore.ResolveAsync(
            new ResolveRuntimeSecretRequest(
                reference.Ref,
                reference.Purpose,
                reference.OwnerRunId,
                reference.OwnerStepId,
                ProtectedMaterialAuditReason),
            ct);
        if (!resolved.Resolved || string.IsNullOrWhiteSpace(resolved.Secret))
            return (null, "approval_material_unavailable");

        ConnectorCallProtectedMaterial material;
        try
        {
            material = ConnectorCallProtectedMaterial.Parser.ParseFrom(
                Convert.FromBase64String(resolved.Secret));
        }
        catch (FormatException)
        {
            return (null, "approval_material_invalid");
        }
        catch (InvalidProtocolBufferException)
        {
            return (null, "approval_material_invalid");
        }

        if (!string.Equals(material.SchemaVersion, ProtectedMaterialSchema, StringComparison.Ordinal) ||
            !FixedTimeDigestEquals(ComputeMaterialDigest(material), plan.MaterialDigestSha256 ?? string.Empty))
        {
            return (null, "approval_material_digest_mismatch");
        }

        var expectedPlan = plan.Clone();
        expectedPlan.MaterialDigestSha256 = string.Empty;
        if (material.ApprovalPlan == null || !material.ApprovalPlan.Equals(expectedPlan))
            return (null, "approval_plan_mismatch");

        if (!MatchesCurrentApprovalAuthority(plan, ctx))
            return (null, "approval_authority_mismatch");

        if (requireUnexpired && IsExpired(plan.ExpiresAt, ctx.UtcNow))
            return (null, "approval_expired");

        return (material, string.Empty);
    }

    private static bool MatchesCurrentApprovalAuthority(
        WorkflowExternalActionPlan plan,
        IWorkflowExecutionContext ctx)
    {
        var provenance = plan.Provenance;
        var authority = ctx.CallerNyxIdAuthority;
        return provenance != null &&
               authority != null &&
               string.Equals(provenance.WorkflowActorId, ctx.AgentId, StringComparison.Ordinal) &&
               string.Equals(provenance.ScopeId, ctx.ScopeId, StringComparison.Ordinal) &&
               string.Equals(provenance.RequesterPlatform, authority.Platform, StringComparison.Ordinal) &&
               string.Equals(provenance.RequesterTenant, authority.Tenant, StringComparison.Ordinal) &&
               string.Equals(provenance.RequesterExternalUserId, authority.ExternalUserId, StringComparison.Ordinal) &&
               string.Equals(provenance.PrincipalSubject, authority.ExternalUserId, StringComparison.Ordinal) &&
               string.Equals(provenance.RequesterCapabilityScope, authority.Scope, StringComparison.Ordinal);
    }

    private static bool IsExpired(Timestamp? expiresAt, DateTimeOffset now) =>
        expiresAt == null || expiresAt.ToDateTimeOffset() <= now;

    private static DateTimeOffset ResolveEffectiveExpiry(WorkflowExternalActionApprovalSnapshot snapshot)
    {
        var planExpiry = snapshot.Plan?.ExpiresAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue;
        var remoteExpiry = snapshot.RemoteExpiresAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue;
        return planExpiry <= remoteExpiry ? planExpiry : remoteExpiry;
    }

    private async Task<TResult> WithRemoteApprovalContextAsync<TResult>(
        WorkflowExternalActionPlan plan,
        IWorkflowExecutionContext ctx,
        Func<Task<TResult>> operation,
        CancellationToken ct)
    {
        var credential = await WorkflowCallerCredentialRuntimeContextAccess.TryGetCredentialAsync(ctx, ct);
        if (!credential.Found)
            throw new InvalidOperationException("Workflow caller credential is unavailable for remote approval.");
        var resolved = await WorkflowCallerAccessTokenResolver.ResolveAsync(
            credential.Credential,
            _callerAccessTokenProvider,
            ct);
        if (string.IsNullOrWhiteSpace(resolved.BearerToken))
            throw new InvalidOperationException("Workflow caller credential is unavailable for remote approval.");
        if (!MatchesCurrentApprovalAuthority(plan, ctx))
            throw new InvalidOperationException("Workflow caller authority no longer matches the approval plan.");

        var provenance = plan.Provenance;
        var toolContext = AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity(plan.ActionId, plan.ActionId, plan.IdempotencyKey),
            Credentials = new AgentToolCredentials(
                resolved.BearerToken,
                NyxIdOrgToken: null,
                SenderNyxIdAccessToken: null,
                NyxIdCredentialKind: ResolveAgentToolCredentialKind(resolved.Kind)),
            DurableNyxIdCredential = resolved.DurableCallerCredential?.Clone(),
            Caller = new AgentToolCallerContext(
                provenance.ScopeId,
                provenance.PrincipalSubject,
                ResponseId: null),
            NyxIdAuthority = new AgentToolNyxIdAuthorityContext(
                provenance.RequesterPlatform,
                provenance.RequesterTenant,
                provenance.RequesterExternalUserId),
            Routing = LLMRequestRoutingContext.Empty,
        };
        using var scope = AgentToolContextScope.Push(toolContext);
        return await operation();
    }

    private static AgentToolNyxIdCredentialKind ResolveAgentToolCredentialKind(
        NyxIdCallerCredentialKind kind) =>
        kind switch
        {
            NyxIdCallerCredentialKind.SourceReadableUserBearer =>
                AgentToolNyxIdCredentialKind.SourceReadableUserBearer,
            NyxIdCallerCredentialKind.ProxyDelegation =>
                AgentToolNyxIdCredentialKind.ProxyDelegation,
            NyxIdCallerCredentialKind.AgentKey =>
                AgentToolNyxIdCredentialKind.AgentKey,
            _ => AgentToolNyxIdCredentialKind.Unspecified,
        };

    private static RemoteToolApprovalRequest BuildRemoteApprovalRequest(WorkflowExternalActionPlan plan) =>
        new(
            plan.ActionId,
            ExternalActionToolName,
            plan.ActionId,
            JsonFormatter.Default.Format(plan),
            ToolApprovalPolicies.ExternalInvocation,
            plan.Destructive);

    private static async Task<RuntimeSecretReference> StoreApprovedCompletionAsync(
        ConnectorApprovalCoordinationState coordination,
        StepCompletedEvent completion,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var runtimeSecretStore = WorkflowRunExecutionContextStateAccess.ResolveRuntimeSecretStore(ctx)
            ?? throw new InvalidOperationException("Workflow connector approval runtime secret store is unavailable.");
        var plan = coordination.Snapshot?.Plan
            ?? throw new InvalidOperationException("Workflow connector approval plan is unavailable.");
        var protectedCompletion = new ConnectorCallProtectedCompletion
        {
            SchemaVersion = ProtectedCompletionSchema,
            Completion = completion.Clone(),
        };
        var approvalWindow = plan.ExpiresAt.ToDateTimeOffset() - plan.CreatedAt.ToDateTimeOffset();
        var executionWindow = TimeSpan.FromMilliseconds(
            (long)Math.Max(1, coordination.Attempts) * Math.Max(1, coordination.TimeoutMs));
        var stored = await runtimeSecretStore.PutAsync(
            new StoreRuntimeSecretRequest(
                CredentialSecretPurposes.WorkflowConnectorExternalActionCompletion,
                plan.Provenance.RunId,
                plan.Provenance.StepId,
                Convert.ToBase64String(protectedCompletion.ToByteArray()),
                approvalWindow + executionWindow,
                ConsumeOnce: false,
                AuditReason: ProtectedCompletionAuditReason),
            ct);
        return stored.Reference.Clone();
    }

    private static async Task<StepCompletedEvent> ResolveApprovedCompletionAsync(
        ConnectorApprovalCoordinationState coordination,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var reference = coordination.CompletionReference;
        if (reference == null || string.IsNullOrWhiteSpace(reference.Ref))
            throw new InvalidOperationException("Approved connector completion reference is unavailable.");

        var runtimeSecretStore = WorkflowRunExecutionContextStateAccess.ResolveRuntimeSecretStore(ctx)
            ?? throw new InvalidOperationException("Workflow connector approval runtime secret store is unavailable.");
        var resolved = await runtimeSecretStore.ResolveAsync(
            new ResolveRuntimeSecretRequest(
                reference.Ref,
                reference.Purpose,
                reference.OwnerRunId,
                reference.OwnerStepId,
                ProtectedCompletionAuditReason),
            ct);
        if (!resolved.Resolved || string.IsNullOrWhiteSpace(resolved.Secret))
            throw new InvalidOperationException("Approved connector completion material is unavailable.");

        try
        {
            var protectedCompletion = ConnectorCallProtectedCompletion.Parser.ParseFrom(
                Convert.FromBase64String(resolved.Secret));
            if (!string.Equals(
                    protectedCompletion.SchemaVersion,
                    ProtectedCompletionSchema,
                    StringComparison.Ordinal) ||
                protectedCompletion.Completion == null)
            {
                throw new InvalidOperationException("Approved connector completion material is invalid.");
            }

            return protectedCompletion.Completion.Clone();
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Approved connector completion material is invalid.", ex);
        }
        catch (InvalidProtocolBufferException ex)
        {
            throw new InvalidOperationException("Approved connector completion material is invalid.", ex);
        }
    }

    private static async Task RevokeProtectedExecutionDataAsync(
        ConnectorApprovalCoordinationState coordination,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var runtimeSecretStore = WorkflowRunExecutionContextStateAccess.ResolveRuntimeSecretStore(ctx);
        if (runtimeSecretStore == null &&
            (coordination.MaterialReference != null || coordination.CompletionReference != null))
        {
            throw new InvalidOperationException("Workflow connector approval runtime secret store is unavailable.");
        }

        if (coordination.MaterialReference is { } materialReference &&
            !string.IsNullOrWhiteSpace(materialReference.Ref))
        {
            await RevokeReferenceAsync(
                runtimeSecretStore!,
                materialReference,
                ProtectedMaterialAuditReason,
                ct);
        }

        coordination.MaterialReference = null;
        if (coordination.CompletionReference is { } completionReference &&
            !string.IsNullOrWhiteSpace(completionReference.Ref))
        {
            await RevokeReferenceAsync(
                runtimeSecretStore!,
                completionReference,
                ProtectedCompletionAuditReason,
                ct);
        }

        coordination.CompletionReference = null;
    }

    private static Task<RevokeRuntimeSecretResult> RevokeReferenceAsync(
        IRuntimeSecretStore runtimeSecretStore,
        RuntimeSecretReference reference,
        string auditReason,
        CancellationToken ct) =>
        runtimeSecretStore.RevokeAsync(
            new RevokeRuntimeSecretRequest(
                reference.Ref,
                reference.Purpose,
                reference.OwnerRunId,
                reference.OwnerStepId,
                auditReason),
            ct);

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
