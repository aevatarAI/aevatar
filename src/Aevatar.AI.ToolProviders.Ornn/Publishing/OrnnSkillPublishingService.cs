using System.Text.Json;

namespace Aevatar.AI.ToolProviders.Ornn.Publishing;

public sealed class OrnnSkillPublishingService
{
    private readonly OrnnSkillPublishValidationPipeline _validationPipeline;
    private readonly OrnnSkillPackageBuilder _packageBuilder;
    private readonly OrnnSkillPackageFormatValidator _formatValidator;
    private readonly OrnnSkillClient _client;

    public OrnnSkillPublishingService(
        OrnnSkillPublishValidationPipeline validationPipeline,
        OrnnSkillPackageBuilder packageBuilder,
        OrnnSkillPackageFormatValidator formatValidator,
        OrnnSkillClient client)
    {
        _validationPipeline = validationPipeline;
        _packageBuilder = packageBuilder;
        _formatValidator = formatValidator;
        _client = client;
    }

    public async Task<OrnnSkillPublishingResult> PublishAsync(
        string accessToken,
        OrnnSkillPublishRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            return OrnnSkillPublishingResult.Failed("missing_access_token", "No NyxID access token available.");

        var localValidation = await _validationPipeline.ValidateAsync(request, ct).ConfigureAwait(false);
        if (!localValidation.IsValid)
            return OrnnSkillPublishingResult.ValidationFailed(localValidation.Diagnostics);

        var (package, buildValidation) = _packageBuilder.Build(request);
        if (package is null)
            return OrnnSkillPublishingResult.ValidationFailed(buildValidation.Diagnostics);

        var formatValidation = await _formatValidator.ValidateAsync(accessToken, package.ZipBytes, ct).ConfigureAwait(false);
        if (!formatValidation.IsValid)
        {
            if (!string.IsNullOrWhiteSpace(formatValidation.Error) && formatValidation.Violations.Count == 0)
                return OrnnSkillPublishingResult.Failed("format_validation_unavailable", formatValidation.Error);

            return OrnnSkillPublishingResult.FormatValidationFailed(
                formatValidation.Violations,
                formatValidation.Error);
        }

        var publish = await _client.PublishSkillAsync(accessToken, package.ZipBytes, ct).ConfigureAwait(false);
        if (!publish.Succeeded)
        {
            if (IsConflictPublishFailure(publish) &&
                await TryResolveExistingPublishedSkillAsync(accessToken, request, ct).ConfigureAwait(false) is { } existing)
            {
                return await CompletePublishedSkillAsync(
                    accessToken,
                    request,
                    existing,
                    package.ZipBytes.Length,
                    publish.RawResponse,
                    ct).ConfigureAwait(false);
            }

            return OrnnSkillPublishingResult.Failed(publish.Failure!);
        }

        return await CompletePublishedSkillAsync(
            accessToken,
            request,
            ExtractPublishedSkill(publish.RawResponse),
            package.ZipBytes.Length,
            publish.RawResponse,
            ct).ConfigureAwait(false);
    }

    private async Task<OrnnSkillPublishingResult> CompletePublishedSkillAsync(
        string accessToken,
        OrnnSkillPublishRequest request,
        PublishedSkillSubject published,
        int packageBytes,
        string rawResponse,
        CancellationToken ct)
    {
        if (IsPublicVisibility(request.Visibility) && !string.IsNullOrWhiteSpace(published.Guid))
        {
            var permissionUpdate = await _client
                .UpdateSkillPermissionsAsync(
                    accessToken,
                    published.Guid.Trim(),
                    new OrnnSkillPermissionUpdateRequest(IsPrivate: false),
                    ct)
                .ConfigureAwait(false);
            if (!permissionUpdate.Succeeded)
            {
                return OrnnSkillPublishingResult.Failed(
                    "permission_update_failed",
                    permissionUpdate.Failure?.Message ?? "Ornn skill permission update failed.",
                    permissionUpdate.Failure);
            }
        }

        return OrnnSkillPublishingResult.Succeeded(
            published.Guid,
            published.Version ?? request.Version,
            published.SkillHash,
            packageBytes,
            rawResponse);
    }

    private async Task<PublishedSkillSubject?> TryResolveExistingPublishedSkillAsync(
        string accessToken,
        OrnnSkillPublishRequest request,
        CancellationToken ct)
    {
        var searchResult = await _client.SearchSkillsAsync(
            accessToken,
            request.Name,
            scope: "mixed",
            page: 1,
            pageSize: 20,
            ct: ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(searchResult.Error))
            return null;

        foreach (var candidate in searchResult.Items)
        {
            if (!string.Equals(candidate.Name, request.Name, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(candidate.Guid))
            {
                continue;
            }

            var exact = await _client.GetExactSkillDetailAsync(
                accessToken,
                candidate.Guid,
                request.Version,
                ct).ConfigureAwait(false);
            if (exact.Value is null || string.IsNullOrWhiteSpace(exact.Value.SkillHash))
                continue;

            return new PublishedSkillSubject(
                candidate.Guid,
                request.Version,
                exact.Value.SkillHash);
        }

        return null;
    }

    private static bool IsConflictPublishFailure(OrnnSkillMutationResponse publish) =>
        publish.Failure?.HttpStatus == 409 ||
        publish.RawResponse.Contains("\"status\":409", StringComparison.Ordinal);

    private static bool IsPublicVisibility(string? visibility) =>
        string.Equals(visibility?.Trim(), "public", StringComparison.OrdinalIgnoreCase);

    public static PublishedSkillSubject ExtractPublishedSkill(string? rawResponse)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
            return new PublishedSkillSubject(null, null, null);

        try
        {
            using var doc = JsonDocument.Parse(rawResponse);
            return ExtractPublishedSkill(doc.RootElement);
        }
        catch (JsonException)
        {
            return new PublishedSkillSubject(null, null, null);
        }
    }

    private static PublishedSkillSubject ExtractPublishedSkill(JsonElement root)
    {
        var subject = ExtractPublishedSkillFromObject(root);
        if (subject.HasAny)
            return subject;

        if (root.ValueKind != JsonValueKind.Object)
            return subject;

        foreach (var propertyName in new[] { "data", "result", "skill" })
        {
            if (!root.TryGetProperty(propertyName, out var nested) || nested.ValueKind != JsonValueKind.Object)
                continue;

            subject = ExtractPublishedSkillFromObject(nested);
            if (subject.HasAny)
                return subject;
        }

        return subject;
    }

    private static PublishedSkillSubject ExtractPublishedSkillFromObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return new PublishedSkillSubject(null, null, null);

        return new PublishedSkillSubject(
            TryGetString(element, "guid", "id", "skill_id", "skillId"),
            TryGetString(element, "version", "subject_version", "subjectVersion"),
            TryGetString(element, "skillHash", "skill_hash", "hash", "subject_hash"));
    }

    private static string? TryGetString(JsonElement element, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!element.TryGetProperty(key, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.String)
                return value.GetString();

            if (value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                return value.ToString();
        }

        return null;
    }
}

public sealed record PublishedSkillSubject(string? Guid, string? Version, string? SkillHash)
{
    public bool HasAny =>
        !string.IsNullOrWhiteSpace(Guid) ||
        !string.IsNullOrWhiteSpace(Version) ||
        !string.IsNullOrWhiteSpace(SkillHash);
}

public sealed record OrnnSkillPublishingResult(
    string Status,
    string? Error,
    IReadOnlyList<OrnnSkillPublishDiagnostic> Diagnostics,
    IReadOnlyList<OrnnSkillPackageFormatViolation> FormatViolations,
    string? Guid,
    string? Version,
    string? SkillHash,
    int PackageBytes,
    string RawResponse,
    OrnnSkillMutationFailure? Failure)
{
    public bool IsSuccess => string.Equals(Status, "success", StringComparison.Ordinal);

    public static OrnnSkillPublishingResult Succeeded(
        string? guid,
        string? version,
        string? skillHash,
        int packageBytes,
        string rawResponse) =>
        new("success", null, [], [], guid, version, skillHash, packageBytes, rawResponse, null);

    public static OrnnSkillPublishingResult Failed(string status, string error) =>
        new(status, error, [], [], null, null, null, 0, string.Empty, null);

    public static OrnnSkillPublishingResult Failed(
        string status,
        string error,
        OrnnSkillMutationFailure? failure) =>
        new(status, error, [], [], null, null, null, 0, string.Empty, failure);

    public static OrnnSkillPublishingResult Failed(OrnnSkillMutationFailure failure) =>
        new("error", failure.Message, [], [], null, null, null, 0, string.Empty, failure);

    public static OrnnSkillPublishingResult ValidationFailed(
        IReadOnlyList<OrnnSkillPublishDiagnostic> diagnostics) =>
        new("validation_error", null, diagnostics, [], null, null, null, 0, string.Empty, null);

    public static OrnnSkillPublishingResult FormatValidationFailed(
        IReadOnlyList<OrnnSkillPackageFormatViolation> violations,
        string? error) =>
        new("format_validation_error", error, [], violations, null, null, null, 0, string.Empty, null);
}
