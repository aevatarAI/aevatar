using Aevatar.AI.Abstractions;
using Aevatar.AI.ToolProviders.Skills;
using FluentAssertions;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

public sealed class ExactRemoteReleaseVerifierTests
{
    private const string SkillGuid = "11111111-1111-1111-1111-111111111111";
    private const string SkillsetGuid = "22222222-2222-2222-2222-222222222222";
    private const string MemberGuid = "33333333-3333-3333-3333-333333333333";
    private static readonly DateTimeOffset PublishedAt = DateTimeOffset.Parse("2026-07-10T12:30:00Z");
    private readonly ExactRemoteReleaseVerifier _verifier = new();

    [Fact]
    public void VerifySkill_WhenEveryReviewedFieldMatches_ReturnsFetchedRelease()
    {
        var release = SkillRelease();
        var expectation = SkillExpectation();

        var verified = _verifier.VerifySkill(release, expectation);

        verified.Should().BeSameAs(release);
    }

    [Fact]
    public void VerifySkill_WhenReferenceOrNameDiffers_RejectsRelease()
    {
        var wrongReference = SkillExpectation() with
        {
            Reference = new ExactRemoteSkillRef { Guid = SkillGuid, LiteralVersion = "1.3" },
        };
        var wrongName = SkillExpectation() with { PublishedName = "different" };

        Action referenceAct = () => _verifier.VerifySkill(SkillRelease(), wrongReference);
        Action nameAct = () => _verifier.VerifySkill(SkillRelease(), wrongName);

        referenceAct.Should().Throw<ExactRemoteFetchException>()
            .Which.FailureKind.Should().Be(ExactRemoteFetchFailureKind.IntegrityMismatch);
        nameAct.Should().Throw<ExactRemoteFetchException>();
    }

    [Theory]
    [InlineData("subject")]
    [InlineData("email-absence")]
    [InlineData("email-value")]
    [InlineData("display-absence")]
    [InlineData("display-value")]
    [InlineData("published-at")]
    public void VerifySkill_WhenAnyPublisherSnapshotFieldDiffers_RejectsRelease(string mismatch)
    {
        var expectation = SkillExpectation() with { Provenance = ProvenanceWithMismatch(mismatch) };

        Action act = () => _verifier.VerifySkill(SkillRelease(), expectation);

        act.Should().Throw<ExactRemoteFetchException>()
            .Which.Message.Should().Contain("provenance");
    }

    [Theory]
    [InlineData("file-count")]
    [InlineData("path-bytes")]
    [InlineData("file-bytes")]
    [InlineData("total-bytes")]
    public void VerifySkill_WhenAnyPackageShapeDimensionExceedsReviewedBound_RejectsRelease(
        string dimension)
    {
        var shape = SkillRelease().Package.Shape;
        shape = dimension switch
        {
            "file-count" => shape with { FileCount = 11 },
            "path-bytes" => shape with { MaximumPathUtf8Bytes = 65 },
            "file-bytes" => shape with { MaximumFileUtf8Bytes = 1025 },
            "total-bytes" => shape with { TotalFileUtf8Bytes = 2049 },
            _ => throw new ArgumentOutOfRangeException(nameof(dimension)),
        };
        var release = SkillRelease();
        release = release with { Package = release.Package with { Shape = shape } };

        Action act = () => _verifier.VerifySkill(release, SkillExpectation());

        act.Should().Throw<ExactRemoteFetchException>()
            .Which.FailureKind.Should().Be(ExactRemoteFetchFailureKind.IntegrityMismatch);
    }

    [Theory]
    [InlineData("file-count", false)]
    [InlineData("path-bytes", false)]
    [InlineData("file-bytes", false)]
    [InlineData("total-bytes", false)]
    [InlineData("file-count", true)]
    [InlineData("path-bytes", true)]
    [InlineData("file-bytes", true)]
    [InlineData("total-bytes", true)]
    public void VerifySkill_WhenAnyReviewedBoundIsNonPositiveOrExpandsAdapterCeiling_RejectsRelease(
        string dimension,
        bool expandsCeiling)
    {
        var bounds = expandsCeiling
            ? ExactRemotePackageBounds.AdapterMaximum
            : SkillExpectation().PackageBounds;
        bounds = (dimension, expandsCeiling) switch
        {
            ("file-count", false) => bounds with { MaximumFileCount = 0 },
            ("path-bytes", false) => bounds with { MaximumPathUtf8Bytes = 0 },
            ("file-bytes", false) => bounds with { MaximumFileUtf8Bytes = 0 },
            ("total-bytes", false) => bounds with { MaximumTotalFileUtf8Bytes = 0 },
            ("file-count", true) => bounds with { MaximumFileCount = bounds.MaximumFileCount + 1 },
            ("path-bytes", true) => bounds with { MaximumPathUtf8Bytes = bounds.MaximumPathUtf8Bytes + 1 },
            ("file-bytes", true) => bounds with { MaximumFileUtf8Bytes = bounds.MaximumFileUtf8Bytes + 1 },
            ("total-bytes", true) => bounds with { MaximumTotalFileUtf8Bytes = bounds.MaximumTotalFileUtf8Bytes + 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(dimension)),
        };
        var expectation = SkillExpectation() with { PackageBounds = bounds };

        Action act = () => _verifier.VerifySkill(SkillRelease(), expectation);

        act.Should().Throw<ExactRemoteFetchException>()
            .Which.FailureKind.Should().Be(ExactRemoteFetchFailureKind.IntegrityMismatch);
    }

    [Fact]
    public void VerifySkill_WhenToolSetDiffersOrContainsDuplicates_RejectsRelease()
    {
        var different = SkillExpectation() with
        {
            DeclaredTools = [new ExactRemoteToolDeclaration("different", "builtin", [])],
        };
        var duplicateTool = Tool();
        var duplicates = SkillExpectation() with { DeclaredTools = [duplicateTool, duplicateTool] };

        Action differentAct = () => _verifier.VerifySkill(SkillRelease(), different);
        Action duplicatesAct = () => _verifier.VerifySkill(SkillRelease(), duplicates);

        differentAct.Should().Throw<ExactRemoteFetchException>();
        duplicatesAct.Should().Throw<ExactRemoteFetchException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VerifySkill_WhenActualOrExpectedToolsContainDuplicates_RejectsRelease(
        bool duplicateActual)
    {
        var duplicateTool = Tool();
        var release = duplicateActual
            ? SkillRelease() with { DeclaredTools = [duplicateTool, duplicateTool] }
            : SkillRelease();
        var expectation = duplicateActual
            ? SkillExpectation()
            : SkillExpectation() with { DeclaredTools = [duplicateTool, duplicateTool] };

        Action act = () => _verifier.VerifySkill(release, expectation);

        act.Should().Throw<ExactRemoteFetchException>()
            .Which.FailureKind.Should().Be(ExactRemoteFetchFailureKind.IntegrityMismatch);
    }

    [Fact]
    public void VerifySkill_WhenToolDeclarationsOnlyShareADelimiterKey_RejectsRelease()
    {
        var release = SkillRelease() with
        {
            DeclaredTools = [new ExactRemoteToolDeclaration("a", "b\u001fc", [])],
        };
        var expectation = SkillExpectation() with
        {
            DeclaredTools = [new ExactRemoteToolDeclaration("a\u001fb", "c", [])],
        };

        Action act = () => _verifier.VerifySkill(release, expectation);

        act.Should().Throw<ExactRemoteFetchException>()
            .Which.FailureKind.Should().Be(ExactRemoteFetchFailureKind.IntegrityMismatch);
    }

    [Fact]
    public void VerifySkillset_WhenEveryReviewedFieldMatches_ReturnsFetchedRelease()
    {
        var release = SkillsetRelease();

        var verified = _verifier.VerifySkillset(release, SkillsetExpectation());

        verified.Should().BeSameAs(release);
    }

    [Fact]
    public void VerifySkillset_WhenReferenceOrNameDiffers_RejectsRelease()
    {
        var wrongReference = SkillsetExpectation() with
        {
            Reference = new ExactRemoteSkillsetRef { Guid = SkillsetGuid, LiteralVersion = "2.1" },
        };
        var wrongName = SkillsetExpectation() with { PublishedName = "different" };

        Action referenceAct = () => _verifier.VerifySkillset(SkillsetRelease(), wrongReference);
        Action nameAct = () => _verifier.VerifySkillset(SkillsetRelease(), wrongName);

        referenceAct.Should().Throw<ExactRemoteFetchException>()
            .Which.FailureKind.Should().Be(ExactRemoteFetchFailureKind.IntegrityMismatch);
        nameAct.Should().Throw<ExactRemoteFetchException>()
            .Which.FailureKind.Should().Be(ExactRemoteFetchFailureKind.IntegrityMismatch);
    }

    [Theory]
    [InlineData("subject")]
    [InlineData("email-absence")]
    [InlineData("email-value")]
    [InlineData("display-absence")]
    [InlineData("display-value")]
    [InlineData("published-at")]
    public void VerifySkillset_WhenAnyPublisherSnapshotFieldDiffers_RejectsRelease(string mismatch)
    {
        var expectation = SkillsetExpectation() with { Provenance = ProvenanceWithMismatch(mismatch) };

        Action act = () => _verifier.VerifySkillset(SkillsetRelease(), expectation);

        act.Should().Throw<ExactRemoteFetchException>()
            .Which.FailureKind.Should().Be(ExactRemoteFetchFailureKind.IntegrityMismatch);
    }

    [Fact]
    public void VerifySkillset_WhenDirectMembersOrClosureDiffer_RejectsRelease()
    {
        var differentDirect = SkillsetExpectation() with
        {
            DirectMembers = [Member("1.1")],
        };
        var differentClosure = SkillsetExpectation() with
        {
            FullClosure = [Member("1.0"), Member("1.0")],
        };

        Action directAct = () => _verifier.VerifySkillset(SkillsetRelease(), differentDirect);
        Action closureAct = () => _verifier.VerifySkillset(SkillsetRelease(), differentClosure);

        directAct.Should().Throw<ExactRemoteFetchException>();
        closureAct.Should().Throw<ExactRemoteFetchException>();
    }

    [Theory]
    [InlineData("direct-members", false)]
    [InlineData("direct-members", true)]
    [InlineData("full-closure", false)]
    [InlineData("full-closure", true)]
    public void VerifySkillset_WhenActualOrExpectedReferenceSetContainsDuplicates_RejectsRelease(
        string field,
        bool duplicateActual)
    {
        var duplicateMembers = new[] { Member("1.0"), Member("1.0") };
        var release = (field, duplicateActual) switch
        {
            ("direct-members", true) => SkillsetRelease() with { DirectMembers = duplicateMembers },
            ("full-closure", true) => SkillsetRelease() with { FullClosure = duplicateMembers },
            _ => SkillsetRelease(),
        };
        var expectation = (field, duplicateActual) switch
        {
            ("direct-members", false) => SkillsetExpectation() with { DirectMembers = duplicateMembers },
            ("full-closure", false) => SkillsetExpectation() with { FullClosure = duplicateMembers },
            ("direct-members", true) or ("full-closure", true) => SkillsetExpectation(),
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        Action act = () => _verifier.VerifySkillset(release, expectation);

        act.Should().Throw<ExactRemoteFetchException>()
            .Which.FailureKind.Should().Be(ExactRemoteFetchFailureKind.IntegrityMismatch);
    }

    [Fact]
    public void VerifyMethods_WhenTopLevelArgumentIsNull_ThrowArgumentNullException()
    {
        Action nullSkillRelease = () => _verifier.VerifySkill(null!, SkillExpectation());
        Action nullSkillExpectation = () => _verifier.VerifySkill(SkillRelease(), null!);
        Action nullSkillsetRelease = () => _verifier.VerifySkillset(null!, SkillsetExpectation());
        Action nullSkillsetExpectation = () => _verifier.VerifySkillset(SkillsetRelease(), null!);

        nullSkillRelease.Should().Throw<ArgumentNullException>().WithParameterName("release");
        nullSkillExpectation.Should().Throw<ArgumentNullException>().WithParameterName("expectation");
        nullSkillsetRelease.Should().Throw<ArgumentNullException>().WithParameterName("release");
        nullSkillsetExpectation.Should().Throw<ArgumentNullException>().WithParameterName("expectation");
    }

    [Fact]
    public void ReleaseEvidence_ContainsNoAuthorizationFields()
    {
        typeof(ExactRemoteVersionProvenance).GetProperties().Select(static property => property.Name)
            .Should().BeEquivalentTo(
                nameof(ExactRemoteVersionProvenance.PublisherSubjectId),
                nameof(ExactRemoteVersionProvenance.PublisherEmailSnapshot),
                nameof(ExactRemoteVersionProvenance.PublisherDisplayNameSnapshot),
                nameof(ExactRemoteVersionProvenance.PublishedAt));
        typeof(ExactRemoteToolDeclaration).GetProperties().Select(static property => property.Name)
            .Should().BeEquivalentTo(
                nameof(ExactRemoteToolDeclaration.Tool),
                nameof(ExactRemoteToolDeclaration.Type),
                nameof(ExactRemoteToolDeclaration.McpServers));

        _verifier.VerifySkill(SkillRelease(), SkillExpectation()).Should().NotBeNull();
    }

    private static ExactRemoteSkillRelease SkillRelease() => new(
        SkillReference(),
        "curated-skill",
        Provenance(),
        new ExactRemotePackage(
            new Dictionary<string, string> { ["SKILL.md"] = "Run it." },
            new ExactRemotePackageShape(1, 8, 7, 7)),
        [Tool()],
        new SkillDefinition
        {
            Name = "curated-skill",
            Description = "Reviewed",
            Instructions = "Run it.",
            Source = SkillSource.Remote,
            RemoteId = SkillGuid,
        });

    private static ReviewedExactRemoteSkillExpectation SkillExpectation() => new(
        SkillReference(),
        "curated-skill",
        Provenance(),
        new ExactRemotePackageBounds(10, 64, 1024, 2048),
        [Tool()]);

    private static ExactRemoteSkillsetRelease SkillsetRelease() => new(
        SkillsetReference(),
        "reviewed-set",
        Provenance(),
        "Use reviewed skills.",
        [Member("1.0")],
        [Member("1.0")]);

    private static ReviewedExactRemoteSkillsetExpectation SkillsetExpectation() => new(
        SkillsetReference(),
        "reviewed-set",
        Provenance(),
        [Member("1.0")],
        [Member("1.0")]);

    private static ExactRemoteVersionProvenance Provenance() => new(
        "publisher-subject",
        "publisher@example.test",
        "Publisher Name",
        PublishedAt);

    private static ExactRemoteVersionProvenance ProvenanceWithMismatch(string mismatch) => mismatch switch
    {
        "subject" => Provenance() with { PublisherSubjectId = "other-subject" },
        "email-absence" => Provenance() with { PublisherEmailSnapshot = null },
        "email-value" => Provenance() with { PublisherEmailSnapshot = "other@example.test" },
        "display-absence" => Provenance() with { PublisherDisplayNameSnapshot = null },
        "display-value" => Provenance() with { PublisherDisplayNameSnapshot = "Other Publisher" },
        "published-at" => Provenance() with { PublishedAt = PublishedAt.AddSeconds(1) },
        _ => throw new ArgumentOutOfRangeException(nameof(mismatch)),
    };

    private static ExactRemoteToolDeclaration Tool() => new(
        "workspace.read",
        "mcp",
        [new ExactRemoteMcpServerDeclaration("workspace-mcp", "2.0")]);

    private static ExactRemoteSkillRef SkillReference() => new()
    {
        Guid = SkillGuid,
        LiteralVersion = "1.2",
    };

    private static ExactRemoteSkillsetRef SkillsetReference() => new()
    {
        Guid = SkillsetGuid,
        LiteralVersion = "2.0",
    };

    private static ExactRemoteSkillRef Member(string version) => new()
    {
        Guid = MemberGuid,
        LiteralVersion = version,
    };
}
