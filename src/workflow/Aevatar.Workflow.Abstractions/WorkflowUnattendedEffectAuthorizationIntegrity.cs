using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Aevatar.Workflow.Abstractions;

/// <summary>
/// Creates and validates the owner grant used by an unattended workflow
/// ingress. The grant is contract-scoped: it never authorizes arbitrary tool
/// calls or a different workflow revision.
/// </summary>
public static class WorkflowUnattendedEffectAuthorizationIntegrity
{
    public const string SchemaVersion = "workflow-unattended-effects.v1";

    public static WorkflowUnattendedEffectAuthorization Create(
        string definitionActorId,
        string scopeId,
        string workflowId,
        string revisionId,
        string routeKey,
        string grantorSubject,
        long definitionVersion,
        WorkflowCallerNyxIdAuthority callerAuthority,
        WorkflowCapabilityAdmissionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(callerAuthority);
        ArgumentNullException.ThrowIfNull(plan);
        var normalizedGrantor = Require(grantorSubject, nameof(grantorSubject));
        EnsureCallerMatchesGrantor(callerAuthority, normalizedGrantor);
        EnsureDurablePlan(plan, workflowId, revisionId, normalizedGrantor);

        var authorization = new WorkflowUnattendedEffectAuthorization
        {
            SchemaVersion = SchemaVersion,
            DefinitionActorId = Require(definitionActorId, nameof(definitionActorId)),
            ScopeId = Require(scopeId, nameof(scopeId)),
            WorkflowId = Require(workflowId, nameof(workflowId)),
            RevisionId = Require(revisionId, nameof(revisionId)),
            AdmissionDigest = Require(plan.AdmissionDigest, nameof(plan.AdmissionDigest)),
            GrantorSubject = normalizedGrantor,
            RouteDigest = ComputeDigest("workflow-webhook-route.v1", Require(routeKey, nameof(routeKey))),
            CallerAuthorityDigest = ComputeCallerAuthorityDigest(callerAuthority),
            DefinitionVersion = RequireDefinitionVersion(definitionVersion),
        };
        authorization.Invocations.Add(BuildAuthorizedInvocations(plan));
        if (authorization.Invocations.Count == 0)
        {
            throw new InvalidOperationException(
                "Unattended execution requires at least one exact durable effect call site.");
        }

        authorization.AuthorizationDigest = ComputeAuthorizationDigest(authorization);
        return authorization;
    }

    public static void ValidateForDefinition(
        WorkflowUnattendedEffectAuthorization authorization,
        WorkflowCallerNyxIdAuthority callerAuthority,
        string routeKey,
        string definitionActorId,
        string scopeId,
        string workflowId,
        string revisionId,
        long definitionVersion,
        WorkflowCapabilityAdmissionPlan plan)
    {
        ValidateForDefinitionCore(
            authorization,
            callerAuthority,
            ComputeDigest("workflow-webhook-route.v1", Require(routeKey, nameof(routeKey))),
            definitionActorId,
            scopeId,
            workflowId,
            revisionId,
            definitionVersion,
            plan);
    }

    /// <summary>
    /// Revalidates a run-start authorization against actor-owned definition facts.
    /// The raw webhook route is deliberately unavailable after ingress; its digest
    /// remains covered by the authorization's integrity digest that was validated
    /// at run start.
    /// </summary>
    public static void ValidateForActorState(
        WorkflowUnattendedEffectAuthorization authorization,
        WorkflowCallerNyxIdAuthority callerAuthority,
        string definitionActorId,
        string scopeId,
        string workflowId,
        string revisionId,
        long definitionVersion,
        WorkflowCapabilityAdmissionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ValidateForDefinitionCore(
            authorization,
            callerAuthority,
            Require(authorization.RouteDigest, nameof(authorization.RouteDigest)),
            definitionActorId,
            scopeId,
            workflowId,
            revisionId,
            definitionVersion,
            plan);
    }

    private static void ValidateForDefinitionCore(
        WorkflowUnattendedEffectAuthorization authorization,
        WorkflowCallerNyxIdAuthority callerAuthority,
        string expectedRouteDigest,
        string definitionActorId,
        string scopeId,
        string workflowId,
        string revisionId,
        long definitionVersion,
        WorkflowCapabilityAdmissionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(callerAuthority);
        ArgumentNullException.ThrowIfNull(plan);
        var normalizedGrantor = Require(authorization.GrantorSubject, nameof(authorization.GrantorSubject));
        EnsureCallerMatchesGrantor(callerAuthority, normalizedGrantor);
        EnsureDurablePlan(plan, workflowId, revisionId, normalizedGrantor);

        if (!string.Equals(authorization.SchemaVersion, SchemaVersion, StringComparison.Ordinal) ||
            !string.Equals(authorization.DefinitionActorId, Require(definitionActorId, nameof(definitionActorId)), StringComparison.Ordinal) ||
            !string.Equals(authorization.ScopeId, Require(scopeId, nameof(scopeId)), StringComparison.Ordinal) ||
            !string.Equals(authorization.WorkflowId, Require(workflowId, nameof(workflowId)), StringComparison.Ordinal) ||
            !string.Equals(authorization.RevisionId, Require(revisionId, nameof(revisionId)), StringComparison.Ordinal) ||
            authorization.DefinitionVersion != RequireDefinitionVersion(definitionVersion) ||
            !FixedTimeEquals(authorization.AdmissionDigest, plan.AdmissionDigest) ||
            !FixedTimeEquals(authorization.RouteDigest, expectedRouteDigest) ||
            !FixedTimeEquals(
                authorization.CallerAuthorityDigest,
                ComputeCallerAuthorityDigest(callerAuthority)))
        {
            throw new InvalidOperationException(
                "Unattended effect authorization does not match the webhook binding.");
        }

        var expectedInvocations = BuildAuthorizedInvocations(plan);
        if (!InvocationsEqual(authorization.Invocations, expectedInvocations) ||
            !FixedTimeEquals(
                authorization.AuthorizationDigest,
                ComputeAuthorizationDigest(authorization)))
        {
            throw new InvalidOperationException(
                "Unattended effect authorization integrity validation failed.");
        }
    }

    public static bool AuthorizesInvocation(
        WorkflowUnattendedEffectAuthorization? authorization,
        WorkflowCallerNyxIdAuthority? callerAuthority,
        WorkflowCapabilityInvocationAdmission? admission)
    {
        if (authorization is null || callerAuthority is null || admission is null ||
            !string.Equals(authorization.SchemaVersion, SchemaVersion, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(authorization.DefinitionActorId) ||
            string.IsNullOrWhiteSpace(authorization.ScopeId) ||
            string.IsNullOrWhiteSpace(authorization.WorkflowId) ||
            string.IsNullOrWhiteSpace(authorization.RevisionId) ||
            authorization.DefinitionVersion < 1 ||
            string.IsNullOrWhiteSpace(authorization.GrantorSubject) ||
            string.IsNullOrWhiteSpace(authorization.AdmissionDigest) ||
            string.IsNullOrWhiteSpace(authorization.RouteDigest) ||
            !FixedTimeEquals(
                authorization.CallerAuthorityDigest,
                ComputeCallerAuthorityDigest(callerAuthority)) ||
            !FixedTimeEquals(
                authorization.AuthorizationDigest,
                ComputeAuthorizationDigest(authorization)))
        {
            return false;
        }

        var expected = TryBuildAuthorizedInvocation(admission);
        if (expected is null)
            return false;

        return authorization.Invocations.Any(candidate =>
            string.Equals(candidate.CallSiteId, expected.CallSiteId, StringComparison.Ordinal) &&
            FixedTimeEquals(candidate.CapabilityContractDigest, expected.CapabilityContractDigest) &&
            FixedTimeEquals(candidate.ExplicitRequestGrantDigest, expected.ExplicitRequestGrantDigest));
    }

    public static WorkflowUnattendedInvocationPermit? CreateInvocationPermit(
        WorkflowUnattendedEffectAuthorization? authorization,
        WorkflowCallerNyxIdAuthority? callerAuthority,
        WorkflowCapabilityInvocationAdmission? admission)
    {
        if (!AuthorizesInvocation(authorization, callerAuthority, admission) ||
            authorization is null || admission is null)
        {
            return null;
        }

        var authorized = TryBuildAuthorizedInvocation(admission);
        return authorized is null
            ? null
            : new WorkflowUnattendedInvocationPermit
            {
                AuthorizationId = authorization.AuthorizationDigest,
                CallSiteId = authorized.CallSiteId,
                CapabilityContractDigest = authorized.CapabilityContractDigest,
                ExplicitRequestGrantDigest = authorized.ExplicitRequestGrantDigest,
            };
    }

    private static void EnsureDurablePlan(
        WorkflowCapabilityAdmissionPlan plan,
        string workflowId,
        string revisionId,
        string grantorSubject)
    {
        if (plan.ExecutionMode != ExternalCapabilityExecutionMode.Durable ||
            !WorkflowCapabilityAdmissionPlanIntegrity.IsCanonicalDurableAuthorizationOwner(
                plan.DurableAuthorizationOwner) ||
            !string.Equals(
                plan.DurableAuthorizationOwner.OwnerSubject,
                Require(grantorSubject, nameof(grantorSubject)),
                StringComparison.Ordinal) ||
            !FixedTimeEquals(
                plan.AdmissionDigest,
                WorkflowCapabilityAdmissionPlanIntegrity.ComputeAdmissionDigest(plan)))
        {
            throw new InvalidOperationException(
                "Unattended execution requires a valid durable capability admission plan owned by the binder.");
        }

        foreach (var invocation in plan.InvocationAdmissions)
        {
            WorkflowCapabilityAdmissionPlanIntegrity
                .ValidateInvocationAdmissionIntrinsicIntegrity(invocation);
            var grant = invocation.NyxIdExplicitRequestGrant;
            if (grant is null)
                continue;
            if (!string.Equals(grant.WorkflowId, Require(workflowId, nameof(workflowId)), StringComparison.Ordinal) ||
                !string.Equals(grant.RevisionId, Require(revisionId, nameof(revisionId)), StringComparison.Ordinal) ||
                grant.GrantorOwnerKind != plan.DurableAuthorizationOwner.OwnerKind ||
                !string.Equals(
                    grant.GrantorOwnerSubject,
                    plan.DurableAuthorizationOwner.OwnerSubject,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Unattended execution grant does not match the workflow revision owner.");
            }
        }
    }

    private static void EnsureCallerMatchesGrantor(
        WorkflowCallerNyxIdAuthority callerAuthority,
        string grantorSubject)
    {
        if (!string.Equals(
                Require(callerAuthority.ExternalUserId, nameof(callerAuthority.ExternalUserId)),
                grantorSubject,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Unattended execution caller authority does not match the durable authorization owner.");
        }
    }

    private static IReadOnlyList<WorkflowUnattendedInvocationAuthorization> BuildAuthorizedInvocations(
        WorkflowCapabilityAdmissionPlan plan) => plan.InvocationAdmissions
        .Select(TryBuildAuthorizedInvocation)
        .Where(static authorization => authorization is not null)
        .Select(static authorization => authorization!)
        .OrderBy(static authorization => authorization.CallSiteId, StringComparer.Ordinal)
        .ToArray();

    private static WorkflowUnattendedInvocationAuthorization? TryBuildAuthorizedInvocation(
        WorkflowCapabilityInvocationAdmission admission)
    {
        if (admission.Capability?.CapabilityCase !=
            ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserRequest)
        {
            return null;
        }

        var capability = admission.Capability.NyxIdUserRequest;
        var policy = capability.ExecutionPolicy;
        var grant = admission.NyxIdExplicitRequestGrant;
        if (policy is null ||
            policy.Risk != NyxIdOperationRisk.Write ||
            policy.Approval != NyxIdOperationApproval.Required ||
            !policy.AllowedExecutionModes.Contains(ExternalCapabilityExecutionMode.Durable) ||
            grant is null ||
            !grant.AllowedExecutionModes.Contains(ExternalCapabilityExecutionMode.Durable) ||
            grant.Risk != policy.Risk ||
            grant.GrantorAuthority != NyxIdExplicitRequestGrantorAuthority.AevatarWorkflowBinder)
        {
            return null;
        }

        var grantDigest = WorkflowCapabilityAdmissionPlanIntegrity.ComputeNyxIdExplicitRequestGrantDigest(grant);
        if (!FixedTimeEquals(capability.ExplicitRequestGrantDigest, grantDigest))
            return null;

        return new WorkflowUnattendedInvocationAuthorization
        {
            CallSiteId = admission.CallSiteId,
            CapabilityContractDigest = capability.ContractDigest,
            ExplicitRequestGrantDigest = grantDigest,
        };
    }

    private static bool InvocationsEqual(
        IEnumerable<WorkflowUnattendedInvocationAuthorization> left,
        IEnumerable<WorkflowUnattendedInvocationAuthorization> right)
    {
        var leftArray = left.OrderBy(static value => value.CallSiteId, StringComparer.Ordinal).ToArray();
        var rightArray = right.OrderBy(static value => value.CallSiteId, StringComparer.Ordinal).ToArray();
        if (leftArray.Length != rightArray.Length)
            return false;
        for (var index = 0; index < leftArray.Length; index++)
        {
            if (!string.Equals(leftArray[index].CallSiteId, rightArray[index].CallSiteId, StringComparison.Ordinal) ||
                !FixedTimeEquals(leftArray[index].CapabilityContractDigest, rightArray[index].CapabilityContractDigest) ||
                !FixedTimeEquals(leftArray[index].ExplicitRequestGrantDigest, rightArray[index].ExplicitRequestGrantDigest))
            {
                return false;
            }
        }
        return true;
    }

    private static string ComputeCallerAuthorityDigest(WorkflowCallerNyxIdAuthority authority) =>
        ComputeDigest(
            "workflow-caller-authority.v1",
            Require(authority.Platform, nameof(authority.Platform)),
            authority.Tenant?.Trim() ?? string.Empty,
            Require(authority.ExternalUserId, nameof(authority.ExternalUserId)),
            Require(authority.Scope, nameof(authority.Scope)),
            Require(authority.BindingId, nameof(authority.BindingId)));

    private static string ComputeAuthorizationDigest(WorkflowUnattendedEffectAuthorization authorization)
    {
        var values = new List<string>
        {
            authorization.SchemaVersion,
            authorization.DefinitionActorId,
            authorization.ScopeId,
            authorization.WorkflowId,
            authorization.RevisionId,
            authorization.AdmissionDigest,
            authorization.GrantorSubject,
            authorization.RouteDigest,
            authorization.CallerAuthorityDigest,
            authorization.DefinitionVersion.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
        };
        foreach (var invocation in authorization.Invocations
                     .OrderBy(static value => value.CallSiteId, StringComparer.Ordinal))
        {
            values.Add(invocation.CallSiteId);
            values.Add(invocation.CapabilityContractDigest);
            values.Add(invocation.ExplicitRequestGrantDigest);
        }
        return ComputeDigest("workflow-unattended-effects-authorization.v1", values.ToArray());
    }

    private static string ComputeDigest(string domain, params string[] values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, domain);
        foreach (var value in values)
            Append(hash, value ?? string.Empty);
        return $"sha256:{Convert.ToHexStringLower(hash.GetHashAndReset())}";
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static bool FixedTimeEquals(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
            return false;
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static string Require(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Unattended effect authorization identity is incomplete.", name)
            : value.Trim();

    private static long RequireDefinitionVersion(long value) =>
        value < 1
            ? throw new ArgumentOutOfRangeException(
                nameof(value),
                "Unattended effect authorization requires a positive definition version.")
            : value;
}
