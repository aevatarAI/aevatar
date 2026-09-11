using System.Collections.Immutable;
using Aevatar.AI.ToolProviders.NyxId;
using static Aevatar.GAgents.Channel.NyxIdRelay.VerifiedChannelRegistrationServiceSelection;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

public interface IChannelRegistrationNyxIdAuthorizationPort
{
    Task<NyxIdApiAccessResult<NyxIdUserServices>> ReadUserServicesAsync(
        string accessToken,
        CancellationToken ct);

    Task<NyxIdApiAccessResult<NyxIdApiKeyScopePlan>> PlanApiKeyScopeAsync(
        string accessToken,
        IReadOnlyList<string> selectedServiceIds,
        string? targetOrganizationId,
        CancellationToken ct);
}

public sealed record ChannelRegistrationAuthorizationPlanningRequest(
    string AccessToken,
    VerifiedChannelRegistrationOwner RegistrationOwner,
    IReadOnlyList<string> RegistrationServiceIds,
    IReadOnlyList<string> RequiredServiceIds)
{
    public string AuthenticatedActorId => RegistrationOwner.AuthenticatedActorId;
    public string RegistrationOwnerScopeId => RegistrationOwner.KeyOwner.Id;
}

public enum ChannelRegistrationKeyOwnerKind
{
    Unspecified = 0,
    Personal = 1,
    Organization = 2,
}

public sealed record ChannelRegistrationKeyOwner(
    ChannelRegistrationKeyOwnerKind Kind,
    string Id);

public sealed record ChannelRegistrationAuthorizationPlanningResult(
    VerifiedChannelRegistrationAuthorizationPlan? Plan,
    string ErrorCode)
{
    public bool Succeeded => Plan is not null && string.IsNullOrEmpty(ErrorCode);
}

public sealed record ChannelRegistrationServiceVerificationResult(
    VerifiedChannelRegistrationServiceSelection? Selection,
    string ErrorCode);

public sealed class VerifiedChannelRegistrationServiceSelection
{
    // The complete planner is nested below both verified stages so it can construct them
    // privately; ordinary consumers in this assembly cannot mint either stage.
    private VerifiedChannelRegistrationServiceSelection(
        ChannelRegistrationAuthorizationPlanningRequest request,
        IEnumerable<NyxIdUserService> inventory)
    {
        AccessToken = request.AccessToken;
        RegistrationOwner = request.RegistrationOwner;
        AuthenticatedActorId = request.AuthenticatedActorId;
        RegistrationOwnerScopeId = request.RegistrationOwnerScopeId;
        Owner = request.RegistrationOwner.KeyOwner;
        TargetOrganizationId = request.RegistrationOwner.TargetOrganizationId;
        RegistrationServiceIds = request.RegistrationServiceIds.ToImmutableArray();
        SelectedServiceIds = request.RegistrationServiceIds.Concat(request.RequiredServiceIds)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
        Inventory = inventory.ToImmutableArray();
    }

    internal string AccessToken { get; }
    internal VerifiedChannelRegistrationOwner RegistrationOwner { get; }
    internal ImmutableArray<NyxIdUserService> Inventory { get; }
    public string AuthenticatedActorId { get; }
    public string RegistrationOwnerScopeId { get; }
    public ChannelRegistrationKeyOwner Owner { get; }
    public string? TargetOrganizationId { get; }
    public ImmutableArray<string> RegistrationServiceIds { get; }
    public ImmutableArray<string> SelectedServiceIds { get; }

    public sealed class VerifiedChannelRegistrationAuthorizationPlan
    {
        private VerifiedChannelRegistrationAuthorizationPlan(
            string authenticatedActorId,
            ChannelRegistrationKeyOwner keyOwner,
            string? targetOrganizationId,
            IEnumerable<string> registrationServiceIds,
            IEnumerable<string> allowedServiceIds,
            IEnumerable<string> allowedNodeIds,
            DateTimeOffset evaluatedAtUtc,
            string scopePlanDigest)
        {
            AuthenticatedActorId = authenticatedActorId;
            KeyOwner = keyOwner;
            TargetOrganizationId = targetOrganizationId;
            RegistrationServiceIds = registrationServiceIds.ToImmutableArray();
            AllowedServiceIds = allowedServiceIds.ToImmutableArray();
            AllowedNodeIds = allowedNodeIds.ToImmutableArray();
            EvaluatedAtUtc = evaluatedAtUtc;
            ScopePlanDigest = scopePlanDigest;
        }

        public string AuthenticatedActorId { get; }

        public ChannelRegistrationKeyOwner KeyOwner { get; }

        public string? TargetOrganizationId { get; }

        public ImmutableArray<string> RegistrationServiceIds { get; }

        public ImmutableArray<string> AllowedServiceIds { get; }

        public ImmutableArray<string> AllowedNodeIds { get; }

        public DateTimeOffset EvaluatedAtUtc { get; }

        public string ScopePlanDigest { get; }

        public sealed class ChannelRegistrationAuthorizationPlanner(
            IChannelRegistrationNyxIdAuthorizationPort authorizationPort)
        {
            private readonly IChannelRegistrationNyxIdAuthorizationPort _authorizationPort =
                authorizationPort ?? throw new ArgumentNullException(nameof(authorizationPort));

            public async Task<ChannelRegistrationAuthorizationPlanningResult> PlanAsync(
                ChannelRegistrationAuthorizationPlanningRequest request,
                CancellationToken ct)
            {
                var verified = await VerifySelectionAsync(request, ct);
                return verified.Selection is null
                    ? Failed(verified.ErrorCode)
                    : await PlanVerifiedAsync(verified.Selection, ct);
            }

            public async Task<ChannelRegistrationAuthorizationPlanningResult> PlanAsync(
                VerifiedChannelRegistrationServiceSelection selection,
                IReadOnlyList<string> requiredServiceIds,
                CancellationToken ct)
            {
                ArgumentNullException.ThrowIfNull(selection);
                var verified = await VerifySelectionAsync(new ChannelRegistrationAuthorizationPlanningRequest(
                    selection.AccessToken, selection.RegistrationOwner,
                    selection.RegistrationServiceIds, requiredServiceIds), ct);
                if (verified.Selection is null)
                    return Failed(verified.ErrorCode);
                if (verified.Selection.Owner != selection.Owner ||
                    !string.Equals(
                        verified.Selection.AuthenticatedActorId,
                        selection.AuthenticatedActorId,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        verified.Selection.TargetOrganizationId,
                        selection.TargetOrganizationId,
                        StringComparison.Ordinal))
                    return Failed("service_owner_forbidden");
                return await PlanVerifiedAsync(verified.Selection, ct);
            }

            public async Task<ChannelRegistrationServiceVerificationResult> VerifySelectionAsync(
                ChannelRegistrationAuthorizationPlanningRequest request,
                CancellationToken ct)
            {
                ArgumentNullException.ThrowIfNull(request);
                ValidateServiceIds(request.RegistrationServiceIds, nameof(request.RegistrationServiceIds));
                ValidateServiceIds(request.RequiredServiceIds, nameof(request.RequiredServiceIds));
                request = request with
                {
                    RegistrationServiceIds = request.RegistrationServiceIds.ToArray(),
                    RequiredServiceIds = request.RequiredServiceIds.ToArray(),
                };
                if (!IsValidVerifiedOwner(request.RegistrationOwner))
                    return new(null, "service_owner_forbidden");

                var inventory = await _authorizationPort.ReadUserServicesAsync(
                    request.AccessToken,
                    ct);
                if (!inventory.Succeeded)
                    return new(null, "nyxid_scope_plan_unavailable");

                var selectedServiceIds = request.RegistrationServiceIds
                    .Concat(request.RequiredServiceIds)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                var selectedServices = inventory.Value!.Services
                    .Where(service => selectedServiceIds.Contains(service.Id, StringComparer.Ordinal))
                    .ToArray();
                if (selectedServices.Length != selectedServiceIds.Length)
                    return new(null, "user_service_not_found");

                if (!ServicesMatchOwner(selectedServices, request.RegistrationOwner))
                    return new(null, "service_owner_forbidden");

                return new(new VerifiedChannelRegistrationServiceSelection(
                    request, inventory.Value.Services), string.Empty);
            }

            private async Task<ChannelRegistrationAuthorizationPlanningResult> PlanVerifiedAsync(
                VerifiedChannelRegistrationServiceSelection selection,
                CancellationToken ct)
            {
                var scopePlan = await _authorizationPort.PlanApiKeyScopeAsync(
                    selection.AccessToken,
                    selection.SelectedServiceIds,
                    selection.TargetOrganizationId,
                    ct);
                if (!scopePlan.Succeeded)
                    return Failed("nyxid_scope_plan_unavailable");

                var plan = scopePlan.Value!;
                if (!IsValidScopePlan(
                        plan,
                        selection.AuthenticatedActorId,
                        selection.Owner,
                        selection.SelectedServiceIds))
                    return Failed("nyxid_scope_plan_unavailable");

                return new ChannelRegistrationAuthorizationPlanningResult(
                    new VerifiedChannelRegistrationAuthorizationPlan(
                        selection.AuthenticatedActorId,
                        selection.Owner,
                        selection.TargetOrganizationId,
                        selection.RegistrationServiceIds,
                        plan.AllowedServiceIds.ToArray(),
                        plan.AllowedNodeIds.ToArray(),
                        plan.EvaluatedAtUtc,
                        plan.NormalizedGrantDigest),
                    string.Empty);
            }

            private static ChannelRegistrationAuthorizationPlanningResult Failed(string errorCode) =>
                new(null, errorCode);

            private static void ValidateServiceIds(
                IReadOnlyList<string> serviceIds,
                string parameterName)
            {
                ArgumentNullException.ThrowIfNull(serviceIds, parameterName);
                if (serviceIds.Any(static serviceId => !IsCanonicalIdentifier(serviceId)))
                    throw new ArgumentException("Service ids must be normalized values.", parameterName);
            }

            private static bool IsValidScopePlan(
                NyxIdApiKeyScopePlan plan,
                string authenticatedActorId,
                ChannelRegistrationKeyOwner owner,
                IReadOnlyList<string> selectedServiceIds)
            {
                var expectedOwner = owner.Kind switch
                {
                    ChannelRegistrationKeyOwnerKind.Personal => new NyxIdScopePlanPrincipal(
                        owner.Id,
                        NyxIdScopePlanPrincipalKind.Personal),
                    ChannelRegistrationKeyOwnerKind.Organization => new NyxIdScopePlanPrincipal(
                        owner.Id,
                        NyxIdScopePlanPrincipalKind.Organization),
                    _ => null,
                };
                if (expectedOwner is null ||
                    !string.Equals(
                        plan.Authority,
                        NyxIdApiAccessResponseParser.ScopePlanAuthority,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        plan.ContractVersion,
                        NyxIdApiAccessResponseParser.ScopePlanContractVersion,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        plan.PolicyVersion,
                        NyxIdApiAccessResponseParser.ScopePlanPolicyVersion,
                        StringComparison.Ordinal) ||
                    plan.AuthenticatedActor != new NyxIdScopePlanPrincipal(
                        authenticatedActorId,
                        NyxIdScopePlanPrincipalKind.Personal) ||
                    plan.IntendedKeyOwner != expectedOwner ||
                    !plan.Services.Select(static service => service.UserServiceId)
                        .SequenceEqual(selectedServiceIds, StringComparer.Ordinal) ||
                    !plan.AllowedServiceIds.SequenceEqual(selectedServiceIds, StringComparer.Ordinal) ||
                    plan.Services.Any(service => service.ResourceOwner != expectedOwner) ||
                    !HasValidNodeGrants(plan.Services, plan.AllowedNodeIds) ||
                    plan.EvaluatedAtUtc.Offset != TimeSpan.Zero ||
                    plan.EvaluatedAtUtc <= DateTimeOffset.UnixEpoch ||
                    !IsLowerSha256Digest(plan.NormalizedGrantDigest))
                {
                    return false;
                }

                return plan.Freshness == new NyxIdScopePlanFreshness(
                           NyxIdScopePlanFreshnessMode.MutationRevalidatedSnapshot,
                           "scope_plan_digest",
                           NyxIdScopePlanPostCreationDrift.FailClosed) &&
                       plan.Completeness == new NyxIdScopePlanCompleteness(
                           true,
                           true,
                           NyxIdScopePlanRouteCandidateBasis.ActiveConfiguredRoutes,
                           true);
            }

            private static bool HasValidNodeGrants(
                IReadOnlyList<NyxIdScopePlanServiceGrant> services,
                IReadOnlyList<string> allowedNodeIds)
            {
                if (!IsCanonicalIdentifierList(allowedNodeIds))
                    return false;

                var grantedNodeIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var service in services)
                {
                    var nodeGrant = service.NodeGrant;
                    if (nodeGrant.Kind == NyxIdScopePlanNodeGrantKind.NotRequired)
                    {
                        if (nodeGrant.NodeIds.Count != 0)
                            return false;
                        continue;
                    }

                    if (nodeGrant.Kind != NyxIdScopePlanNodeGrantKind.Required ||
                        nodeGrant.NodeIds.Count == 0 ||
                        !IsCanonicalIdentifierList(nodeGrant.NodeIds))
                    {
                        return false;
                    }

                    foreach (var nodeId in nodeGrant.NodeIds)
                        grantedNodeIds.Add(nodeId);
                }

                return grantedNodeIds.SetEquals(allowedNodeIds);
            }

            private static bool IsCanonicalIdentifierList(IReadOnlyList<string> values)
            {
                string? previous = null;
                foreach (var value in values)
                {
                    if (!IsCanonicalIdentifier(value) ||
                        previous is not null && StringComparer.Ordinal.Compare(previous, value) >= 0)
                    {
                        return false;
                    }
                    previous = value;
                }
                return true;
            }

            private static bool IsCanonicalIdentifier(string? value) =>
                !string.IsNullOrWhiteSpace(value) &&
                string.Equals(value, value.Trim(), StringComparison.Ordinal);

            private static bool IsLowerSha256Digest(string value)
            {
                if (value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal))
                    return false;

                foreach (var character in value.AsSpan(7))
                {
                    if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                        return false;
                }
                return true;
            }

            private static bool IsValidVerifiedOwner(
                VerifiedChannelRegistrationOwner? owner)
            {
                if (owner is null ||
                    !IsCanonicalIdentifier(owner.AuthenticatedActorId) ||
                    !IsCanonicalIdentifier(owner.KeyOwner.Id))
                {
                    return false;
                }

                return owner.KeyOwner.Kind switch
                {
                    ChannelRegistrationKeyOwnerKind.Personal =>
                        owner.TargetOrganizationId is null &&
                        string.Equals(
                            owner.AuthenticatedActorId,
                            owner.KeyOwner.Id,
                            StringComparison.Ordinal),
                    ChannelRegistrationKeyOwnerKind.Organization =>
                        !string.Equals(
                            owner.AuthenticatedActorId,
                            owner.KeyOwner.Id,
                            StringComparison.Ordinal) &&
                        string.Equals(
                            owner.TargetOrganizationId,
                            owner.KeyOwner.Id,
                            StringComparison.Ordinal),
                    _ => false,
                };
            }

            private static bool ServicesMatchOwner(
                IReadOnlyList<NyxIdUserService> services,
                VerifiedChannelRegistrationOwner owner)
            {
                if (services.Any(static service => !service.IsActive))
                    return false;

                return owner.KeyOwner.Kind switch
                {
                    ChannelRegistrationKeyOwnerKind.Personal => services.All(service =>
                        service.CredentialSource.Kind == NyxIdUserServiceCredentialSourceKind.Personal &&
                        service.CredentialSource.Allowed),
                    ChannelRegistrationKeyOwnerKind.Organization => services.All(service =>
                        service.CredentialSource.Kind == NyxIdUserServiceCredentialSourceKind.Organization &&
                        service.CredentialSource.Allowed &&
                        service.CredentialSource.OrganizationRole == NyxIdOrganizationRole.Admin &&
                        string.Equals(
                            service.CredentialSource.OrganizationId,
                            owner.KeyOwner.Id,
                            StringComparison.Ordinal)),
                    _ => false,
                };
            }
        }
    }
}
