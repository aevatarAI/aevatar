using System.Security.Cryptography;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.CatalogSkills;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using Aevatar.AI.ToolProviders.Ornn.Publishing;
using Aevatar.GAgentService.Abstractions.CatalogSkills;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.Ornn.CatalogSkills;

/// <summary>External operations for a single administrator update request.</summary>
public sealed class OrnnCatalogRecommendedSkillUpdateSteps(
    INyxIdClientCredentialsTokenSource publisherCredentials,
    NyxIdCatalogSkillClient catalog,
    NyxIdRecommendedSkillGenerator generator,
    OrnnSkillPublishingService publishing,
    OrnnSkillClient skills,
    ILogger<OrnnCatalogRecommendedSkillUpdateSteps>? logger = null) : ICatalogRecommendedSkillUpdateSteps
{
    private readonly ILogger _logger = logger ?? NullLogger<OrnnCatalogRecommendedSkillUpdateSteps>.Instance;

    public async Task<CatalogRecommendedSkillUpdateTarget> ResolveTargetAsync(CatalogRecommendedSkillUpdateRequest request, CancellationToken ct = default)
    {
        LogStage(request, CatalogRecommendedSkillUpdateStage.ResolveTarget);
        var token = await PublisherTokenAsync(CatalogRecommendedSkillUpdateStage.ResolveTarget, ct).ConfigureAwait(false);
        var target = await catalog.ReadRecommendationsAsync(token, request.CatalogServiceId, ct).ConfigureAwait(false);
        var reference = target.Recommendations.SingleOrDefault(item => item.Source == "ornn" && item.SkillId == request.SkillId)
            ?? throw Failure(CatalogRecommendedSkillUpdateErrorCode.TargetNotFound, CatalogRecommendedSkillUpdateStage.ResolveTarget,
                "The exact skill is not a recommendation of the requested catalog.");
        if (reference.Version != request.ExpectedVersion)
            throw Failure(CatalogRecommendedSkillUpdateErrorCode.ExpectedVersionConflict, CatalogRecommendedSkillUpdateStage.ValidateVersion,
                "The catalog recommendation no longer matches expectedVersion.");
        LogStage(request, CatalogRecommendedSkillUpdateStage.ValidateVersion);
        ValidateNewVersion(request.NewVersion, request.ExpectedVersion);

        var identity = RequireRead(await skills.GetPublisherIdentityAsync(token, ct).ConfigureAwait(false), CatalogRecommendedSkillUpdateStage.ResolveTarget);
        var detail = RequireRead(await skills.GetSkillDetailAsync(token, request.SkillId, ct).ConfigureAwait(false), CatalogRecommendedSkillUpdateStage.ResolveTarget);
        if (detail.Guid != request.SkillId || string.IsNullOrWhiteSpace(detail.Name) || detail.Name != reference.Name)
            throw Failure(CatalogRecommendedSkillUpdateErrorCode.TargetMismatch, CatalogRecommendedSkillUpdateStage.ResolveTarget,
                "Ornn skill identity does not match the catalog recommendation.");
        if (string.IsNullOrWhiteSpace(identity.UserId) ||
            !identity.Permissions.Contains("ornn:skill:update", StringComparer.Ordinal) ||
            !(detail.CreatedBy == identity.UserId || identity.Permissions.Contains("ornn:admin:skill", StringComparer.Ordinal)))
            throw Failure(CatalogRecommendedSkillUpdateErrorCode.Forbidden, CatalogRecommendedSkillUpdateStage.ResolveTarget,
                "The configured publisher cannot manage this Ornn skill.", 403);
        if (detail.IsPrivate is null || string.IsNullOrWhiteSpace(detail.Version))
            throw Failure(CatalogRecommendedSkillUpdateErrorCode.ContractInvalid, CatalogRecommendedSkillUpdateStage.ResolveTarget,
                "Ornn skill detail lacks authoritative visibility or version.");
        ValidateNewVersion(request.NewVersion, detail.Version);
        var versions = RequireRead(await skills.GetSkillVersionsAsync(token, request.SkillId, ct).ConfigureAwait(false), CatalogRecommendedSkillUpdateStage.ValidateVersion);
        if (versions.Items.Any(version => version.Version == request.NewVersion))
            throw Failure(CatalogRecommendedSkillUpdateErrorCode.VersionOccupied, CatalogRecommendedSkillUpdateStage.ValidateVersion,
                "The requested Ornn version already exists.");
        target.SkillName = detail.Name;
        return target;
    }

    public async Task<CatalogRecommendedSkillUpdatePackage> PreparePackageAsync(
        CatalogRecommendedSkillUpdateRequest request, CatalogRecommendedSkillUpdateTarget target, CancellationToken ct = default)
    {
        LogStage(request, CatalogRecommendedSkillUpdateStage.ReadContract);
        var token = await PublisherTokenAsync(CatalogRecommendedSkillUpdateStage.ReadContract, ct).ConfigureAwait(false);
        var content = await generator.GenerateAsync(token, request.CatalogServiceId, ct).ConfigureAwait(false);
        LogStage(request, CatalogRecommendedSkillUpdateStage.GenerateContent);
        if (content.Name != target.SkillName)
            throw Failure(CatalogRecommendedSkillUpdateErrorCode.TargetMismatch, CatalogRecommendedSkillUpdateStage.GenerateContent,
                "The generated catalog skill name does not match the recommended skill.");
        LogStage(request, CatalogRecommendedSkillUpdateStage.ValidatePackage);
        var prepared = await publishing.PrepareAsync(token, new OrnnSkillPublishRequest
        {
            Name = content.Name, Version = request.NewVersion, Description = content.Description,
            Category = content.Category, InstructionsMarkdown = content.InstructionsMarkdown,
            Tags = content.Tags, ToolList = content.ToolList, Visibility = "public",
        }, ct).ConfigureAwait(false);
        if (prepared.Package is null)
            throw Failure(CatalogRecommendedSkillUpdateErrorCode.PackageInvalid, CatalogRecommendedSkillUpdateStage.ValidatePackage,
                "Generated catalog package did not pass the shared publication validation.");
        return new(prepared.Package.ZipBytes, content.Name);
    }

    public async Task<CatalogRecommendedSkillPublication> PublishVersionAsync(
        CatalogRecommendedSkillUpdateRequest request, CatalogRecommendedSkillUpdatePackage package, CancellationToken ct = default)
    {
        LogStage(request, CatalogRecommendedSkillUpdateStage.PublishVersion);
        var token = await PublisherTokenAsync(CatalogRecommendedSkillUpdateStage.PublishVersion, ct).ConfigureAwait(false);
        var response = await skills.UpdateSkillAsync(token, request.SkillId, package.ZipBytes, ct).ConfigureAwait(false);
        if (!response.Succeeded)
            throw MutationFailure(response.Failure!, CatalogRecommendedSkillUpdateStage.PublishVersion);
        var published = OrnnSkillPublishingService.ExtractPublishedSkill(response.RawResponse);
        var expectedDigest = Convert.ToHexStringLower(SHA256.HashData(package.ZipBytes));
        if (published.Guid != request.SkillId || published.Version != request.NewVersion || published.SkillHash != expectedDigest)
            throw new CatalogRecommendedSkillUpdateException(CatalogRecommendedSkillUpdateErrorCode.PublicationUncertain,
                CatalogRecommendedSkillUpdateStage.PublishVersion, "Ornn returned an unverified publication receipt.", outcomeUncertain: true);
        return new CatalogRecommendedSkillPublication
        {
            SkillId = published.Guid, SkillName = package.SkillName, Version = published.Version, ManifestDigest = published.SkillHash,
        };
    }

    public async Task<CatalogRecommendedSkillPublication> VerifyPublicationAsync(
        CatalogRecommendedSkillUpdateRequest request,
        CatalogRecommendedSkillPublication publication,
        CatalogRecommendedSkillUpdateExecutionCredential credential,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        LogStage(request, CatalogRecommendedSkillUpdateStage.VerifyPublication);
        var publisherToken = await PublisherTokenAsync(CatalogRecommendedSkillUpdateStage.VerifyPublication, ct).ConfigureAwait(false);
        var detail = RequireRead(await skills.GetExactSkillDetailAsync(publisherToken, publication.SkillId, publication.Version, ct).ConfigureAwait(false),
            CatalogRecommendedSkillUpdateStage.VerifyPublication);
        VerifyExact(detail, publication);
        if (detail.IsPrivate is true)
        {
            var permissions = await skills.UpdateSkillPermissionsAsync(publisherToken, publication.SkillId, new(false), ct).ConfigureAwait(false);
            if (!permissions.Succeeded)
                throw MutationFailure(permissions.Failure!, CatalogRecommendedSkillUpdateStage.VerifyPublication);
            detail = RequireRead(await skills.GetExactSkillDetailAsync(publisherToken, publication.SkillId, publication.Version, ct).ConfigureAwait(false),
                CatalogRecommendedSkillUpdateStage.VerifyPublication);
            VerifyExact(detail, publication);
        }
        if (detail.IsPrivate is not false)
            throw Failure(CatalogRecommendedSkillUpdateErrorCode.VisibilityUnavailable, CatalogRecommendedSkillUpdateStage.VerifyPublication,
                "The exact Ornn publication is not confirmed public.");
        var identity = RequireRead(await skills.GetPublisherIdentityAsync(credential.BearerToken, ct).ConfigureAwait(false),
            CatalogRecommendedSkillUpdateStage.VerifyPublication);
        if (identity.UserId != request.AdministratorId)
            throw Failure(CatalogRecommendedSkillUpdateErrorCode.Forbidden, CatalogRecommendedSkillUpdateStage.VerifyPublication,
                "Administrator bearer identity does not match the authorized operation administrator.", 403);
        var consumerDetail = RequireRead(await skills.GetExactSkillDetailAsync(credential.BearerToken, publication.SkillId, publication.Version, ct).ConfigureAwait(false),
            CatalogRecommendedSkillUpdateStage.VerifyPublication);
        VerifyExact(consumerDetail, publication);
        if (consumerDetail.IsPrivate is not false)
            throw Failure(CatalogRecommendedSkillUpdateErrorCode.VisibilityUnavailable, CatalogRecommendedSkillUpdateStage.VerifyPublication,
                "Consumer read did not confirm public visibility.");
        var content = RequireRead(await skills.GetExactSkillJsonAsync(credential.BearerToken, publication.SkillId, publication.Version, ct).ConfigureAwait(false),
            CatalogRecommendedSkillUpdateStage.VerifyPublication);
        if (content.Name != publication.SkillName || content.Version != publication.Version ||
            content.Files is null || !content.Files.Any(file =>
                (file.Key == "SKILL.md" || file.Key == $"{publication.SkillName}/SKILL.md") && !string.IsNullOrWhiteSpace(file.Value)))
            throw Failure(CatalogRecommendedSkillUpdateErrorCode.PublicationInvalid, CatalogRecommendedSkillUpdateStage.VerifyPublication,
                "Consumer could not read the exact published package.");
        var verified = publication.Clone();
        verified.IsPublic = true;
        verified.ConsumerReadable = true;
        return verified;
    }

    public async Task<CatalogRecommendedSkillReferenceWrite> PersistReferenceAsync(
        CatalogRecommendedSkillUpdateRequest request, CatalogRecommendedSkillUpdateTarget target,
        CatalogRecommendedSkillPublication publication, CancellationToken ct = default)
    {
        LogStage(request, CatalogRecommendedSkillUpdateStage.PersistReference);
        RequireVerified(publication);
        var token = await PublisherTokenAsync(CatalogRecommendedSkillUpdateStage.PersistReference, ct).ConfigureAwait(false);
        var current = await catalog.ReadRecommendationsAsync(token, request.CatalogServiceId, ct, CatalogRecommendedSkillUpdateStage.PersistReference).ConfigureAwait(false);
        var existing = FindReference(current, publication.SkillId);
        if (existing is null || existing.Version != request.ExpectedVersion || current.CatalogSkillsRevision != target.CatalogSkillsRevision)
            throw Failure(CatalogRecommendedSkillUpdateErrorCode.ReferenceConflict, CatalogRecommendedSkillUpdateStage.PersistReference,
                "Catalog recommendations changed after this operation resolved its target.");
        var desired = target.Clone();
        var replacement = FindReference(desired, publication.SkillId)
            ?? throw Failure(CatalogRecommendedSkillUpdateErrorCode.TargetMismatch, CatalogRecommendedSkillUpdateStage.PersistReference,
                "Recorded catalog snapshot does not contain the publication target.");
        replacement.Name = publication.SkillName;
        replacement.Version = publication.Version;
        replacement.ManifestDigest = publication.ManifestDigest;
        var written = await catalog.ReplaceRecommendationsAsync(token, request.CatalogServiceId, target.CatalogSkillsRevision,
            request.OperationId, desired.Recommendations, ct).ConfigureAwait(false);
        if (written.CatalogSkillsRevision <= target.CatalogSkillsRevision || !written.Recommendations.SequenceEqual(desired.Recommendations))
            throw Failure(CatalogRecommendedSkillUpdateErrorCode.ReferenceInvalid, CatalogRecommendedSkillUpdateStage.PersistReference,
                "NyxID did not confirm the requested recommendation revision.");
        return Reference(written.CatalogSkillsRevision, publication, confirmed: false);
    }

    public async Task<CatalogRecommendedSkillReferenceWrite> VerifyReferenceAsync(
        CatalogRecommendedSkillUpdateRequest request, CatalogRecommendedSkillPublication publication,
        CatalogRecommendedSkillReferenceWrite reference, CancellationToken ct = default)
    {
        LogStage(request, CatalogRecommendedSkillUpdateStage.VerifyReference);
        RequireVerified(publication);
        var token = await PublisherTokenAsync(CatalogRecommendedSkillUpdateStage.VerifyReference, ct).ConfigureAwait(false);
        var observed = await catalog.ReadRecommendationsAsync(token, request.CatalogServiceId, ct, CatalogRecommendedSkillUpdateStage.VerifyReference).ConfigureAwait(false);
        if (observed.CatalogSkillsRevision < reference.SkillsRevision || !Matches(FindReference(observed, publication.SkillId), publication))
            throw Failure(CatalogRecommendedSkillUpdateErrorCode.ReferenceConflict, CatalogRecommendedSkillUpdateStage.VerifyReference,
                "Catalog readback does not point to this exact publication.");
        return Reference(observed.CatalogSkillsRevision, publication, confirmed: true);
    }

    private async Task<string> PublisherTokenAsync(CatalogRecommendedSkillUpdateStage stage, CancellationToken ct) =>
        await publisherCredentials.GetAccessTokenAsync(ct).ConfigureAwait(false) is { Length: > 0 } token ? token
            : throw Failure(CatalogRecommendedSkillUpdateErrorCode.Forbidden, stage, "Configured catalog publishing credentials are unavailable.", 403);

    private static void ValidateNewVersion(string proposed, string current)
    {
        static Version Parse(string value)
        {
            var pieces = value.Split('.');
            if (pieces is not [var major, var minor] || !int.TryParse(major, out var majorValue) ||
                !int.TryParse(minor, out var minorValue) || majorValue < 0 || minorValue < 0 ||
                majorValue.ToString(System.Globalization.CultureInfo.InvariantCulture) != major ||
                minorValue.ToString(System.Globalization.CultureInfo.InvariantCulture) != minor)
                throw Failure(CatalogRecommendedSkillUpdateErrorCode.InvalidVersion, CatalogRecommendedSkillUpdateStage.ValidateVersion,
                    "Ornn versions require canonical nonnegative major.minor integers.");
            return new Version(majorValue, minorValue);
        }
        if (Parse(proposed) <= Parse(current))
            throw Failure(CatalogRecommendedSkillUpdateErrorCode.VersionOccupied, CatalogRecommendedSkillUpdateStage.ValidateVersion,
                "New version must be greater than every published Ornn version.");
    }

    private static T RequireRead<T>(OrnnExactSkillReadResult<T> result, CatalogRecommendedSkillUpdateStage stage) where T : class =>
        result.Value ?? throw Failure(result.ProxyStatus is 401 or 403 ? CatalogRecommendedSkillUpdateErrorCode.Forbidden
            : result.ProxyStatus == 404 ? CatalogRecommendedSkillUpdateErrorCode.TargetNotFound : CatalogRecommendedSkillUpdateErrorCode.DependencyUnavailable,
            stage, "Ornn could not provide the required authoritative read.", result.ProxyStatus);

    private static void VerifyExact(OrnnExactSkillDetail detail, CatalogRecommendedSkillPublication publication)
    {
        if (detail.Guid != publication.SkillId || detail.Name != publication.SkillName || detail.Version != publication.Version ||
            detail.SkillHash != publication.ManifestDigest || !NyxIdCatalogSkillClient.IsDigest(detail.SkillHash))
            throw Failure(CatalogRecommendedSkillUpdateErrorCode.PublicationInvalid, CatalogRecommendedSkillUpdateStage.VerifyPublication,
                "Exact Ornn read did not match the recorded publication.");
    }

    private static void RequireVerified(CatalogRecommendedSkillPublication publication)
    {
        if (!publication.IsPublic || !publication.ConsumerReadable)
            throw Failure(CatalogRecommendedSkillUpdateErrorCode.PublicationInvalid, CatalogRecommendedSkillUpdateStage.PersistReference,
                "Public visibility and consumer readability must be verified before changing recommendations.");
    }

    private static CatalogRecommendedSkillReference? FindReference(CatalogRecommendedSkillUpdateTarget target, string skillId) =>
        target.Recommendations.SingleOrDefault(item => item.Source == "ornn" && item.SkillId == skillId);

    private static bool Matches(CatalogRecommendedSkillReference? reference, CatalogRecommendedSkillPublication publication) =>
        reference is not null && reference.Name == publication.SkillName && reference.Version == publication.Version && reference.ManifestDigest == publication.ManifestDigest;

    private static CatalogRecommendedSkillReferenceWrite Reference(long revision, CatalogRecommendedSkillPublication publication, bool confirmed) => new()
    {
        SkillsRevision = revision, Version = publication.Version, ManifestDigest = publication.ManifestDigest, Confirmed = confirmed,
    };

    private static CatalogRecommendedSkillUpdateException MutationFailure(OrnnSkillMutationFailure failure, CatalogRecommendedSkillUpdateStage stage) =>
        new(failure.HttpStatus is 401 or 403 ? CatalogRecommendedSkillUpdateErrorCode.Forbidden
                : failure.HttpStatus == 409 ? CatalogRecommendedSkillUpdateErrorCode.VersionOccupied
                : failure.Outcome == AgentToolFailureOutcome.OutcomeUncertain ? CatalogRecommendedSkillUpdateErrorCode.PublicationUncertain
                : CatalogRecommendedSkillUpdateErrorCode.PublicationFailed,
            stage, "Ornn rejected or could not confirm the requested mutation.", failure.HttpStatus,
            outcomeUncertain: stage == CatalogRecommendedSkillUpdateStage.PublishVersion && failure.Outcome == AgentToolFailureOutcome.OutcomeUncertain);

    private static CatalogRecommendedSkillUpdateException Failure(CatalogRecommendedSkillUpdateErrorCode code,
        CatalogRecommendedSkillUpdateStage stage, string message, int? status = null) =>
        new(code, stage, message, status);

    private void LogStage(CatalogRecommendedSkillUpdateRequest request, CatalogRecommendedSkillUpdateStage stage) =>
        _logger.LogInformation("Catalog recommended skill update. operationId={OperationId} administratorId={AdministratorId} catalogServiceId={CatalogServiceId} skillId={SkillId} expectedVersion={ExpectedVersion} newVersion={NewVersion} stage={Stage}",
            request.OperationId, request.AdministratorId, request.CatalogServiceId, request.SkillId, request.ExpectedVersion, request.NewVersion,
            JsonNamingPolicy.SnakeCaseLower.ConvertName(stage.ToString()));
}
