# OpenViking 调研报告

> 调研对象：[volcengine/OpenViking](https://github.com/volcengine/OpenViking)
> 版本：v0.1.0（2026-02-14 发布）
> 团队：字节跳动火山引擎 Viking 团队

---

## 1. 项目定位

OpenViking 自称 **Context Database for AI Agents**——为 AI Agent 设计的上下文数据库。它的核心主张是：传统 RAG 以扁平化向量切片存储上下文，缺乏层次感、不可观测、不可迭代；OpenViking 用**虚拟文件系统范式**把 Memory / Resource / Skill 三类上下文统一管理，提供分层加载、目录递归检索、会话记忆自演进等能力。

---

## 2. 架构总览

```
┌──────────────────────────────────────────────────────┐
│                     Client (SDK)                     │
│         Embedded (本地) / HTTP (远程 Server)           │
└───────────────────────┬──────────────────────────────┘
                        │ delegates
┌───────────────────────▼──────────────────────────────┐
│                   Service Layer                      │
│  FSService · SearchService · SessionService          │
│  ResourceService · RelationService · PackService     │
└───┬──────────────────┬──────────────────┬────────────┘
    │                  │                  │
    ▼                  ▼                  ▼
┌─────────┐    ┌─────────────┐    ┌────────────┐
│Retrieve │    │   Session   │    │   Parse    │
│ Intent  │    │ add/used    │    │ Doc parse  │
│ Rerank  │    │ commit      │    │ L0/L1/L2   │
│ Hier.   │    │ compress    │    │ TreeBuild  │
└────┬────┘    └──────┬──────┘    └─────┬──────┘
     │                │                 │
     │         ┌──────▼──────┐          │
     │         │ Compressor  │          │
     │         │ Memory Dedup│          │
     │         └──────┬──────┘          │
     └────────────────┼────────────────-┘
                      ▼
┌──────────────────────────────────────────────────────┐
│              Storage Layer (双层存储)                  │
│  AGFS (内容存储: 文件/目录)  +  Vector Index (语义索引) │
└──────────────────────────────────────────────────────┘
```

---

## 3. 核心亮点

### 3.1 虚拟文件系统范式 (AGFS + Viking URI)

**问题**：传统 RAG 把记忆、资源、技能各放各处，碎片化严重。

**方案**：所有上下文统一映射到 `viking://` 虚拟文件系统下，用目录层级组织：

```
viking://
├── resources/           # 外部知识（文档、代码库、网页）
├── user/memories/       # 用户记忆（偏好、实体、事件）
└── agent/               # Agent 自身
    ├── skills/          # 可调用能力
    ├── memories/        # 学习到的经验（cases / patterns）
    └── instructions/    # 指令
```

**价值**：
- Agent 可以用 `ls` / `read` / `glob` / `find` 等确定性操作浏览上下文，而不是只能做模糊的向量搜索
- URI 是全局唯一标识，跨模块引用无歧义
- 目录结构本身就携带了"分类"和"层级"语义，不需要额外的元数据标签体系

### 3.2 三级信息分层 (L0 / L1 / L2)

**问题**：把大量上下文一次性塞进 prompt，既贵又容易超窗口。

**方案**：每个目录/文件自动生成三层摘要：

| 层级 | 文件 | Token 量级 | 用途 |
|------|------|-----------|------|
| **L0** | `.abstract.md` | ~100 tokens | 向量检索、快速过滤 |
| **L1** | `.overview.md` | ~2k tokens | Rerank、内容导航、规划决策 |
| **L2** | 原始文件 | 无上限 | 按需深度阅读 |

**生成机制**：
- 自底向上（叶节点 → 父目录 → 根目录）
- 子目录的 L0 聚合进父目录的 L1，形成层级导航
- 多模态内容（图片/视频）的 L0/L1 也是文本描述

**价值**：
- Agent 只需 L0 就能判断"要不要看"，用 L1 就能决策"怎么用"，L2 只在必要时加载
- Token 消耗可控：大多数场景 L1 就够用
- 天然支持"渐进式加载"，适合长上下文窗口管理

### 3.3 目录递归检索 (Hierarchical Retrieval)

**问题**：单次向量检索难以处理复杂意图，缺乏全局视角。

**方案**：两阶段检索 —— 意图分析 + 层级递归：

```
Query → IntentAnalyzer (LLM 分析, 生成 0-5 个 TypedQuery)
  → 每个 TypedQuery:
     1. 全局向量搜索 → 定位高分目录
     2. 目录内二次检索 → 精细探索
     3. 优先级队列驱动递归下钻 → 子目录继续
     4. 收敛检测（TopK 3轮不变则停止）
  → Rerank → 聚合结果
```

**关键参数**：
- 分数传播：`final_score = 0.5 * embedding_score + 0.5 * parent_score`
- 收敛轮数：3
- 全局候选数：3

**价值**：
- "先锁定高分目录，再精细探索内容"——结合了确定性定位和语义匹配
- 检索轨迹完全可追踪（先去了哪个目录、在那里找到了什么），可观测性强
- 比扁平向量检索多了"结构感知"

### 3.4 自动会话管理与记忆自演进

**问题**：传统 memory 只记录用户交互流水，缺乏结构化的经验沉淀。

**方案**：Session 生命周期 `Create → Interact → Commit`：

1. **交互阶段**：`add_message` 记录对话，`used` 记录使用了哪些上下文/技能
2. **Commit 阶段**：
   - 压缩归档历史消息，生成 L0/L1 摘要
   - LLM 提取 6 类记忆：

| 分类 | 归属 | 说明 | 可合并 |
|------|------|------|--------|
| profile | user | 用户身份属性 | 是 |
| preferences | user | 用户偏好 | 是 |
| entities | user | 人物/项目等实体 | 是 |
| events | user | 事件/决策 | 否 |
| cases | agent | 问题+解决方案 | 否 |
| patterns | agent | 可复用模式 | 是 |

3. **去重决策**：向量预过滤找到相似记忆 → LLM 决定 `CREATE / UPDATE / MERGE / SKIP`

**价值**：
- Agent "越用越聪明"：任务经验自动沉淀为 cases/patterns
- 用户画像自动累积：偏好、实体关系逐步丰富
- 记忆不是无限膨胀的流水账，而是经过压缩去重的结构化知识

### 3.5 双层存储分离 (AGFS + Vector Index)

**方案**：内容存储和语义索引彻底解耦：

- **AGFS**：虚拟文件系统，存 L0/L1/L2 全文内容、多媒体文件、关系表
- **Vector Index**：只存 URI + 向量 + 元数据，不存文件内容

**价值**：
- 向量索引内存占用小（不存内容），可独立扩缩
- 数据源单一（所有内容从 AGFS 读），不会出现索引和内容不一致
- 删除/移动文件时自动同步向量索引

---

## 4. 技术局限与待观察点

| 方面 | 说明 |
|------|------|
| **LLM 依赖重** | L0/L1 生成、意图分析、记忆提取、去重决策都依赖 LLM 调用，写入延迟高、成本可观 |
| **Python 生态绑定** | 核心用 Python 实现，C++ 扩展做高性能索引；.NET 生态需完全重写 |
| **单机优先** | Embedded 模式是主推路径，分布式场景（多 Agent 共享上下文）支持有限 |
| **记忆去重质量** | 6 类记忆的提取和去重完全依赖 LLM 判断，准确度随模型能力波动 |
| **检索参数固定** | 分数传播系数 0.5、收敛轮数 3 等是硬编码，缺乏自适应调优机制 |
| **Skill 管理粗粒度** | Skill 只是存了描述文档，没有真正的能力注册/调用/反馈闭环 |

---

## 5. 对 Aevatar 的适配分析

### 5.1 Aevatar 现有上下文管理

Aevatar 当前的上下文管理分布在三处，缺乏统一范式：

| 机制 | 位置 | 能力 |
|------|------|------|
| **AgentContext** | `Foundation.Core/Context` | AsyncLocal 键值传播，跨 Agent 数据携带 |
| **RunContext** | `Foundation.Core/Context/RunManager` | Run 级生命周期 + 取消 |
| **ProjectionContext** | `Workflow.Projection` | Run 级投影状态（ReadModel + EventSink） |
| **State/EventSourcing** | `Foundation.Core` | Agent 状态持久化 |

**缺失**：
- 没有跨 Run 的持久记忆（用户偏好、Agent 经验）
- 没有外部知识管理（文档、代码库等资源检索）
- 没有上下文分层加载策略（Token 预算管理）
- 没有统一的上下文寻址体系

### 5.2 可复用的设计理念（不依赖 OpenViking 代码）

以下理念可以在 Aevatar 的 .NET/C# 架构中独立实现，不需要引入 Python 依赖：

#### A. 统一上下文寻址 (URI Scheme)

在 Aevatar 中引入 `aevatar://` URI 体系：

```
aevatar://
├── resources/{project}/       # 外部知识
├── user/{userId}/memories/    # 用户记忆
├── agent/{agentId}/
│   ├── memories/              # Agent 经验
│   ├── skills/                # Agent 能力
│   └── instructions/          # Agent 指令
└── session/{runId}/           # 会话上下文
```

这与 Aevatar 的 `EnvelopePropagation.Baggage` 传播机制天然兼容——URI 可以作为跨 hop 传播的上下文引用值。

#### B. 三级信息分层 (L0/L1/L2)

在 `Aevatar.AI.Core` 层实现上下文分层：

```csharp
public interface IContextEntry
{
    string Uri { get; }
    ContextType Type { get; }  // Resource / Memory / Skill

    string Abstract { get; }   // L0: ~100 tokens
    string Overview { get; }   // L1: ~2k tokens
    // L2: 通过 IContextStore.ReadAsync(uri) 按需加载
}
```

与 Workflow 的 Token 预算管理集成：Step 执行前先用 L0/L1 筛选相关上下文，只在必要时加载 L2。

#### C. 记忆提取与自演进

在 Workflow Projection Pipeline 中增加一个 **MemoryExtractionProjector**：

```
EventEnvelope (from Actor Stream)
  → ProjectionCoordinator
  → Multiple Projectors (parallel):
     ├── ReadModelProjector     → ReadModel Store
     ├── AGUIEventProjector     → SSE/WebSocket
     └── MemoryExtractionProjector → Memory Store (新增)
```

Run 完成后，MemoryExtractionProjector 汇总本次 Run 的事件流，调用 LLM 提取结构化记忆，写入持久存储。

#### D. 层级检索策略

在 `Aevatar.AI.Core` 中实现两阶段检索，复用 Aevatar 已有的 Embedding 能力：

1. 意图分析：用 LLM 把用户查询拆解为 TypedQuery 列表
2. 层级检索：先用 L0 向量粗筛 → 按目录聚合 → 目录内 L1 精排 → 必要时加载 L2

### 5.3 推荐实现路径

分为 3 个阶段，渐进式引入：

```
Phase 1: 上下文寻址与存储基础设施
├── Aevatar.Context.Abstractions    # URI Scheme, IContextStore, IContextEntry, ContextType
├── Aevatar.Context.Core            # AGFS 的 .NET 等价物：本地文件 + 内存两种后端
└── 集成到 Foundation.Abstractions  # IAgentContext 扩展 URI 引用能力

Phase 2: 分层加载与检索
├── Aevatar.Context.Extraction      # L0/L1 生成（依赖 Aevatar.AI.Abstractions 的 ILLMProvider）
├── Aevatar.Context.Retrieval       # 向量索引 + 层级检索策略
└── 集成到 Workflow.Core            # Step 执行前的上下文注入

Phase 3: 记忆自演进
├── Aevatar.Context.Memory          # 6 类记忆提取、去重决策
├── MemoryExtractionProjector       # 接入统一 Projection Pipeline
└── 集成到 Session/Run 生命周期     # Run 完成 → 触发记忆提取
```

### 5.4 模块映射关系

| OpenViking 模块 | Aevatar 对应位置 | 实现策略 |
|----------------|-----------------|---------|
| VikingFS + AGFS | `Aevatar.Context.Core` | 重新实现，用 `IFileProvider` 或直接文件系统；不需要 C++ 扩展 |
| Viking URI | `Aevatar.Context.Abstractions` | `AevatarUri` 值对象，与 `EnvelopePropagation.Baggage` / 上下文检索集成 |
| L0/L1/L2 | `Aevatar.Context.Extraction` | 依赖 `ILLMProvider` 生成；自底向上遍历逻辑在此实现 |
| Vector Index | `Aevatar.Context.Retrieval` | 可接入 MEAI 的 `IEmbeddingGenerator` + 本地 HNSW 或外部向量库 |
| IntentAnalyzer | `Aevatar.Context.Retrieval` | LLM 意图分析 → TypedQuery 生成 |
| HierarchicalRetriever | `Aevatar.Context.Retrieval` | 优先级队列 + 目录递归，.NET 中用 `PriorityQueue<T>` |
| SessionService | 已有 `RunManager` + `Projection Pipeline` | 扩展而非重写；在 Projection 末尾追加记忆提取 |
| Compressor | `Aevatar.Context.Memory` | 6 类记忆提取 + LLM 去重决策 |
| Parse (文档解析) | `Aevatar.Context.Core` 或独立模块 | MD/PDF/HTML 解析 + TreeBuilder |

### 5.5 架构约束校验

按照 `AGENTS.md` 的顶级架构要求验证：

| 要求 | 是否满足 | 说明 |
|------|---------|------|
| 严格分层 | 是 | Abstractions → Core → Retrieval/Memory/Extraction 分层清晰 |
| 统一投影链路 | 是 | MemoryExtractionProjector 接入已有 Projection Pipeline |
| 读写分离 | 是 | 写入走 ContextStore，读取走 Retrieval；记忆提取通过事件触发 |
| 依赖反转 | 是 | 上层依赖 Abstractions 中的接口 |
| 不保留无效层 | 是 | 不引入空转发；每层有明确职责 |
| 禁止 `Workflow.Core` 依赖 `AI.Core` | 是 | 上下文模块独立于 Workflow 和 AI，通过 Abstractions 交互 |

---

## 6. 成本与收益评估

### 收益

| 能力 | 当前 | 引入后 |
|------|------|--------|
| 跨 Run 记忆 | 无 | 用户偏好 + Agent 经验自动累积 |
| 外部知识检索 | 无 | 文档/代码库/网页统一索引和检索 |
| Token 预算管理 | 无 | L0/L1/L2 渐进加载，按需控制 |
| 上下文可观测性 | 弱（键值传播） | URI 寻址 + 检索轨迹可追踪 |
| Agent 个性化 | 无 | 记忆驱动的个性化响应 |

### 成本

| 项目 | 估算 |
|------|------|
| Phase 1（基础设施） | 2-3 周；纯 .NET 实现，无外部依赖 |
| Phase 2（检索） | 2-3 周；需要向量索引集成 |
| Phase 3（记忆） | 2-3 周；LLM 调用设计 + Projection 集成 |
| LLM 运行成本 | L0/L1 生成 + 意图分析 + 记忆提取，每次 Run 额外 3-5 次 LLM 调用 |

---

## 7. 结论

OpenViking 的核心贡献不在于某个具体算法，而在于**上下文管理的范式**：用文件系统的直觉来组织、分层、检索和演进 Agent 的上下文。这个范式与 Aevatar 的分层架构、统一投影链路、事件驱动模型高度兼容。

推荐 **不引入 OpenViking 作为依赖**（Python 生态不兼容），而是**借鉴其范式在 .NET 中重新实现**，分三个阶段渐进交付。其中 Phase 1（URI 寻址 + 内容存储）的 ROI 最高，可以立即开始。
