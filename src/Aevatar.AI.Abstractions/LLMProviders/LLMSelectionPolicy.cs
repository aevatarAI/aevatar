using System.Text;

namespace Aevatar.AI.Abstractions.LLMProviders;

public static class LLMSelectionPolicy
{
    public const string GatewayRoute = "/api/v1/llm/gateway/v1";
    public const int MaxModelIdUtf8Bytes = 256;
    public const int MaxModelsPerCatalog = 2_048;

    public static void ValidateSelection(LLMSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        if (selection.ModelSelection is null)
            throw new InvalidOperationException("LLM selection must include a model selection.");

        ValidateModelSelection(selection.ModelSelection);

        switch (selection.RouteKind)
        {
            case LLMRouteKind.Unspecified:
                RequireEmpty(selection.RouteValue, "Unspecified route value");
                RequireEmpty(selection.NyxIdUserServiceId, "Unspecified service ID");
                RequireEmpty(selection.ServiceSlugSnapshot, "Unspecified service slug");
                if (selection.ModelSelection.Kind != LLMModelSelectionKind.Unspecified)
                    throw new InvalidOperationException("Unspecified route requires an unspecified model selection.");
                return;
            case LLMRouteKind.Gateway:
                if (!string.Equals(selection.RouteValue, GatewayRoute, StringComparison.Ordinal))
                    throw new InvalidOperationException("Gateway route is not canonical.");
                RequireEmpty(selection.NyxIdUserServiceId, "Gateway service ID");
                RequireEmpty(selection.ServiceSlugSnapshot, "Gateway service slug");
                RequireSelectedModelKind(selection.ModelSelection.Kind);
                return;
            case LLMRouteKind.NyxIdUserService:
                ValidateIdentity(selection.NyxIdUserServiceId, "NyxID user service ID");
                ValidateServiceSlug(selection.ServiceSlugSnapshot);
                var expectedRoute = $"/api/v1/proxy/s/{selection.ServiceSlugSnapshot}";
                if (!string.Equals(selection.RouteValue, expectedRoute, StringComparison.Ordinal))
                    throw new InvalidOperationException("NyxID user service route is not canonical.");
                RequireSelectedModelKind(selection.ModelSelection.Kind);
                return;
            default:
                throw new InvalidOperationException("LLM route kind is unsupported.");
        }
    }

    public static void ValidateRouteTarget(LLMRouteTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        switch (target.SourceIdentityCase)
        {
            case LLMRouteTarget.SourceIdentityOneofCase.CatalogServiceId:
                ValidateIdentity(target.CatalogServiceId, "NyxID catalog service ID");
                ValidateServiceSlug(target.ServiceSlugSnapshot);
                return;
            case LLMRouteTarget.SourceIdentityOneofCase.UserServiceId:
                ValidateIdentity(target.UserServiceId, "NyxID user service ID");
                ValidateServiceSlug(target.ServiceSlugSnapshot);
                return;
            default:
                throw new InvalidOperationException("LLM route target kind is unsupported.");
        }
    }

    public static void ValidateCatalog(LLMModelCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        switch (catalog.Certainty)
        {
            case LLMModelCatalogCertainty.Enumerated:
                ValidateEnumeratedCatalog(catalog);
                return;
            case LLMModelCatalogCertainty.NotVerifiable:
            case LLMModelCatalogCertainty.Unavailable:
                if (catalog.ModelIds.Count != 0 || !string.IsNullOrEmpty(catalog.DefaultModelId))
                    throw new InvalidOperationException("A non-enumerated catalog cannot expose selectable model IDs.");
                if (catalog.DiagnosticKind == LLMModelCatalogDiagnosticKind.Unspecified)
                    throw new InvalidOperationException("A non-enumerated catalog requires a diagnostic.");
                return;
            default:
                throw new InvalidOperationException("LLM model catalog certainty is unsupported.");
        }
    }

    public static LLMModelCatalog NormalizeCatalog(
        IEnumerable<string?> rawModelIds,
        string? rawDefaultModelId,
        LLMModelCatalogDiagnosticKind emptyDiagnostic)
    {
        ArgumentNullException.ThrowIfNull(rawModelIds);

        var modelIds = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var rawModelId in rawModelIds)
        {
            ValidateModelId(rawModelId);
            modelIds.Add(rawModelId!);
            if (modelIds.Count > MaxModelsPerCatalog)
                throw new InvalidOperationException($"An LLM catalog cannot contain more than {MaxModelsPerCatalog} models.");
        }

        if (modelIds.Count == 0)
        {
            if (!string.IsNullOrEmpty(rawDefaultModelId))
                throw new InvalidOperationException("An empty LLM catalog cannot declare a default model.");
            if (emptyDiagnostic == LLMModelCatalogDiagnosticKind.Unspecified)
                throw new InvalidOperationException("An empty LLM catalog requires a diagnostic.");

            return new LLMModelCatalog
            {
                Certainty = LLMModelCatalogCertainty.NotVerifiable,
                DiagnosticKind = emptyDiagnostic,
            };
        }

        var defaultModelId = rawDefaultModelId ?? string.Empty;
        if (!string.IsNullOrEmpty(defaultModelId))
        {
            ValidateModelId(defaultModelId);
            if (!modelIds.Contains(defaultModelId))
                throw new InvalidOperationException("The default LLM model must be present in the exact catalog.");
        }

        var catalog = new LLMModelCatalog
        {
            Certainty = LLMModelCatalogCertainty.Enumerated,
            DefaultModelId = defaultModelId,
        };
        catalog.ModelIds.Add(modelIds);
        ValidateCatalog(catalog);
        return catalog;
    }

    public static bool IsExplicitModelEnumerated(
        LLMSelection selection,
        LLMModelCatalog catalog)
    {
        if (selection?.ModelSelection?.Kind != LLMModelSelectionKind.ExplicitModel ||
            catalog?.Certainty != LLMModelCatalogCertainty.Enumerated)
            return false;

        return catalog.ModelIds.Contains(selection.ModelSelection.ModelId, StringComparer.Ordinal);
    }

    public static string CompatibilityDefaultModel(LLMSelection selection)
    {
        ValidateSelection(selection);
        return selection.ModelSelection.Kind == LLMModelSelectionKind.ExplicitModel
            ? selection.ModelSelection.ModelId
            : string.Empty;
    }

    public static string CompatibilityRoute(LLMSelection selection)
    {
        ValidateSelection(selection);
        return selection.RouteKind == LLMRouteKind.Unspecified ? string.Empty : selection.RouteValue;
    }

    public static LLMSelection SystemDefaultSelection() => new()
    {
        ModelSelection = new LLMModelSelection
        {
            Kind = LLMModelSelectionKind.Unspecified,
        },
    };

    public static LLMSelectionPersistenceStatus ClassifyPersisted(
        LLMSelection? selection,
        string? legacyRoute,
        string? legacyModel)
    {
        if (selection is null)
        {
            return string.IsNullOrEmpty(legacyRoute) && string.IsNullOrEmpty(legacyModel)
                ? LLMSelectionPersistenceStatus.SystemDefault
                : LLMSelectionPersistenceStatus.LegacyRepairRequired;
        }

        try
        {
            ValidateSelection(selection);
        }
        catch (InvalidOperationException)
        {
            return LLMSelectionPersistenceStatus.LegacyRepairRequired;
        }

        if (selection.RouteKind != LLMRouteKind.Unspecified)
            return LLMSelectionPersistenceStatus.Ready;

        return string.IsNullOrEmpty(legacyRoute) && string.IsNullOrEmpty(legacyModel)
            ? LLMSelectionPersistenceStatus.SystemDefault
            : LLMSelectionPersistenceStatus.LegacyRepairRequired;
    }

    public static LLMControlContext ApplyTo(LLMControlContext current, LLMSelection selection)
    {
        ArgumentNullException.ThrowIfNull(current);
        ValidateSelection(selection);

        if (selection.RouteKind == LLMRouteKind.Unspecified)
            return current;

        var routeTarget = selection.RouteKind == LLMRouteKind.NyxIdUserService
            ? new LLMRouteTarget
            {
                UserServiceId = selection.NyxIdUserServiceId,
                ServiceSlugSnapshot = selection.ServiceSlugSnapshot,
            }
            : null;

        return current with
        {
            NyxIdRoutePreference = selection.RouteValue,
            RouteTarget = routeTarget,
            ModelOverride = selection.ModelSelection.Kind == LLMModelSelectionKind.ExplicitModel
                ? selection.ModelSelection.ModelId
                : current.ModelOverride,
        };
    }

    private static void ValidateEnumeratedCatalog(LLMModelCatalog catalog)
    {
        if (catalog.ModelIds.Count == 0 || catalog.ModelIds.Count > MaxModelsPerCatalog)
            throw new InvalidOperationException("An enumerated LLM catalog must contain a bounded non-empty model list.");

        string? previous = null;
        foreach (var modelId in catalog.ModelIds)
        {
            ValidateModelId(modelId);
            if (previous is not null && string.CompareOrdinal(previous, modelId) >= 0)
                throw new InvalidOperationException("An enumerated LLM catalog must be ordinal-sorted and distinct.");
            previous = modelId;
        }

        if (!string.IsNullOrEmpty(catalog.DefaultModelId))
        {
            ValidateModelId(catalog.DefaultModelId);
            if (!catalog.ModelIds.Contains(catalog.DefaultModelId, StringComparer.Ordinal))
                throw new InvalidOperationException("The default LLM model must be present in the exact catalog.");
        }
    }

    private static void ValidateModelSelection(LLMModelSelection modelSelection)
    {
        switch (modelSelection.Kind)
        {
            case LLMModelSelectionKind.Unspecified:
            case LLMModelSelectionKind.ProviderDefault:
                RequireEmpty(modelSelection.ModelId, "Non-explicit model ID");
                return;
            case LLMModelSelectionKind.ExplicitModel:
                ValidateModelId(modelSelection.ModelId);
                return;
            default:
                throw new InvalidOperationException("LLM model selection kind is unsupported.");
        }
    }

    private static void ValidateModelId(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId) ||
            !string.Equals(modelId, modelId.Trim(), StringComparison.Ordinal) ||
            modelId.Any(char.IsControl) ||
            Encoding.UTF8.GetByteCount(modelId) > MaxModelIdUtf8Bytes)
            throw new InvalidOperationException("LLM model ID is not canonical.");

        if (modelId.IndexOfAny(['*', '?', '[', ']', '{', '}']) >= 0)
            throw new InvalidOperationException("LLM model patterns are not supported.");
    }

    private static void ValidateIdentity(string value, string name)
    {
        if (string.IsNullOrEmpty(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl))
            throw new InvalidOperationException($"{name} is not canonical.");
    }

    private static void ValidateServiceSlug(string slug)
    {
        if (!NyxIdServiceSlugPolicy.IsCanonical(slug))
            throw new InvalidOperationException("NyxID service slug is not a canonical path segment.");
    }

    private static void RequireSelectedModelKind(LLMModelSelectionKind kind)
    {
        if (kind is not LLMModelSelectionKind.ProviderDefault and not LLMModelSelectionKind.ExplicitModel)
            throw new InvalidOperationException("A selected LLM route requires a provider-default or explicit model selection.");
    }

    private static void RequireEmpty(string value, string name)
    {
        if (!string.IsNullOrEmpty(value))
            throw new InvalidOperationException($"{name} must be empty.");
    }
}
