---
title: "Aevatar Inspector — OTel-First Live Actor System Visualizer 设计快照"
status: history
owner: eanzhao
---

# Aevatar Inspector — OTel-First Live Actor System Visualizer

> **本文档是已归档的设计快照（snapshot, non-authoritative）。** 真正落地的权威口径请看：
>
> - ADR [0022](../../adr/0022-otel-aevatar-semantic-conventions.md) — OTel semantic conventions for `aevatar.*` activities
> - ADR [0023](../../adr/0023-two-tier-inspector-architecture.md) — Two-tier Inspector architecture (canonical readmodel vs observation OTel)
> - [docs/canon/observability.md](../../canon/observability.md) — semantic conventions 的活文档
> - CI guard: [`tools/ci/inspector_tier_boundary_guard.sh`](../../../tools/ci/inspector_tier_boundary_guard.sh)
>
> 这份快照保留的是从问题陈述到落地决策的**完整推演路径**和**被拒绝的备选**，便于将来回溯"为什么是 Approach D 而不是 A/B/C"。实施细节以上面四份权威文档为准；本文里如有冲突，以权威文档为准。
>
> 落地分支：`feat/2026-05-12_otel-inspector`（自 `feature/lark-bot` 切，回流时 rebase 到 `dev`）。
>
> Provenance: 2026-05-11 起草 → 经 office-hours + /plan-eng-review + /plan-design-review 三轮迭代到 rev 5。

## Problem Statement

aevatar 的核心抽象（actor / projection / readmodel / workflow）现在只能"靠脑子建模"。开发时想确认一个 GAgent 真的被激活了、parent-child 真的连上了、消息真的传到了对面的 actor、projection 真的追上了 readmodel，目前要靠日志 + 单元测试 + 想象力。

已有的 `demos/Aevatar.Demos.Cli` 能跑场景但只出**静态 HTML 报告**（playback）。`demos/Aevatar.Demos.Workflow.Web` (Aevatar Workflow Studio) 是 workflow YAML 编辑器，不是 runtime 观察器。中间这层"可视化的 live debugger"是空的。

构建动机是双重的：**严肃** — 给自己开发框架时多一只眼睛；**轻松** — 一个值得 amuse myself 的东西。

## What Makes This Cool

1. **一次扩展，两个产品**：扩展 OpenTelemetry 既给所有 aevatar 用户带来更完整的 trace（可接 Jaeger/Tempo/Honeycomb），也给本项目 Inspector 一个标准的 observation 数据源。
2. **空间生态隐喻 + Linear 美学**：actors 是会脉动的几何节点、messages 是飞过去的粒子 — 用极简几何抽象表达"系统活着"，不走默认通用 SaaS 路线。
3. **一个画布看全部**：actor topology + 消息流 + projection 滞后 + readmodel 快照 + workflow run 都在一个画布上分层叠加，无 tab 切换。

## Constraints

- 仅观察单进程；cluster/多节点 V2 再说
- **V1 仅支持 Local runtime**；Orleans runtime (生产) V2 再说（grain placement / `OnActivateAsync` 时机不同 — V1 不覆盖）
- **V1 仅 actor 列表 + 消息流 + projection materialize 动画 + readmodel state + workflow run**；**parent-child topology 视图延期到 V2**（topology projector 不在 V1 范围；现 `GAgentRegistryCurrentStateDocument` 的 state_root 不含稳定 parent 字段）
- Read-only V1（不从 UI 创 actor / 发消息）
- 零外仓改动（CLAUDE.md 强制规则：不动 NyxID/chrono-*）
- 不自创 observation bus —— 复用 OTel + 现有 hook 接口
- 2.5-3.5 周时间盒，超期视为返工信号
- 遵循 CLAUDE.md "禁止双轨实现" / "Protobuf 优先" / "actor 即业务实体" / "查询走 readmodel" / "事实源唯一" / "禁止中间层维护 ID → 上下文/事实状态的进程内映射" 等强约束

## Premises (已确认)

1. **Live tap, not replay** — 通过 OTel `ActivityListener` 观察一个正在跑的 aevatar 进程；不预先回放 JSON
2. **统一空间画布** — 共享一个 canvas，workflow run 是叠加 ribbon 层，无 tab
3. **Read-only V1** — UI 只看，外部驱动（CLI、lark 真实消息、workflow yaml 触发）
4. **单进程 + Local runtime scope** — 观察本地一个 Local runtime aevatar 进程；cluster + Orleans V2 再说
5. **位置 & 技术栈** — 新建 `demos/Aevatar.Demos.Inspector` (Host) + `demos/Aevatar.Demos.Inspector.Web` (前端 React)；复用 `Aevatar.Demos.Workflow.Web` 的字体/颜色 token 但 IA 独立
6. **零外仓改动**
7. **observation 入口** — 扩展 `AevatarActivitySource` (单一 source `Aevatar.Agents`)，**通过修改 `ProjectionMaterializerRegistration` 内部装配点包裹 materializer**（不引入 Scrutor），workflow 等通过现有 projection pipeline 自动获得 observation
8. **严格两层** — Tier 1 (canonical state) = readmodel/projection；Tier 2 (observation) = OTel；inspector backend 不持有任何"actor 是否活着 / 当前 state / parent-child 关系"等业务事实，全部走 Tier 1。**这是本设计的核心约束**

## Architecture: Two Tiers（核心约束）

**为防止 OTel 既做观察又做事实源造成"双轨"，本设计严格分两层。** 详细决议见 ADR [0023](../../adr/0023-two-tier-inspector-architecture.md)。

### Tier 1 — Canonical State Tier（事实源）

所有"是什么"的查询走这里。读 readmodel / projection doc store，**永不**靠 OTel 推断。

| Inspector 端点 | 真相来源（readmodel + query port） | 状态 |
|----------------|------------------------|------|
| `GET /api/inspector/actors` | `GAgentRegistryCurrentStateDocument` 经由 `IGAgentRegistryCurrentStateQueryPort`（或仓库内对应的现有读端口） | ✅ 现有 |
| `GET /api/inspector/workflow-runs` | `WorkflowExecutionCurrentStateDocument` 经由 `IWorkflowExecutionCurrentStateQueryPort` (`src/workflow/Aevatar.Workflow.Application.Abstractions/Projections/IWorkflowExecutionCurrentStateQueryPort.cs`) | ✅ 现有 |
| `GET /api/inspector/readmodels` | 列出注册了的 readmodel 元数据；从现有 `IProjectionDocumentMetadataProvider` 实现枚举 | ✅ 现有 |
| `GET /api/inspector/readmodels/:name` | 经 readmodel 的现有 query port；payload 内 `state_root` (Protobuf `Any`) 在 host 端 unpack 为 typed message → 用 `Google.Protobuf.JsonFormatter` 序列化为 JSON 给浏览器 | ✅ 现有 |
| ~~`GET /api/inspector/topology`~~ | **不在 V1 范围**（无 topology projector） | ⏭ V2 |

**V1 actor 视图说明**：UI 渲染 actor 为按类型分组的列表（无 parent-child 边）。V2 引入专门的 `AgentTopologyCurrentStateProjector` 后再开启拓扑视图。

### Tier 2 — Observation Tier（瞬时动画）

所有"刚刚发生了什么"的瞬时信号走 OTel ActivityListener，**仅用于 UI 动画**（粒子飞行、节点脉冲、river 水滴）。

- 性质：sampled、lossy（OTel 标准行为）；不可作为业务事实依据
- 通路：in-process `ActivityListener` (`ShouldListenTo: source.Name == "Aevatar.Agents"`) → `BoundedChannel<TelemetryFrame>(capacity=1000, FullMode=DropOldest, SingleReader=false)` → SSE `/api/inspector/events`
- **Channel 策略**：bounded + drop-oldest。100-1000 events/sec 高活跃场景下不 OOM，不反压回 runtime；UI 慢则丢最旧帧（动画轻微跳，Tier 1 真相不受影响）
- **没有 ring buffer，无 warm-start replay**：连接到 SSE 后只接收 live tail；新加入的 UI 缺失前序动画不影响 Tier 1 真相。这避免任何"中间层维护 telemetry 队列被消费"的歧义，也避免与 CLAUDE.md "禁止中间层维护进程内映射" 的边界争议
- 工作流 fanout 问题：workflow runtime emit 路径**不改**，不引入 fanout；Workflow Studio 继续走原 `WorkflowEvent` SSE；Inspector 既查 Tier 1 readmodel 拿 workflow run 当前状态、又订阅 Tier 2 的 `aevatar.workflow.step` activity 拿瞬时动画提示 — 两个消费者读同一份事实（committed events），不要求 emitter 多路输出

### 边界规则总结

| 问题 | 走哪 |
|------|------|
| "有哪些 actor 活着？" | Tier 1 |
| "readmodel X 现在的 state version 是几？" | Tier 1 |
| "workflow run abc 跑到第几步？" | Tier 1 |
| "刚才有消息从 A 飞到 B 吗？（动画）" | Tier 2 |
| "刚才哪个 projector materialize 了哪个事件？（水滴）" | Tier 2 |
| "actor X 的 handler 刚刚耗时多少？（脉冲）" | Tier 2 |
| "parent-child topology" | **V2** (现 V1 无) |

UI 启动时：先打 Tier 1 拿 ground truth → 渲染静态列表 → 同时连 Tier 2 SSE 接动画。Tier 2 丢事件不影响正确性，只影响动画密度。

### 自动化护栏

**CI guard** [`tools/ci/inspector_tier_boundary_guard.sh`](../../../tools/ci/inspector_tier_boundary_guard.sh)：扫描 `demos/Aevatar.Demos.Inspector*` 内容禁止：(a) `/api/inspector/*` endpoint 方法读 Tier 2 `Channel<TelemetryFrame>` / 任何 telemetry 缓冲；(b) endpoint 返回历史 telemetry frame 列表的形状。无 guard 则两层规则只能靠 PR review 守，违反 CLAUDE.md "治理前置：架构规则必须可自动化验证"。

## Approaches Considered

### Approach A: Hook-Driven Live Bus

实现 `IGAgentExecutionHook` impl + `IProjectionMaterializer` decorator 写进自建 Channel；新建 SSE endpoint 推 UI。

- Effort M (2-3 周)、Risk 中
- 优势：直接、topology 重建简单
- 弱点：⚠️ **双轨**（OTel 一套 + 自建 channel 一套），违反 CLAUDE.md；外部价值仅限 Inspector

### Approach B: OTel Listener + Custom UI

纯消费 `AevatarActivitySource` 已经在发的 spans。

- Effort S (1-2 周)、Risk 低
- 优势：零新 instrumentation
- 弱点：现有 OTel coverage 不够（spawn/deactivate、projection materialize、readmodel write 都没活动）

### Approach C: Workflow Studio Inspector Tab

在 `Aevatar.Demos.Workflow.Web` 里加 Inspector tab。

- Effort M (2 周)、Risk 中低
- 优势：复用现有 UI scaffold
- 弱点：Workflow Studio IA 是 yaml editor；硬塞 inspector 视觉冲突；耦合

### Approach D: OTel-First + Two-Tier Inspector ⭐ (采纳)

**先把 OTel 缺的观察点补齐（Tier 2 完整）；Inspector 后端用两层架构：Tier 1 走 readmodel 拿事实，Tier 2 订阅 OTel 拿动画。**

- Effort M-L (2.5-3.5 周)、Risk 中
- 优势：
  - **单一观察 surface**，符合 CLAUDE.md "禁止双轨"
  - **事实源唯一**，readmodel = 真相，OTel = 装饰
  - 附带产品价值：任何 aevatar 部署接 Jaeger/Tempo/Honeycomb 都能拿到完整 trace
  - Inspector 是 thin consumer，可换可弃
  - 符合 OpenTelemetry GenAI semantic conventions 标准
- 弱点：
  - tag 命名一旦公开半凝固，要批量评审 + ADR
  - 测试面更大（OTel 自身 + Tier 1 readmodel + Tier 2 SSE + UI）

## Recommended Approach: D + Two-Tier

### 后端：OTel 扩展（单一 `Aevatar.Agents` ActivitySource）

LLM/Tool 继续独立 `GenAIActivitySource`（符合 OTel GenAI SemConv）。

**新增清单**：

| 观察点 | 现状 | 新增 activity / tag | 落点 |
|--------|------|---------------------|------|
| Event handler | ✅ `HandleEvent:{type}` 已发 | 加 `aevatar.agent.type` tag | `src/Aevatar.Foundation.Abstractions/Observability/AevatarActivitySource.cs` |
| Actor spawn (V1 Local-only) | ❌ 无 | 新 activity `aevatar.agent.spawn`；tag: agent.id / type | `LocalActorRuntime.CreateAsync` (具体路径以实际 impl 为准) |
| Actor deactivate | ❌ 无 | 新 activity `aevatar.agent.deactivate` | `IActorDeactivationHook` 包装（已有 hook 接口） |
| Actor link / unlink | ❌ 无 | 新 activity `aevatar.agent.link` / `aevatar.agent.unlink`；tag: parent.id / child.id；**事件级数据存档进 OTel；topology 视图本身 V2 才有** | `IActorRuntime.LinkAsync` / `UnlinkAsync` impl 点 |
| Projection materialize（含所有 projector，含 workflow） | ❌ 仅 hook | wrap `IProjectionMaterializer<T>.ProjectAsync` → activity `aevatar.projection.materialize`；tag: projection.name / state.version / last.event.id；context type 在 switch 内追加 type-specific tag (e.g. `aevatar.workflow.run_id`) | `src/Aevatar.CQRS.Projection.Core/DependencyInjection/ProjectionMaterializerRegistration.cs` —— **修改 `AddCurrentStateProjectionMaterializer` / `AddProjectionArtifactMaterializer` 内部，在 enumerable 注册前包一层 `ObservedProjectionMaterializer<TContext>`**（避免 Scrutor 依赖且兼容 `TryAddEnumerable`） |
| ReadModel upsert | ❌ 无 | wrap `IProjectionWriteDispatcher<TReadModel>.UpsertAsync` → activity `aevatar.readmodel.upsert`；tag: readmodel.name / state.version | 同 dispatcher 注册中心 |
| ReadModel delete | ❌ 无 | wrap `IProjectionWriteDispatcher<TReadModel>.DeleteAsync` → activity `aevatar.readmodel.delete`；tag: readmodel.name / readmodel.id | 同上 |
| Workflow run / step | ✅ 现有 `WorkflowEvent` SSE 流 | 通过 projection materialize decorator **自动**获得；workflow-specific tag 在 decorator context-type switch 加 `aevatar.workflow.run_id` / `aevatar.workflow.step` —— **server emit 路径不改、无 fanout**；可选额外 activity `aevatar.workflow.run` 装饰 `WorkflowExecutionRunEventProjector` 入口 | `src/workflow/Aevatar.Workflow.Presentation.AGUIAdapter/WorkflowExecutionRunEventProjector.cs` (server-side projector，**不**改 `Aevatar.Workflow.Sdk` 客户端) |
| LLM / Tool | ✅ `GenAIActivitySource` 完整 | 不动 | — |

**DI 装配点（具体）**：

```csharp
// ProjectionMaterializerRegistration.cs — 修正后
public static IServiceCollection AddCurrentStateProjectionMaterializer<TContext, TMaterializer>(
    this IServiceCollection services)
    where TContext : class, IProjectionMaterializationContext
    where TMaterializer : class, ICurrentStateProjectionMaterializer<TContext>
{
    services.TryAddSingleton<TMaterializer>();  // singleton concrete
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IProjectionMaterializer<TContext>>(sp =>
        new ObservedProjectionMaterializer<TContext>(sp.GetRequiredService<TMaterializer>())));
    services.TryAddEnumerable(ServiceDescriptor.Singleton<ICurrentStateProjectionMaterializer<TContext>>(sp =>
        sp.GetRequiredService<TMaterializer>()));
    return services;
}

// ObservedProjectionMaterializer<TContext>.ProjectAsync —— error swallow 模式
public async ValueTask ProjectAsync(TContext context, EventEnvelope envelope, CancellationToken ct)
{
    using var activity = AevatarActivitySource.StartProjectionMaterialize(
        projectionName: typeof(TContext).Name,
        lastEventId: envelope.EventId);
    try
    {
        // Workflow-specific tag enrichment (V1 = simple is-type switch；context type > 5 时重构 Strategy)
        if (context is IWorkflowProjectionContext wf)
        {
            try { activity?.SetTag("aevatar.workflow.run_id", wf.RunId);
                  activity?.SetTag("aevatar.workflow.step", wf.StepName); }
            catch { /* tag set failure must not block business path */ }
        }
        await _inner.ProjectAsync(context, envelope, ct);
        try { activity?.SetTag("aevatar.projection.state.version", context.NewVersion);
              activity?.SetStatus(ActivityStatusCode.Ok); }
        catch { }
    }
    catch (Exception ex)
    {
        try { activity?.SetStatus(ActivityStatusCode.Error, ex.Message); } catch { }
        throw;
    }
}
```

**IProjectionWriteDispatcher 注册中心 (已定位)**：`src/Aevatar.CQRS.Projection.Runtime/DependencyInjection/ServiceCollectionExtensions.cs:11`，单点 open generic：
```csharp
services.TryAddSingleton(typeof(IProjectionWriteDispatcher<>), typeof(ProjectionStoreDispatcher<>));
// 改为：
services.TryAddSingleton(typeof(ProjectionStoreDispatcher<>));
services.TryAddSingleton(typeof(IProjectionWriteDispatcher<>), typeof(ObservedProjectionWriteDispatcher<>));
// ObservedProjectionWriteDispatcher<T> 的 ctor inject ProjectionStoreDispatcher<T>
```

**AevatarActivitySource helper 方法 (DRY)**：为 8 个新 activity 各定义一个 typed factory：
```csharp
public static class AevatarActivitySource {
    public static Activity? StartAgentSpawn(string agentId, string agentType);
    public static Activity? StartAgentDeactivate(string agentId);
    public static Activity? StartAgentLink(string parentId, string childId);
    public static Activity? StartAgentUnlink(string parentId, string childId);
    public static Activity? StartProjectionMaterialize(string projectionName, string lastEventId);
    public static Activity? StartReadmodelUpsert(string readmodelName, string id);
    public static Activity? StartReadmodelDelete(string readmodelName, string id);
    public static Activity? StartWorkflowRun(string runId, string workflowName);
}
// 每个 emit 点 1 行，不是 5 行散落 tag set
```

**新 semantic conventions**（详见 [docs/canon/observability.md](../../canon/observability.md)）：

```
ActivitySource: Aevatar.Agents (已存在，扩展)

Activity names (新增):
  aevatar.agent.spawn         [experimental]  -- V1 Local only; Orleans V2 will fire from OnActivateAsync (timing differs)
  aevatar.agent.deactivate    [experimental]
  aevatar.agent.link          [experimental]
  aevatar.agent.unlink        [experimental]
  aevatar.projection.materialize  [experimental]  -- 覆盖所有 projector 包括 workflow
  aevatar.readmodel.upsert    [experimental]
  aevatar.readmodel.delete    [experimental]
  aevatar.workflow.run        [experimental]  -- 装饰 WorkflowExecutionRunEventProjector 入口

Tags (新增；除 已有 aevatar.agent.id / aevatar.event.*):
  aevatar.agent.type
  aevatar.agent.parent     -- 仅出现在 link/unlink (动态)，不挂 spawn
  aevatar.projection.name
  aevatar.projection.state.version
  aevatar.projection.last_event_id
  aevatar.readmodel.name
  aevatar.readmodel.state.version
  aevatar.readmodel.id     -- 仅 delete activity
  aevatar.workflow.run_id
  aevatar.workflow.step
```

**稳定性承诺**: 全部 `[experimental]` 起步。每个 tag 在 [docs/canon/observability.md](../../canon/observability.md) 标注 stable / experimental 等级，参照 OpenTelemetry GenAI SemConv pattern。stable 化通过 ADR 升级。

### 后端：Inspector Host

新建 `demos/Aevatar.Demos.Inspector` (ASP.NET Core minimal host)：

- **同进程嵌入 Local aevatar runtime** — Inspector host 本身是个 aevatar host，所有 demo 触发的 actor 与它共进程，OTel 自然观察
- 注册 `Aevatar.Agents` ActivityListener，sampler force ALWAYS_ON（Inspector 模式专用，覆盖默认采样配置）
- **Tier 1 endpoints** (REST, 全 JSON, 读 readmodel):
  - `GET /api/inspector/actors`
  - `GET /api/inspector/readmodels`（列表）
  - `GET /api/inspector/readmodels/:name`
  - `GET /api/inspector/workflow-runs` 
- **Tier 2 endpoint** (SSE, JSON event payload):
  - `GET /api/inspector/events` — pure live tail，无 warm-start replay
- **Wire 格式**: 所有 Host→browser endpoint 均 JSON：
  - readmodel `state_root` (Protobuf `Any`) 在 host 端用 `Google.Protobuf.JsonFormatter` (或仓库内已有 Studio.Projection 的同类 helper，A.3 调研落实) unpack 为 typed JSON
  - SSE TelemetryFrame 也 JSON
  - CLAUDE.md "Protobuf 优先" 的适用范围明确是仓库内部 actor↔actor / 跨节点通信；Host→browser demo 边界为已记录例外（见 [docs/canon/observability.md](../../canon/observability.md)）

### 前端：Inspector.Web

技术栈：React + TypeScript + Vite（参照 `Aevatar.Demos.Workflow.Web` 已有 scaffold）。字体 JetBrains Mono / Oswald 复用。**IA 独立**。

**V1 布局** (Linear 风 3-pane + ribbon)：

```
┌────────────────────────────────────────────────────────────────┐
│ TOP BAR    [Live ●] | filter: actor type | event kind          │
├──────────┬───────────────────────────────────────┬─────────────┤
│ LEFT     │ CENTER CANVAS                         │ RIGHT       │
│ Actor    │  Actors grouped by type               │ Inspector   │
│ tree     │  (V1 = flat group;                    │ - selected  │
│ (count   │   V2 will overlay parent-child edges  │   actor     │
│  by      │   when topology projector ships)      │ - state     │
│  type)   │                                       │ - events    │
│          │                            ║          │   listening │
│          │   projection river ──>     ║          │             │
│          │                            ║ readmodels│            │
│          │                            ║ (monument)│            │
├──────────┴───────────────────────────────────────┴─────────────┤
│ WORKFLOW RIBBON (active runs as horizontal bars)               │
└────────────────────────────────────────────────────────────────┘
```

视觉参考截图见同目录 [`2026-05-11-aevatar-inspector-mockup-variant-A.png`](2026-05-11-aevatar-inspector-mockup-variant-A.png)。
**实现按 spec 文本走，不按 mockup 还原 force-directed**（mockup 出图呈现为 hierarchy tree，实现里要保持 force-directed graph）。

**数据流**:
- 启动：拉 Tier 1 `/api/inspector/actors` + `/workflow-runs` + `/readmodels` → 渲染静态视图
- 启动后：连 Tier 2 SSE → 每条 activity 触发对应动画（particle / pulse / river drop）
- 周期 (5s) 重拉 Tier 1 拿最新真相（手 polling；watch endpoint 后续可加）

**视觉语言**（Linear / Bret Victor）：

- 背景: charcoal `#0E0F12`
- Actor 基础: 小圆 `#3B4A52` (muted teal-grey)
- Active pulse: 暖琥珀 `#E89E5B`
- Message particle: 小亮点 (V1 无 topology 边时退化为 actor 间的短线段动画或自身脉冲，等 V2 topology 边出现后正式沿边飞)
- Projection river: 右侧垂直条带，每次 materialize 一个水滴下落
- ReadModel monument: 矩形钢色 `#5B6E78`，显示 schema name + state version 数字
- Workflow ribbon bar: 横向 progress 条
- Motion: subtle, 300-800ms cubic-bezier-out；idle actor 灰度淡化

**交互**：Hover actor → 高亮关联 projection；Click → 在 Right Inspector 打开详情（state from Tier 1）；拖拽平移 / 滚轮缩放；filter 隐藏类型。

### Hierarchy Ranking (Pass 1 / plan-design-review)

打开 UI 后，用户视觉路径优先级：

| 顺位 | 元素 | 为什么 |
|------|------|--------|
| 1st | Center canvas — 当前 active actor 的 amber pulse | 最强视觉对比；这是"系统活着"的证据 |
| 2nd | Left actor tree group counts | orientation："系统里有什么 / 多少个" |
| 3rd | Bottom workflow ribbon active progress bars | "现在在跑什么" |
| 4th | Right Inspector pane | 只在 click 后才有内容；按需出现 |
| 5th | Top bar status | peripheral acknowledgment |
| Hidden | ReadModel monuments | info-on-demand，hover 才出 detail |

### Interaction States (Pass 2 / plan-design-review)

每个 UI 区都要 5 个状态：

| FEATURE | LOADING | EMPTY | ERROR | SUCCESS | PARTIAL |
|---------|---------|-------|-------|---------|---------|
| Actor list (left) | 4 skeleton rows + shimmer | "No live actors. Run a demo scenario to populate." + suggested command 在 hint | "Failed to load actor registry. Retry" + cause | populated tree | grouped header with count，没有 children rows |
| Canvas (center) | dim circle outlines, 无 edges | empty viewport + hint "Actors appear here when alive" | "Canvas data unavailable" overlay (Tier 1 仍可查) | full nodes + edges + active pulse | static topology + "Live signal lost" pill in top bar |
| Workflow ribbon (bottom) | thin progress hint | "No active workflow runs" small text 在底部 | "Workflow runtime offline" | bars filling | bar 已显示但 step ticks 懒加载 |
| Right Inspector pane | "Select an actor to inspect" empty hint | same | "Failed to fetch state. Retry" | full key-value table | state shown + "Refreshing version 42→43..." badge |
| ReadModel monuments | dashed border placeholder | omitted（无 readmodel 就不画） | red border + "?" version number | normal rect + version number | normal rect + version number frozen + "stale" amber dot |

### User Journey (Pass 3 / plan-design-review)

5-second visceral: "calm, alive"。5-minute behavioral: "I can find what I need"。5-year reflective: "this is in my toolkit"。

| STEP | DEVELOPER DOES | DEVELOPER FEELS | DESIGN SUPPORTS |
|------|----------------|-----------------|------------------|
| 1 | `dotnet run --project Inspector` | "Will this just work?" | startup terminal log 显式 URL `http://localhost:5100` |
| 2 | 浏览器打开 localhost:5100 | "What am I looking at?" | empty state copy: "Run a demo scenario in another terminal..." |
| 3 | 另一个 shell 跑 `Demos.Cli -- run hierarchy` | "Will I see it?" | < 2s 内 actor 节点 fade-in 出现 |
| 4 | 看消息飞 | "Oh." (recognition) | amber 粒子在 edges 上飞 + active pulse — 第一个"alive"瞬间 |
| 5 | 点 actor | "What's inside?" | right pane fill < 100ms (Tier 1 cached) |
| 6 | 场景结束，actors fade to idle | "Did I miss something?" | idle grey + ribbon 显示完成的 run summary；无焦虑感 |
| 7 | 第二天回来 debug 真实问题 | "Is this still useful?" | top bar 显示 uptime / connection state — durable tool feel |

### Anti-Slop Guarantees (Pass 4 / plan-design-review)

显式禁止（结合 CLAUDE.md 前端规则 + AI slop blacklist）：

- 无 3 列 feature grid 任何地方
- 无 icons-in-colored-circles
- 无 emoji 作为设计元素
- 无 `border-radius` > 4px 在任何矩形（rectangles stay sharp；只有 actor circles 是圆的，because they're circles）
- 无 `text-align: center` 在任何结构容器
- 无 purple / violet / indigo 在 palette 任何角落
- 无 gradient backgrounds（charcoal 是 flat `#0E0F12`）
- 无 decorative shadows 在 inactive elements
- 无 hover 提供 "modal" 弹窗（hover 应当原地高亮）

### Color Token & Text Foreground (Pass 6 / plan-design-review)

WCAG AA 4.5:1 对比度审计（against `#0E0F12` background）：

| Token | 用途 | 对比度 | 通过 |
|-------|------|--------|------|
| `#E89E5B` amber | active pulse, accent text | 5.2:1 | ✓ |
| `#5B6E78` cool steel | monument labels, large UI element | 3.0:1 | △ AA Large only |
| `#3B4A52` actor base | **circles only — NEVER text** | 1.6:1 | ✗ for text |
| `#2A2D32` edge grey | edge lines only, never text | 1.4:1 | ✗ for text |
| **NEW** `#C8CED4` 浅灰 | primary text (labels, IDs, body) | 11.5:1 | ✓ AAA |
| **NEW** `#8A929B` 中灰 | secondary text (timestamps, counts) | 5.6:1 | ✓ AA |

**强制**：`#3B4A52` 和 `#2A2D32` **永远不用于 text foreground**；只用于 shape fill / stroke。文字用新增的 `#C8CED4` (primary) 和 `#8A929B` (secondary)。

### Responsive (Pass 6 / plan-design-review)

- **V1**: desktop-only — min-width `1280px`，optimal `1440-1920px`
- `< 1280px`：splash "Inspector requires a desktop browser ≥ 1280px"
- Mobile / tablet：V1 不支持
- **V2**: 折叠 left pane + bottom ribbon → 平板 (≥ 768px) 支持

### Accessibility Baseline (Pass 6 / plan-design-review)

V1 承诺级别 = **Baseline (AA color contrast + Tab/Enter/Esc/Arrow keyboard nav)**。ARIA / screen reader / prefers-contrast / prefers-reduced-motion 全部 **V2**。

具体：

- **Color contrast**: 上面表格里 `#C8CED4` / `#8A929B` 为 text，过 AA
- **Keyboard**:
  - `Tab` 在 panes 间切换（Left → Center → Right → Bottom Ribbon → 回 Left）
  - Left pane / Right pane 内 `Tab` 继续按 list item 顺序
  - Actor list 内 `Arrow Up / Down` 移动选择
  - `Enter` 选中聚焦的 actor，触发 Right Inspector
  - `Esc` 反选，关闭 Right Inspector pane（变为 "Select an actor" empty state）
  - `/` (slash) 聚焦 filter dropdown
- **Focus rings**: 1px amber outline (`#E89E5B`) on focused element；overrides default browser blue
- **Touch targets**: ≥ 24px hit area（不是 mobile 但 trackpad 用户需要）
- **不做 V1**: ARIA roles, `aria-live` for SSE updates, screen reader narration, `prefers-reduced-motion` (V1 motion 本就 subtle), high-contrast mode

### Deferred Design Decisions (Pass 7 — committed defaults)

下列边缘 case 在 V1 已选定 default，实现按此走；如发现问题改回 doc：

- **Workflow ribbon sort**: start-time, newest 在顶
- **Actor color when type unknown / null**: `#3B4A52` + dotted ring；label "?:unknown"
- **Many active pulses simultaneously**: cap 5 visible pulses；多余以 "recent activity" small dot on actor 表示
- **Particle path when 无 edge yet**: radial outward fade（actor 周围 80px 半径，500ms 淡出）
- **Workflow ribbon overflow (> 6 runs)**: newest 6 visible，older 折叠在 "+N more" disclosure 按钮

### Phase B/C Exit Gate（防视觉爆 budget）
- B 结束时必须能：从 Tier 1 拉数据 + Tier 2 SSE 可见 + actor 列表 + hover 信息
- C "debugger-grade dense" 完成后才进 polish：粒子动画 / projection river / readmodel monument / workflow ribbon 是 D 抛光
- 若 C 末时间剩 < 3 天，跳过粒子 / river / monument，ship 静态 dense + hover + filter 版

## Open Questions

1. **OQ-1 — Readmodel JSON 序列化 helper**: Studio.Projection / 现有 Workflow projection query path 是否已有 `Any → typed → JSON` helper 可复用？若有则直接用；若无则 Phase A.3 在 `Aevatar.Demos.Inspector` 内本地实现（标 internal scope）
2. **OQ-2 — 画布性能**: 100+ 节点 + 持续动画，Canvas2D + RAF 够不够；不达上 PIXI.js。Phase D 性能验证
3. **OQ-3 — ActivityListener 性能 overhead**: Phase A.5 跑 microbenchmark，确认 attach listener 后 HandleEvent 路径 < 5% overhead（OTel 设计来低成本但不是零）

（已解决：~~OQ — emitter fanout~~ → "workflow runtime emit 不改"；~~OQ — OTel sampling~~ → "Tier 1 readmodel 是真相，Tier 2 force ALWAYS_ON 仅本地 Inspector"；~~OQ — topology readmodel~~ → "V1 范围外"；~~OQ — workflow run readmodel~~ → "`WorkflowExecutionCurrentStateDocument` via `IWorkflowExecutionCurrentStateQueryPort`"；~~OQ — Local spawn 落点~~ → "`LocalActorRuntime.CreateAsync` line 55，spawn activity 在 line 73 `var actor = new LocalActor` 之后 emit，跳过 idempotent return path (line 58 `_actors.TryGetValue` 命中时)"；~~OQ — IProjectionWriteDispatcher 注册中心~~ → "`src/Aevatar.CQRS.Projection.Runtime/DependencyInjection/ServiceCollectionExtensions.cs:11` 单点 open generic"。）

## Success Criteria

V1 完成的标准：

- [x] ADR-0022 `OTel semantic conventions for aevatar.* activities` 已立卷于 `docs/adr/0022-otel-aevatar-semantic-conventions.md` ✓ 已落地
- [x] ADR-0023 `Two-tier Inspector architecture (canonical readmodel vs observation OTel)` 已立卷于 `docs/adr/0023-two-tier-inspector-architecture.md` ✓ 已落地
- [x] `docs/canon/observability.md` 新增：OTel semantic conventions section + experimental 等级标注 + 双层架构 + Host→browser JSON wire format 例外 ✓ 已落地
- [x] `AevatarActivitySource` 已扩展上面表格中**全部 8 个**新 activity + 4 个新 tag
- [x] `ProjectionMaterializerRegistration.AddCurrentStateProjectionMaterializer` / `AddProjectionArtifactMaterializer` 已修改，包装 `ObservedProjectionMaterializer<TContext>`；新增 projector 自动获 OTel activity 而无业务侧改动
- [x] `IProjectionWriteDispatcher<TReadModel>` 的 `UpsertAsync` 和 `DeleteAsync` 各被 `ObservedProjectionWriteDispatcher<TReadModel>` 包装（注册中心 Phase A.3 调研确认）
- [x] `demos/Aevatar.Demos.Inspector` host 启动；Tier 1 4 个 REST endpoint 工作；Tier 2 SSE 工作；readmodel `state_root` JSON unpack 正确
- [x] 通过 Inspector 内置 `POST /api/inspector/demo/hierarchy` 验收 hierarchy 场景：
  - actors 出现在按类型分组的列表中（**Tier 1 数据**）
  - 消息事件触发 actor 节点脉冲动画（**Tier 2 数据**）
  - hierarchy 场景结束后 actor 保持 idle 可读状态
- [x] workflow ribbon 数据路径已接 `IWorkflowExecutionCurrentStateQueryPort`，单元测试覆盖 seeded workflow readmodel → `/api/inspector/workflow-runs`
- [x] projection/readmodel observation 数据路径已接 `ObservedProjectionMaterializer<TContext>` + `ObservedProjectionWriteDispatcher<TReadModel>`；UI 已渲染 readmodel monument/version
- [x] **CRITICAL regression test**: `LocalActorRuntime.CreateAsync` 在 idempotent path (actor 已存在) **不** 发 `aevatar.agent.spawn` activity (否则同一 actor id 反复"spawn" 糊掉 topology)
- [x] **CRITICAL** Tier 1 / Tier 2 boundary E2E test：关闭 `ActivityListener` 或丢 Tier 2 SSE，`/api/inspector/actors` 等 Tier 1 端点结果**仍然正确**
- [x] Channel backpressure 测试：短突发确认 Channel drop-oldest 工作、Tier 1 不受影响、动画轻微 stutter 但 UI 不卡
- [x] 关 OTel ActivityListener → UI 优雅地"动画停止"但拓扑视图仍然可读
- [x] `tools/ci/inspector_tier_boundary_guard.sh` 工作；故意写一个违规 endpoint 触发 guard fail ✓ 已落地（scaffold + self-test pass）
- [x] Architecture guards pass: `bash tools/ci/architecture_guards.sh` + 相关专项 (`projection_state_version_guard.sh` 等)
- [x] **CLAUDE.md 强约束通过审计**：
  - 无双轨实现（workflow emit 不 fanout）
  - 中间层无 ID→state 映射（无 ring buffer，无 warm-start replay）
  - readmodel 是事实源（所有业务查询走 Tier 1）
  - JSON 仅在 Host→browser 边界使用（其他全 Protobuf）

## Distribution Plan

- **形态**：仓库内 `demos/Aevatar.Demos.Inspector*` 项目。不脱仓发布。
- **运行方式**：`dotnet run --project demos/Aevatar.Demos.Inspector` 启动后端 (default :5100)；前端 `npm run dev` 或预 build 嵌入 wwwroot 走单一 host
- **CI/CD**：纳入 `aevatar.slnx`；前端 build artifact 单独构建 step；**不**挂 `playground_asset_drift_guard.sh`（IA 独立 per Premise 5；PR review + CI build 校验代替）；新增 `tools/ci/inspector_tier_boundary_guard.sh` 守 Tier 1/Tier 2 不被穿透
- **对外**：OTel 扩展本身就是 aevatar 用户的产品价值；可在 `docs/canon/observability.md` + 主 README 提及
- **不进 release**：demo 性质，不打 nuget 包；用户从仓库 clone 跑

## Next Steps (实施序列)

**Phase A — OTel 扩展（2 周，独立可 ship）**

- A.1 (~3 天): 起草 `docs/canon/observability.md` semantic conventions；起草 ADR-0022 + ADR-0023；内部评审；OQ-1 (Local spawn 路径) / OQ-2 (dispatcher 注册中心) / OQ-3 (JSON helper 复用) 调研结论填卷 ← ✓ 已落地（OQ-1 / OQ-2 现地解决；OQ-3 留 Phase A.3）
- A.2 (~2 天): 扩展 `AevatarActivitySource`：spawn / deactivate / link / unlink activities；patch **`LocalActorRuntime` only**（Orleans 留 V2）；加 agent.type tag 到 HandleEvent；单元测试覆盖
- A.3 (~3-4 天): 修改 `ProjectionMaterializerRegistration` 包装 `ObservedProjectionMaterializer<TContext>`；定位 `IProjectionWriteDispatcher` 注册中心并包装 `ObservedProjectionWriteDispatcher<TReadModel>`；workflow 通过 projection materialize 路径自动获得 OTel (无需独立步骤)；过 `projection_state_version_guard.sh` 等专项 guard；context-type switch 内为 workflow projection context 加 `aevatar.workflow.*` tag
- A.4 (~1 天): 可选额外：`aevatar.workflow.run` activity 装饰 `WorkflowExecutionRunEventProjector` 入口（如果 A.3 自动覆盖已经够用、可跳过本步）
- A.5 (~1 天): 跑现有 `Demos.Cli` 全场景，导出 OTel 到 console；肉眼审计完整性；架构 guards 全过；**新增 microbenchmark** — high-throughput Demos.Cli 场景 with/without `AevatarActivitySource` listener attached，记录 HandleEvent 路径 overhead < 5%
- A.6 (~1 天): 写 `tools/ci/inspector_tier_boundary_guard.sh` 雏形（即使 Inspector 还没存在，guard 文件先就位）；PR #1 ship — OTel 扩展独立有价值 ← ✓ 已落地（提前到 A.1 阶段，scaffold + 3 个 self-test pass）

**Phase B — Inspector Host + 前端 scaffold（1 周）**

- B.1: 新建 `demos/Aevatar.Demos.Inspector` 项目；wire 进 Local aevatar runtime；**同进程嵌入用 minimal config** — `InMemoryEventStore` + `InMemoryStateStore` + 不接 LLM provider (mock 服务返回空)；目的是观察，不是 demo workflow 跑真 LLM
- B.2: 注册 `Aevatar.Agents` ActivityListener + force ALWAYS_ON sampler + Channel<TelemetryFrame> (live broadcaster only, no ring buffer)
- B.3: Tier 1 REST endpoints (actors / workflow-runs / readmodels / readmodels/:name) — 全部读 readmodel + Any unpack to typed JSON
- B.4: Tier 2 SSE endpoint `/api/inspector/events` (pure live tail)
- B.5: 新建 `demos/Aevatar.Demos.Inspector.Web`（Vite + React）；scaffold 复用 Workflow.Web 的 tokens
- B.6: 集成测试：Tier 1 端点正确性 + Tier 2 SSE 流活的 + 故意丢 Tier 2 不影响 Tier 1
- B.7: `tools/ci/inspector_tier_boundary_guard.sh` 接入 Inspector 项目，覆盖测试
- **Exit Gate**: Tier 1 拉数据可见 + Tier 2 SSE 可见 + actor list + hover 工作

**Phase C — 可视化层（1-1.5 周）— Exit Gate 严格**

- C.1: Top bar + Left actor tree + Right inspector pane（dense Linear list 风）
- C.2: Center canvas Actor-by-type grouping (Canvas2D + RAF)；先静态布局 + hover 高亮
- C.3: Workflow ribbon (横向 progress bar)
- **C-Exit Gate**: 若至此 < 3 天剩余，跳过 C.4-5，直接 ship dense 版
- C.4: Activity pulse 动画（actor 节点 receive 消息时脉冲）；消息粒子（V1 退化为短线段或自身高亮，V2 topology 出来后正式沿边飞）
- C.5: Projection river + ReadModel monument 视觉层

**Phase D — 抛光（0.5 周）**

- D.1: 性能：100 节点动画 60fps 验证；不达则 PIXI.js
- D.2: 文档：`demos/Aevatar.Demos.Inspector/README.md` + GIF/screencast
- D.3: 主 demos/README.md 增一段

**总：3.5-4 周**（topology 已剥离 V2 节省时间）

## Reviewer Concerns（已采纳的 rev 1+2 反馈摘要）

**Rev 1 (quality 6/10) — 15 issues**：全部采纳，核心是引入 Two-Tier 架构 + 删除工作流 fanout + 修正 ring buffer 语义。

**Rev 2 (quality 7/10) — 12 issues**：全部采纳，核心修正：

- **Rev2 Issue 1 (`WriteAsync` 不存在)** → 改用 `UpsertAsync` + `DeleteAsync`；新增 `aevatar.readmodel.delete` activity
- **Rev2 Issue 2 (Scrutor 不在仓库)** → 改为修改 `ProjectionMaterializerRegistration` 内部装配点包 `ObservedProjectionMaterializer`，避免 Scrutor 依赖、兼容 `TryAddEnumerable` 注册风格
- **Rev2 Issue 3 (workflow 落点错)** → 改为 `Aevatar.Workflow.Presentation.AGUIAdapter/WorkflowExecutionRunEventProjector.cs` (server-side)，**不**改 `Aevatar.Workflow.Sdk` (client-side)；A.4 可选化（因 A.3 已自动覆盖）
- **Rev2 Issue 4 (Phase A.2 Orleans 漏)** → V1 严格 Local-only，A.2 仅 patch `LocalActorRuntime`；Orleans V2
- **Rev2 Issue 5 (topology projector 时间盒)** → V1 不做 topology projector，actor 视图退化为按类型分组的列表；topology V2 单独立项 — 时间盒诚实
- **Rev2 Issue 6 (OQ-2 应已知)** → 直接定位 `WorkflowExecutionCurrentStateDocument` 经 `IWorkflowExecutionCurrentStateQueryPort`，不再当 OQ
- **Rev2 Issue 7 (无 CI guard for tier boundary)** → 新增 `tools/ci/inspector_tier_boundary_guard.sh` 作为 Phase A.6 / B.7 交付
- **Rev2 Issue 8 (JSON wire 不全)** → 显式覆盖所有 endpoint (REST + SSE)；readmodel Any unpack 用 `JsonFormatter` 或仓库内已有 helper
- **Rev2 Issue 9 (warm-start 不明)** → 删除 ring buffer + warm-start，纯 live tail；UI 缺失前序动画不影响 Tier 1
- **Rev2 Issue 10 (Local vs Orleans spawn)** → 文档明示 `aevatar.agent.spawn` 在 V1 = Local CreateAsync；Orleans V2 是 `OnActivateAsync`，时机不同
- **Rev2 Issue 11 (A.3/A.4 顺序)** → A.3 一并覆盖 workflow（projection materialize decorator 内 context-type switch 加 workflow tag）；A.4 缩为可选装饰步
- **Rev2 Issue 12 (ADR slots)** → 已固定 0021、0022

## Approved Mockups

| Screen | Mockup | Direction | Notes |
|--------|--------|-----------|-------|
| Inspector canvas (populated state) | [`2026-05-11-aevatar-inspector-mockup-variant-A.png`](2026-05-11-aevatar-inspector-mockup-variant-A.png) | Linear-restrained — balanced density, faithful to spec colors/fonts/layout | force-directed graph rendering in artifact looks more hierarchy-tree-like than force-directed — **implementation should follow spec text, not this artifact**. 3 variants generated, A approved. B (Cosmograph-dense) reserved for potential V2 "power user mode". C (Monastic-dark) rejected: workflow indicator drifted toward purple, violates CLAUDE.md frontend rules. |

## NOT in scope

- **Parent-child topology projector + visual edges** — V2 (current V1 has no topology readmodel; UI shows actors flat by type)
- **Orleans runtime support** — V2 (V1 = Local only; `OnActivateAsync` 时机不同需要单独设计)
- **Tier 1 watch endpoint** (替代 5s polling) — V2 (UX 改进，非 architecture)
- **Cluster / multi-node observation** — V2
- **OTel tag stability promotion (experimental → stable)** — 走 ADR 升级流程，本 V1 全 experimental
- **Workflow Studio integration** — Workflow Studio 继续走自己 SSE；Inspector 是独立产品
- **Recording / playback** — V1 是 live tail；不录制；用户重跑场景 = 再观察
- **Nuget 包发布** — demo 项目，clone & run

## Failure modes & coverage matrix

| 失败模式 | 测试? | 错误处理? | 用户可见? | Critical Gap? |
|---------|------|----------|----------|---------------|
| OTel ActivityListener 断 → 动画停 | 待加 | UI 处理 | "Live signal lost" | NO (Tier 1 仍可读) |
| readmodel `state_root` unpack 失败 | 待加 | endpoint catch | error state | NO |
| Activity emit 抛 → 业务路径 | 待加 (in patch) | try/catch swallow | — | NO (patched) |
| `LocalActorRuntime` idempotent path 误发 spawn | **MUST**有 regression test | — | UI 显示重复 actor | **YES — CRITICAL** |
| Tier 2 SSE 丢事件 → Tier 1 端点错 | **MUST**有 boundary test | — | 错误的 actor 列表 | **YES — CRITICAL** |
| Channel<TelemetryFrame> 堆积 → OOM | 已 patch (BoundedChannel drop-oldest) | drop policy | 动画 stutter | NO (patched) |
| Inspector host crash → Demos.Cli 失活 | 待加 | exit code | "Inspector down" | NO |

**2 个 critical gaps**：都已写入 Success Criteria 的 test list。

## Worktree parallelization

**Sequential implementation, no parallelization opportunity** — solo dev project；Phase A→B→C→D 严格顺序（A 是 dependency for B；B 是 dependency for C；C 是 dependency for D）。Phase A 内部 A.1 → (A.2, A.3, A.6) 理论可并行但 solo dev 无收益。
