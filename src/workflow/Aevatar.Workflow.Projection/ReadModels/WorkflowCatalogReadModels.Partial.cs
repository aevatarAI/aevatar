using System.Collections.Generic;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Projection.ReadModels;

public sealed partial class WorkflowCatalogCurrentStateDocument : IProjectionReadModel<WorkflowCatalogCurrentStateDocument>
{
    public DateTimeOffset UpdatedAt
    {
        get => UpdatedAtUtcValue == null ? default : UpdatedAtUtcValue.ToDateTimeOffset();
        set => UpdatedAtUtcValue = Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }

    public IList<string> Primitives
    {
        get => PrimitiveEntries;
        set => WorkflowCatalogReadModelCollections.ReplaceCollection(PrimitiveEntries, value);
    }

    public IList<WorkflowCatalogRoleReadModel> Roles
    {
        get => RoleEntries;
        set => WorkflowCatalogReadModelCollections.ReplaceCollection(RoleEntries, value);
    }

    public IList<WorkflowCatalogStepReadModel> Steps
    {
        get => StepEntries;
        set => WorkflowCatalogReadModelCollections.ReplaceCollection(StepEntries, value);
    }

    public IList<WorkflowCatalogEdgeReadModel> Edges
    {
        get => EdgeEntries;
        set => WorkflowCatalogReadModelCollections.ReplaceCollection(EdgeEntries, value);
    }

    public IList<string> RequiredConnectors
    {
        get => RequiredConnectorEntries;
        set => WorkflowCatalogReadModelCollections.ReplaceCollection(RequiredConnectorEntries, value);
    }

    public IList<string> WorkflowCalls
    {
        get => WorkflowCallEntries;
        set => WorkflowCatalogReadModelCollections.ReplaceCollection(WorkflowCallEntries, value);
    }
}

public sealed partial class WorkflowCatalogRoleReadModel
{
    public IList<string> EventModules
    {
        get => EventModuleEntries;
        set => WorkflowCatalogReadModelCollections.ReplaceCollection(EventModuleEntries, value);
    }

    public IList<string> Connectors
    {
        get => ConnectorEntries;
        set => WorkflowCatalogReadModelCollections.ReplaceCollection(ConnectorEntries, value);
    }
}

public sealed partial class WorkflowCatalogStepReadModel
{
    public IDictionary<string, string> Parameters
    {
        get => ParameterEntries;
        set => WorkflowCatalogReadModelCollections.ReplaceMap(ParameterEntries, value);
    }

    public IDictionary<string, string> Branches
    {
        get => BranchEntries;
        set => WorkflowCatalogReadModelCollections.ReplaceMap(BranchEntries, value);
    }

    public IList<WorkflowCatalogChildStepReadModel> Children
    {
        get => ChildEntries;
        set => WorkflowCatalogReadModelCollections.ReplaceCollection(ChildEntries, value);
    }
}

internal static class WorkflowCatalogReadModelCollections
{
    public static void ReplaceCollection<T>(RepeatedField<T> target, IEnumerable<T>? source)
    {
        ArgumentNullException.ThrowIfNull(target);

        target.Clear();
        if (source == null)
            return;

        target.Add(source);
    }

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
