using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions;

namespace Aevatar.GAgents.NyxidChat.AgentProfiles;

public interface INyxIdChatAgentProfileResolver
{
    Task<NyxIdChatAgentProfileResolution> ResolveAsync(
        NyxIdChatAgentProfileSelectionRequest request,
        CancellationToken ct = default);
}

public sealed record NyxIdChatAgentProfileSelectionRequest(
    string ScopeId,
    string ConversationActorId,
    AgentProfileReference? ExplicitReference);

public enum NyxIdChatAgentProfileSelectionSource
{
    None = 0,
    ExplicitCallerReference = 1,
    ExplicitSystemReference = 2,
    ScopeDefault = 3,
    SystemDefault = 4,
}

public enum NyxIdChatAgentProfileResolutionStatus
{
    Unprofiled = 0,
    Selected = 1,
    ExplicitReferenceInvalid = 2,
    BindingUnavailable = 3,
    ProfileUnavailable = 4,
    ProfileNotPublished = 5,
    ReadModelUnavailable = 6,
    SnapshotDigestMismatch = 7,
}

public sealed class NyxIdChatAgentProfileResolution
{
    private readonly AgentProfileSnapshot? _profile;

    private NyxIdChatAgentProfileResolution(
        NyxIdChatAgentProfileResolutionStatus status,
        NyxIdChatAgentProfileSelectionSource source,
        AgentProfileSnapshot? profile)
    {
        Status = status;
        Source = source;
        _profile = profile?.Clone();
    }

    public NyxIdChatAgentProfileResolutionStatus Status { get; }
    public NyxIdChatAgentProfileSelectionSource Source { get; }
    public AgentProfileSnapshot? Profile => _profile?.Clone();
    public bool IsSelected => Status == NyxIdChatAgentProfileResolutionStatus.Selected;
    public bool IsFailure => Status is not (
        NyxIdChatAgentProfileResolutionStatus.Unprofiled or
        NyxIdChatAgentProfileResolutionStatus.Selected);

    public static NyxIdChatAgentProfileResolution Selected(
        AgentProfileSnapshot profile,
        NyxIdChatAgentProfileSelectionSource source)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (source == NyxIdChatAgentProfileSelectionSource.None)
            throw new ArgumentException("A selected profile requires a selection source.", nameof(source));

        return new NyxIdChatAgentProfileResolution(
            NyxIdChatAgentProfileResolutionStatus.Selected,
            source,
            profile);
    }

    public static NyxIdChatAgentProfileResolution Unprofiled() => new(
        NyxIdChatAgentProfileResolutionStatus.Unprofiled,
        NyxIdChatAgentProfileSelectionSource.None,
        null);

    public static NyxIdChatAgentProfileResolution Failure(
        NyxIdChatAgentProfileResolutionStatus status,
        NyxIdChatAgentProfileSelectionSource source = NyxIdChatAgentProfileSelectionSource.None)
    {
        if (status is NyxIdChatAgentProfileResolutionStatus.Unprofiled or
            NyxIdChatAgentProfileResolutionStatus.Selected)
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Failure status is required.");
        }

        return new NyxIdChatAgentProfileResolution(status, source, null);
    }
}
