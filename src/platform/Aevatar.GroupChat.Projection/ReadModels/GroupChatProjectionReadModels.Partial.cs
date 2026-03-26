using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GroupChat.Projection.ReadModels;

public sealed partial class GroupTimelineReadModel : IProjectionReadModel<GroupTimelineReadModel>
{
    public DateTimeOffset UpdatedAt
    {
        get => GroupChatProjectionReadModelSupport.ToDateTimeOffset(UpdatedAtUtcValue);
        set => UpdatedAtUtcValue = GroupChatProjectionReadModelSupport.ToTimestamp(value);
    }

    public IList<string> ParticipantAgentIds
    {
        get => ParticipantAgentIdEntries;
        set => GroupChatProjectionReadModelSupport.ReplaceCollection(ParticipantAgentIdEntries, value);
    }

    public IList<GroupTimelineMessageReadModel> Messages
    {
        get => MessageEntries;
        set => GroupChatProjectionReadModelSupport.ReplaceCollection(MessageEntries, value);
    }

    public IList<GroupParticipantRuntimeBindingReadModel> ParticipantRuntimeBindings
    {
        get => ParticipantRuntimeBindingEntries;
        set => GroupChatProjectionReadModelSupport.ReplaceCollection(ParticipantRuntimeBindingEntries, value);
    }
}

public sealed partial class AgentFeedReadModel : IProjectionReadModel<AgentFeedReadModel>
{
    public DateTimeOffset UpdatedAt
    {
        get => GroupChatProjectionReadModelSupport.ToDateTimeOffset(UpdatedAtUtcValue);
        set => UpdatedAtUtcValue = GroupChatProjectionReadModelSupport.ToTimestamp(value);
    }

    public IList<AgentFeedItemReadModel> NextItems
    {
        get => NextItemEntries;
        set => GroupChatProjectionReadModelSupport.ReplaceCollection(NextItemEntries, value);
    }
}

public sealed partial class SourceCatalogReadModel : IProjectionReadModel<SourceCatalogReadModel>
{
    public DateTimeOffset UpdatedAt
    {
        get => GroupChatProjectionReadModelSupport.ToDateTimeOffset(UpdatedAtUtcValue);
        set => UpdatedAtUtcValue = GroupChatProjectionReadModelSupport.ToTimestamp(value);
    }
}

public sealed partial class GroupTimelineMessageReadModel
{
    public IList<GroupTimelineSourceRefReadModel> SourceRefs
    {
        get => SourceRefEntries;
        set => GroupChatProjectionReadModelSupport.ReplaceCollection(SourceRefEntries, value);
    }

    public IList<GroupTimelineEvidenceRefReadModel> EvidenceRefs
    {
        get => EvidenceRefEntries;
        set => GroupChatProjectionReadModelSupport.ReplaceCollection(EvidenceRefEntries, value);
    }

    public IList<string> DerivedFromSignalIds
    {
        get => DerivedFromSignalIdEntries;
        set => GroupChatProjectionReadModelSupport.ReplaceCollection(DerivedFromSignalIdEntries, value);
    }

    public IList<string> DirectHintAgentIds
    {
        get => DirectHintAgentIdEntries;
        set => GroupChatProjectionReadModelSupport.ReplaceCollection(DirectHintAgentIdEntries, value);
    }
}

internal static class GroupChatProjectionReadModelSupport
{
    public static Timestamp ToTimestamp(DateTimeOffset value) =>
        Timestamp.FromDateTimeOffset(value.ToUniversalTime());

    public static DateTimeOffset ToDateTimeOffset(Timestamp? value) =>
        value == null ? default : value.ToDateTimeOffset();

    public static void ReplaceCollection<T>(RepeatedField<T> target, IEnumerable<T>? source)
    {
        target.Clear();
        if (source != null)
            target.Add(source);
    }
}
