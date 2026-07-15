using Google.Protobuf.WellKnownTypes;
using ApplicationFileArtifactSourceKind = Aevatar.Workflow.Application.Abstractions.Runs.FileArtifactSourceKind;

namespace Aevatar.Workflow.Application.Abstractions.Queries;

public sealed partial class WorkflowActorSnapshot
{
    public DateTimeOffset LastUpdatedAt
    {
        get => LastUpdatedAtUtc == null ? default : LastUpdatedAtUtc.ToDateTimeOffset();
        set => LastUpdatedAtUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }

    public bool? LastSuccess
    {
        get => LastSuccessWrapper;
        set => LastSuccessWrapper = value;
    }

    public WorkflowRunCompletionStatus CompletionStatus
    {
        get => (WorkflowRunCompletionStatus)CompletionStatusValue;
        set => CompletionStatusValue = (int)value;
    }
}

public sealed partial class WorkflowActorProjectionState
{
    public DateTimeOffset LastUpdatedAt
    {
        get => LastUpdatedAtUtc == null ? default : LastUpdatedAtUtc.ToDateTimeOffset();
        set => LastUpdatedAtUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }
}

public sealed partial class WorkflowRunFileRef
{
    public ApplicationFileArtifactSourceKind SourceKind
    {
        get => (ApplicationFileArtifactSourceKind)SourceKindValue;
        set => SourceKindValue = (int)value;
    }
}

// Refactor (iter29/cluster-029-workflow-history-artifact):
//   Old pattern: timeline DTOs were named as actor timeline readmodel items.
//   New principle: timeline data is exported from the workflow-run artifact, not exposed as an actor current-state readmodel.
public sealed partial class WorkflowRunTimelineExportItem
{
    public DateTimeOffset Timestamp
    {
        get => TimestampUtc == null ? default : TimestampUtc.ToDateTimeOffset();
        set => TimestampUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }
}

// Refactor (iter29/cluster-029-workflow-history-artifact):
//   Old pattern: graph nodes were named as actor graph readmodel nodes.
//   New principle: graph nodes are part of a workflow-run graph export artifact.
public sealed partial class WorkflowRunGraphExportNode
{
    public DateTimeOffset UpdatedAt
    {
        get => UpdatedAtUtc == null ? default : UpdatedAtUtc.ToDateTimeOffset();
        set => UpdatedAtUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }
}

// Refactor (iter29/cluster-029-workflow-history-artifact):
//   Old pattern: graph edges were named as actor graph readmodel edges.
//   New principle: graph edges are part of a workflow-run graph export artifact.
public sealed partial class WorkflowRunGraphExportEdge
{
    public DateTimeOffset UpdatedAt
    {
        get => UpdatedAtUtc == null ? default : UpdatedAtUtc.ToDateTimeOffset();
        set => UpdatedAtUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }
}
