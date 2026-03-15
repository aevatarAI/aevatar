# Aevatar.Workflow.Projection

Workflow 领域的 CQRS 读侧实现。当前投影已经切到 run-isolated 语义：

- Projection root 是 `WorkflowRunGAgent` actor id
- `WorkflowExecutionReport.ProjectionScope = RunIsolated`
- current-state / insight report / timeline / graph / AGUI 都消费同一条 committed observation 主链

口径说明：

- current-state readmodel 直接消费 `EventEnvelope<CommittedStateEventPublished>` 中的 committed state。
- workflow insight/report 不再由 projection 自己维护第二套状态机；run events 会先桥接到 `WorkflowRunInsightGAgent`，再由 insight actor 的 committed state 物化成 report readmodel。

## 组成

- `WorkflowExecutionProjectionPort`
- `WorkflowRunInsightBridgeProjector`
- `WorkflowRunInsightReadModelProjector`
- `WorkflowRunTimelineReadModelProjector`
- `WorkflowRunGraphMirrorProjector`
- `WorkflowExecutionAGUIEventProjector`
- `ContextProjectionActivationService<WorkflowExecutionRuntimeLease, WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>`
- `ContextProjectionReleaseService<WorkflowExecutionRuntimeLease, WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>`
- `WorkflowProjectionQueryReader`

`WorkflowRunInsightBridgeProjector` 现在只负责把 committed workflow/AI events 转成 `WorkflowRunInsightGAgent` 的输入，不再在 projection 层直接维护 report 状态机。

## 主链路

```mermaid
%%{init: {"maxTextSize": 100000, "flowchart": {"useMaxWidth": false, "nodeSpacing": 10, "rankSpacing": 50}, "themeVariables": {"fontSize": "10px"}}}%%
flowchart LR
  RUN["WorkflowRunGAgent committed envelopes"]
  COOR["ProjectionCoordinator"]
  CUR["WorkflowExecutionCurrentStateProjector"]
  BRIDGE["WorkflowRunInsightBridgeProjector"]
  INSIGHT["WorkflowRunInsightGAgent"]
  REPORTP["WorkflowRunInsightReadModelProjector"]
  TIMELINEP["WorkflowRunTimelineReadModelProjector"]
  GRAPHP["WorkflowRunGraphMirrorProjector"]
  AG["WorkflowExecutionAGUIEventProjector"]
  CURDOC["Current-State Document"]
  REPORT["WorkflowExecutionReport"]
  TIMELINE["WorkflowRunTimelineDocument"]
  GRAPH["Graph Store"]
  HUB["ProjectionSessionEventHub<WorkflowRunEvent>"]

  RUN --> COOR
  COOR --> CUR
  COOR --> BRIDGE
  COOR --> AG
  CUR --> CURDOC
  BRIDGE --> INSIGHT
  INSIGHT --> REPORTP
  INSIGHT --> TIMELINEP
  INSIGHT --> GRAPHP
  REPORTP --> REPORT
  TIMELINEP --> TIMELINE
  GRAPHP --> GRAPH
  AG --> HUB
```

## 关键约束

- 不新增第二条 workflow read-side pipeline
- 不使用中间层进程内事实映射管理投影生命周期
- projection ownership 继续由 coordinator actor/分布式状态串行裁决
- query 返回的是 run actor 快照，不再是 definition actor 共享会话

## ReadModel 语义

当前 workflow run 的 readmodel 已按消费场景拆开：

- `WorkflowExecutionCurrentStateDocument`
  - actor 当前态查询
- `WorkflowRunTimelineDocument`
  - timeline 查询
- `WorkflowRunGraphMirrorReadModel -> Graph Store`
  - graph 查询
- `WorkflowExecutionReport`
  - insight report / export

## Query

Query reader 对外仍保留 `/api/actors/*` 这组接口名，但语义已经切成：

- actor = run actor
- graph root = run actor
- snapshot/timeline/subgraph 全部按 run-isolated 返回
- timeline 直接读取 `WorkflowRunTimelineDocument`
- graph 直接读取 graph store，不再从 `WorkflowExecutionReport` 派生
