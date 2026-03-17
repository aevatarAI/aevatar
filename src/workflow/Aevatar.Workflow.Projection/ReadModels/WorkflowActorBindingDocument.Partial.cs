using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Projection.ReadModels;

public sealed partial class WorkflowActorBindingDocument : IProjectionReadModel<WorkflowActorBindingDocument>
{
    public DateTimeOffset CreatedAt
    {
        get => WorkflowActorBindingDocumentSupport.ToDateTimeOffset(CreatedAtUtcValue);
        set => CreatedAtUtcValue = WorkflowActorBindingDocumentSupport.ToTimestamp(value);
    }

    public DateTimeOffset UpdatedAt
    {
        get => WorkflowActorBindingDocumentSupport.ToDateTimeOffset(UpdatedAtUtcValue);
        set => UpdatedAtUtcValue = WorkflowActorBindingDocumentSupport.ToTimestamp(value);
    }

    public WorkflowActorKind ActorKind
    {
        get => (WorkflowActorKind)ActorKindValue;
        set => ActorKindValue = (int)value;
    }

    public IDictionary<string, string> InlineWorkflowYamls
    {
        get => InlineWorkflowYamlEntries;
        set => WorkflowActorBindingDocumentSupport.ReplaceMap(InlineWorkflowYamlEntries, value);
    }
}

internal static class WorkflowActorBindingDocumentSupport
{
    public static Timestamp ToTimestamp(DateTimeOffset value) =>
        Timestamp.FromDateTimeOffset(value.ToUniversalTime());

    public static DateTimeOffset ToDateTimeOffset(Timestamp? value) =>
        value == null ? default : value.ToDateTimeOffset();

    public static void ReplaceMap<TKey, TValue>(
        MapField<TKey, TValue> target,
        IEnumerable<KeyValuePair<TKey, TValue>>? source)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(target);

        target.Clear();
        if (source == null)
            return;

        foreach (var (key, value) in source)
            target[key] = value;
    }
}
