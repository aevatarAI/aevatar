using System.Text;
using Google.Protobuf;

namespace Aevatar.GAgentService.Abstractions.AgentProfiles;

public sealed record CreateAgentProfileRequest(
    string ProfileSlug,
    string? OwnerHandle,
    string DisplayName,
    string Purpose,
    string Instructions,
    AgentProfileToolPolicy ToolPolicy);

public sealed record UpdateAgentProfileDraftRequest(
    string DisplayName,
    string Purpose,
    string Instructions,
    AgentProfileToolPolicy ToolPolicy);

public sealed record UpsertAgentProfileSkillBindingRequest(
    AgentProfileSkillActivationMode ActivationMode,
    ExactOrnnSkillReference Skill);

public sealed record AgentProfileSkillResolutionSummary(
    string BindingId,
    ExactOrnnSkillReference ExactReference,
    ByteString ContentSha256)
{
    public AgentProfileSkillResolutionSummary DeepClone() =>
        this with { ExactReference = ExactReference.Clone() };
}

public sealed record AgentProfileValidationReport(
    bool Valid,
    long DraftRevision,
    ByteString DraftSha256,
    IReadOnlyList<AgentProfileSafeDiagnostic> Diagnostics,
    IReadOnlyList<AgentProfileSkillResolutionSummary> ResolvedSkills)
{
    public AgentProfileValidationReport DeepClone() =>
        this with
        {
            Diagnostics = Diagnostics.Select(static diagnostic => diagnostic.Clone()).ToArray(),
            ResolvedSkills = ResolvedSkills.Select(static skill => skill.DeepClone()).ToArray(),
        };
}

public sealed record AgentProfileNamespaceEntrySnapshot(
    long AuthorityStateVersion,
    string LastEventId,
    string ProfileId,
    AgentProfileReference Reference,
    AgentProfileOwnerIdentity Owner,
    string OwningScopeId,
    AgentProfileProvisioningStatus Status,
    AgentProfilePublishedSummary? PublishedSummary)
{
    public AgentProfileNamespaceEntrySnapshot DeepClone() =>
        this with
        {
            Reference = Reference.Clone(),
            Owner = Owner.Clone(),
            PublishedSummary = PublishedSummary?.Clone(),
        };
}

public sealed record AgentProfileManagementSnapshot(
    long AuthorityStateVersion,
    string LastEventId,
    AgentProfileIdentity Identity,
    AgentProfileContent Draft,
    long DraftRevision,
    ByteString DraftSha256,
    long PublishedRevision,
    ByteString PublishedSnapshotSha256,
    ByteString PublishedSourceDraftSha256,
    AgentProfileMutationOutcome? LastMutation)
{
    public string ProfileId => Identity.ProfileId;

    public AgentProfileManagementSnapshot DeepClone() =>
        this with
        {
            Identity = Identity.Clone(),
            Draft = Draft.Clone(),
            LastMutation = LastMutation?.Clone(),
        };
}

public sealed record AgentProfileExecutionSnapshot(
    long AuthorityStateVersion,
    string LastEventId,
    AgentProfilePublishedSnapshot Snapshot)
{
    public string ProfileId => Snapshot.Identity?.ProfileId ?? string.Empty;

    public AgentProfileExecutionSnapshot DeepClone() =>
        this with { Snapshot = Snapshot.Clone() };
}

public sealed record AgentProfileDiscoverySnapshot(
    AgentProfileReference Reference,
    string DisplayName,
    string Purpose,
    long PublishedRevision,
    bool Available)
{
    public AgentProfileDiscoverySnapshot DeepClone() =>
        this with { Reference = Reference.Clone() };
}

public sealed record AgentProfileActorTargets(
    string NamespaceActorId,
    string ProfileActorId);

public sealed class ExactOrnnSkillResolutionResult
{
    private readonly ResolvedOrnnSkillPackage? _package;
    private readonly AgentProfileSafeDiagnostic? _failure;

    private ExactOrnnSkillResolutionResult(
        ResolvedOrnnSkillPackage? package,
        AgentProfileSafeDiagnostic? failure)
    {
        _package = package?.Clone();
        _failure = failure?.Clone();
    }

    public bool IsSuccess => _package is not null;

    public ResolvedOrnnSkillPackage? Package => _package?.Clone();

    public AgentProfileSafeDiagnostic? Failure => _failure?.Clone();

    public static ExactOrnnSkillResolutionResult Success(ResolvedOrnnSkillPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return new ExactOrnnSkillResolutionResult(package, null);
    }

    public static ExactOrnnSkillResolutionResult Failed(
        string code,
        string message = "",
        string path = "") =>
        new(
            null,
            new AgentProfileSafeDiagnostic
            {
                Code = BoundText(code, 128),
                Message = BoundText(message, 512),
                Path = BoundText(path, 512),
            });

    private static string BoundText(string? value, int maxUtf8Bytes)
    {
        var normalized = (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Normalize(NormalizationForm.FormC);
        var bytes = Encoding.UTF8.GetBytes(normalized);
        if (bytes.Length <= maxUtf8Bytes)
            return normalized;

        var length = maxUtf8Bytes;
        while (length > 0 && (bytes[length] & 0xC0) == 0x80)
            length--;
        return Encoding.UTF8.GetString(bytes, 0, length);
    }
}

public sealed class AgentProfileContractValidationException : ArgumentException
{
    public AgentProfileContractValidationException(
        IReadOnlyList<AgentProfileSafeDiagnostic> diagnostics)
        : base(diagnostics.Count == 0
            ? "Agent Profile contract validation failed."
            : diagnostics[0].Code)
    {
        Diagnostics = diagnostics.Select(static diagnostic => diagnostic.Clone()).ToArray();
    }

    public IReadOnlyList<AgentProfileSafeDiagnostic> Diagnostics { get; }
}
