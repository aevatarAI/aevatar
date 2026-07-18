using Aevatar.AI.Abstractions;

namespace Aevatar.AI.ToolProviders.Skills;

/// <summary>Verifies fetched releases against caller-reviewed immutable expectations.</summary>
public sealed class ExactRemoteReleaseVerifier
{
    public ExactRemoteSkillRelease VerifySkill(
        ExactRemoteSkillRelease release,
        ReviewedExactRemoteSkillExpectation expectation)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(expectation);

        var reference = expectation.Reference;
        VerifyReference(release.Reference, reference, ExactRemoteResourceKind.Skill);
        VerifyText(release.PublishedName, expectation.PublishedName, "published name", reference);
        VerifyProvenance(release.Provenance, expectation.Provenance, ExactRemoteResourceKind.Skill, reference.Guid, reference.LiteralVersion);
        VerifyBounds(release.Package.Shape, expectation.PackageBounds, reference);
        VerifyTools(release.DeclaredTools, expectation.DeclaredTools, reference);
        return release;
    }

    public ExactRemoteSkillsetRelease VerifySkillset(
        ExactRemoteSkillsetRelease release,
        ReviewedExactRemoteSkillsetExpectation expectation)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(expectation);

        var reference = expectation.Reference;
        VerifyReference(release.Reference, reference, ExactRemoteResourceKind.Skillset);
        VerifyText(release.PublishedName, expectation.PublishedName, "published name", reference);
        VerifyProvenance(release.Provenance, expectation.Provenance, ExactRemoteResourceKind.Skillset, reference.Guid, reference.LiteralVersion);
        VerifyExactRefs(release.DirectMembers, expectation.DirectMembers, "direct members", reference);
        VerifyExactRefs(release.FullClosure, expectation.FullClosure, "full closure", reference);
        return release;
    }

    private static void VerifyReference(
        ExactRemoteSkillRef actual,
        ExactRemoteSkillRef expected,
        ExactRemoteResourceKind resourceKind)
    {
        if (!string.Equals(actual.Guid, expected.Guid, StringComparison.Ordinal) ||
            !string.Equals(actual.LiteralVersion, expected.LiteralVersion, StringComparison.Ordinal))
        {
            throw ExactRemoteFetchException.IntegrityMismatch(
                resourceKind,
                expected.Guid,
                expected.LiteralVersion,
                "exact reference differs from the reviewed reference");
        }
    }

    private static void VerifyReference(
        ExactRemoteSkillsetRef actual,
        ExactRemoteSkillsetRef expected,
        ExactRemoteResourceKind resourceKind)
    {
        if (!string.Equals(actual.Guid, expected.Guid, StringComparison.Ordinal) ||
            !string.Equals(actual.LiteralVersion, expected.LiteralVersion, StringComparison.Ordinal))
        {
            throw ExactRemoteFetchException.IntegrityMismatch(
                resourceKind,
                expected.Guid,
                expected.LiteralVersion,
                "exact reference differs from the reviewed reference");
        }
    }

    private static void VerifyText(
        string actual,
        string expected,
        string field,
        ExactRemoteSkillRef reference)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw ExactRemoteFetchException.IntegrityMismatch(
                ExactRemoteResourceKind.Skill,
                reference.Guid,
                reference.LiteralVersion,
                $"{field} differs from the reviewed value");
        }
    }

    private static void VerifyText(
        string actual,
        string expected,
        string field,
        ExactRemoteSkillsetRef reference)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw ExactRemoteFetchException.IntegrityMismatch(
                ExactRemoteResourceKind.Skillset,
                reference.Guid,
                reference.LiteralVersion,
                $"{field} differs from the reviewed value");
        }
    }

    private static void VerifyProvenance(
        ExactRemoteVersionProvenance actual,
        ExactRemoteVersionProvenance expected,
        ExactRemoteResourceKind resourceKind,
        string guid,
        string literalVersion)
    {
        var matches = string.Equals(actual.PublisherSubjectId, expected.PublisherSubjectId, StringComparison.Ordinal) &&
                      string.Equals(actual.PublisherEmailSnapshot, expected.PublisherEmailSnapshot, StringComparison.Ordinal) &&
                      string.Equals(actual.PublisherDisplayNameSnapshot, expected.PublisherDisplayNameSnapshot, StringComparison.Ordinal) &&
                      actual.PublishedAt.ToUniversalTime() == expected.PublishedAt.ToUniversalTime();
        if (!matches)
        {
            throw ExactRemoteFetchException.IntegrityMismatch(
                resourceKind,
                guid,
                literalVersion,
                "version publisher provenance differs from the reviewed snapshot");
        }
    }

    private static void VerifyBounds(
        ExactRemotePackageShape shape,
        ExactRemotePackageBounds bounds,
        ExactRemoteSkillRef reference)
    {
        var boundsArePositive = bounds.MaximumFileCount > 0 &&
                                bounds.MaximumPathUtf8Bytes > 0 &&
                                bounds.MaximumFileUtf8Bytes > 0 &&
                                bounds.MaximumTotalFileUtf8Bytes > 0;
        var shapeFits = shape.FileCount <= bounds.MaximumFileCount &&
                        shape.MaximumPathUtf8Bytes <= bounds.MaximumPathUtf8Bytes &&
                        shape.MaximumFileUtf8Bytes <= bounds.MaximumFileUtf8Bytes &&
                        shape.TotalFileUtf8Bytes <= bounds.MaximumTotalFileUtf8Bytes;
        if (!boundsArePositive || !shapeFits)
        {
            throw ExactRemoteFetchException.IntegrityMismatch(
                ExactRemoteResourceKind.Skill,
                reference.Guid,
                reference.LiteralVersion,
                "package shape exceeds the reviewed bounds or the reviewed bounds are not positive");
        }
    }

    private static void VerifyTools(
        IReadOnlyList<ExactRemoteToolDeclaration> actual,
        IReadOnlyList<ExactRemoteToolDeclaration> expected,
        ExactRemoteSkillRef reference)
    {
        var actualDeclarations = ToolDeclarations(actual);
        var expectedDeclarations = ToolDeclarations(expected);
        if (actualDeclarations.Count != actual.Count || expectedDeclarations.Count != expected.Count ||
            !actualDeclarations.SetEquals(expectedDeclarations))
        {
            throw ExactRemoteFetchException.IntegrityMismatch(
                ExactRemoteResourceKind.Skill,
                reference.Guid,
                reference.LiteralVersion,
                "declared tools differ from the reviewed set");
        }
    }

    private static HashSet<ExactRemoteToolDeclaration> ToolDeclarations(
        IReadOnlyList<ExactRemoteToolDeclaration> tools) =>
        tools.ToHashSet(ExactRemoteToolDeclarationComparer.Instance);

    private static void VerifyExactRefs(
        IReadOnlyList<ExactRemoteSkillRef> actual,
        IReadOnlyList<ExactRemoteSkillRef> expected,
        string field,
        ExactRemoteSkillsetRef reference)
    {
        var actualKeys = RefKeys(actual);
        var expectedKeys = RefKeys(expected);
        if (actualKeys.Count != actual.Count || expectedKeys.Count != expected.Count ||
            !actualKeys.SetEquals(expectedKeys))
        {
            throw ExactRemoteFetchException.IntegrityMismatch(
                ExactRemoteResourceKind.Skillset,
                reference.Guid,
                reference.LiteralVersion,
                $"{field} differ from the reviewed set");
        }
    }

    private static HashSet<(string Guid, string LiteralVersion)> RefKeys(
        IReadOnlyList<ExactRemoteSkillRef> references) =>
        references.Select(static reference => (reference.Guid, reference.LiteralVersion)).ToHashSet();
}
