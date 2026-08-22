using Aevatar.AI.Abstractions;
using Google.Protobuf;

namespace Aevatar.GAgentService.Abstractions.AgentProfiles;

/// <summary>
/// Resolves a version-pinned Ornn skill into the small, validated evidence set needed by Profile sealing.
/// </summary>
public interface IExactOrnnSkillResolver
{
    Task<ExactOrnnSkillResolutionResult> ResolveAsync(
        string nyxIdAccessToken,
        ExactRemoteSkillRef reference,
        CancellationToken ct = default);
}

public sealed record ResolvedOrnnSkillPackage
{
    public required string SkillGuid { get; init; }
    public required string LiteralVersion { get; init; }
    public required string CanonicalName { get; init; }
    public required string PublisherId { get; init; }
    public required ByteString SkillSha256 { get; init; }
    public required int SkillMarkdownUtf8Bytes { get; init; }
    public IReadOnlyList<string> DeclaredToolNames { get; init; } = [];
}

public sealed record ExactOrnnSkillResolutionResult(
    ResolvedOrnnSkillPackage? Package,
    string? DiagnosticCode)
{
    public bool IsSuccess => Package is not null && DiagnosticCode is null;

    public static ExactOrnnSkillResolutionResult Success(ResolvedOrnnSkillPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return new ExactOrnnSkillResolutionResult(package with
        {
            DeclaredToolNames = package.DeclaredToolNames
                .Order(StringComparer.Ordinal)
                .ToArray(),
        }, null);
    }

    public static ExactOrnnSkillResolutionResult Failure(string diagnosticCode) =>
        new(null, diagnosticCode);
}

public sealed record AgentProfileSealingDiagnostic(string Code, string Field, string Message);

public sealed record AgentProfileSealingContext(
    long CurrentDraftRevision,
    long NextPublishedRevision,
    DateTimeOffset PublishedAt,
    string? NyxIdAccessToken);

public sealed record AgentProfileSealedSkillEvidence(
    string IntentId,
    string SkillGuid,
    string LiteralVersion,
    ByteString SkillSha256);

public sealed record AgentProfileSealingResult(
    AgentProfilePublishedSnapshot? Snapshot,
    IReadOnlyList<AgentProfileSealingDiagnostic> Diagnostics)
{
    public bool IsSuccess => Snapshot is not null && Diagnostics.Count == 0;

    public static AgentProfileSealingResult Success(AgentProfilePublishedSnapshot snapshot) =>
        new(snapshot.Clone(), []);

    public static AgentProfileSealingResult Failure(IReadOnlyList<AgentProfileSealingDiagnostic> diagnostics) =>
        new(null, diagnostics.ToArray());
}

public interface IAgentProfileSkillSealer
{
    Task<AgentProfileSealingResult> ResolveAndSealAsync(
        AgentProfileIdentity identity,
        AgentProfileDraft draft,
        AgentProfileSealingContext context,
        CancellationToken ct = default);
}
