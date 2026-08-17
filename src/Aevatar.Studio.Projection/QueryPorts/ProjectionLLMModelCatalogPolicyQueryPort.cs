using System.Text;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.UserConfig;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Projection.ReadModels;
using ApplicationPolicyMode = Aevatar.Studio.Application.Studio.Abstractions.LLMModelCatalogPolicyMode;
using ApplicationPolicySource = Aevatar.Studio.Application.Studio.Abstractions.LLMModelCatalogPolicySource;
using ProtoPolicyMode = Aevatar.GAgents.UserConfig.LLMModelCatalogPolicyMode;

namespace Aevatar.Studio.Projection.QueryPorts;

public sealed class ProjectionLLMModelCatalogPolicyQueryPort : ILLMModelCatalogPolicyQueryPort
{
    private readonly IProjectionDocumentReader<LLMModelCatalogPolicyCurrentStateDocument, string>
        _documentReader;

    public ProjectionLLMModelCatalogPolicyQueryPort(
        IProjectionDocumentReader<LLMModelCatalogPolicyCurrentStateDocument, string> documentReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
    }

    public async Task<LLMModelCatalogPolicySnapshot?> GetAsync(
        LLMModelCatalogPolicyOwner owner,
        CancellationToken ct = default)
    {
        var actorId = LLMModelCatalogPolicyActorIdMapper.Build(owner);
        var document = await _documentReader.GetAsync(actorId, ct);
        if (document is null)
            return null;

        var projectedOwner = MapOwner(document);
        if (projectedOwner != owner)
        {
            throw new InvalidOperationException(
                $"Model catalog policy document '{document.Id}' owner does not match its query key.");
        }

        ValidateCommittedDocument(document, actorId);
        var mode = MapMode(document.Mode);
        ValidateOwnerMode(projectedOwner, mode, document.Sources.Count);
        var sources = document.Sources
            .Select(source => MapSource(projectedOwner.Kind, source))
            .ToArray();
        ValidateSources(sources);

        return new LLMModelCatalogPolicySnapshot(
            Owner: projectedOwner,
            Mode: mode,
            Sources: sources,
            StateVersion: document.StateVersion,
            UpdatedAtUtc: document.UpdatedAt!.ToDateTimeOffset(),
            LastMutationId: document.LastMutationId);
    }

    private static LLMModelCatalogPolicyOwner MapOwner(
        LLMModelCatalogPolicyCurrentStateDocument document) => document.OwnerType switch
        {
            LLMModelCatalogPolicyOwnerType.Platform when string.IsNullOrEmpty(document.ScopeId) =>
                LLMModelCatalogPolicyOwner.Platform,
            LLMModelCatalogPolicyOwnerType.Scope when IsCanonicalText(
                document.ScopeId,
                LLMModelCatalogPolicyLimits.MaxScopeIdUtf8Bytes,
                allowEmpty: false) =>
                LLMModelCatalogPolicyOwner.ForScope(document.ScopeId),
            _ => throw new InvalidOperationException("Projected model catalog policy owner is invalid."),
        };

    private static ApplicationPolicyMode MapMode(ProtoPolicyMode mode) => mode switch
    {
        ProtoPolicyMode.InheritPlatform => ApplicationPolicyMode.InheritPlatform,
        ProtoPolicyMode.Custom => ApplicationPolicyMode.Custom,
        _ => throw new InvalidOperationException("Projected model catalog policy mode is invalid."),
    };

    private static ApplicationPolicySource MapSource(
        LLMModelCatalogPolicyOwnerKind ownerKind,
        Aevatar.GAgents.UserConfig.LLMModelCatalogPolicySource source)
    {
        if (source.Source is null || source.ExplicitModels is null)
            throw new InvalidOperationException("Projected model catalog policy source is incomplete.");

        LLMModelSourceIdentity identity = (ownerKind, source.Source.SourceIdentityCase) switch
        {
            (LLMModelCatalogPolicyOwnerKind.Platform,
                NyxIDModelSourceReference.SourceIdentityOneofCase.CatalogServiceId) =>
                new NyxIDCatalogServiceModelSourceIdentity(source.Source.CatalogServiceId),
            (LLMModelCatalogPolicyOwnerKind.Scope,
                NyxIDModelSourceReference.SourceIdentityOneofCase.UserServiceId) =>
                new NyxIDUserServiceModelSourceIdentity(source.Source.UserServiceId),
            _ => throw new InvalidOperationException(
                "Projected model catalog policy source identity does not match its owner."),
        };

        if (!IsCanonicalText(
                identity.ServiceId,
                LLMModelCatalogPolicyLimits.MaxServiceIdentityUtf8Bytes,
                allowEmpty: false))
            throw new InvalidOperationException("Projected model catalog policy source identity is invalid.");

        var selection = MapExplicitModels(source.ExplicitModels.UpstreamModelIds);

        var slug = source.Source.ServiceSlugSnapshot;
        if (!IsCanonicalText(
                slug,
                LLMModelCatalogPolicyLimits.MaxServiceSlugUtf8Bytes,
                allowEmpty: true))
            throw new InvalidOperationException("Projected model catalog policy service slug snapshot is invalid.");

        return new ApplicationPolicySource(
            identity,
            string.IsNullOrEmpty(slug) ? null : slug,
            selection);
    }

    private static ExplicitLLMModels MapExplicitModels(IEnumerable<string> upstreamModelIds)
    {
        var modelIds = upstreamModelIds.ToArray();
        if (modelIds.Length is 0 or > LLMModelCatalogPolicyLimits.MaxExplicitModelsPerSource ||
            modelIds.Any(static modelId => !IsCanonicalText(
                modelId,
                LLMSelectionPolicy.MaxModelIdUtf8Bytes,
                allowEmpty: false)) ||
            modelIds.Distinct(StringComparer.Ordinal).Count() != modelIds.Length)
        {
            throw new InvalidOperationException(
                "Projected model catalog policy explicit model selection is invalid.");
        }

        return new ExplicitLLMModels(modelIds);
    }

    private static void ValidateCommittedDocument(
        LLMModelCatalogPolicyCurrentStateDocument document,
        string actorId)
    {
        if (!string.Equals(document.Id, actorId, StringComparison.Ordinal) ||
            !string.Equals(document.ActorId, actorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Projected model catalog policy document identity is invalid.");
        }
        if (document.StateVersion <= 0 ||
            string.IsNullOrWhiteSpace(document.LastEventId) ||
            document.UpdatedAt is null ||
            !IsCanonicalText(
                document.LastMutationId,
                LLMModelCatalogPolicyLimits.MaxMutationIdUtf8Bytes,
                allowEmpty: false))
        {
            throw new InvalidOperationException(
                "Projected model catalog policy committed state evidence is incomplete.");
        }
    }

    private static void ValidateOwnerMode(
        LLMModelCatalogPolicyOwner owner,
        ApplicationPolicyMode mode,
        int sourceCount)
    {
        if (owner.Kind == LLMModelCatalogPolicyOwnerKind.Platform &&
            mode != ApplicationPolicyMode.Custom)
        {
            throw new InvalidOperationException(
                "Projected platform model catalog policy mode must be custom.");
        }
        if (owner.Kind == LLMModelCatalogPolicyOwnerKind.Scope &&
            mode == ApplicationPolicyMode.InheritPlatform &&
            sourceCount != 0)
        {
            throw new InvalidOperationException(
                "Projected inherited scope model catalog policy must not contain sources.");
        }
    }

    private static void ValidateSources(IReadOnlyList<ApplicationPolicySource> sources)
    {
        if (sources.Count > LLMModelCatalogPolicyLimits.MaxSources)
            throw new InvalidOperationException("Projected model catalog policy contains too many sources.");

        var identities = new HashSet<string>(StringComparer.Ordinal);
        var serviceSlugs = new HashSet<string>(StringComparer.Ordinal);
        var explicitModelCount = 0;
        foreach (var source in sources)
        {
            var identityKey = $"{source.SourceIdentity.GetType().Name}:{source.SourceIdentity.ServiceId}";
            if (!identities.Add(identityKey))
                throw new InvalidOperationException("Projected model catalog policy contains duplicate sources.");
            if (!NyxIdServiceSlugPolicy.IsCanonical(source.ServiceSlugSnapshot) ||
                !serviceSlugs.Add(source.ServiceSlugSnapshot!))
            {
                throw new InvalidOperationException(
                    "Projected model catalog policy contains an invalid or duplicate service slug snapshot.");
            }

            explicitModelCount += source.ModelSelection.UpstreamModelIds.Count;
        }

        if (explicitModelCount > LLMModelCatalogPolicyLimits.MaxExplicitModelsPerPolicy)
        {
            throw new InvalidOperationException(
                "Projected model catalog policy contains too many explicit model IDs.");
        }
    }

    private static bool IsCanonicalText(string value, int maximumUtf8Bytes, bool allowEmpty)
    {
        if (value.Length == 0)
            return allowEmpty;

        return !string.IsNullOrWhiteSpace(value) &&
               string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
               !value.Any(char.IsControl) &&
               Encoding.UTF8.GetByteCount(value) <= maximumUtf8Bytes;
    }
}
