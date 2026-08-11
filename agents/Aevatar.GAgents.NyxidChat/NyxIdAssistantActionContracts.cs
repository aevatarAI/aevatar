using System.Collections.Frozen;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.NyxidChat;

internal enum NyxIdAssistantActionCapabilityKind
{
    RequestProducer = 1,
    AdmissionParser = 2,
    AGUIMapper = 3,
    SafeResourcePredicate = 4,
    PostconditionVerifier = 5,
    EvidencePredicate = 6,
    AuthorityResolver = 7,
    RetryGenerationPolicy = 8,
}

internal enum NyxIdAssistantActionAuthorityRequirement
{
    BrowserOwnerSubject = 1,
    ExactKeyMutationAuthority = 2,
}

internal enum NyxIdAssistantActionEvidenceStrategy
{
    UserServiceCurrentState = 1,
    UserServiceAuthorization = 2,
    AgentApiKeyCurrentState = 3,
    KeyRotationLineage = 4,
}

internal enum NyxIdAssistantActionRetryStrategy
{
    StableBrowserActionRequest = 1,
    AuthorityBoundGeneration = 2,
}

internal enum NyxIdAssistantActionReplayPolicy
{
    StableActionRequestIdentity = 1,
    ExactMutationGeneration = 2,
}

internal sealed record NyxIdAssistantActionSemanticContract(
    string WireAction,
    NyxIdAssistantActionKind Action,
    FrozenSet<NyxIdAssistantActionParams.ParamsOneofCase> AllowedParamsCases,
    NyxIdChatSafeResourceRef.ResourceOneofCase CompletedResourceCase,
    NyxIdAssistantActionAuthorityRequirement AuthorityRequirement,
    NyxIdAssistantActionEvidenceStrategy EvidenceStrategy,
    NyxIdAssistantActionRetryStrategy RetryStrategy,
    NyxIdAssistantActionRisk RegistryRisk,
    bool RegistryRememberEligible,
    NyxIdAssistantActionReplayPolicy ReplayPolicy);

internal static class NyxIdAssistantActionSemanticContracts
{
    private static readonly FrozenDictionary<string, NyxIdAssistantActionSemanticContract>
        ByWireAction =
        new[]
        {
            Contract(
                "service.connect",
                NyxIdAssistantActionKind.ServiceConnect,
                NyxIdChatSafeResourceRef.ResourceOneofCase.UserService,
                NyxIdAssistantActionAuthorityRequirement.BrowserOwnerSubject,
                NyxIdAssistantActionEvidenceStrategy.UserServiceCurrentState,
                NyxIdAssistantActionRetryStrategy.StableBrowserActionRequest,
                rememberEligible: true,
                NyxIdAssistantActionReplayPolicy.StableActionRequestIdentity,
                NyxIdAssistantActionParams.ParamsOneofCase.CatalogServiceConnect,
                NyxIdAssistantActionParams.ParamsOneofCase.CustomServiceConnect),
            Contract(
                "service.reauthorize",
                NyxIdAssistantActionKind.ServiceReauthorize,
                NyxIdChatSafeResourceRef.ResourceOneofCase.UserService,
                NyxIdAssistantActionAuthorityRequirement.BrowserOwnerSubject,
                NyxIdAssistantActionEvidenceStrategy.UserServiceAuthorization,
                NyxIdAssistantActionRetryStrategy.StableBrowserActionRequest,
                rememberEligible: false,
                NyxIdAssistantActionReplayPolicy.StableActionRequestIdentity,
                NyxIdAssistantActionParams.ParamsOneofCase.ServiceReauthorize),
            Contract(
                "key.create",
                NyxIdAssistantActionKind.KeyCreate,
                NyxIdChatSafeResourceRef.ResourceOneofCase.Key,
                NyxIdAssistantActionAuthorityRequirement.ExactKeyMutationAuthority,
                NyxIdAssistantActionEvidenceStrategy.AgentApiKeyCurrentState,
                NyxIdAssistantActionRetryStrategy.AuthorityBoundGeneration,
                rememberEligible: false,
                NyxIdAssistantActionReplayPolicy.ExactMutationGeneration,
                NyxIdAssistantActionParams.ParamsOneofCase.KeyCreate),
            Contract(
                "key.rotate",
                NyxIdAssistantActionKind.KeyRotate,
                NyxIdChatSafeResourceRef.ResourceOneofCase.Key,
                NyxIdAssistantActionAuthorityRequirement.ExactKeyMutationAuthority,
                NyxIdAssistantActionEvidenceStrategy.KeyRotationLineage,
                NyxIdAssistantActionRetryStrategy.AuthorityBoundGeneration,
                rememberEligible: false,
                NyxIdAssistantActionReplayPolicy.ExactMutationGeneration,
                NyxIdAssistantActionParams.ParamsOneofCase.KeyRotate),
        }.ToFrozenDictionary(static contract => contract.WireAction, StringComparer.Ordinal);

    private static readonly FrozenDictionary<NyxIdAssistantActionKind, NyxIdAssistantActionSemanticContract>
        ByAction = ByWireAction.Values.ToFrozenDictionary(static contract => contract.Action);

    public static bool TryGet(
        string wireAction,
        out NyxIdAssistantActionSemanticContract contract) =>
        ByWireAction.TryGetValue(wireAction, out contract!);

    public static bool TryGet(
        NyxIdAssistantActionKind action,
        out NyxIdAssistantActionSemanticContract contract) =>
        ByAction.TryGetValue(action, out contract!);

    private static NyxIdAssistantActionSemanticContract Contract(
        string wireAction,
        NyxIdAssistantActionKind action,
        NyxIdChatSafeResourceRef.ResourceOneofCase completedResourceCase,
        NyxIdAssistantActionAuthorityRequirement authorityRequirement,
        NyxIdAssistantActionEvidenceStrategy evidenceStrategy,
        NyxIdAssistantActionRetryStrategy retryStrategy,
        bool rememberEligible,
        NyxIdAssistantActionReplayPolicy replayPolicy,
        params NyxIdAssistantActionParams.ParamsOneofCase[] allowedParamsCases) =>
        new(
            wireAction,
            action,
            allowedParamsCases.ToFrozenSet(),
            completedResourceCase,
            authorityRequirement,
            evidenceStrategy,
            retryStrategy,
            NyxIdAssistantActionRisk.Grant,
            rememberEligible,
            replayPolicy);
}

// One registration proves one real construction path. Admission and AGUI
// registrations separately cover every legal params variant for the action.
internal sealed record NyxIdAssistantActionRequestProducerRegistration(
    NyxIdAssistantActionKind Action,
    string WireAction,
    NyxIdAssistantActionParams.ParamsOneofCase ProducedParamsCase,
    Func<NyxIdAssistantActionParams, NyxIdAssistantActionParams> Producer,
    NyxIdAssistantActionParams ProbeInput);

internal sealed record NyxIdAssistantActionAdmissionParserRegistration(
    NyxIdAssistantActionKind Action,
    FrozenSet<NyxIdAssistantActionParams.ParamsOneofCase> ParamsCases,
    FrozenSet<NyxIdAssistantActionParamsSchemaVariant> ParamsSchemaVariants,
    Func<JsonElement, NyxIdAssistantActionParams> Parser,
    FrozenDictionary<NyxIdAssistantActionParams.ParamsOneofCase, string> ProbeParamsJson);

internal sealed record NyxIdAssistantActionAGUIMapperRegistration(
    NyxIdAssistantActionKind Action,
    FrozenSet<NyxIdAssistantActionParams.ParamsOneofCase> ParamsCases,
    Func<NyxIdChatActionRequestState, NyxIdAssistantActionRequestWirePayload?> Mapper);

internal sealed record NyxIdAssistantActionSafeResourcePredicateRegistration(
    NyxIdAssistantActionKind Action,
    NyxIdChatSafeResourceRef.ResourceOneofCase CompletedResourceCase,
    Func<
        NyxIdAssistantActionKind,
        NyxIdChatActionDisposition,
        NyxIdChatSafeResourceRef?,
        bool> Predicate);

internal delegate Task<NyxIdChatActionPostconditionResult>
    NyxIdAssistantActionPostconditionVerifier<TExpectation, TEvidence>(
        NyxIdActionPostconditionPort port,
        NyxIdChatActionPostconditionInput input,
        AgentToolExecutionContextPayload? transientToolContext,
        Func<TExpectation, TEvidence, bool> evidencePredicate,
        CancellationToken ct);

internal abstract record NyxIdAssistantActionPostconditionCapabilityRegistration(
    NyxIdAssistantActionKind Action,
    NyxIdAssistantActionEvidenceStrategy EvidenceStrategy,
    NyxIdChatActionPostconditionInput ProbeInput)
{
    internal abstract bool HasPostconditionVerifier { get; }
    internal abstract bool HasEvidencePredicate { get; }

    internal abstract Task<NyxIdChatActionPostconditionResult> VerifyAsync(
        NyxIdActionPostconditionPort port,
        NyxIdChatActionPostconditionInput input,
        AgentToolExecutionContextPayload? transientToolContext,
        CancellationToken ct);

    internal abstract bool PostconditionVerifierMatchesProbe();
    internal abstract bool EvidencePredicateMatchesProbe();
    internal abstract NyxIdAssistantActionPostconditionCapabilityRegistration
        WithoutPostconditionVerifier();
    internal abstract NyxIdAssistantActionPostconditionCapabilityRegistration
        WithoutEvidencePredicate();
}

internal sealed record NyxIdAssistantActionEvidencePredicateProbeCase<
    TExpectation,
    TEvidence>(
    TExpectation Expectation,
    TEvidence MatchingEvidence,
    TEvidence MismatchedEvidence);

internal sealed record NyxIdAssistantActionPostconditionCapabilityRegistration<
    TExpectation,
    TEvidence>(
    NyxIdAssistantActionKind Action,
    NyxIdAssistantActionEvidenceStrategy EvidenceStrategy,
    NyxIdAssistantActionPostconditionVerifier<TExpectation, TEvidence>? Verifier,
    Func<TExpectation, TEvidence, bool>? EvidencePredicate,
    NyxIdChatActionPostconditionInput ProbeInput,
    IReadOnlyList<
        NyxIdAssistantActionEvidencePredicateProbeCase<TExpectation, TEvidence>>
        EvidencePredicateProbeCases)
    : NyxIdAssistantActionPostconditionCapabilityRegistration(
        Action,
        EvidenceStrategy,
        ProbeInput)
{
    internal override bool HasPostconditionVerifier => Verifier is not null;
    internal override bool HasEvidencePredicate => EvidencePredicate is not null;

    internal override Task<NyxIdChatActionPostconditionResult> VerifyAsync(
        NyxIdActionPostconditionPort port,
        NyxIdChatActionPostconditionInput input,
        AgentToolExecutionContextPayload? transientToolContext,
        CancellationToken ct)
    {
        if (Verifier is null || EvidencePredicate is null)
            throw new InvalidOperationException("The postcondition capability is incomplete.");

        return Verifier(port, input, transientToolContext, EvidencePredicate, ct);
    }

    internal override bool PostconditionVerifierMatchesProbe()
    {
        if (Verifier is null || ProbeInput.Action != Action)
            return false;

        try
        {
            var verification = Verifier(
                new NyxIdActionPostconditionPort(null, null, TimeProvider.System),
                ProbeInput.Clone(),
                null,
                static (_, _) => false,
                CancellationToken.None);
            return verification.IsCompletedSuccessfully &&
                   !verification.Result.Verified &&
                   string.Equals(
                       verification.Result.FailureCode,
                       NyxIdActionPostconditionPort.UnavailableCode,
                       StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    internal override bool EvidencePredicateMatchesProbe()
    {
        if (EvidencePredicate is null || EvidencePredicateProbeCases.Count == 0)
            return false;

        try
        {
            return EvidencePredicateProbeCases.All(probe =>
                EvidencePredicate(probe.Expectation, probe.MatchingEvidence) &&
                !EvidencePredicate(probe.Expectation, probe.MismatchedEvidence));
        }
        catch
        {
            return false;
        }
    }

    internal override NyxIdAssistantActionPostconditionCapabilityRegistration
        WithoutPostconditionVerifier() =>
        this with { Verifier = null };

    internal override NyxIdAssistantActionPostconditionCapabilityRegistration
        WithoutEvidencePredicate() =>
        this with { EvidencePredicate = null };
}

internal readonly record struct NyxIdServiceConnectEvidenceExpectation(
    NyxIdAssistantActionParams Params,
    string? ExpectedUserServiceId);

internal readonly record struct NyxIdServiceReauthorizeEvidenceExpectation(
    NyxIdServiceReauthorizeParams Requested);

internal readonly record struct NyxIdKeyCreateEvidenceExpectation(
    NyxIdKeyCreateParams Requested,
    string ExpectedKeyId);

internal readonly record struct NyxIdKeyRotateEvidenceExpectation(
    NyxIdKeyRotateParams Requested,
    string ExpectedKeyId);

internal sealed record NyxIdAssistantActionAuthorityResolverRegistration(
    NyxIdAssistantActionKind Action,
    NyxIdAssistantActionAuthorityRequirement AuthorityRequirement,
    Func<string, string, bool> OwnerSubjectMatches);

internal sealed record NyxIdAssistantActionRetryGenerationPolicyRegistration(
    NyxIdAssistantActionKind Action,
    NyxIdAssistantActionRetryStrategy RetryStrategy,
    NyxIdAssistantActionReplayPolicy ReplayPolicy,
    Func<string, string[], string> StableIdentityBuilder);

internal sealed record NyxIdAssistantActionCapabilityReadinessSnapshot(
    NyxIdAssistantActionKind Action,
    string WireAction,
    FrozenSet<NyxIdAssistantActionCapabilityKind> MissingCapabilities)
{
    public bool Executable => MissingCapabilities.Count == 0;
}

internal sealed class NyxIdAssistantActionCapabilityRegistrations
{
    private readonly FrozenDictionary<NyxIdAssistantActionKind,
        NyxIdAssistantActionRequestProducerRegistration> _requestProducers;
    private readonly FrozenDictionary<NyxIdAssistantActionKind,
        NyxIdAssistantActionAdmissionParserRegistration> _admissionParsers;
    private readonly FrozenDictionary<NyxIdAssistantActionKind,
        NyxIdAssistantActionAGUIMapperRegistration> _aguiMappers;
    private readonly FrozenDictionary<NyxIdAssistantActionKind,
        NyxIdAssistantActionSafeResourcePredicateRegistration> _safeResourcePredicates;
    private readonly FrozenDictionary<NyxIdAssistantActionKind,
        NyxIdAssistantActionPostconditionCapabilityRegistration> _postconditionCapabilities;
    private readonly FrozenDictionary<NyxIdAssistantActionKind,
        NyxIdAssistantActionAuthorityResolverRegistration> _authorityResolvers;
    private readonly FrozenDictionary<NyxIdAssistantActionKind,
        NyxIdAssistantActionRetryGenerationPolicyRegistration> _retryGenerationPolicies;

    private NyxIdAssistantActionCapabilityRegistrations(
        IEnumerable<NyxIdAssistantActionRequestProducerRegistration> requestProducers,
        IEnumerable<NyxIdAssistantActionAdmissionParserRegistration> admissionParsers,
        IEnumerable<NyxIdAssistantActionAGUIMapperRegistration> aguiMappers,
        IEnumerable<NyxIdAssistantActionSafeResourcePredicateRegistration> safeResourcePredicates,
        IEnumerable<NyxIdAssistantActionPostconditionCapabilityRegistration> postconditionCapabilities,
        IEnumerable<NyxIdAssistantActionAuthorityResolverRegistration> authorityResolvers,
        IEnumerable<NyxIdAssistantActionRetryGenerationPolicyRegistration> retryGenerationPolicies)
    {
        _requestProducers = requestProducers.ToFrozenDictionary(static item => item.Action);
        _admissionParsers = admissionParsers.ToFrozenDictionary(static item => item.Action);
        _aguiMappers = aguiMappers.ToFrozenDictionary(static item => item.Action);
        _safeResourcePredicates = safeResourcePredicates.ToFrozenDictionary(static item => item.Action);
        _postconditionCapabilities =
            postconditionCapabilities.ToFrozenDictionary(static item => item.Action);
        _authorityResolvers = authorityResolvers.ToFrozenDictionary(static item => item.Action);
        _retryGenerationPolicies = retryGenerationPolicies.ToFrozenDictionary(static item => item.Action);
    }

    public static NyxIdAssistantActionCapabilityRegistrations Current { get; } = CreateCurrent();

    public NyxIdAssistantActionCapabilityRegistrations Without(
        NyxIdAssistantActionKind action,
        NyxIdAssistantActionCapabilityKind capability) =>
        capability switch
        {
            NyxIdAssistantActionCapabilityKind.RequestProducer => Copy(
                requestProducers: _requestProducers.Values.Where(item => item.Action != action)),
            NyxIdAssistantActionCapabilityKind.AdmissionParser => Copy(
                admissionParsers: _admissionParsers.Values.Where(item => item.Action != action)),
            NyxIdAssistantActionCapabilityKind.AGUIMapper => Copy(
                aguiMappers: _aguiMappers.Values.Where(item => item.Action != action)),
            NyxIdAssistantActionCapabilityKind.SafeResourcePredicate => Copy(
                safeResourcePredicates: _safeResourcePredicates.Values.Where(item => item.Action != action)),
            NyxIdAssistantActionCapabilityKind.PostconditionVerifier => Copy(
                postconditionCapabilities: _postconditionCapabilities.Values.Select(item =>
                    item.Action == action ? item.WithoutPostconditionVerifier() : item)),
            NyxIdAssistantActionCapabilityKind.EvidencePredicate => Copy(
                postconditionCapabilities: _postconditionCapabilities.Values.Select(item =>
                    item.Action == action ? item.WithoutEvidencePredicate() : item)),
            NyxIdAssistantActionCapabilityKind.AuthorityResolver => Copy(
                authorityResolvers: _authorityResolvers.Values.Where(item => item.Action != action)),
            NyxIdAssistantActionCapabilityKind.RetryGenerationPolicy => Copy(
                retryGenerationPolicies: _retryGenerationPolicies.Values.Where(item => item.Action != action)),
            _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, null),
        };

    public NyxIdAssistantActionCapabilityRegistrations With(
        NyxIdAssistantActionAdmissionParserRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(registration.ParamsCases);
        ArgumentNullException.ThrowIfNull(registration.ParamsSchemaVariants);
        ArgumentNullException.ThrowIfNull(registration.Parser);
        ArgumentNullException.ThrowIfNull(registration.ProbeParamsJson);
        return Copy(
            admissionParsers: _admissionParsers.Values
                .Where(item => item.Action != registration.Action)
                .Append(registration));
    }

    public NyxIdAssistantActionCapabilityRegistrations With(
        NyxIdAssistantActionSafeResourcePredicateRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(registration.Predicate);
        return Copy(
            safeResourcePredicates: _safeResourcePredicates.Values
                .Where(item => item.Action != registration.Action)
                .Append(registration));
    }

    public NyxIdAssistantActionCapabilityRegistrations WithPostconditionVerifier<
        TExpectation,
        TEvidence>(
        NyxIdAssistantActionKind action,
        NyxIdAssistantActionPostconditionVerifier<TExpectation, TEvidence> verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        if (!_postconditionCapabilities.TryGetValue(action, out var current))
            return this;

        var replacement =
            current is NyxIdAssistantActionPostconditionCapabilityRegistration<
                TExpectation,
                TEvidence> typed
                ? typed with { Verifier = verifier }
                : current.WithoutPostconditionVerifier();
        return Copy(
            postconditionCapabilities: _postconditionCapabilities.Values
                .Where(item => item.Action != action)
                .Append(replacement));
    }

    public NyxIdAssistantActionCapabilityRegistrations WithEvidencePredicate<
        TExpectation,
        TEvidence>(
        NyxIdAssistantActionKind action,
        Func<TExpectation, TEvidence, bool> evidencePredicate,
        IReadOnlyList<
            NyxIdAssistantActionEvidencePredicateProbeCase<TExpectation, TEvidence>> probeCases)
    {
        ArgumentNullException.ThrowIfNull(evidencePredicate);
        ArgumentNullException.ThrowIfNull(probeCases);
        if (!_postconditionCapabilities.TryGetValue(action, out var current))
            return this;

        var replacement =
            current is NyxIdAssistantActionPostconditionCapabilityRegistration<
                TExpectation,
                TEvidence> typed
                ? typed with
                {
                    EvidencePredicate = evidencePredicate,
                    EvidencePredicateProbeCases = probeCases,
                }
                : current.WithoutEvidencePredicate();
        return Copy(
            postconditionCapabilities: _postconditionCapabilities.Values
                .Where(item => item.Action != action)
                .Append(replacement));
    }

    public bool IsExecutable(
        NyxIdAssistantActionSemanticContract semantic,
        NyxIdAssistantActionRevisionDescriptorSnapshot descriptor) =>
        MissingCapabilities(semantic, descriptor).Count == 0;

    public IReadOnlyList<NyxIdAssistantActionCapabilityKind> MissingCapabilities(
        NyxIdAssistantActionSemanticContract semantic,
        NyxIdAssistantActionRevisionDescriptorSnapshot descriptor)
    {
        var missing = new List<NyxIdAssistantActionCapabilityKind>(8);
        if (!_requestProducers.TryGetValue(semantic.Action, out var producer) ||
            !string.Equals(producer.WireAction, semantic.WireAction, StringComparison.Ordinal) ||
            !semantic.AllowedParamsCases.Contains(producer.ProducedParamsCase) ||
            !RequestProducerMatches(producer))
        {
            missing.Add(NyxIdAssistantActionCapabilityKind.RequestProducer);
        }
        if (!_admissionParsers.TryGetValue(semantic.Action, out var parser) ||
            !parser.ParamsCases.SetEquals(semantic.AllowedParamsCases) ||
            !parser.ParamsSchemaVariants.Contains(descriptor.ParamsSchemaVariant) ||
            !ParserMatchesDeclaredCases(parser))
        {
            missing.Add(NyxIdAssistantActionCapabilityKind.AdmissionParser);
        }
        if (!_aguiMappers.TryGetValue(semantic.Action, out var mapper) ||
            !mapper.ParamsCases.SetEquals(semantic.AllowedParamsCases) ||
            !AGUIMapperMatches(mapper, semantic.WireAction))
        {
            missing.Add(NyxIdAssistantActionCapabilityKind.AGUIMapper);
        }
        if (!_safeResourcePredicates.TryGetValue(semantic.Action, out var resource) ||
            resource.CompletedResourceCase != semantic.CompletedResourceCase ||
            !SafeResourcePredicateMatches(resource))
        {
            missing.Add(NyxIdAssistantActionCapabilityKind.SafeResourcePredicate);
        }
        _postconditionCapabilities.TryGetValue(semantic.Action, out var postcondition);
        if (postcondition is null ||
            postcondition.EvidenceStrategy != semantic.EvidenceStrategy ||
            !postcondition.PostconditionVerifierMatchesProbe())
        {
            missing.Add(NyxIdAssistantActionCapabilityKind.PostconditionVerifier);
        }
        if (postcondition is null ||
            postcondition.EvidenceStrategy != semantic.EvidenceStrategy ||
            !postcondition.EvidencePredicateMatchesProbe())
        {
            missing.Add(NyxIdAssistantActionCapabilityKind.EvidencePredicate);
        }
        if (!_authorityResolvers.TryGetValue(semantic.Action, out var authority) ||
            authority.AuthorityRequirement != semantic.AuthorityRequirement ||
            !AuthorityResolverMatches(authority))
        {
            missing.Add(NyxIdAssistantActionCapabilityKind.AuthorityResolver);
        }
        if (!_retryGenerationPolicies.TryGetValue(semantic.Action, out var retry) ||
            retry.RetryStrategy != semantic.RetryStrategy ||
            retry.ReplayPolicy != semantic.ReplayPolicy ||
            !RetryGenerationPolicyMatches(retry))
        {
            missing.Add(NyxIdAssistantActionCapabilityKind.RetryGenerationPolicy);
        }
        return missing;
    }

    public bool TryGetAdmissionParser(
        NyxIdAssistantActionSemanticContract semantic,
        NyxIdAssistantActionRevisionDescriptorSnapshot descriptor,
        out Func<JsonElement, NyxIdAssistantActionParams> parser)
    {
        if (_admissionParsers.TryGetValue(semantic.Action, out var registration) &&
            registration.ParamsCases.SetEquals(semantic.AllowedParamsCases) &&
            registration.ParamsSchemaVariants.Contains(descriptor.ParamsSchemaVariant) &&
            ParserMatchesDeclaredCases(registration))
        {
            parser = registration.Parser;
            return true;
        }

        parser = null!;
        return false;
    }

    public bool TryGetRequestProducer(
        NyxIdAssistantActionSemanticContract semantic,
        out Func<NyxIdAssistantActionParams, NyxIdAssistantActionParams> producer)
    {
        if (_requestProducers.TryGetValue(semantic.Action, out var registration) &&
            string.Equals(registration.WireAction, semantic.WireAction, StringComparison.Ordinal) &&
            semantic.AllowedParamsCases.Contains(registration.ProducedParamsCase) &&
            RequestProducerMatches(registration))
        {
            producer = registration.Producer;
            return true;
        }

        producer = null!;
        return false;
    }

    public bool TryResolvePostconditionCapability(
        NyxIdAssistantActionKind action,
        out NyxIdAssistantActionPostconditionCapabilityRegistration registration)
    {
        if (_postconditionCapabilities.TryGetValue(action, out registration!) &&
            registration.HasPostconditionVerifier &&
            registration.HasEvidencePredicate)
        {
            return true;
        }

        registration = null!;
        return false;
    }

    private NyxIdAssistantActionCapabilityRegistrations Copy(
        IEnumerable<NyxIdAssistantActionRequestProducerRegistration>? requestProducers = null,
        IEnumerable<NyxIdAssistantActionAdmissionParserRegistration>? admissionParsers = null,
        IEnumerable<NyxIdAssistantActionAGUIMapperRegistration>? aguiMappers = null,
        IEnumerable<NyxIdAssistantActionSafeResourcePredicateRegistration>? safeResourcePredicates = null,
        IEnumerable<NyxIdAssistantActionPostconditionCapabilityRegistration>?
            postconditionCapabilities = null,
        IEnumerable<NyxIdAssistantActionAuthorityResolverRegistration>? authorityResolvers = null,
        IEnumerable<NyxIdAssistantActionRetryGenerationPolicyRegistration>? retryGenerationPolicies = null) =>
        new(
            requestProducers ?? _requestProducers.Values,
            admissionParsers ?? _admissionParsers.Values,
            aguiMappers ?? _aguiMappers.Values,
            safeResourcePredicates ?? _safeResourcePredicates.Values,
            postconditionCapabilities ?? _postconditionCapabilities.Values,
            authorityResolvers ?? _authorityResolvers.Values,
            retryGenerationPolicies ?? _retryGenerationPolicies.Values);

    private static bool ParserMatchesDeclaredCases(
        NyxIdAssistantActionAdmissionParserRegistration registration)
    {
        if (registration.ProbeParamsJson.Count != registration.ParamsCases.Count ||
            registration.ParamsCases.Any(paramsCase =>
                !registration.ProbeParamsJson.ContainsKey(paramsCase)))
        {
            return false;
        }

        try
        {
            foreach (var (expectedCase, paramsJson) in registration.ProbeParamsJson)
            {
                using var document = JsonDocument.Parse(paramsJson);
                if (registration.Parser(document.RootElement).ParamsCase != expectedCase)
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool RequestProducerMatches(
        NyxIdAssistantActionRequestProducerRegistration registration)
    {
        if (registration.ProbeInput.ParamsCase != registration.ProducedParamsCase)
            return false;

        try
        {
            return registration.Producer(registration.ProbeInput.Clone()).ParamsCase ==
                   registration.ProducedParamsCase;
        }
        catch
        {
            return false;
        }
    }

    private static bool AGUIMapperMatches(
        NyxIdAssistantActionAGUIMapperRegistration registration,
        string expectedWireAction)
    {
        try
        {
            return registration.ParamsCases.All(paramsCase =>
            {
                var mapped = registration.Mapper(new NyxIdChatActionRequestState
                {
                    SchemaVersion = NyxIdAssistantActionRegistry.SupportedSchemaVersion,
                    Action = registration.Action,
                    Params = ProbeActionParams(paramsCase),
                });
                return mapped is not null &&
                       string.Equals(mapped.Action, expectedWireAction, StringComparison.Ordinal) &&
                       WireParamsMatch(paramsCase, mapped.Params);
            });
        }
        catch
        {
            return false;
        }
    }

    private static bool AuthorityResolverMatches(
        NyxIdAssistantActionAuthorityResolverRegistration registration)
    {
        try
        {
            return registration.OwnerSubjectMatches("owner-probe", "owner-probe") &&
                   !registration.OwnerSubjectMatches("owner-probe", "owner-other");
        }
        catch
        {
            return false;
        }
    }

    private static bool RetryGenerationPolicyMatches(
        NyxIdAssistantActionRetryGenerationPolicyRegistration registration)
    {
        try
        {
            var first = registration.StableIdentityBuilder(
                "action",
                ["actor-probe", "turn-probe"]);
            var repeated = registration.StableIdentityBuilder(
                "action",
                ["actor-probe", "turn-probe"]);
            var changed = registration.StableIdentityBuilder(
                "action",
                ["actor-probe", "turn-other"]);
            return string.Equals(first, repeated, StringComparison.Ordinal) &&
                   !string.Equals(first, changed, StringComparison.Ordinal) &&
                   first.StartsWith("action-", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool SafeResourcePredicateMatches(
        NyxIdAssistantActionSafeResourcePredicateRegistration registration)
    {
        var matchingResource = ProbeResource(registration.CompletedResourceCase);
        var mismatchedResource = ProbeResource(
            registration.CompletedResourceCase ==
                NyxIdChatSafeResourceRef.ResourceOneofCase.UserService
                ? NyxIdChatSafeResourceRef.ResourceOneofCase.Key
                : NyxIdChatSafeResourceRef.ResourceOneofCase.UserService);
        try
        {
            return registration.Predicate(
                       registration.Action,
                       NyxIdChatActionDisposition.Completed,
                       matchingResource) &&
                   !registration.Predicate(
                       registration.Action,
                       NyxIdChatActionDisposition.Completed,
                       mismatchedResource);
        }
        catch
        {
            return false;
        }
    }

    private static NyxIdChatSafeResourceRef ProbeResource(
        NyxIdChatSafeResourceRef.ResourceOneofCase resourceCase) =>
        resourceCase switch
        {
            NyxIdChatSafeResourceRef.ResourceOneofCase.UserService => new NyxIdChatSafeResourceRef
            {
                UserService = new NyxIdChatUserServiceRef { UserServiceId = "us-probe" },
            },
            NyxIdChatSafeResourceRef.ResourceOneofCase.Key => new NyxIdChatSafeResourceRef
            {
                Key = new NyxIdChatKeyRef { KeyId = "key-probe" },
            },
            _ => new NyxIdChatSafeResourceRef(),
        };

    private static NyxIdAssistantActionParams ProbeActionParams(
        NyxIdAssistantActionParams.ParamsOneofCase paramsCase) =>
        paramsCase switch
        {
            NyxIdAssistantActionParams.ParamsOneofCase.CatalogServiceConnect =>
                new NyxIdAssistantActionParams
                {
                    CatalogServiceConnect = new NyxIdCatalogServiceConnectParams
                    {
                        ServiceSlug = "api-probe",
                    },
                },
            NyxIdAssistantActionParams.ParamsOneofCase.CustomServiceConnect =>
                new NyxIdAssistantActionParams
                {
                    CustomServiceConnect = new NyxIdCustomServiceConnectParams
                    {
                        Name = "Probe",
                        EndpointUrl = "https://probe.example.com/",
                        AuthMethod = "none",
                    },
                },
            NyxIdAssistantActionParams.ParamsOneofCase.ServiceReauthorize =>
                new NyxIdAssistantActionParams
                {
                    ServiceReauthorize = new NyxIdServiceReauthorizeParams
                    {
                        UserServiceId = "us-probe",
                        RequestedScopes = { "read" },
                    },
                },
            NyxIdAssistantActionParams.ParamsOneofCase.KeyCreate =>
                new NyxIdAssistantActionParams
                {
                    KeyCreate = new NyxIdKeyCreateParams
                    {
                        Name = "agent-probe",
                        Platform = "codex",
                        AllowedServiceIds = { "us-probe" },
                    },
                },
            NyxIdAssistantActionParams.ParamsOneofCase.KeyRotate =>
                new NyxIdAssistantActionParams
                {
                    KeyRotate = new NyxIdKeyRotateParams { KeyId = "key-probe" },
                },
            _ => new NyxIdAssistantActionParams(),
        };

    private static NyxIdChatActionPostconditionInput ProbePostconditionInput(
        NyxIdAssistantActionKind action)
    {
        var input = new NyxIdChatActionPostconditionInput
        {
            ScopeId = "scope-probe",
            OwnerSubject = "owner-probe",
            OriginTurnId = "turn-probe",
            ActionRequestId = "action-request-probe",
            Action = action,
            ReportedDisposition = NyxIdChatActionDisposition.Completed,
            RequestedAt = Timestamp.FromDateTimeOffset(
                DateTimeOffset.FromUnixTimeSeconds(1_700_000_000)),
        };

        switch (action)
        {
            case NyxIdAssistantActionKind.ServiceConnect:
                input.Params = ProbeActionParams(
                    NyxIdAssistantActionParams.ParamsOneofCase.CatalogServiceConnect);
                break;
            case NyxIdAssistantActionKind.ServiceReauthorize:
                input.Params = ProbeActionParams(
                    NyxIdAssistantActionParams.ParamsOneofCase.ServiceReauthorize);
                input.ResourceHint = new NyxIdChatSafeResourceRef
                {
                    UserService = new NyxIdChatUserServiceRef
                    {
                        UserServiceId = "us-probe",
                    },
                };
                break;
            case NyxIdAssistantActionKind.KeyCreate:
                input.Params = ProbeActionParams(
                    NyxIdAssistantActionParams.ParamsOneofCase.KeyCreate);
                input.ResourceHint = new NyxIdChatSafeResourceRef
                {
                    Key = new NyxIdChatKeyRef { KeyId = "key-probe" },
                };
                break;
            case NyxIdAssistantActionKind.KeyRotate:
                input.Params = ProbeActionParams(
                    NyxIdAssistantActionParams.ParamsOneofCase.KeyRotate);
                input.ResourceHint = new NyxIdChatSafeResourceRef
                {
                    Key = new NyxIdChatKeyRef { KeyId = "key-replacement" },
                };
                break;
        }

        return input;
    }

    private static bool WireParamsMatch(
        NyxIdAssistantActionParams.ParamsOneofCase paramsCase,
        NyxIdAssistantActionWireParams? wireParams) =>
        wireParams is not null && paramsCase switch
        {
            NyxIdAssistantActionParams.ParamsOneofCase.CatalogServiceConnect =>
                wireParams.ParamsCase ==
                NyxIdAssistantActionWireParams.ParamsOneofCase.CatalogService,
            NyxIdAssistantActionParams.ParamsOneofCase.CustomServiceConnect =>
                wireParams.ParamsCase ==
                NyxIdAssistantActionWireParams.ParamsOneofCase.CustomService,
            NyxIdAssistantActionParams.ParamsOneofCase.ServiceReauthorize =>
                wireParams.ParamsCase ==
                NyxIdAssistantActionWireParams.ParamsOneofCase.ServiceReauthorize,
            NyxIdAssistantActionParams.ParamsOneofCase.KeyCreate =>
                wireParams.ParamsCase == NyxIdAssistantActionWireParams.ParamsOneofCase.None &&
                !string.IsNullOrWhiteSpace(wireParams.KeyCreateName) &&
                !string.IsNullOrWhiteSpace(wireParams.KeyCreatePlatform) &&
                wireParams.KeyCreateAllowedServiceIds.Count > 0,
            NyxIdAssistantActionParams.ParamsOneofCase.KeyRotate =>
                wireParams.ParamsCase == NyxIdAssistantActionWireParams.ParamsOneofCase.None &&
                !string.IsNullOrWhiteSpace(wireParams.KeyRotateKeyId),
            _ => false,
        };

    private static NyxIdAssistantActionCapabilityRegistrations CreateCurrent()
    {
        var serviceConnectCases = Cases(
            NyxIdAssistantActionParams.ParamsOneofCase.CatalogServiceConnect,
            NyxIdAssistantActionParams.ParamsOneofCase.CustomServiceConnect);
        var serviceReauthorizeCases = Cases(
            NyxIdAssistantActionParams.ParamsOneofCase.ServiceReauthorize);
        var keyCreateCases = Cases(NyxIdAssistantActionParams.ParamsOneofCase.KeyCreate);
        var keyRotateCases = Cases(NyxIdAssistantActionParams.ParamsOneofCase.KeyRotate);
        var serviceConnectVariants = SchemaVariants(
            NyxIdAssistantActionParamsSchemaVariant.ServiceConnect);
        var serviceReauthorizeVariants = SchemaVariants(
            NyxIdAssistantActionParamsSchemaVariant.ServiceReauthorize);
        var keyCreateVariants = SchemaVariants(
            NyxIdAssistantActionParamsSchemaVariant.LeastScopeKeyCreate);
        var keyRotateVariants = SchemaVariants(
            NyxIdAssistantActionParamsSchemaVariant.KeyRotate);

        return new NyxIdAssistantActionCapabilityRegistrations(
            [
                new(
                    NyxIdAssistantActionKind.ServiceConnect,
                    "service.connect",
                    NyxIdAssistantActionParams.ParamsOneofCase.CatalogServiceConnect,
                    NyxIdAssistantActionRegistry.ProduceCatalogServiceConnect,
                    ProbeActionParams(
                        NyxIdAssistantActionParams.ParamsOneofCase.CatalogServiceConnect)),
                new(
                    NyxIdAssistantActionKind.KeyCreate,
                    "key.create",
                    NyxIdAssistantActionParams.ParamsOneofCase.KeyCreate,
                    NyxIdAssistantActionRegistry.ProduceKeyCreate,
                    ProbeActionParams(NyxIdAssistantActionParams.ParamsOneofCase.KeyCreate)),
                new(
                    NyxIdAssistantActionKind.KeyRotate,
                    "key.rotate",
                    NyxIdAssistantActionParams.ParamsOneofCase.KeyRotate,
                    NyxIdAssistantActionRegistry.ProduceKeyRotate,
                    ProbeActionParams(NyxIdAssistantActionParams.ParamsOneofCase.KeyRotate)),
            ],
            [
                new(
                    NyxIdAssistantActionKind.ServiceConnect,
                    serviceConnectCases,
                    serviceConnectVariants,
                    NyxIdAssistantActionRegistry.ParseServiceConnect,
                    ProbeParams(
                        (NyxIdAssistantActionParams.ParamsOneofCase.CatalogServiceConnect,
                            "{\"catalogService\":{\"serviceSlug\":\"api-probe\"}}"),
                        (NyxIdAssistantActionParams.ParamsOneofCase.CustomServiceConnect,
                            "{\"customService\":{\"name\":\"Probe\",\"endpointUrl\":\"https://probe.example.com\",\"authMethod\":\"none\"}}"))),
                new(
                    NyxIdAssistantActionKind.ServiceReauthorize,
                    serviceReauthorizeCases,
                    serviceReauthorizeVariants,
                    NyxIdAssistantActionRegistry.ParseServiceReauthorize,
                    ProbeParams(
                        (NyxIdAssistantActionParams.ParamsOneofCase.ServiceReauthorize,
                            "{\"userServiceId\":\"us-probe\",\"requestedScopes\":[\"read\"]}"))),
                new(
                    NyxIdAssistantActionKind.KeyCreate,
                    keyCreateCases,
                    keyCreateVariants,
                    NyxIdAssistantActionRegistry.ParseKeyCreate,
                    ProbeParams(
                        (NyxIdAssistantActionParams.ParamsOneofCase.KeyCreate,
                            "{\"name\":\"agent-probe\",\"platform\":\"codex\",\"allowedServiceIds\":[\"us-probe\"]}"))),
                new(
                    NyxIdAssistantActionKind.KeyRotate,
                    keyRotateCases,
                    keyRotateVariants,
                    NyxIdAssistantActionRegistry.ParseKeyRotate,
                    ProbeParams(
                        (NyxIdAssistantActionParams.ParamsOneofCase.KeyRotate,
                            "{\"keyId\":\"key-probe\"}"))),
            ],
            [
                new(NyxIdAssistantActionKind.ServiceConnect, serviceConnectCases,
                    NyxIdChatConversationAguiFrameBuilder.MapActionRequestWirePayload),
                new(NyxIdAssistantActionKind.ServiceReauthorize, serviceReauthorizeCases,
                    NyxIdChatConversationAguiFrameBuilder.MapActionRequestWirePayload),
                new(NyxIdAssistantActionKind.KeyCreate, keyCreateCases,
                    NyxIdChatConversationAguiFrameBuilder.MapActionRequestWirePayload),
                new(NyxIdAssistantActionKind.KeyRotate, keyRotateCases,
                    NyxIdChatConversationAguiFrameBuilder.MapActionRequestWirePayload),
            ],
            [
                new(NyxIdAssistantActionKind.ServiceConnect,
                    NyxIdChatSafeResourceRef.ResourceOneofCase.UserService,
                    NyxIdChatBrowserActions.ResourceMatchesAction),
                new(NyxIdAssistantActionKind.ServiceReauthorize,
                    NyxIdChatSafeResourceRef.ResourceOneofCase.UserService,
                    NyxIdChatBrowserActions.ResourceMatchesAction),
                new(NyxIdAssistantActionKind.KeyCreate,
                    NyxIdChatSafeResourceRef.ResourceOneofCase.Key,
                    NyxIdChatBrowserActions.ResourceMatchesAction),
                new(NyxIdAssistantActionKind.KeyRotate,
                    NyxIdChatSafeResourceRef.ResourceOneofCase.Key,
                    NyxIdChatBrowserActions.ResourceMatchesAction),
            ],
            [
                new NyxIdAssistantActionPostconditionCapabilityRegistration<
                    NyxIdServiceConnectEvidenceExpectation,
                    NyxIdAuthorizationServiceEvidence>(
                    NyxIdAssistantActionKind.ServiceConnect,
                    NyxIdAssistantActionEvidenceStrategy.UserServiceCurrentState,
                    NyxIdActionPostconditionPort.VerifyServiceConnectPostconditionAsync,
                    NyxIdActionPostconditionPort.ServiceConnectEvidenceMatches,
                    ProbePostconditionInput(NyxIdAssistantActionKind.ServiceConnect),
                    [
                        new(
                            new NyxIdServiceConnectEvidenceExpectation(
                                ProbeActionParams(
                                    NyxIdAssistantActionParams.ParamsOneofCase
                                        .CatalogServiceConnect),
                                null),
                            new NyxIdAuthorizationServiceEvidence
                            {
                                UserServiceId = "us-probe",
                                ServiceSlug = "api-probe",
                                Access = NyxIdAuthorizationAccess.Permitted,
                            },
                            new NyxIdAuthorizationServiceEvidence
                            {
                                UserServiceId = "us-probe",
                                ServiceSlug = "api-other",
                                Access = NyxIdAuthorizationAccess.Permitted,
                            }),
                        new(
                            new NyxIdServiceConnectEvidenceExpectation(
                                ProbeActionParams(
                                    NyxIdAssistantActionParams.ParamsOneofCase
                                        .CustomServiceConnect),
                                "us-probe"),
                            new NyxIdAuthorizationServiceEvidence
                            {
                                UserServiceId = "us-probe",
                                Access = NyxIdAuthorizationAccess.Permitted,
                            },
                            new NyxIdAuthorizationServiceEvidence
                            {
                                UserServiceId = "us-other",
                                Access = NyxIdAuthorizationAccess.Permitted,
                            }),
                    ]),
                new NyxIdAssistantActionPostconditionCapabilityRegistration<
                    NyxIdServiceReauthorizeEvidenceExpectation,
                    NyxIdUserServiceAuthorizationEvidence>(
                    NyxIdAssistantActionKind.ServiceReauthorize,
                    NyxIdAssistantActionEvidenceStrategy.UserServiceAuthorization,
                    NyxIdActionPostconditionPort.VerifyServiceReauthorizePostconditionAsync,
                    NyxIdActionPostconditionPort.ServiceReauthorizeEvidenceMatches,
                    ProbePostconditionInput(NyxIdAssistantActionKind.ServiceReauthorize),
                    [
                        new(
                            new NyxIdServiceReauthorizeEvidenceExpectation(
                                ProbeActionParams(
                                        NyxIdAssistantActionParams.ParamsOneofCase
                                            .ServiceReauthorize)
                                    .ServiceReauthorize),
                            new NyxIdUserServiceAuthorizationEvidence(
                                "us-probe",
                                "credential-probe",
                                true,
                                NyxIdUserServiceCredentialStatus.Active,
                                NyxIdOAuthConnectionStatus.Active,
                                ["read"],
                                DateTimeOffset.UnixEpoch),
                            new NyxIdUserServiceAuthorizationEvidence(
                                "us-probe",
                                "credential-probe",
                                true,
                                NyxIdUserServiceCredentialStatus.Active,
                                NyxIdOAuthConnectionStatus.Active,
                                [],
                                DateTimeOffset.UnixEpoch)),
                    ]),
                new NyxIdAssistantActionPostconditionCapabilityRegistration<
                    NyxIdKeyCreateEvidenceExpectation,
                    NyxIdAgentApiKeyEvidence>(
                    NyxIdAssistantActionKind.KeyCreate,
                    NyxIdAssistantActionEvidenceStrategy.AgentApiKeyCurrentState,
                    NyxIdActionPostconditionPort.VerifyKeyCreatePostconditionAsync,
                    NyxIdActionPostconditionPort.KeyCreateEvidenceMatches,
                    ProbePostconditionInput(NyxIdAssistantActionKind.KeyCreate),
                    [
                        new(
                            new NyxIdKeyCreateEvidenceExpectation(
                                ProbeActionParams(
                                        NyxIdAssistantActionParams.ParamsOneofCase.KeyCreate)
                                    .KeyCreate,
                                "key-probe"),
                            new NyxIdAgentApiKeyEvidence(
                                "key-probe",
                                "agent-probe",
                                ["proxy"],
                                "codex",
                                true,
                                ["us-probe"],
                                false,
                                [],
                                false,
                                DateTimeOffset.UnixEpoch,
                                null),
                            new NyxIdAgentApiKeyEvidence(
                                "key-probe",
                                "agent-probe",
                                ["proxy"],
                                "codex",
                                true,
                                ["us-probe"],
                                true,
                                [],
                                false,
                                DateTimeOffset.UnixEpoch,
                                null)),
                    ]),
                new NyxIdAssistantActionPostconditionCapabilityRegistration<
                    NyxIdKeyRotateEvidenceExpectation,
                    NyxIdAgentApiKeyEvidence>(
                    NyxIdAssistantActionKind.KeyRotate,
                    NyxIdAssistantActionEvidenceStrategy.KeyRotationLineage,
                    NyxIdActionPostconditionPort.VerifyKeyRotatePostconditionAsync,
                    NyxIdActionPostconditionPort.KeyRotateEvidenceMatches,
                    ProbePostconditionInput(NyxIdAssistantActionKind.KeyRotate),
                    [
                        new(
                            new NyxIdKeyRotateEvidenceExpectation(
                                ProbeActionParams(
                                        NyxIdAssistantActionParams.ParamsOneofCase.KeyRotate)
                                    .KeyRotate,
                                "key-replacement"),
                            new NyxIdAgentApiKeyEvidence(
                                "key-replacement",
                                "agent-probe",
                                ["proxy"],
                                "codex",
                                true,
                                ["us-probe"],
                                false,
                                [],
                                false,
                                DateTimeOffset.UnixEpoch,
                                new NyxIdApiKeyVersionEvidence(
                                    "key-probe",
                                    2,
                                    DateTimeOffset.UnixEpoch)),
                            new NyxIdAgentApiKeyEvidence(
                                "key-replacement",
                                "agent-probe",
                                ["proxy"],
                                "codex",
                                true,
                                ["us-probe"],
                                false,
                                [],
                                false,
                                DateTimeOffset.UnixEpoch,
                                new NyxIdApiKeyVersionEvidence(
                                    "key-other",
                                    2,
                                    DateTimeOffset.UnixEpoch))),
                    ]),
            ],
            [
                new(NyxIdAssistantActionKind.ServiceConnect,
                    NyxIdAssistantActionAuthorityRequirement.BrowserOwnerSubject,
                    NyxIdChatBrowserActions.OwnerSubjectsMatch),
            ],
            [
                new(NyxIdAssistantActionKind.ServiceConnect,
                    NyxIdAssistantActionRetryStrategy.StableBrowserActionRequest,
                    NyxIdAssistantActionReplayPolicy.StableActionRequestIdentity,
                    NyxIdChatBrowserActions.BuildStableIdentity),
            ]);
    }

    private static FrozenSet<NyxIdAssistantActionParams.ParamsOneofCase> Cases(
        params NyxIdAssistantActionParams.ParamsOneofCase[] values) =>
        values.ToFrozenSet();

    private static FrozenSet<NyxIdAssistantActionParamsSchemaVariant> SchemaVariants(
        params NyxIdAssistantActionParamsSchemaVariant[] values) =>
        values.ToFrozenSet();

    private static FrozenDictionary<NyxIdAssistantActionParams.ParamsOneofCase, string>
        ProbeParams(
            params (NyxIdAssistantActionParams.ParamsOneofCase ParamsCase, string Json)[] values) =>
        values.ToFrozenDictionary(static value => value.ParamsCase, static value => value.Json);
}
