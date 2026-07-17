using Aevatar.AI.Abstractions;

namespace Aevatar.AI.ToolProviders.Skills;

public sealed record ExactRemoteVersionProvenance(
    string PublisherSubjectId,
    string? PublisherEmailSnapshot,
    string? PublisherDisplayNameSnapshot,
    DateTimeOffset PublishedAt);

public sealed record ExactRemoteMcpServerDeclaration(string Mcp, string Version);

public sealed record ExactRemoteToolDeclaration(
    string Tool,
    string Type,
    IReadOnlyList<ExactRemoteMcpServerDeclaration> McpServers);

public sealed record ExactRemotePackageShape(
    int FileCount,
    int MaximumPathUtf8Bytes,
    long MaximumFileUtf8Bytes,
    long TotalFileUtf8Bytes);

public sealed record ExactRemotePackage(
    IReadOnlyDictionary<string, string> Files,
    ExactRemotePackageShape Shape);

public sealed record ExactRemotePackageBounds(
    int MaximumFileCount,
    int MaximumPathUtf8Bytes,
    long MaximumFileUtf8Bytes,
    long MaximumTotalFileUtf8Bytes)
{
    public static ExactRemotePackageBounds AdapterMaximum { get; } = new(
        MaximumFileCount: 1000,
        MaximumPathUtf8Bytes: 512,
        MaximumFileUtf8Bytes: 25L * 1024 * 1024,
        MaximumTotalFileUtf8Bytes: 50L * 1024 * 1024);
}

public sealed record ExactRemoteSkillRelease(
    ExactRemoteSkillRef Reference,
    string PublishedName,
    ExactRemoteVersionProvenance Provenance,
    ExactRemotePackage Package,
    IReadOnlyList<ExactRemoteToolDeclaration> DeclaredTools,
    SkillDefinition Definition);

public sealed record ExactRemoteSkillsetRelease(
    ExactRemoteSkillsetRef Reference,
    string PublishedName,
    ExactRemoteVersionProvenance Provenance,
    string Instructions,
    IReadOnlyList<ExactRemoteSkillRef> DirectMembers,
    IReadOnlyList<ExactRemoteSkillRef> FullClosure);

public sealed record ReviewedExactRemoteSkillExpectation(
    ExactRemoteSkillRef Reference,
    string PublishedName,
    ExactRemoteVersionProvenance Provenance,
    ExactRemotePackageBounds PackageBounds,
    IReadOnlyList<ExactRemoteToolDeclaration> DeclaredTools);

public sealed record ReviewedExactRemoteSkillsetExpectation(
    ExactRemoteSkillsetRef Reference,
    string PublishedName,
    ExactRemoteVersionProvenance Provenance,
    IReadOnlyList<ExactRemoteSkillRef> DirectMembers,
    IReadOnlyList<ExactRemoteSkillRef> FullClosure);
