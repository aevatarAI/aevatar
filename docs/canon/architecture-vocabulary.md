---
title: "Architecture Vocabulary"
status: active
owner: eanzhao
---

# Architecture Vocabulary

本文是架构评审与重构讨论时使用的统一词汇表。它把外部的 deepening / Ports & Adapters 词汇与 aevatar 内部已有的术语对齐，避免在 review 时同一个概念出现多种叫法。

适用场景：

- `arch-audit`、`improve-codebase-architecture` 等架构评审 skill 的产出
- ADR 的 Context / Decision 文段
- PR 描述中讨论"重构动机"时
- code review 中评估"接口该不该这样切"时

如果只是讲业务功能（"member-first", "channel relay", "scripting"），用领域词汇即可，不必套用本表。

Chat 相关的 transcript、execution state、prompt context 与 user memory 不是通用架构词的
别名；其稳定产品语义见
[conversation-context-and-memory.md](conversation-context-and-memory.md)。

## 1. 核心词汇映射

| 通用词汇 | aevatar 已有术语 | 含义与口径 |
|---|---|---|
| Module | Actor / GAgent / 项目（`Aevatar.<Layer>.<Feature>`） | 任何"接口 + 实现"的单元，尺度无关；在 aevatar 主线上最常落到 Actor / GAgent / 一个 .csproj。 |
| Interface | Port + 命令/事件 proto + ReadModel 查询契约 | "调用方必须知道才能正确使用"的全部事实：类型签名、不变式（invariant）、顺序（ordering）、错误模式、配置、性能特性。**不只是 C# `interface` 关键字。** |
| Implementation | Actor body / Adapter 内部 / `*.cs` 实现文件 | 接口背后的代码主体。**与 Adapter 区分**：一个 thing 可以是"小 adapter + 大 implementation"（比如真实 ES 仓储），也可以是"大 adapter + 小 implementation"（比如 in-memory fake）。 |
| Depth（深度） | 业务实体内聚（"Actor 即业务实体"原则）；"删除优先"留下来的模块 | 接口杠杆率：接口很小、覆盖的行为很多 → **deep**；接口几乎和实现一样宽 → **shallow**。aevatar 的"Actor 即业务实体（数据 + 方法同住）"就是 depth 的具体实例。 |
| Seam（接缝） | Port（如 `IActorDispatchPort`、`IEventPublisher`）+ 命令分发契约 | 一个**可替换实现的位置**：能在不改这里代码的前提下换掉行为。在 aevatar 中，Port 就是 seam。 |
| Adapter | 同义。e.g. `LocalActorPublisher`、`RuntimeBackedActorRuntime`、各种 `InMemory*` | 满足某 seam 上 Interface 的具体实现。描述**角色**（填哪个槽），不描述**实质**（里面是什么）。 |
| Leverage | "一处 Port，N 处调用 + M 处测试都获益" | Depth 给调用方的回报：一份实现服务多个调用点和测试。 |
| Locality | "单一权威拥有者"原则、"事实源唯一" | Depth 给维护者的回报：变更、bug、知识、验证集中在一个位置；改一处 → 各处自动修复。 |

### 1.1 已经存在但容易和上面混淆的词

| aevatar 术语 | **不**等价于 | 区分 |
|---|---|---|
| 边界（如"Actor 边界"、"权威源边界"） | Seam | "边界"在 aevatar 是**所有权 / 责任范围**（接近 DDD bounded context）。Seam 是**可替换实现的位置**。一个 actor 的边界不是 seam；它周围的 Port 才是 seam。 |
| ReadModel | Interface | ReadModel 是**查询副本**（actor-scoped current-state replica）。它的查询契约（IXxxQueryPort）才是 Interface / Seam。 |
| Projection Pipeline | Adapter | Projection 是**物化机制**（committed event → readmodel），不是某个 seam 上的具体实现。"Projection 通道下的具体投影器"才类比 Adapter。 |
| Service（如 `WriteService`、`QueryService`） | Module（无脑套用） | aevatar 的应用层契约必须承载业务语义、不是纯转发空壳；一个 "Service" 是不是 Module 取决于它的 Interface 是不是足够 deep。多数情况下，正确的形态是更窄的 `IXxxQueryPort` / `IActorDispatchPort`，而不是泛 `Service`。 |
| Router | 新的转发 Actor / hot-path 调度中心 | 在 ingress 语境里，router = **config actor + boundary resolver**：`ChatRoutePolicyGAgent` 只作为 per-scope 配置权威持有策略，`ChatRouteResolver` 只在入口边界做无状态、瞬时决策；它不是新增 actor hop，也不持久化决策。见 [ADR-0024: Chat Route Policy — Config Actor + Boundary Resolver](../adr/0024-chat-route-policy.md)。 |

### 1.2 Actor evolution 词汇

| 术语 | 含义与口径 |
|---|---|
| Lazy state migration | 同一 actor、同一 identity、同一事实拥有者内的 state schema 懒迁移。只消费历史 state 与 runtime envelope 上的 schema version；不得做 I/O、跨 actor 调用、projection 写入或 readmodel 读取。 |
| Projection-driven bootstrap | split / merge / re-key 时，从旧 actor 已提交事实或 committed state publication 物化新 actor bootstrap 输入的流程。bootstrap 输入不是新事实；新 actor 提交自己的 domain event 后才成为权威事实。 |
| Retire cleanup | actor 演进完成后，对旧 actor、旧 readmodel、旧索引或旧路由的显式退役清理。它是演进协议的一部分，不是 query path 的临时兼容判断。 |
| Re-key redirect | actor identity/key 改写时，旧 key 到新 key 的显式重定向事实。它用于目标解析与清理窗口，不允许调用方解析 actorId 字符串，也不允许用 commandId/correlationId 代替 actorId。 |
| Actor replace | 旧 actor owner 被新 actor owner 替换，语义不再是同一 actor 内演进。新 owner 必须提交自己的 domain event；旧 owner 通过 retire cleanup 退出，不能保留双事实源或空壳兼容。 |

### 1.3 Realtime streaming 词汇

| 术语 | 含义与口径 |
|---|---|
| Realtime session | 一个入口请求到实时输出生命周期的共享应用层契约。文本/AGUI 与 voice control/transcript 使用 `IRealtimeSession<TInbound,TReceipt,TStartError,TOutboundFrame,TCompletion>` 表达同一组 `start -> accepted/error -> outbound frames -> completion` 语义；它不暴露 actor runtime、projection lease 或 transport socket。 |
| Projection-backed realtime stream | 由 Projection Session Pipeline 承载的实时控制流。它消费 actor-owned committed/control facts 或同源 session event，并按 `RootActorId + SessionId` fan-out 给 live sink；AGUI text frames 与 voice control/transcript frames 属于这类流。 |
| Volatile media stream port | 原始媒体字节的临时传输端口，例如 voice PCM WebSocket/WebRTC attachment。它不是 `IEventSink`、不是 projection port、不是 readmodel writer；raw PCM 不进入 committed event、event store、projection document 或 readmodel。 |

## 2. 关键原则（与 CLAUDE.md 已有规则的映射）

### 2.1 Deletion test（删除测试）

> 想象删掉这个模块。如果复杂度消失了，它就是 pass-through；如果复杂度在 N 个调用方处重新出现，它就是有价值的。

对应 CLAUDE.md：

- **删除优先**：空转发、重复抽象、无业务价值代码直接删除，不保留兼容空壳。
- **抽象一旦能被滥用即设计未完成**：允许绕过读写分离 / actor 边界 / 权威源的通用接口须继续收窄。

> 落地口径：在评审一个新增"中间层"时，先做删除测试。如果删除后调用方各自要重新写出同样的逻辑，保留；如果调用方只是少调一层、自己已能完成，删除。

### 2.2 The interface is the test surface

> 调用方和测试穿过同一个 seam。如果想要测"接口背后"的内容，模块的形状大概率是错的。

对应 CLAUDE.md：

- **读写分离**：查询走 readmodel；不暴露 actor 内部 state 或 event replay 作为查询主路径。
- **Actor 测试通过 inbox / 行为契约**：不通过 reflection 拆 actor 内部状态字段。

### 2.3 One adapter = hypothetical seam, two adapters = real seam

> 只有一个 adapter 时，seam 是想象出来的；至少要有两个 adapter（通常是 production + test）才值得引入 Port。

对应 CLAUDE.md：

- **禁止预留兼容空壳**。
- **本地可用不等于分布式正确**：仅本地 runtime 偶然细节才成立的实现视为未完成设计。

> 落地口径：新增 Port 时必须同时给出至少两个 Adapter（典型：runtime-backed + in-memory test fake），且两个都被实际使用。只有"未来可能要换"的 Port 不能落地。

### 2.4 Deep over shallow（深 vs 浅）

> 模块要"深"——大量行为藏在小接口背后；不要"浅"——接口几乎和实现一样复杂。

对应 CLAUDE.md：

- **Actor 即业务实体**：一个 actor = 一个业务实体（数据 + 方法同住）；禁止按技术功能（读 / 写 / 投影）拆分同一业务实体为多个 actor。
- **命名跟随职责**：接口 / 类型 / 目录命名描述职责边界，不泄露 `runtime/stream/protocol` 偶然细节。

## 3. 使用约定

1. **领域语言** vs **架构语言** 分开使用：
    - 描述业务（"member 是 Studio 的唯一主语"）→ 用 `docs/canon/role-model.md` 等领域文档里的词汇。
    - 描述结构（"这个 module 太 shallow，应该 deepen 进 GAgent"）→ 用本表词汇。
2. ADR 的 Context 段落用本表词汇描述当前结构问题；Decision 段落仍可使用领域语言落地。
3. **不要混用**：同一段话里"边界"和"seam"含义清晰可分；但"boundary 边界"如果用来指可替换位置就是错的，应该改成"port"或"seam"。
4. 中文写作时优先用括号显式给出英文术语：例如"接缝（seam）"、"深度（depth）"、"端口（port）"。`seam` 是概念（可替换实现的位置），`port` 是这个概念在 aevatar 里最常见的落地形态——两者不要互换使用，避免"边界"被随意替代。

## 4. 词汇拒绝清单

下列词在架构讨论中**不要单独使用**，因为它们已经被业务或基础设施语义占用 / 含糊：

- "boundary / 边界" 用于"可替换位置"——改用 **port / seam**。
- "service" 用于泛指一个模块——改用 **module / port** 或具体业务名（`*GAgent`、`*Projector`）。
- "API" 仅指类型签名——架构层面要谈 **interface**（含不变式与错误模式）。
- "component" 指 UI 组件以外的概念——通常是 **module** 或 **adapter**。

## 5. 参考

- [Mattpocock skills - improve-codebase-architecture/LANGUAGE.md](https://github.com/mattpocock/skills/blob/main/improve-codebase-architecture/LANGUAGE.md) — 本表的外部出处。
- Michael Feathers, *Working Effectively with Legacy Code* — seam 概念原始出处。
- [overview.md](overview.md) — aevatar 项目主架构。
- [architecture.md](architecture.md) — Foundation 层接口与运行时模型。
- [cqrs-projection.md](cqrs-projection.md) — 读写分离与 Projection Pipeline。
