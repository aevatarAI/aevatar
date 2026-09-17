using System.Text;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Application.Studio.Services;

internal sealed class LLMModelCatalogPolicyApplicationService : ILLMModelCatalogPolicyApplicationService
{
    private readonly ILLMModelCatalogPolicyQueryPort _queryPort;
    private readonly ILLMModelCatalogPolicyCommandPort _commandPort;
    private readonly INyxIdModelSourceInventoryPort _inventoryPort;
    private readonly INyxIdModelDiscoveryPort _modelDiscoveryPort;

    public LLMModelCatalogPolicyApplicationService(
        ILLMModelCatalogPolicyQueryPort queryPort,
        ILLMModelCatalogPolicyCommandPort commandPort,
        INyxIdModelSourceInventoryPort inventoryPort,
        INyxIdModelDiscoveryPort modelDiscoveryPort)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _commandPort = commandPort ?? throw new ArgumentNullException(nameof(commandPort));
        _inventoryPort = inventoryPort ?? throw new ArgumentNullException(nameof(inventoryPort));
        _modelDiscoveryPort = modelDiscoveryPort ?? throw new ArgumentNullException(nameof(modelDiscoveryPort));
    }

    public async Task<LLMModelCatalogView> GetScopeAsync(
        string scopeId,
        CancellationToken ct = default)
    {
        var owner = LLMModelCatalogPolicyOwner.ForScope(ValidateScopeId(scopeId));
        try
        {
            var scopePolicy = await _queryPort.GetAsync(owner, ct).ConfigureAwait(false);
            var platformPolicy = scopePolicy?.Mode == LLMModelCatalogPolicyMode.Custom
                ? null
                : await _queryPort
                    .GetAsync(LLMModelCatalogPolicyOwner.Platform, ct)
                    .ConfigureAwait(false);
            return BuildScopeView(owner, scopePolicy, platformPolicy);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Unavailable(
                "MODEL_CATALOG_READ_UNAVAILABLE",
                "The model catalog read model is temporarily unavailable.",
                ex);
        }
    }

    public async Task<LLMModelCatalogView> GetPlatformAsync(CancellationToken ct = default)
    {
        try
        {
            var policy = await _queryPort
                .GetAsync(LLMModelCatalogPolicyOwner.Platform, ct)
                .ConfigureAwait(false);
            return BuildPlatformView(policy);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Unavailable(
                "MODEL_CATALOG_READ_UNAVAILABLE",
                "The model catalog read model is temporarily unavailable.",
                ex);
        }
    }

    public Task<UserConfigSaveReceipt> ReplaceScopeAsync(
        string scopeId,
        ReplaceScopeLLMModelCatalogIntent intent,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var owner = LLMModelCatalogPolicyOwner.ForScope(ValidateScopeId(scopeId));
        var command = ParseScopeReplaceIntent(owner, intent);
        return DispatchAsync(command, ct);
    }

    public Task<UserConfigSaveReceipt> ResetScopeAsync(
        string scopeId,
        LLMModelCatalogResetIntent intent,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var owner = LLMModelCatalogPolicyOwner.ForScope(ValidateScopeId(scopeId));
        var mutationId = ValidateMutation(intent.MutationId);
        if (intent.ExpectedStateVersion < 0)
            throw Invalid("INVALID_STATE_VERSION", "expectedStateVersion must be non-negative.");

        return DispatchAsync(
            new ReplaceLLMModelCatalogPolicy(
                owner,
                LLMModelCatalogPolicyMode.InheritPlatform,
                [],
                intent.ExpectedStateVersion,
                mutationId),
            ct);
    }

    public Task<UserConfigSaveReceipt> ReplacePlatformAsync(
        ReplacePlatformLLMModelCatalogIntent intent,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var command = ParsePlatformReplaceIntent(intent);
        return DispatchAsync(command, ct);
    }

    public async Task<IReadOnlyList<NyxIdScopeModelSourceService>> GetScopeCandidatesAsync(
        string bearerToken,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);
        try
        {
            var inventory = await _inventoryPort
                .GetScopeModelSourcesAsync(bearerToken, ct)
                .ConfigureAwait(false);
            return inventory.Services
                .OrderBy(
                    static service => service.DisplayName ?? service.CatalogServiceDisplayName ?? service.Slug,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (NyxIdModelSourceInventoryException ex)
        {
            throw MapInventoryFailure(
                ex,
                "The authoritative NyxID key inventory is temporarily unavailable.");
        }
        catch (Exception ex)
        {
            throw Unavailable(
                "NYXID_INVENTORY_UNAVAILABLE",
                "The authoritative NyxID key inventory is temporarily unavailable.",
                ex);
        }
    }

    public async Task<IReadOnlyList<NyxIdPlatformModelSourceService>> GetPlatformCandidatesAsync(
        string bearerToken,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);
        try
        {
            var inventory = await _inventoryPort
                .GetPlatformCatalogServicesAsync(bearerToken, ct)
                .ConfigureAwait(false);
            return inventory.Services
                .OrderBy(static service => service.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (NyxIdModelSourceInventoryException ex)
        {
            throw MapInventoryFailure(
                ex,
                "The NyxID service catalog is temporarily unavailable.");
        }
        catch (Exception ex)
        {
            throw Unavailable(
                "NYXID_INVENTORY_UNAVAILABLE",
                "The NyxID service catalog is temporarily unavailable.",
                ex);
        }
    }

    public async Task<LLMModelSourceDiscoveryView> DiscoverScopeModelsAsync(
        string bearerToken,
        string userServiceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);
        var sourceIdentity = ValidateSourceIdentity(userServiceId, "userServiceId");
        try
        {
            var inventory = await _inventoryPort
                .GetScopeModelSourcesAsync(bearerToken, ct)
                .ConfigureAwait(false);
            var source = inventory.Services.FirstOrDefault(candidate =>
                string.Equals(candidate.UserServiceId, sourceIdentity, StringComparison.Ordinal));
            if (source is null)
            {
                throw Conflict(
                    "NYXID_MODEL_SOURCE_NOT_FOUND",
                    "The selected NyxID user service is no longer present in the authoritative inventory.");
            }
            if (!source.IsCallable)
            {
                throw Conflict(
                    "NYXID_MODEL_SOURCE_UNAVAILABLE",
                    "The selected NyxID user service is not currently callable.");
            }

            var discovered = await _modelDiscoveryPort
                .GetScopeModelsAsync(bearerToken, source.Slug, source.UserServiceId, ct)
                .ConfigureAwait(false);
            return new LLMModelSourceDiscoveryView(
                source.UserServiceId,
                source.Slug,
                discovered.ModelIds,
                discovered.DefaultModelId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (LLMModelCatalogApplicationException)
        {
            throw;
        }
        catch (NyxIdModelSourceInventoryException ex)
        {
            throw MapInventoryFailure(
                ex,
                "The authoritative NyxID key inventory is temporarily unavailable.");
        }
        catch (NyxIdModelDiscoveryException ex)
        {
            throw MapDiscoveryFailure(ex);
        }
        catch (Exception ex)
        {
            throw Unavailable(
                "NYXID_MODELS_UNAVAILABLE",
                "The selected service's model list is temporarily unavailable.",
                ex);
        }
    }

    public async Task<LLMModelSourceDiscoveryView> DiscoverPlatformModelsAsync(
        string bearerToken,
        string catalogServiceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);
        var sourceIdentity = ValidateSourceIdentity(catalogServiceId, "catalogServiceId");
        try
        {
            var inventory = await _inventoryPort
                .GetPlatformCatalogServicesAsync(bearerToken, ct)
                .ConfigureAwait(false);
            var source = inventory.Services.FirstOrDefault(candidate =>
                string.Equals(candidate.CatalogServiceId, sourceIdentity, StringComparison.Ordinal));
            if (source is null)
            {
                throw Conflict(
                    "NYXID_MODEL_SOURCE_NOT_FOUND",
                    "The selected NyxID catalog service is no longer present in the authoritative inventory.");
            }
            if (!source.IsSelectable)
            {
                throw Conflict(
                    "NYXID_MODEL_SOURCE_UNAVAILABLE",
                    "The selected NyxID catalog service is not currently selectable as a platform default.");
            }

            var discovered = await _modelDiscoveryPort
                .GetPlatformModelsAsync(bearerToken, source.CatalogServiceId, ct)
                .ConfigureAwait(false);
            return new LLMModelSourceDiscoveryView(
                source.CatalogServiceId,
                source.Slug,
                discovered.ModelIds,
                discovered.DefaultModelId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (LLMModelCatalogApplicationException)
        {
            throw;
        }
        catch (NyxIdModelSourceInventoryException ex)
        {
            throw MapInventoryFailure(
                ex,
                "The NyxID service catalog is temporarily unavailable.");
        }
        catch (NyxIdModelDiscoveryException ex)
        {
            throw MapDiscoveryFailure(ex);
        }
        catch (Exception ex)
        {
            throw Unavailable(
                "NYXID_MODELS_UNAVAILABLE",
                "The selected service's model list is temporarily unavailable.",
                ex);
        }
    }

    private static LLMModelCatalogView BuildScopeView(
        LLMModelCatalogPolicyOwner owner,
        LLMModelCatalogPolicySnapshot? scopePolicy,
        LLMModelCatalogPolicySnapshot? platformPolicy)
    {
        var usesScopePolicy = scopePolicy?.Mode == LLMModelCatalogPolicyMode.Custom;
        var effective = usesScopePolicy ? scopePolicy : platformPolicy;
        return new LLMModelCatalogView(
            owner,
            scopePolicy?.Mode ?? LLMModelCatalogPolicyMode.InheritPlatform,
            scopePolicy is not null,
            scopePolicy?.StateVersion ?? 0,
            scopePolicy?.UpdatedAtUtc,
            scopePolicy?.Sources ?? [],
            usesScopePolicy
                ? LLMModelCatalogEffectiveSourceKind.Scope
                : LLMModelCatalogEffectiveSourceKind.Platform,
            effective?.Sources ?? [],
            scopePolicy?.LastMutationId);
    }

    private static LLMModelCatalogView BuildPlatformView(LLMModelCatalogPolicySnapshot? policy) =>
        new(
            LLMModelCatalogPolicyOwner.Platform,
            LLMModelCatalogPolicyMode.Custom,
            policy is not null,
            policy?.StateVersion ?? 0,
            policy?.UpdatedAtUtc,
            policy?.Sources ?? [],
            LLMModelCatalogEffectiveSourceKind.Platform,
            policy?.Sources ?? [],
            policy?.LastMutationId);

    private static ReplaceLLMModelCatalogPolicy ParseScopeReplaceIntent(
        LLMModelCatalogPolicyOwner owner,
        ReplaceScopeLLMModelCatalogIntent intent)
    {
        var mutationId = ValidateMutation(intent.MutationId);
        if (intent.ExpectedStateVersion < 0)
            throw Invalid("INVALID_STATE_VERSION", "expectedStateVersion must be non-negative.");
        if (intent.Mode is not LLMModelCatalogPolicyMode.InheritPlatform and
            not LLMModelCatalogPolicyMode.Custom)
        {
            throw Invalid(
                "INVALID_MODE",
                "mode must be inherit_platform or custom_replace.");
        }
        if (intent.Mode == LLMModelCatalogPolicyMode.InheritPlatform && intent.Sources is { Count: > 0 })
        {
            throw Invalid(
                "INHERIT_SOURCES_NOT_EMPTY",
                "inherit_platform cannot contain sources.");
        }

        var sources = ParseSources(intent.Sources, ParseScopeSource);
        return new ReplaceLLMModelCatalogPolicy(
            owner,
            intent.Mode,
            sources,
            intent.ExpectedStateVersion,
            mutationId);
    }

    private static ReplaceLLMModelCatalogPolicy ParsePlatformReplaceIntent(
        ReplacePlatformLLMModelCatalogIntent intent)
    {
        var mutationId = ValidateMutation(intent.MutationId);
        if (intent.ExpectedStateVersion < 0)
            throw Invalid("INVALID_STATE_VERSION", "expectedStateVersion must be non-negative.");
        var sources = ParseSources(intent.Sources, ParsePlatformSource);
        return new ReplaceLLMModelCatalogPolicy(
            LLMModelCatalogPolicyOwner.Platform,
            LLMModelCatalogPolicyMode.Custom,
            sources,
            intent.ExpectedStateVersion,
            mutationId);
    }

    private static IReadOnlyList<LLMModelCatalogPolicySource> ParseSources<TIntent>(
        IReadOnlyList<TIntent?>? inputs,
        Func<TIntent, ParsedSource> parse)
        where TIntent : class
    {
        if (inputs is null)
            throw Invalid("SOURCES_REQUIRED", "sources must be an array.");
        if (inputs.Count > LLMModelCatalogPolicyLimits.MaxSources)
        {
            throw Invalid(
                "TOO_MANY_SOURCES",
                $"At most {LLMModelCatalogPolicyLimits.MaxSources} model sources are allowed.");
        }

        var sources = new List<LLMModelCatalogPolicySource>(inputs.Count);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var serviceSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var explicitModelCount = 0;
        foreach (var input in inputs)
        {
            if (input is null)
                throw Invalid("SOURCE_REQUIRED", "Each sources entry must be an object.");

            var parsed = parse(input);
            if (!identities.Add(parsed.IdentityKey))
            {
                throw Invalid(
                    "DUPLICATE_SOURCE",
                    "Each NyxID service identity may appear only once.");
            }
            if (!serviceSlugs.Add(parsed.Source.ServiceSlugSnapshot!))
            {
                throw Invalid(
                    "DUPLICATE_SERVICE_SLUG",
                    "Each serviceSlugSnapshot may appear only once in a model catalog policy.");
            }

            explicitModelCount += parsed.ExplicitModelCount;
            if (explicitModelCount > LLMModelCatalogPolicyLimits.MaxExplicitModelsPerPolicy)
            {
                throw Invalid(
                    "TOO_MANY_MODEL_IDS_TOTAL",
                    $"A model catalog policy may contain at most {LLMModelCatalogPolicyLimits.MaxExplicitModelsPerPolicy} explicit model IDs.");
            }
            sources.Add(parsed.Source);
        }
        return sources;
    }

    private static ParsedSource ParseScopeSource(ScopeLLMModelCatalogSourceIntent input)
    {
        var userServiceId = Normalize(input.UserServiceId);
        if (userServiceId is null)
            throw Invalid("USER_SERVICE_ID_REQUIRED", "Scope sources require an exact userServiceId.");

        var (slug, selection, explicitModelCount) = ParseSourceValues(
            input.ServiceSlugSnapshot,
            input.ModelSelection);
        return BuildParsedSource(
            new NyxIDUserServiceModelSourceIdentity(userServiceId),
            $"user:{userServiceId}",
            userServiceId,
            slug,
            selection,
            explicitModelCount);
    }

    private static ParsedSource ParsePlatformSource(PlatformLLMModelCatalogSourceIntent input)
    {
        var catalogServiceId = Normalize(input.CatalogServiceId);
        if (catalogServiceId is null)
            throw Invalid("CATALOG_SERVICE_ID_REQUIRED", "Platform sources require catalogServiceId.");

        var (slug, selection, explicitModelCount) = ParseSourceValues(
            input.ServiceSlugSnapshot,
            input.ModelSelection);
        return BuildParsedSource(
            new NyxIDCatalogServiceModelSourceIdentity(catalogServiceId),
            $"catalog:{catalogServiceId}",
            catalogServiceId,
            slug,
            selection,
            explicitModelCount);
    }

    private static ParsedSource BuildParsedSource(
        LLMModelSourceIdentity identity,
        string identityKey,
        string sourceIdentity,
        string? slug,
        ExplicitLLMModels selection,
        int explicitModelCount)
    {

        if (ContainsControlCharacter(sourceIdentity))
            throw Invalid("INVALID_SERVICE_ID", "Service identities must not contain control characters.");
        if (ExceedsUtf8Limit(sourceIdentity, LLMModelCatalogPolicyLimits.MaxServiceIdentityUtf8Bytes))
        {
            throw Invalid(
                "SERVICE_ID_TOO_LONG",
                $"Service identities must be at most {LLMModelCatalogPolicyLimits.MaxServiceIdentityUtf8Bytes} UTF-8 bytes.");
        }

        return new ParsedSource(
            new LLMModelCatalogPolicySource(identity, slug, selection),
            identityKey,
            explicitModelCount);
    }

    private static (string Slug, ExplicitLLMModels Selection, int ExplicitModelCount)
        ParseSourceValues(
            string? serviceSlugSnapshot,
            ExplicitLLMModelsIntent? selectionIntent)
    {
        if (string.IsNullOrWhiteSpace(serviceSlugSnapshot))
        {
            throw Invalid(
                "SERVICE_SLUG_REQUIRED",
                "serviceSlugSnapshot is required for model routing.");
        }
        var slug = serviceSlugSnapshot;
        if (!NyxIdServiceSlugPolicy.IsCanonical(slug))
        {
            throw Invalid(
                "INVALID_SERVICE_SLUG",
                "serviceSlugSnapshot must be a canonical NyxID service slug.");
        }

        var (selection, explicitModelCount) = ParseSelection(selectionIntent);
        return (slug, selection, explicitModelCount);
    }

    private static (ExplicitLLMModels Selection, int ExplicitModelCount) ParseSelection(
        ExplicitLLMModelsIntent? input)
    {
        if (input is null)
        {
            throw Invalid(
                "INVALID_MODEL_SELECTION",
                "modelSelection.mode must be explicit_models.");
        }

        var rawModelIds = input.ModelIds;
        if (rawModelIds is null)
            throw Invalid("MODEL_IDS_REQUIRED", "explicit_models requires a modelIds array.");
        if (rawModelIds.Count > LLMModelCatalogPolicyLimits.MaxExplicitModelsPerSource)
        {
            throw Invalid(
                "TOO_MANY_MODEL_IDS",
                $"Each model source may contain at most {LLMModelCatalogPolicyLimits.MaxExplicitModelsPerSource} explicit model IDs.");
        }

        var modelIds = new List<string>(rawModelIds.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawModelId in rawModelIds)
        {
            var modelId = Normalize(rawModelId);
            if (modelId is null)
            {
                throw Invalid(
                    "MODEL_ID_REQUIRED",
                    "modelIds entries must be non-empty strings.");
            }
            if (ContainsControlCharacter(modelId))
                throw Invalid("INVALID_MODEL_ID", "Model IDs must not contain control characters.");
            if (ExceedsUtf8Limit(modelId, LLMSelectionPolicy.MaxModelIdUtf8Bytes))
            {
                throw Invalid(
                    "MODEL_ID_TOO_LONG",
                    $"Model IDs must be at most {LLMSelectionPolicy.MaxModelIdUtf8Bytes} UTF-8 bytes.");
            }
            if (seen.Add(modelId))
                modelIds.Add(modelId);
        }
        if (modelIds.Count == 0)
            throw Invalid("MODEL_IDS_REQUIRED", "explicit_models requires at least one model id.");
        return (new ExplicitLLMModels(modelIds), modelIds.Count);
    }

    private async Task<UserConfigSaveReceipt> DispatchAsync(
        ReplaceLLMModelCatalogPolicy command,
        CancellationToken ct)
    {
        try
        {
            var receipt = await _commandPort.ReplaceAsync(command, ct).ConfigureAwait(false);
            if (!receipt.Accepted)
            {
                throw Unavailable(
                    "MODEL_CATALOG_DISPATCH_REJECTED",
                    "The model catalog command was not accepted for dispatch.");
            }
            return receipt;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (LLMModelCatalogApplicationException)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            throw new LLMModelCatalogApplicationException(
                LLMModelCatalogApplicationErrorKind.InvalidRequest,
                "INVALID_MODEL_CATALOG",
                ex.Message,
                ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new LLMModelCatalogApplicationException(
                LLMModelCatalogApplicationErrorKind.Conflict,
                "MODEL_CATALOG_CONFLICT",
                ex.Message,
                ex);
        }
        catch (Exception ex)
        {
            throw Unavailable(
                "MODEL_CATALOG_DISPATCH_UNAVAILABLE",
                "The model catalog command could not be dispatched.",
                ex);
        }
    }

    private static string ValidateScopeId(string scopeId)
    {
        if (string.IsNullOrWhiteSpace(scopeId))
            throw Invalid("SCOPE_ID_REQUIRED", "scopeId is required.");
        var normalized = scopeId.Trim();
        if (ExceedsUtf8Limit(normalized, LLMModelCatalogPolicyLimits.MaxScopeIdUtf8Bytes))
        {
            throw Invalid(
                "SCOPE_ID_TOO_LONG",
                $"scopeId must be at most {LLMModelCatalogPolicyLimits.MaxScopeIdUtf8Bytes} UTF-8 bytes.");
        }
        return normalized;
    }

    private static string ValidateMutation(string? mutationId)
    {
        var normalized = Normalize(mutationId);
        if (normalized is null)
            throw Invalid("MUTATION_ID_REQUIRED", "mutationId is required.");
        if (ExceedsUtf8Limit(normalized, LLMModelCatalogPolicyLimits.MaxMutationIdUtf8Bytes))
        {
            throw Invalid(
                "MUTATION_ID_TOO_LONG",
                $"mutationId must be at most {LLMModelCatalogPolicyLimits.MaxMutationIdUtf8Bytes} UTF-8 bytes.");
        }
        return normalized;
    }

    private static string ValidateSourceIdentity(string? sourceIdentity, string fieldName)
    {
        var normalized = Normalize(sourceIdentity);
        if (normalized is null)
            throw Invalid("SERVICE_ID_REQUIRED", $"{fieldName} is required.");
        if (ContainsControlCharacter(normalized))
            throw Invalid("INVALID_SERVICE_ID", $"{fieldName} must not contain control characters.");
        if (ExceedsUtf8Limit(normalized, LLMModelCatalogPolicyLimits.MaxServiceIdentityUtf8Bytes))
        {
            throw Invalid(
                "SERVICE_ID_TOO_LONG",
                $"{fieldName} must be at most {LLMModelCatalogPolicyLimits.MaxServiceIdentityUtf8Bytes} UTF-8 bytes.");
        }
        return normalized;
    }

    private static LLMModelCatalogApplicationException Invalid(string code, string message) =>
        new(LLMModelCatalogApplicationErrorKind.InvalidRequest, code, message);

    private static LLMModelCatalogApplicationException Conflict(string code, string message) =>
        new(LLMModelCatalogApplicationErrorKind.Conflict, code, message);

    private static LLMModelCatalogApplicationException Unavailable(
        string code,
        string message,
        Exception? innerException = null) =>
        new(LLMModelCatalogApplicationErrorKind.Unavailable, code, message, innerException);

    private static LLMModelCatalogApplicationException MapInventoryFailure(
        NyxIdModelSourceInventoryException exception,
        string unavailableMessage) =>
        exception.Kind switch
        {
            NyxIdModelSourceInventoryFailureKind.AuthenticationRejected => new(
                LLMModelCatalogApplicationErrorKind.AuthenticationRejected,
                "NYXID_AUTHENTICATION_REJECTED",
                "NyxID rejected the bearer token.",
                exception),
            NyxIdModelSourceInventoryFailureKind.Forbidden => new(
                LLMModelCatalogApplicationErrorKind.Forbidden,
                "NYXID_INVENTORY_FORBIDDEN",
                "NyxID denied access to the requested inventory.",
                exception),
            _ => Unavailable("NYXID_INVENTORY_UNAVAILABLE", unavailableMessage, exception),
        };

    private static LLMModelCatalogApplicationException MapDiscoveryFailure(
        NyxIdModelDiscoveryException exception) =>
        exception.Kind switch
        {
            NyxIdModelDiscoveryFailureKind.UpstreamRejected => Unavailable(
                "NYXID_MODELS_UPSTREAM_REJECTED",
                "The selected service rejected model discovery. Verify its credential and upstream access.",
                exception),
            NyxIdModelDiscoveryFailureKind.EndpointNotFound => Unavailable(
                "NYXID_MODELS_ENDPOINT_UNAVAILABLE",
                "The selected service does not expose a usable /models endpoint.",
                exception),
            NyxIdModelDiscoveryFailureKind.ResponseInvalid => Unavailable(
                "NYXID_MODELS_RESPONSE_INVALID",
                "The selected service returned an invalid model list.",
                exception),
            NyxIdModelDiscoveryFailureKind.ResponseTooLarge => Unavailable(
                "NYXID_MODELS_RESPONSE_TOO_LARGE",
                "The selected service returned a model list that exceeds the supported limit.",
                exception),
            _ => Unavailable(
                "NYXID_MODELS_UNAVAILABLE",
                "The selected service's model list is temporarily unavailable.",
                exception),
        };

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool ContainsControlCharacter(string value) => value.Any(char.IsControl);

    private static bool ExceedsUtf8Limit(string value, int maximumBytes) =>
        Encoding.UTF8.GetByteCount(value) > maximumBytes;

    private sealed record ParsedSource(
        LLMModelCatalogPolicySource Source,
        string IdentityKey,
        int ExplicitModelCount);
}
