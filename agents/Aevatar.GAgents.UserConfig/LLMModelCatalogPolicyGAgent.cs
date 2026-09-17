using System.Text;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;

namespace Aevatar.GAgents.UserConfig;

[GAgent("llm.model-catalog-policy")]
public sealed class LLMModelCatalogPolicyGAgent
    : GAgentBase<LLMModelCatalogPolicyGAgentState>, IProjectedActor
{
    public static string ProjectionKind => "llm-model-catalog-policy";

    [EventHandler(EndpointName = "replacePolicy")]
    public async Task HandleReplacePolicy(ReplaceLLMModelCatalogPolicyCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var mutationId = NormalizeRequired(
            command.MutationId,
            "mutation_id",
            LLMModelCatalogPolicyLimits.MaxMutationIdUtf8Bytes);
        var replacedEvent = BuildReplacedEvent(State, command, mutationId);
        var expectedActorId = LLMModelCatalogPolicyConventions.BuildActorId(
            replacedEvent.OwnerType,
            replacedEvent.ScopeId);
        if (!string.Equals(Id, expectedActorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"LLM model catalog policy actor '{Id}' does not match canonical identity '{expectedActorId}'.");
        }
        if (command.ExpectedStateVersion < 0)
            throw new InvalidOperationException("LLM model catalog policy expected_state_version must be non-negative.");
        if (string.Equals(State.LastMutationId, mutationId, StringComparison.Ordinal))
        {
            if (PolicyMatchesState(State, replacedEvent))
                return;

            throw new InvalidOperationException(
                $"LLM model catalog policy mutation_id '{mutationId}' was already used for a different policy.");
        }

        var currentVersion = EventSourcing?.CurrentVersion
            ?? throw new InvalidOperationException("LLM model catalog policy event sourcing is unavailable.");
        if (command.ExpectedStateVersion != currentVersion)
        {
            throw new InvalidOperationException(
                $"LLM model catalog policy expected_state_version {command.ExpectedStateVersion} " +
                $"does not match committed state version {currentVersion}.");
        }

        await PersistDomainEventAsync(replacedEvent);
    }

    protected override LLMModelCatalogPolicyGAgentState TransitionState(
        LLMModelCatalogPolicyGAgentState current,
        IMessage evt)
    {
        return StateTransitionMatcher
            .Match(current, evt)
            .On<LLMModelCatalogPolicyReplacedEvent>(ApplyPolicyReplaced)
            .OrCurrent();
    }

    public static LLMModelCatalogPolicyReplacedEvent BuildReplacedEvent(
        LLMModelCatalogPolicyGAgentState state,
        ReplaceLLMModelCatalogPolicyCommand command,
        string? normalizedMutationId = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(command);

        var ownerType = command.OwnerType;
        var scopeId = command.ScopeId?.Trim() ?? string.Empty;
        ValidateOwner(ownerType, scopeId, command.Mode);
        ValidateOwnerIsStable(state, ownerType, scopeId);

        var normalizedSources = NormalizeSources(ownerType, command.Mode, command.Sources);
        var evt = new LLMModelCatalogPolicyReplacedEvent
        {
            OwnerType = ownerType,
            ScopeId = scopeId,
            Mode = command.Mode,
            MutationId = normalizedMutationId ?? NormalizeRequired(
                command.MutationId,
                "mutation_id",
                LLMModelCatalogPolicyLimits.MaxMutationIdUtf8Bytes),
        };
        evt.Sources.AddRange(normalizedSources);
        return evt;
    }

    internal static LLMModelCatalogPolicyGAgentState ApplyPolicyReplaced(
        LLMModelCatalogPolicyGAgentState state,
        LLMModelCatalogPolicyReplacedEvent evt)
    {
        var updated = new LLMModelCatalogPolicyGAgentState
        {
            OwnerType = evt.OwnerType,
            ScopeId = evt.ScopeId,
            Mode = evt.Mode,
            LastMutationId = evt.MutationId,
        };
        updated.Sources.AddRange(evt.Sources.Select(static source => source.Clone()));
        return updated;
    }

    private static bool PolicyMatchesState(
        LLMModelCatalogPolicyGAgentState state,
        LLMModelCatalogPolicyReplacedEvent evt) =>
        state.OwnerType == evt.OwnerType &&
        string.Equals(state.ScopeId, evt.ScopeId, StringComparison.Ordinal) &&
        state.Mode == evt.Mode &&
        state.Sources.SequenceEqual(evt.Sources);

    private static void ValidateOwner(
        LLMModelCatalogPolicyOwnerType ownerType,
        string scopeId,
        LLMModelCatalogPolicyMode mode)
    {
        switch (ownerType)
        {
            case LLMModelCatalogPolicyOwnerType.Platform:
                if (scopeId.Length != 0)
                    throw new InvalidOperationException("Platform model catalog policy must not carry scope_id.");
                if (mode != LLMModelCatalogPolicyMode.Custom)
                    throw new InvalidOperationException("Platform model catalog policy mode must be custom.");
                break;
            case LLMModelCatalogPolicyOwnerType.Scope:
                if (scopeId.Length == 0)
                    throw new InvalidOperationException("Scope model catalog policy requires scope_id.");
                ValidateCanonicalText(
                    scopeId,
                    "scope_id",
                    LLMModelCatalogPolicyLimits.MaxScopeIdUtf8Bytes);
                if (mode is not (LLMModelCatalogPolicyMode.InheritPlatform or LLMModelCatalogPolicyMode.Custom))
                {
                    throw new InvalidOperationException(
                        "Scope model catalog policy mode must be inherit_platform or custom.");
                }
                break;
            default:
                throw new InvalidOperationException("Model catalog policy owner_type is required.");
        }
    }

    private static void ValidateOwnerIsStable(
        LLMModelCatalogPolicyGAgentState state,
        LLMModelCatalogPolicyOwnerType ownerType,
        string scopeId)
    {
        if (state.OwnerType == LLMModelCatalogPolicyOwnerType.Unspecified)
            return;

        if (state.OwnerType != ownerType ||
            !string.Equals(state.ScopeId, scopeId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Model catalog policy owner cannot change after initialization.");
        }
    }

    private static IReadOnlyList<LLMModelCatalogPolicySource> NormalizeSources(
        LLMModelCatalogPolicyOwnerType ownerType,
        LLMModelCatalogPolicyMode mode,
        IEnumerable<LLMModelCatalogPolicySource> sources)
    {
        var sourceList = sources?.ToArray() ?? [];
        if (sourceList.Length > LLMModelCatalogPolicyLimits.MaxSources)
        {
            throw new InvalidOperationException(
                $"Model catalog policy cannot contain more than {LLMModelCatalogPolicyLimits.MaxSources} sources.");
        }
        if (mode == LLMModelCatalogPolicyMode.InheritPlatform)
        {
            if (sourceList.Length != 0)
                throw new InvalidOperationException("inherit_platform policy must not carry sources.");
            return [];
        }

        var normalized = new List<LLMModelCatalogPolicySource>(sourceList.Length);
        var sourceIdentities = new HashSet<string>(StringComparer.Ordinal);
        var serviceSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var explicitModelCount = 0;
        foreach (var source in sourceList)
        {
            if (source?.Source is null)
                throw new InvalidOperationException("Model catalog policy source identity is required.");
            if (source.ExplicitModels is null)
                throw new InvalidOperationException("Model catalog policy explicit_models is required.");

            var normalizedReference = NormalizeSourceReference(ownerType, source.Source);
            var identityKey = $"{normalizedReference.SourceIdentityCase}:{ResolveSourceId(normalizedReference)}";
            if (!sourceIdentities.Add(identityKey))
                throw new InvalidOperationException("Model catalog policy source identities must be unique.");
            if (!serviceSlugs.Add(normalizedReference.ServiceSlugSnapshot))
            {
                throw new InvalidOperationException(
                    "Model catalog policy service slug snapshots must be unique.");
            }

            var normalizedModels = NormalizeExplicitModels(source.ExplicitModels);
            explicitModelCount += normalizedModels.UpstreamModelIds.Count;
            if (explicitModelCount > LLMModelCatalogPolicyLimits.MaxExplicitModelsPerPolicy)
            {
                throw new InvalidOperationException(
                    $"Model catalog policy cannot contain more than " +
                    $"{LLMModelCatalogPolicyLimits.MaxExplicitModelsPerPolicy} explicit model IDs.");
            }

            normalized.Add(new LLMModelCatalogPolicySource
            {
                Source = normalizedReference,
                ExplicitModels = normalizedModels,
            });
        }

        return normalized;
    }

    private static NyxIDModelSourceReference NormalizeSourceReference(
        LLMModelCatalogPolicyOwnerType ownerType,
        NyxIDModelSourceReference source)
    {
        var serviceSlugSnapshot = source.ServiceSlugSnapshot ?? string.Empty;
        if (!NyxIdServiceSlugPolicy.IsCanonical(serviceSlugSnapshot))
        {
            throw new InvalidOperationException(
                "service_slug_snapshot must be a canonical NyxID service slug.");
        }

        var normalized = new NyxIDModelSourceReference
        {
            ServiceSlugSnapshot = serviceSlugSnapshot,
        };

        switch (source.SourceIdentityCase)
        {
            case NyxIDModelSourceReference.SourceIdentityOneofCase.CatalogServiceId:
                if (ownerType == LLMModelCatalogPolicyOwnerType.Scope)
                {
                    throw new InvalidOperationException(
                        "Scope model catalog policy must reference an exact NyxID user service.");
                }
                normalized.CatalogServiceId = NormalizeRequired(
                    source.CatalogServiceId,
                    "catalog_service_id",
                    LLMModelCatalogPolicyLimits.MaxServiceIdentityUtf8Bytes);
                break;
            case NyxIDModelSourceReference.SourceIdentityOneofCase.UserServiceId:
                if (ownerType == LLMModelCatalogPolicyOwnerType.Platform)
                {
                    throw new InvalidOperationException(
                        "Platform model catalog policy cannot reference a scope-owned NyxID user service.");
                }
                normalized.UserServiceId = NormalizeRequired(
                    source.UserServiceId,
                    "user_service_id",
                    LLMModelCatalogPolicyLimits.MaxServiceIdentityUtf8Bytes);
                break;
            default:
                throw new InvalidOperationException("Model catalog policy source identity is required.");
        }

        return normalized;
    }

    private static ExplicitLLMModelIDs NormalizeExplicitModels(ExplicitLLMModelIDs explicitModels)
    {
        var modelIds = explicitModels.UpstreamModelIds;
        if (modelIds.Count > LLMModelCatalogPolicyLimits.MaxExplicitModelsPerSource)
        {
            throw new InvalidOperationException(
                $"A model source cannot contain more than " +
                $"{LLMModelCatalogPolicyLimits.MaxExplicitModelsPerSource} explicit model IDs.");
        }
        var normalizedIds = new List<string>(modelIds.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var modelId in modelIds)
        {
            var normalized = NormalizeRequired(
                modelId,
                "upstream_model_id",
                LLMSelectionPolicy.MaxModelIdUtf8Bytes);
            if (seen.Add(normalized))
                normalizedIds.Add(normalized);
        }
        if (normalizedIds.Count == 0)
            throw new InvalidOperationException("explicit_models requires at least one upstream_model_id.");

        var normalizedModels = new ExplicitLLMModelIDs();
        normalizedModels.UpstreamModelIds.AddRange(normalizedIds);
        return normalizedModels;
    }

    private static string ResolveSourceId(NyxIDModelSourceReference source) =>
        source.SourceIdentityCase switch
        {
            NyxIDModelSourceReference.SourceIdentityOneofCase.CatalogServiceId => source.CatalogServiceId,
            NyxIDModelSourceReference.SourceIdentityOneofCase.UserServiceId => source.UserServiceId,
            _ => string.Empty,
        };

    private static string NormalizeRequired(string? value, string fieldName, int maximumUtf8Bytes)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            throw new InvalidOperationException($"{fieldName} is required.");
        ValidateCanonicalText(normalized, fieldName, maximumUtf8Bytes);
        return normalized;
    }

    private static void ValidateCanonicalText(string value, string fieldName, int maximumUtf8Bytes)
    {
        if (value.Any(char.IsControl))
            throw new InvalidOperationException($"{fieldName} must not contain control characters.");
        if (Encoding.UTF8.GetByteCount(value) > maximumUtf8Bytes)
        {
            throw new InvalidOperationException(
                $"{fieldName} must be at most {maximumUtf8Bytes} UTF-8 bytes.");
        }
    }
}
