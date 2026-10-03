using Aevatar.GAgentService.Abstractions.CatalogSkills;
using Aevatar.GAgentService.Application.CatalogSkills;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class CatalogRecommendedSkillUpdateApplicationServiceTests
{
    [Fact]
    public async Task Update_CompletesPublicationAndCatalogReadbackBeforeReturning()
    {
        var steps = new Steps();
        using var cancellation = new CancellationTokenSource();
        var outcome = await new CatalogRecommendedSkillUpdateApplicationService(steps).UpdateAsync(Request(), Credential(), cancellation.Token);

        outcome.Status.Should().Be(CatalogRecommendedSkillUpdateStatus.Updated);
        outcome.Stage.Should().Be(CatalogRecommendedSkillUpdateStage.VerifyReference);
        outcome.Publication.Should().BeEquivalentTo(steps.VerifiedPublication);
        outcome.Reference.Confirmed.Should().BeTrue();
        outcome.Reference.SkillsRevision.Should().Be(8);
        outcome.Failure.Should().BeNull();
        steps.Calls.Should().Equal(CatalogRecommendedSkillUpdateStage.ResolveTarget, CatalogRecommendedSkillUpdateStage.ReadContract,
            CatalogRecommendedSkillUpdateStage.PublishVersion, CatalogRecommendedSkillUpdateStage.VerifyPublication,
            CatalogRecommendedSkillUpdateStage.PersistReference, CatalogRecommendedSkillUpdateStage.VerifyReference);
        steps.Tokens.Should().OnlyContain(token => token == cancellation.Token);
    }

    [Theory]
    [InlineData("1.0", "1.0")]
    [InlineData("2.0", "1.9")]
    [InlineData("1.0", "1.1.0")]
    [InlineData("1.0", "latest")]
    [InlineData("1.0", "01.1")]
    [InlineData("1.0", "2147483648.0")]
    public async Task Update_RejectsInvalidVersionsBeforeExternalCalls(string previous, string next)
    {
        var steps = new Steps();
        var request = Request();
        request.ExpectedVersion = previous;
        request.NewVersion = next;
        var outcome = await new CatalogRecommendedSkillUpdateApplicationService(steps).UpdateAsync(request, Credential());
        outcome.Status.Should().Be(CatalogRecommendedSkillUpdateStatus.Failed);
        outcome.Failure.Code.Should().Be(CatalogRecommendedSkillUpdateErrorCode.InvalidVersion);
        steps.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_EachRequestResolvesCurrentTargetWithoutCachedCompletion()
    {
        var steps = new Steps();
        var service = new CatalogRecommendedSkillUpdateApplicationService(steps);
        var first = await service.UpdateAsync(Request(), Credential());
        steps.Target.Recommendations[0].Version = "1.1";
        var second = await service.UpdateAsync(Request(), Credential());

        first.Status.Should().Be(CatalogRecommendedSkillUpdateStatus.Updated);
        second.Failure.Code.Should().Be(CatalogRecommendedSkillUpdateErrorCode.ExpectedVersionConflict);
        second.Publication.Should().BeNull();
        steps.Calls.Count(stage => stage == CatalogRecommendedSkillUpdateStage.ResolveTarget).Should().Be(2);
        steps.Calls.Count(stage => stage == CatalogRecommendedSkillUpdateStage.PublishVersion).Should().Be(1);
    }

    [Theory]
    [InlineData(CatalogRecommendedSkillUpdateStage.ResolveTarget, false)]
    [InlineData(CatalogRecommendedSkillUpdateStage.ReadContract, false)]
    [InlineData(CatalogRecommendedSkillUpdateStage.PublishVersion, false)]
    [InlineData(CatalogRecommendedSkillUpdateStage.VerifyPublication, true)]
    [InlineData(CatalogRecommendedSkillUpdateStage.PersistReference, true)]
    [InlineData(CatalogRecommendedSkillUpdateStage.VerifyReference, true)]
    public async Task Update_StopsAtFailedStepAndPreservesOnlyConfirmedFacts(CatalogRecommendedSkillUpdateStage stage, bool published)
    {
        var steps = new Steps
        {
            FailureStage = stage,
            Failure = new CatalogRecommendedSkillUpdateException(CatalogRecommendedSkillUpdateErrorCode.Forbidden, stage, "Access denied.", 403),
        };
        var outcome = await new CatalogRecommendedSkillUpdateApplicationService(steps).UpdateAsync(Request(), Credential());

        outcome.Status.Should().Be(CatalogRecommendedSkillUpdateStatus.Failed);
        outcome.Stage.Should().Be(stage);
        outcome.Failure.DownstreamStatusCode.Should().Be(403);
        (outcome.Publication is not null).Should().Be(published);
        if (published) outcome.Publication.Version.Should().Be("1.1");
        (outcome.Reference?.Confirmed == true).Should().BeFalse();
        steps.Calls.Last().Should().Be(stage);
        steps.Calls.Count(call => call == stage).Should().Be(1);
    }

    [Fact]
    public async Task Update_UncertainPublicationIsNotRetriedOrWrittenToCatalog()
    {
        var steps = new Steps
        {
            FailureStage = CatalogRecommendedSkillUpdateStage.PublishVersion,
            Failure = new HttpRequestException("private document and bearer secret"),
        };
        var outcome = await new CatalogRecommendedSkillUpdateApplicationService(steps).UpdateAsync(Request(), Credential());

        outcome.Status.Should().Be(CatalogRecommendedSkillUpdateStatus.Uncertain);
        outcome.Failure.Code.Should().Be(CatalogRecommendedSkillUpdateErrorCode.PublicationUncertain);
        outcome.Failure.SafeMessage.Should().NotContain("secret");
        outcome.Publication.Should().BeNull();
        steps.Calls.Count(stage => stage == CatalogRecommendedSkillUpdateStage.PublishVersion).Should().Be(1);
        steps.Calls.Should().NotContain(CatalogRecommendedSkillUpdateStage.PersistReference);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("version")]
    [InlineData("digest")]
    public async Task Update_InvalidPublishReceiptDoesNotClaimConfirmedPublication(string mismatch)
    {
        var steps = new Steps();
        if (mismatch == "identity") steps.Publication.SkillId = "another-skill";
        if (mismatch == "version") steps.Publication.Version = "1.2";
        if (mismatch == "digest") steps.Publication.ManifestDigest = "invalid";
        var outcome = await new CatalogRecommendedSkillUpdateApplicationService(steps).UpdateAsync(Request(), Credential());
        outcome.Status.Should().Be(CatalogRecommendedSkillUpdateStatus.Uncertain);
        outcome.Publication.Should().BeNull();
        steps.Calls.Should().NotContain(CatalogRecommendedSkillUpdateStage.VerifyPublication);
    }

    [Theory]
    [InlineData("private")]
    [InlineData("unreadable")]
    [InlineData("digest")]
    [InlineData("name")]
    public async Task Update_UnverifiedPublicationNeverReplacesRecommendation(string mismatch)
    {
        var steps = new Steps();
        if (mismatch == "private") steps.VerifiedPublication.IsPublic = false;
        if (mismatch == "unreadable") steps.VerifiedPublication.ConsumerReadable = false;
        if (mismatch == "digest") steps.VerifiedPublication.ManifestDigest = new string('b', 64);
        if (mismatch == "name") steps.VerifiedPublication.SkillName = "another-name";
        var outcome = await new CatalogRecommendedSkillUpdateApplicationService(steps).UpdateAsync(Request(), Credential());
        outcome.Status.Should().Be(CatalogRecommendedSkillUpdateStatus.Failed);
        outcome.Stage.Should().Be(CatalogRecommendedSkillUpdateStage.VerifyPublication);
        outcome.Publication.Should().BeEquivalentTo(steps.Publication);
        steps.Calls.Should().NotContain(CatalogRecommendedSkillUpdateStage.PersistReference);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("version")]
    [InlineData("digest")]
    [InlineData("unconfirmed")]
    public async Task Update_RequiresExactCatalogReadbackBeforeSuccess(string mismatch)
    {
        var steps = new Steps();
        if (mismatch == "revision") steps.VerifiedReference.SkillsRevision = 7;
        if (mismatch == "version") steps.VerifiedReference.Version = "1.2";
        if (mismatch == "digest") steps.VerifiedReference.ManifestDigest = new string('b', 64);
        if (mismatch == "unconfirmed") steps.VerifiedReference.Confirmed = false;
        var outcome = await new CatalogRecommendedSkillUpdateApplicationService(steps).UpdateAsync(Request(), Credential());
        outcome.Status.Should().Be(CatalogRecommendedSkillUpdateStatus.Failed);
        outcome.Failure.Code.Should().Be(CatalogRecommendedSkillUpdateErrorCode.ReferenceInvalid);
        outcome.Publication.Should().BeEquivalentTo(steps.VerifiedPublication);
        outcome.Reference.Confirmed.Should().BeFalse();
    }

    [Fact]
    public async Task Update_CancellationStopsRequestWithoutContinuingInBackground()
    {
        using var cancellation = new CancellationTokenSource();
        var steps = new Steps
        {
            AfterStep = stage => { if (stage == CatalogRecommendedSkillUpdateStage.PublishVersion) cancellation.Cancel(); },
        };
        var update = () => new CatalogRecommendedSkillUpdateApplicationService(steps).UpdateAsync(Request(), Credential(), cancellation.Token);
        await update.Should().ThrowAsync<OperationCanceledException>();
        steps.Calls.Last().Should().Be(CatalogRecommendedSkillUpdateStage.PublishVersion);
    }

    [Fact]
    public async Task Update_PreCanceledRequestDoesNotCallExternalServices()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var steps = new Steps();
        var update = () => new CatalogRecommendedSkillUpdateApplicationService(steps).UpdateAsync(Request(), Credential(), cancellation.Token);
        await update.Should().ThrowAsync<OperationCanceledException>();
        steps.Calls.Should().BeEmpty();
    }

    private static CatalogRecommendedSkillUpdateRequest Request() => new()
    {
        OperationId = Guid.NewGuid().ToString("D"), AdministratorId = "admin-alpha", CatalogServiceId = "catalog-alpha",
        SkillId = "skill-alpha", ExpectedVersion = "1.0", NewVersion = "1.1",
    };

    private static CatalogRecommendedSkillUpdateExecutionCredential Credential() =>
        new("administrator-bearer");

    private sealed class Steps : ICatalogRecommendedSkillUpdateSteps
    {
        public List<CatalogRecommendedSkillUpdateStage> Calls { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public CatalogRecommendedSkillUpdateStage? FailureStage { get; init; }
        public Exception? Failure { get; init; }
        public Action<CatalogRecommendedSkillUpdateStage>? AfterStep { get; init; }
        public CatalogRecommendedSkillUpdateTarget Target { get; } = new()
        {
            CatalogSkillsRevision = 6, SkillName = "catalog-connected-service",
            Recommendations = { new CatalogRecommendedSkillReference { Source = "ornn", SkillId = "skill-alpha", Version = "1.0" } },
        };
        public CatalogRecommendedSkillPublication Publication { get; } = PublicationFacts();
        public CatalogRecommendedSkillPublication VerifiedPublication { get; } = PublicationFacts(readable: true);
        public CatalogRecommendedSkillReferenceWrite VerifiedReference { get; } = ReferenceFacts(confirmed: true);

        public Task<CatalogRecommendedSkillUpdateTarget> ResolveTargetAsync(CatalogRecommendedSkillUpdateRequest request, CancellationToken ct = default) =>
            Run(CatalogRecommendedSkillUpdateStage.ResolveTarget, Target, ct);
        public Task<CatalogRecommendedSkillUpdatePackage> PreparePackageAsync(CatalogRecommendedSkillUpdateRequest request, CatalogRecommendedSkillUpdateTarget target, CancellationToken ct = default) =>
            Run(CatalogRecommendedSkillUpdateStage.ReadContract, new CatalogRecommendedSkillUpdatePackage([1, 2], target.SkillName), ct);
        public Task<CatalogRecommendedSkillPublication> PublishVersionAsync(CatalogRecommendedSkillUpdateRequest request, CatalogRecommendedSkillUpdatePackage package, CancellationToken ct = default) =>
            Run(CatalogRecommendedSkillUpdateStage.PublishVersion, Publication, ct);
        public Task<CatalogRecommendedSkillPublication> VerifyPublicationAsync(CatalogRecommendedSkillUpdateRequest request,
            CatalogRecommendedSkillPublication publication, CatalogRecommendedSkillUpdateExecutionCredential credential,
            CancellationToken ct = default) =>
            Run(CatalogRecommendedSkillUpdateStage.VerifyPublication, VerifiedPublication, ct);
        public Task<CatalogRecommendedSkillReferenceWrite> PersistReferenceAsync(CatalogRecommendedSkillUpdateRequest request, CatalogRecommendedSkillUpdateTarget target, CatalogRecommendedSkillPublication publication, CancellationToken ct = default) =>
            Run(CatalogRecommendedSkillUpdateStage.PersistReference, ReferenceFacts(confirmed: false), ct);
        public Task<CatalogRecommendedSkillReferenceWrite> VerifyReferenceAsync(CatalogRecommendedSkillUpdateRequest request, CatalogRecommendedSkillPublication publication, CatalogRecommendedSkillReferenceWrite reference, CancellationToken ct = default) =>
            Run(CatalogRecommendedSkillUpdateStage.VerifyReference, VerifiedReference, ct);

        private Task<T> Run<T>(CatalogRecommendedSkillUpdateStage stage, T result, CancellationToken ct)
        {
            Calls.Add(stage);
            Tokens.Add(ct);
            ct.ThrowIfCancellationRequested();
            if (FailureStage == stage) throw Failure!;
            AfterStep?.Invoke(stage);
            return Task.FromResult(result);
        }
        private static CatalogRecommendedSkillPublication PublicationFacts(bool readable = false) => new()
        {
            SkillId = "skill-alpha", SkillName = "catalog-connected-service", Version = "1.1", ManifestDigest = new string('a', 64),
            IsPublic = readable, ConsumerReadable = readable,
        };
        private static CatalogRecommendedSkillReferenceWrite ReferenceFacts(bool confirmed) => new()
        {
            SkillsRevision = 8, Version = "1.1", ManifestDigest = new string('a', 64), Confirmed = confirmed,
        };
    }
}
