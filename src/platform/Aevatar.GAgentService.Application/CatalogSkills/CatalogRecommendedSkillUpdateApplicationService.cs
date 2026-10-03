using Aevatar.GAgentService.Abstractions.CatalogSkills;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgentService.Application.CatalogSkills;

public sealed class CatalogRecommendedSkillUpdateApplicationService(
    ICatalogRecommendedSkillUpdateSteps steps,
    ILogger<CatalogRecommendedSkillUpdateApplicationService>? logger = null) : ICatalogRecommendedSkillUpdateApplicationService
{
    private readonly ILogger _logger = logger ?? NullLogger<CatalogRecommendedSkillUpdateApplicationService>.Instance;

    public async Task<CatalogRecommendedSkillUpdateOutcome> UpdateAsync(
        CatalogRecommendedSkillUpdateRequest request,
        CatalogRecommendedSkillUpdateExecutionCredential credential,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(credential);
        request = request.Clone();
        var outcome = new CatalogRecommendedSkillUpdateOutcome
        {
            Request = request,
            Stage = CatalogRecommendedSkillUpdateStage.ValidateVersion,
        };
        try
        {
            ct.ThrowIfCancellationRequested();
            CatalogRecommendedSkillUpdateValidation.Validate(request);
            EnterStage(CatalogRecommendedSkillUpdateStage.ResolveTarget);
            var target = await steps.ResolveTargetAsync(request.Clone(), ct);
            ValidateTarget(request, target);

            EnterStage(CatalogRecommendedSkillUpdateStage.ReadContract);
            var package = await steps.PreparePackageAsync(request.Clone(), target.Clone(), ct);
            if (package.ZipBytes.Length == 0 || string.IsNullOrWhiteSpace(package.SkillName))
                throw new CatalogRecommendedSkillUpdateException(CatalogRecommendedSkillUpdateErrorCode.PackageInvalid,
                    CatalogRecommendedSkillUpdateStage.ValidatePackage, "Generated skill archive is incomplete.");

            EnterStage(CatalogRecommendedSkillUpdateStage.PublishVersion);
            var publication = await steps.PublishVersionAsync(request.Clone(), package, ct);
            ValidatePublication(request, publication, requireReadable: false);
            outcome.Publication = publication.Clone();

            EnterStage(CatalogRecommendedSkillUpdateStage.VerifyPublication);
            var verified = await steps.VerifyPublicationAsync(request.Clone(), publication.Clone(), credential, ct);
            ValidatePublication(request, verified, requireReadable: true);
            if (verified.ManifestDigest != publication.ManifestDigest || verified.SkillName != publication.SkillName)
                throw new CatalogRecommendedSkillUpdateException(CatalogRecommendedSkillUpdateErrorCode.PublicationInvalid,
                    CatalogRecommendedSkillUpdateStage.VerifyPublication, "Published version facts changed during verification.");
            outcome.Publication = verified.Clone();

            EnterStage(CatalogRecommendedSkillUpdateStage.PersistReference);
            var reference = await steps.PersistReferenceAsync(request.Clone(), target.Clone(), verified.Clone(), ct);
            ValidateReference(target, verified, reference, requireConfirmed: false);
            outcome.Reference = reference.Clone();
            outcome.Reference.Confirmed = false;

            EnterStage(CatalogRecommendedSkillUpdateStage.VerifyReference);
            reference = await steps.VerifyReferenceAsync(request.Clone(), verified.Clone(), outcome.Reference.Clone(), ct);
            ValidateReference(target, verified, reference, requireConfirmed: true);
            if (reference.SkillsRevision < outcome.Reference.SkillsRevision)
                throw new CatalogRecommendedSkillUpdateException(CatalogRecommendedSkillUpdateErrorCode.ReferenceInvalid,
                    CatalogRecommendedSkillUpdateStage.VerifyReference, "Catalog readback is older than the written revision.");
            outcome.Reference = reference.Clone();
            outcome.Status = CatalogRecommendedSkillUpdateStatus.Updated;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Catalog recommended skill update request canceled. operationId={OperationId} stage={Stage} publicationConfirmed={PublicationConfirmed}",
                request.OperationId, outcome.Stage, outcome.Publication is not null);
            throw;
        }
        catch (CatalogRecommendedSkillUpdateException failure)
        {
            RecordFailure(outcome, failure);
        }
        catch (Exception)
        {
            // External exceptions can contain credentials or documents. Report only safe stage facts.
            var uncertain = outcome.Stage == CatalogRecommendedSkillUpdateStage.PublishVersion;
            RecordFailure(outcome, new CatalogRecommendedSkillUpdateException(
                uncertain ? CatalogRecommendedSkillUpdateErrorCode.PublicationUncertain : CatalogRecommendedSkillUpdateErrorCode.DependencyUnavailable,
                outcome.Stage,
                uncertain
                    ? "Publication may have succeeded without a confirmed response. Check Ornn before submitting another update."
                    : "An update dependency was unavailable. Check the reported publication and catalog facts before another update.",
                outcomeUncertain: uncertain));
        }
        _logger.LogInformation(
            "Catalog recommended skill update request completed. operationId={OperationId} administratorId={AdministratorId} catalogServiceId={CatalogServiceId} skillId={SkillId} previousVersion={PreviousVersion} version={Version} stage={Stage} status={Status} failureCode={FailureCode} downstreamStatusCode={DownstreamStatusCode} publicationConfirmed={PublicationConfirmed} catalogReferenceUpdated={CatalogReferenceUpdated}",
            request.OperationId, request.AdministratorId, request.CatalogServiceId, request.SkillId, request.ExpectedVersion,
            request.NewVersion, outcome.Stage, outcome.Status, outcome.Failure?.Code, outcome.Failure?.DownstreamStatusCode,
            outcome.Publication is not null, outcome.Reference?.Confirmed == true);
        return outcome;

        void EnterStage(CatalogRecommendedSkillUpdateStage stage)
        {
            ct.ThrowIfCancellationRequested();
            outcome.Stage = stage;
        }
    }

    private static void ValidateTarget(CatalogRecommendedSkillUpdateRequest request, CatalogRecommendedSkillUpdateTarget target)
    {
        var matching = target.Recommendations.Where(item => item.Source == "ornn" && item.SkillId == request.SkillId).ToArray();
        if (matching.Length != 1 || target.CatalogSkillsRevision < 0 || string.IsNullOrWhiteSpace(target.SkillName))
            throw new CatalogRecommendedSkillUpdateException(CatalogRecommendedSkillUpdateErrorCode.TargetMismatch,
                CatalogRecommendedSkillUpdateStage.ResolveTarget, "Catalog must contain one exact target recommendation with complete facts.");
        if (matching[0].Version != request.ExpectedVersion)
            throw new CatalogRecommendedSkillUpdateException(CatalogRecommendedSkillUpdateErrorCode.ExpectedVersionConflict,
                CatalogRecommendedSkillUpdateStage.ValidateVersion, "The catalog recommendation no longer has expectedVersion.");
    }

    private static void ValidatePublication(CatalogRecommendedSkillUpdateRequest request,
        CatalogRecommendedSkillPublication publication, bool requireReadable)
    {
        if (publication.SkillId != request.SkillId || publication.Version != request.NewVersion ||
            string.IsNullOrWhiteSpace(publication.SkillName) || publication.ManifestDigest.Length != 64 ||
            !publication.ManifestDigest.All(Uri.IsHexDigit))
            throw new CatalogRecommendedSkillUpdateException(
                requireReadable ? CatalogRecommendedSkillUpdateErrorCode.PublicationInvalid : CatalogRecommendedSkillUpdateErrorCode.PublicationUncertain,
                requireReadable ? CatalogRecommendedSkillUpdateStage.VerifyPublication : CatalogRecommendedSkillUpdateStage.PublishVersion,
                "The exact published skill identity, version or manifest digest could not be verified.",
                outcomeUncertain: !requireReadable);
        if (requireReadable && (!publication.IsPublic || !publication.ConsumerReadable))
            throw new CatalogRecommendedSkillUpdateException(CatalogRecommendedSkillUpdateErrorCode.VisibilityUnavailable,
                CatalogRecommendedSkillUpdateStage.VerifyPublication, "The exact published version must be public and readable by its consumer.");
    }

    private static void ValidateReference(CatalogRecommendedSkillUpdateTarget target, CatalogRecommendedSkillPublication publication,
        CatalogRecommendedSkillReferenceWrite reference, bool requireConfirmed)
    {
        if (reference.SkillsRevision <= target.CatalogSkillsRevision || reference.Version != publication.Version ||
            reference.ManifestDigest != publication.ManifestDigest || requireConfirmed && !reference.Confirmed)
            throw new CatalogRecommendedSkillUpdateException(CatalogRecommendedSkillUpdateErrorCode.ReferenceInvalid,
                requireConfirmed ? CatalogRecommendedSkillUpdateStage.VerifyReference : CatalogRecommendedSkillUpdateStage.PersistReference,
                "The catalog did not confirm the exact published version, digest and authoritative revision.");
    }

    private static void RecordFailure(CatalogRecommendedSkillUpdateOutcome outcome, CatalogRecommendedSkillUpdateException failure)
    {
        outcome.Status = failure.OutcomeUncertain ? CatalogRecommendedSkillUpdateStatus.Uncertain : CatalogRecommendedSkillUpdateStatus.Failed;
        outcome.Stage = failure.Stage;
        outcome.Failure = new CatalogRecommendedSkillUpdateFailure
        {
            Code = failure.Code,
            Stage = failure.Stage,
            SafeMessage = failure.Message,
            DownstreamStatusCode = failure.DownstreamStatusCode ?? 0,
        };
    }
}
