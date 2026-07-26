using System.Text;
using Google.Protobuf;

namespace Aevatar.GAgentService.Abstractions.AgentProfiles;

public sealed record CreateAgentProfileRequest(
    string ProfileSlug,
    string? OwnerHandle,
    string DisplayName,
    string Purpose,
    string Instructions,
    AgentProfileToolPolicy ToolPolicy,
    AgentProfileToolPolicy? RecoveryToolPolicy = null)
{
    private AgentProfileToolPolicy _toolPolicy = ToolPolicy.Clone();
    private AgentProfileToolPolicy? _recoveryToolPolicy = RecoveryToolPolicy?.Clone();

    public AgentProfileToolPolicy ToolPolicy
    {
        get => _toolPolicy.Clone();
        init => _toolPolicy = value.Clone();
    }

    public AgentProfileToolPolicy? RecoveryToolPolicy
    {
        get => _recoveryToolPolicy?.Clone();
        init => _recoveryToolPolicy = value?.Clone();
    }
}

public sealed record UpdateAgentProfileDraftRequest(
    string DisplayName,
    string Purpose,
    string Instructions,
    AgentProfileToolPolicy ToolPolicy,
    AgentProfileToolPolicy? RecoveryToolPolicy = null)
{
    private AgentProfileToolPolicy _toolPolicy = ToolPolicy.Clone();
    private AgentProfileToolPolicy? _recoveryToolPolicy = RecoveryToolPolicy?.Clone();

    public AgentProfileToolPolicy ToolPolicy
    {
        get => _toolPolicy.Clone();
        init => _toolPolicy = value.Clone();
    }

    public AgentProfileToolPolicy? RecoveryToolPolicy
    {
        get => _recoveryToolPolicy?.Clone();
        init => _recoveryToolPolicy = value?.Clone();
    }
}

public sealed record UpsertAgentProfileSkillBindingRequest(
    AgentProfileSkillActivationMode ActivationMode,
    ExactOrnnSkillReference Skill,
    AgentProfileSkillRoutingPolicy? RoutingPolicy = null)
{
    private ExactOrnnSkillReference _skill = Skill.Clone();
    private AgentProfileSkillRoutingPolicy? _routingPolicy = RoutingPolicy?.Clone();

    public ExactOrnnSkillReference Skill
    {
        get => _skill.Clone();
        init => _skill = value.Clone();
    }

    public AgentProfileSkillRoutingPolicy? RoutingPolicy
    {
        get => _routingPolicy?.Clone();
        init => _routingPolicy = value?.Clone();
    }
}

public sealed record AgentProfileSkillResolutionSummary(
    string BindingId,
    ExactOrnnSkillReference ExactReference,
    ByteString ContentSha256)
{
    private ExactOrnnSkillReference _exactReference = ExactReference.Clone();

    public ExactOrnnSkillReference ExactReference
    {
        get => _exactReference.Clone();
        init => _exactReference = value.Clone();
    }

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
    private IReadOnlyList<AgentProfileSafeDiagnostic> _diagnostics =
        Diagnostics.Select(static diagnostic => diagnostic.Clone()).ToArray();
    private IReadOnlyList<AgentProfileSkillResolutionSummary> _resolvedSkills =
        ResolvedSkills.Select(static skill => skill.DeepClone()).ToArray();

    public IReadOnlyList<AgentProfileSafeDiagnostic> Diagnostics
    {
        get => _diagnostics.Select(static diagnostic => diagnostic.Clone()).ToArray();
        init => _diagnostics = value.Select(static diagnostic => diagnostic.Clone()).ToArray();
    }

    public IReadOnlyList<AgentProfileSkillResolutionSummary> ResolvedSkills
    {
        get => _resolvedSkills.Select(static skill => skill.DeepClone()).ToArray();
        init => _resolvedSkills = value.Select(static skill => skill.DeepClone()).ToArray();
    }

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
    private AgentProfileReference _reference = Reference.Clone();
    private AgentProfileOwnerIdentity _owner = Owner.Clone();
    private AgentProfilePublishedSummary? _publishedSummary = PublishedSummary?.Clone();

    public AgentProfileReference Reference
    {
        get => _reference.Clone();
        init => _reference = value.Clone();
    }

    public AgentProfileOwnerIdentity Owner
    {
        get => _owner.Clone();
        init => _owner = value.Clone();
    }

    public AgentProfilePublishedSummary? PublishedSummary
    {
        get => _publishedSummary?.Clone();
        init => _publishedSummary = value?.Clone();
    }

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
    private AgentProfileIdentity _identity = Identity.Clone();
    private AgentProfileContent _draft = Draft.Clone();
    private AgentProfileMutationOutcome? _lastMutation = LastMutation?.Clone();

    public AgentProfileIdentity Identity
    {
        get => _identity.Clone();
        init => _identity = value.Clone();
    }

    public AgentProfileContent Draft
    {
        get => _draft.Clone();
        init => _draft = value.Clone();
    }

    public AgentProfileMutationOutcome? LastMutation
    {
        get => _lastMutation?.Clone();
        init => _lastMutation = value?.Clone();
    }

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
    private AgentProfilePublishedSnapshot _snapshot = Snapshot.Clone();

    public AgentProfilePublishedSnapshot Snapshot
    {
        get => _snapshot.Clone();
        init => _snapshot = value.Clone();
    }

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
    private AgentProfileReference _reference = Reference.Clone();

    public AgentProfileReference Reference
    {
        get => _reference.Clone();
        init => _reference = value.Clone();
    }

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
    private readonly IReadOnlyList<AgentProfileSafeDiagnostic> _diagnostics;

    public AgentProfileContractValidationException(
        IReadOnlyList<AgentProfileSafeDiagnostic> diagnostics)
        : base(diagnostics.Count == 0
            ? "Agent Profile contract validation failed."
            : diagnostics[0].Code)
    {
        _diagnostics = diagnostics.Select(static diagnostic => diagnostic.Clone()).ToArray();
    }

    public IReadOnlyList<AgentProfileSafeDiagnostic> Diagnostics =>
        _diagnostics.Select(static diagnostic => diagnostic.Clone()).ToArray();
}
