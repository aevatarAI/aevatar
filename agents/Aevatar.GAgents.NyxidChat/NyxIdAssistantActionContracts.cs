using System.Collections.Frozen;
using System.Text.Json;

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

internal sealed record NyxIdAssistantActionRequestProducerRegistration(
    NyxIdAssistantActionKind Action,
    string WireAction,
    Type ImplementationType);

internal sealed record NyxIdAssistantActionAdmissionParserRegistration(
    NyxIdAssistantActionKind Action,
    FrozenSet<NyxIdAssistantActionParams.ParamsOneofCase> ParamsCases,
    FrozenSet<NyxIdAssistantActionParamsSchemaVariant> ParamsSchemaVariants,
    Func<JsonElement, NyxIdAssistantActionParams> Parser,
    Type ImplementationType);

internal sealed record NyxIdAssistantActionAGUIMapperRegistration(
    NyxIdAssistantActionKind Action,
    FrozenSet<NyxIdAssistantActionParams.ParamsOneofCase> ParamsCases,
    Type ImplementationType);

internal sealed record NyxIdAssistantActionSafeResourcePredicateRegistration(
    NyxIdAssistantActionKind Action,
    NyxIdChatSafeResourceRef.ResourceOneofCase CompletedResourceCase,
    Type ImplementationType);

internal sealed record NyxIdAssistantActionPostconditionVerifierRegistration(
    NyxIdAssistantActionKind Action,
    NyxIdAssistantActionEvidenceStrategy EvidenceStrategy,
    Type ImplementationType);

internal sealed record NyxIdAssistantActionEvidencePredicateRegistration(
    NyxIdAssistantActionKind Action,
    NyxIdAssistantActionEvidenceStrategy EvidenceStrategy,
    Type ImplementationType);

internal sealed record NyxIdAssistantActionAuthorityResolverRegistration(
    NyxIdAssistantActionKind Action,
    NyxIdAssistantActionAuthorityRequirement AuthorityRequirement,
    Type ImplementationType);

internal sealed record NyxIdAssistantActionRetryGenerationPolicyRegistration(
    NyxIdAssistantActionKind Action,
    NyxIdAssistantActionRetryStrategy RetryStrategy,
    NyxIdAssistantActionReplayPolicy ReplayPolicy,
    Type ImplementationType);

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
        NyxIdAssistantActionPostconditionVerifierRegistration> _postconditionVerifiers;
    private readonly FrozenDictionary<NyxIdAssistantActionKind,
        NyxIdAssistantActionEvidencePredicateRegistration> _evidencePredicates;
    private readonly FrozenDictionary<NyxIdAssistantActionKind,
        NyxIdAssistantActionAuthorityResolverRegistration> _authorityResolvers;
    private readonly FrozenDictionary<NyxIdAssistantActionKind,
        NyxIdAssistantActionRetryGenerationPolicyRegistration> _retryGenerationPolicies;

    private NyxIdAssistantActionCapabilityRegistrations(
        IEnumerable<NyxIdAssistantActionRequestProducerRegistration> requestProducers,
        IEnumerable<NyxIdAssistantActionAdmissionParserRegistration> admissionParsers,
        IEnumerable<NyxIdAssistantActionAGUIMapperRegistration> aguiMappers,
        IEnumerable<NyxIdAssistantActionSafeResourcePredicateRegistration> safeResourcePredicates,
        IEnumerable<NyxIdAssistantActionPostconditionVerifierRegistration> postconditionVerifiers,
        IEnumerable<NyxIdAssistantActionEvidencePredicateRegistration> evidencePredicates,
        IEnumerable<NyxIdAssistantActionAuthorityResolverRegistration> authorityResolvers,
        IEnumerable<NyxIdAssistantActionRetryGenerationPolicyRegistration> retryGenerationPolicies)
    {
        _requestProducers = requestProducers.ToFrozenDictionary(static item => item.Action);
        _admissionParsers = admissionParsers.ToFrozenDictionary(static item => item.Action);
        _aguiMappers = aguiMappers.ToFrozenDictionary(static item => item.Action);
        _safeResourcePredicates = safeResourcePredicates.ToFrozenDictionary(static item => item.Action);
        _postconditionVerifiers = postconditionVerifiers.ToFrozenDictionary(static item => item.Action);
        _evidencePredicates = evidencePredicates.ToFrozenDictionary(static item => item.Action);
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
                postconditionVerifiers: _postconditionVerifiers.Values.Where(item => item.Action != action)),
            NyxIdAssistantActionCapabilityKind.EvidencePredicate => Copy(
                evidencePredicates: _evidencePredicates.Values.Where(item => item.Action != action)),
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
        ArgumentNullException.ThrowIfNull(registration.ImplementationType);
        return Copy(
            admissionParsers: _admissionParsers.Values
                .Where(item => item.Action != registration.Action)
                .Append(registration));
    }

    public NyxIdAssistantActionCapabilityRegistrations With(
        NyxIdAssistantActionSafeResourcePredicateRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(registration.ImplementationType);
        return Copy(
            safeResourcePredicates: _safeResourcePredicates.Values
                .Where(item => item.Action != registration.Action)
                .Append(registration));
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
            !string.Equals(producer.WireAction, semantic.WireAction, StringComparison.Ordinal))
        {
            missing.Add(NyxIdAssistantActionCapabilityKind.RequestProducer);
        }
        if (!_admissionParsers.TryGetValue(semantic.Action, out var parser) ||
            !parser.ParamsCases.SetEquals(semantic.AllowedParamsCases) ||
            !parser.ParamsSchemaVariants.Contains(descriptor.ParamsSchemaVariant))
        {
            missing.Add(NyxIdAssistantActionCapabilityKind.AdmissionParser);
        }
        if (!_aguiMappers.TryGetValue(semantic.Action, out var mapper) ||
            !mapper.ParamsCases.SetEquals(semantic.AllowedParamsCases))
        {
            missing.Add(NyxIdAssistantActionCapabilityKind.AGUIMapper);
        }
        if (!_safeResourcePredicates.TryGetValue(semantic.Action, out var resource) ||
            resource.CompletedResourceCase != semantic.CompletedResourceCase)
        {
            missing.Add(NyxIdAssistantActionCapabilityKind.SafeResourcePredicate);
        }
        if (!_postconditionVerifiers.TryGetValue(semantic.Action, out var verifier) ||
            verifier.EvidenceStrategy != semantic.EvidenceStrategy)
        {
            missing.Add(NyxIdAssistantActionCapabilityKind.PostconditionVerifier);
        }
        if (!_evidencePredicates.TryGetValue(semantic.Action, out var evidence) ||
            evidence.EvidenceStrategy != semantic.EvidenceStrategy)
        {
            missing.Add(NyxIdAssistantActionCapabilityKind.EvidencePredicate);
        }
        if (!_authorityResolvers.TryGetValue(semantic.Action, out var authority) ||
            authority.AuthorityRequirement != semantic.AuthorityRequirement)
        {
            missing.Add(NyxIdAssistantActionCapabilityKind.AuthorityResolver);
        }
        if (!_retryGenerationPolicies.TryGetValue(semantic.Action, out var retry) ||
            retry.RetryStrategy != semantic.RetryStrategy ||
            retry.ReplayPolicy != semantic.ReplayPolicy)
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
            registration.ParamsSchemaVariants.Contains(descriptor.ParamsSchemaVariant))
        {
            parser = registration.Parser;
            return true;
        }

        parser = null!;
        return false;
    }

    private NyxIdAssistantActionCapabilityRegistrations Copy(
        IEnumerable<NyxIdAssistantActionRequestProducerRegistration>? requestProducers = null,
        IEnumerable<NyxIdAssistantActionAdmissionParserRegistration>? admissionParsers = null,
        IEnumerable<NyxIdAssistantActionAGUIMapperRegistration>? aguiMappers = null,
        IEnumerable<NyxIdAssistantActionSafeResourcePredicateRegistration>? safeResourcePredicates = null,
        IEnumerable<NyxIdAssistantActionPostconditionVerifierRegistration>? postconditionVerifiers = null,
        IEnumerable<NyxIdAssistantActionEvidencePredicateRegistration>? evidencePredicates = null,
        IEnumerable<NyxIdAssistantActionAuthorityResolverRegistration>? authorityResolvers = null,
        IEnumerable<NyxIdAssistantActionRetryGenerationPolicyRegistration>? retryGenerationPolicies = null) =>
        new(
            requestProducers ?? _requestProducers.Values,
            admissionParsers ?? _admissionParsers.Values,
            aguiMappers ?? _aguiMappers.Values,
            safeResourcePredicates ?? _safeResourcePredicates.Values,
            postconditionVerifiers ?? _postconditionVerifiers.Values,
            evidencePredicates ?? _evidencePredicates.Values,
            authorityResolvers ?? _authorityResolvers.Values,
            retryGenerationPolicies ?? _retryGenerationPolicies.Values);

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
                    typeof(NyxIdAssistantActionRegistry)),
                new(
                    NyxIdAssistantActionKind.KeyCreate,
                    "key.create",
                    typeof(NyxIdAssistantActionRegistry)),
                new(
                    NyxIdAssistantActionKind.KeyRotate,
                    "key.rotate",
                    typeof(NyxIdAssistantActionRegistry)),
            ],
            [
                new(
                    NyxIdAssistantActionKind.ServiceConnect,
                    serviceConnectCases,
                    serviceConnectVariants,
                    NyxIdAssistantActionRegistry.ParseServiceConnect,
                    typeof(NyxIdAssistantActionRegistry)),
                new(
                    NyxIdAssistantActionKind.ServiceReauthorize,
                    serviceReauthorizeCases,
                    serviceReauthorizeVariants,
                    NyxIdAssistantActionRegistry.ParseServiceReauthorize,
                    typeof(NyxIdAssistantActionRegistry)),
                new(
                    NyxIdAssistantActionKind.KeyCreate,
                    keyCreateCases,
                    keyCreateVariants,
                    NyxIdAssistantActionRegistry.ParseKeyCreate,
                    typeof(NyxIdAssistantActionRegistry)),
                new(
                    NyxIdAssistantActionKind.KeyRotate,
                    keyRotateCases,
                    keyRotateVariants,
                    NyxIdAssistantActionRegistry.ParseKeyRotate,
                    typeof(NyxIdAssistantActionRegistry)),
            ],
            [
                new(NyxIdAssistantActionKind.ServiceConnect, serviceConnectCases,
                    typeof(NyxIdChatConversationAguiFrameBuilder)),
                new(NyxIdAssistantActionKind.ServiceReauthorize, serviceReauthorizeCases,
                    typeof(NyxIdChatConversationAguiFrameBuilder)),
                new(NyxIdAssistantActionKind.KeyCreate, keyCreateCases,
                    typeof(NyxIdChatConversationAguiFrameBuilder)),
                new(NyxIdAssistantActionKind.KeyRotate, keyRotateCases,
                    typeof(NyxIdChatConversationAguiFrameBuilder)),
            ],
            [
                new(NyxIdAssistantActionKind.ServiceConnect,
                    NyxIdChatSafeResourceRef.ResourceOneofCase.UserService,
                    typeof(NyxIdChatBrowserActions)),
                new(NyxIdAssistantActionKind.ServiceReauthorize,
                    NyxIdChatSafeResourceRef.ResourceOneofCase.UserService,
                    typeof(NyxIdChatBrowserActions)),
                new(NyxIdAssistantActionKind.KeyCreate,
                    NyxIdChatSafeResourceRef.ResourceOneofCase.Key,
                    typeof(NyxIdChatBrowserActions)),
                new(NyxIdAssistantActionKind.KeyRotate,
                    NyxIdChatSafeResourceRef.ResourceOneofCase.Key,
                    typeof(NyxIdChatBrowserActions)),
            ],
            [
                new(NyxIdAssistantActionKind.ServiceConnect,
                    NyxIdAssistantActionEvidenceStrategy.UserServiceCurrentState,
                    typeof(NyxIdActionPostconditionPort)),
                new(NyxIdAssistantActionKind.ServiceReauthorize,
                    NyxIdAssistantActionEvidenceStrategy.UserServiceAuthorization,
                    typeof(NyxIdActionPostconditionPort)),
                new(NyxIdAssistantActionKind.KeyCreate,
                    NyxIdAssistantActionEvidenceStrategy.AgentApiKeyCurrentState,
                    typeof(NyxIdActionPostconditionPort)),
                new(NyxIdAssistantActionKind.KeyRotate,
                    NyxIdAssistantActionEvidenceStrategy.KeyRotationLineage,
                    typeof(NyxIdActionPostconditionPort)),
            ],
            [
                new(NyxIdAssistantActionKind.ServiceConnect,
                    NyxIdAssistantActionEvidenceStrategy.UserServiceCurrentState,
                    typeof(NyxIdActionPostconditionPort)),
                new(NyxIdAssistantActionKind.ServiceReauthorize,
                    NyxIdAssistantActionEvidenceStrategy.UserServiceAuthorization,
                    typeof(NyxIdActionPostconditionPort)),
                new(NyxIdAssistantActionKind.KeyCreate,
                    NyxIdAssistantActionEvidenceStrategy.AgentApiKeyCurrentState,
                    typeof(NyxIdActionPostconditionPort)),
                new(NyxIdAssistantActionKind.KeyRotate,
                    NyxIdAssistantActionEvidenceStrategy.KeyRotationLineage,
                    typeof(NyxIdActionPostconditionPort)),
            ],
            [
                new(NyxIdAssistantActionKind.ServiceConnect,
                    NyxIdAssistantActionAuthorityRequirement.BrowserOwnerSubject,
                    typeof(NyxIdChatBrowserActions)),
            ],
            [
                new(NyxIdAssistantActionKind.ServiceConnect,
                    NyxIdAssistantActionRetryStrategy.StableBrowserActionRequest,
                    NyxIdAssistantActionReplayPolicy.StableActionRequestIdentity,
                    typeof(NyxIdChatBrowserActions)),
            ]);
    }

    private static FrozenSet<NyxIdAssistantActionParams.ParamsOneofCase> Cases(
        params NyxIdAssistantActionParams.ParamsOneofCase[] values) =>
        values.ToFrozenSet();

    private static FrozenSet<NyxIdAssistantActionParamsSchemaVariant> SchemaVariants(
        params NyxIdAssistantActionParamsSchemaVariant[] values) =>
        values.ToFrozenSet();
}
