# Projection Store/ReadModel Full Refactor Plan (No Compatibility)

- Date: 2026-02-24
- Status: Completed
- Scope: `Aevatar.CQRS.Projection.*` + `Aevatar.Workflow.Projection` + `Aevatar.Workflow.Extensions.Hosting`

## 1. Refactor Goals

1. Remove single-provider selection (`providerName + factory + runtime options`) and switch to one-to-many fan-out.
2. Keep Document/Graph as independent provider categories.
3. Make read model routing capability-driven:
   - `IDocumentReadModel` -> document store fan-out
   - `IGraphReadModel` -> graph store fan-out
   - both interfaces -> both paths
4. Preserve index metadata and graph relation semantics:
   - document: `DocumentIndexMetadata`
   - graph: `IGraphReadModel.GraphNodes/GraphEdges`
5. Delete redundant abstraction layers and dead code without compatibility shims.

## 2. Target Architecture

### 2.1 Write Pipeline

```mermaid
%%{init: {"maxTextSize": 100000, "flowchart": {"useMaxWidth": false, "nodeSpacing": 10, "rankSpacing": 50}, "themeVariables": {"fontSize": "10px"}}}%%
flowchart LR
    A["Domain Event Stream"] --> B["Projection Coordinator / Dispatcher"]
    B --> C["Workflow ReadModel Reducers + Projectors"]
    C --> D["IProjectionMaterializationRouter<TReadModel,TKey>"]

    D --> E["ProjectionDocumentStoreFanout<TReadModel,TKey>"]
    D --> F["ProjectionGraphMaterializer<TReadModel>"]

    E --> E1["Document Provider #1 (InMemory)"]
    E --> E2["Document Provider #2 (Elasticsearch)"]

    F --> G["ProjectionGraphStoreFanout"]
    G --> G1["Graph Provider #1 (InMemory)"]
    G --> G2["Graph Provider #2 (Neo4j)"]
```

### 2.2 Query Pipeline

```mermaid
%%{init: {"maxTextSize": 100000, "flowchart": {"useMaxWidth": false, "nodeSpacing": 10, "rankSpacing": 50}, "themeVariables": {"fontSize": "10px"}}}%%
flowchart LR
    Q1["WorkflowProjectionQueryReader"] --> Q2["IDocumentProjectionStore<WorkflowExecutionReport,string>"]
    Q1 --> Q3["IProjectionGraphStore"]

    Q2 --> Q4["ProjectionDocumentStoreFanout (primary read store + fan-out write)"]
    Q3 --> Q5["ProjectionGraphStoreFanout (primary read store + fan-out write)"]

    Q4 --> Q6["Document Primary Provider"]
    Q5 --> Q7["Graph Primary Provider"]
```

## 3. Major Structural Changes

## 3.1 Removed (hard delete)

- `ProjectionDocumentRuntimeOptions`
- `ProjectionGraphRuntimeOptions`
- `ProjectionProviderNames`
- `IProjectionDocumentStoreFactory`
- `IProjectionGraphStoreFactory`
- `ProjectionProviderSelectionException`
- `ProjectionDocumentStoreFactory`
- `ProjectionGraphStoreFactory`
- `ProjectionStoreRegistrationSelector`

## 3.2 Added

- `ProjectionDocumentStoreFanout<TReadModel, TKey>`
- `ProjectionGraphStoreFanout`

## 3.3 Runtime DI Model

`AddProjectionReadModelRuntime()` now registers:

- `IDocumentProjectionStore<,>` -> `ProjectionDocumentStoreFanout<,>`
- `IProjectionGraphStore` -> `ProjectionGraphStoreFanout`
- `IProjectionGraphMaterializer<>`
- `IProjectionMaterializationRouter<,>`
- `IProjectionDocumentMetadataResolver`

## 3.4 Workflow Provider Registration Model

`AddWorkflowProjectionReadModelProviders(configuration)` now uses enable flags:

- `Projection:Document:Providers:InMemory:Enabled`
- `Projection:Document:Providers:Elasticsearch:Enabled`
- `Projection:Graph:Providers:InMemory:Enabled`
- `Projection:Graph:Providers:Neo4j:Enabled`

Rules:

1. Legacy single-select keys (`Projection:Document:Provider`, `Projection:Graph:Provider`) are rejected.
2. InMemory providers are fallback defaults when durable providers are not enabled.
3. `Projection:Policies:DenyInMemoryGraphFactStore=true` forbids in-memory graph fact store.

## 3.5 Elasticsearch Metadata Behavior

`ElasticsearchProjectionReadModelStore` now consumes full `DocumentIndexMetadata`:

- `IndexName` as logical scope input
- `MappingJson` as index mappings object
- `Settings` as index settings object
- `Aliases` as index alias object

Index bootstrap now uses metadata payload instead of fixed `{"mappings":{"dynamic":true}}`.

## 4. Project-Level Responsibility Split (Post-Refactor)

- `Aevatar.CQRS.Projection.Stores.Abstractions`
  - pure read model/store contracts and metadata contracts
- `Aevatar.CQRS.Projection.Runtime.Abstractions`
  - store registration + materialization contracts only
- `Aevatar.CQRS.Projection.Runtime`
  - fan-out composition + graph materialization + metadata resolver
- `Aevatar.CQRS.Projection.Providers.*`
  - concrete provider implementations only
- `Aevatar.Workflow.Projection`
  - workflow reducers/projectors/read model/query services
- `Aevatar.Workflow.Extensions.Hosting`
  - host-layer provider enablement and policy binding

## 5. Implementation Status by Module

- Runtime: completed
- Runtime.Abstractions: completed
- Provider extensions: completed
- Workflow projection composition: completed
- Workflow hosting provider extension: completed
- Tests: completed and updated to fan-out semantics
- Readme/docs sync: completed

## 6. Verification

Commands executed:

1. `dotnet build aevatar.slnx --nologo`
2. `dotnet test aevatar.slnx --nologo`
3. `bash tools/ci/architecture_guards.sh`
4. `bash tools/ci/projection_route_mapping_guard.sh`
5. `bash tools/ci/solution_split_guards.sh`
6. `bash tools/ci/solution_split_test_guards.sh`
7. `bash tools/ci/test_stability_guards.sh`

Result: all passed.
