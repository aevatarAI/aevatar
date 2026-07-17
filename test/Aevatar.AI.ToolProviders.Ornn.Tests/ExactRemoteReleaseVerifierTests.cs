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
        var provenance = mismatch switch
        {
            "subject" => Provenance() with { PublisherSubjectId = "other-subject" },
            "email-absence" => Provenance() with { PublisherEmailSnapshot = null },
            "email-value" => Provenance() with { PublisherEmailSnapshot = "other@example.test" },
            "display-absence" => Provenance() with { PublisherDisplayNameSnapshot = null },
            "display-value" => Provenance() with { PublisherDisplayNameSnapshot = "Other Publisher" },
            "published-at" => Provenance() with { PublishedAt = PublishedAt.AddSeconds(1) },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch)),
        };
        var expectation = SkillExpectation() with { Provenance = provenance };

        Action act = () => _verifier.VerifySkill(SkillRelease(), expectation);

        act.Should().Throw<ExactRemoteFetchException>()
            .Which.Message.Should().Contain("provenance");
    }

    [Fact]
    public void VerifySkill_WhenPackageExceedsReviewedBoundsOrBoundsExpandAdapterCeiling_RejectsRelease()
    {
        var tooTight = SkillExpectation() with
        {
            PackageBounds = new ExactRemotePackageBounds(1, 10, 3, 3),
        };
        var expanded = SkillExpectation() with
        {
            PackageBounds = ExactRemotePackageBounds.AdapterMaximum with
            {
                MaximumFileCount = ExactRemotePackageBounds.AdapterMaximum.MaximumFileCount + 1,
            },
        };

        Action tightAct = () => _verifier.VerifySkill(SkillRelease(), tooTight);
        Action expandedAct = () => _verifier.VerifySkill(SkillRelease(), expanded);

        tightAct.Should().Throw<ExactRemoteFetchException>();
        expandedAct.Should().Throw<ExactRemoteFetchException>();
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
