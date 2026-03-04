# Paper Ingestion Agent — 技术设计文档 v2

> 基于统一 TeX 知识节点架构的论文摄取系统。所有知识节点本质上是结构化的 `.tex` 文档。

---

## 1. 概述

### 1.1 背景与动机

Sisyphus 当前知识图谱面临两个核心问题：

1. **缺乏文献摄取能力** — 知识完全由 `sisyphus_research` 循环中 LLM 的内在知识和推理生成，无法直接引用原始学术文献
2. **Context Size 不可控** — 随着图谱增长到数百/数千节点，Researcher 读取图谱数据发送给 LLM 时，容易因 context 超限导致 400 错误

本文档提出的设计用**一个统一方案**同时解决这两个问题：

> **所有知识节点 = 结构化的 `.tex` 文档**，遵循统一的 Knowledge Node Schema，通过 Knowledge Node Skill 约束格式。

这意味着：
- Paper Ingestion 的输出是标准化 `.tex` 节点
- Research Session 的输出也是标准化 `.tex` 节点
- 两条管线共享同一套 schema、同一个 skill
- Context 管理变成确定性的 TeX 结构解析（提取 `\abstract`），而非 LLM 或 embedding 模型

### 1.2 核心需求

| # | 需求 | 说明 |
|---|------|------|
| 1 | TeX 项目解析 | 解析 TeX/LaTeX 项目 AST（`\section`, `\begin{theorem}`, `\cite{}`），支持单文件或 tar.gz 多文件项目 |
| 2 | 知识提取 → 标准化 TeX 节点 | 将论文中的定理、假说、方法、结论等提取为遵循 Knowledge Node Schema 的 `.tex` 节点 |
| 3 | 任意长度输入 | 通过分块策略处理任意长度 TeX 文档（论文、博士论文、书籍） |
| 4 | 显式引用关系 | 节点间关系通过 `\noderef[relation]{id}` 显式声明，而非 LLM 推断 |
| 5 | 通过 NyxId MCP 写入 | 遵循 Agent → MCP Connector → NyxId → Chrono Graph 路径 |
| 6 | 统一 Research 管线 | 同一套 schema + skill 同时服务于论文摄取和自主研究循环 |

### 1.3 设计哲学

```
需要理解力（AI 判断）→ 放在 Agent 层
  • 语义分类（这段文字是假说、定义还是推理？）
  • 知识提取（从论文段落中提炼出结构化 claim）
  • 引用关系判断（\noderef 的 relation 类型）

需要格式约束（模板规范）→ 放在 Skill 层
  • TeX 节点模板格式
  • 字段必填规则、token 上限
  • 引用语法规范

只需要搬运（CRUD/解析）→ 放在 Application 层
  • TeX AST 解析、分块
  • 文件上传、归档解压
  • TeX 节点解析（提取 abstract、noderef）

通用基础设施 → 放在 Chrono Platform
  • 图谱存储（Chrono Graph / Neo4j）
  • 文件存储（Chrono Storage）
```

### 1.4 与 v1 设计的主要变更

| 维度 | v1 设计 | v2 设计 |
|------|--------|--------|
| 节点格式 | JSON blob（松散的 content/evidence 字段） | 结构化 `.tex` 文档，强制模板 |
| 节点类型 | 12+ 种（paper, section, abstract, claim, proof, figure, table, equation, method, result, citation, annotation） | 统一 `.tex` 节点 + `\nodetype{}` 声明类型 |
| 边的来源 | LLM 推断语义关系 | 显式 `\noderef[relation]{id}`，确定性解析 |
| Context 管理 | 无（依赖全量 snapshot） | 提取 `\abstract{}` 做精简投影，token 上限硬约束 |
| PDF 支持 | Phase 2 设计 | 移除，纯 TeX |
| 与 Research 的关系 | 独立管线，不同 node schema | 统一管线，共享 schema + skill |
| Skill | 无 | Knowledge Node Skill 约束所有 agent |

---

## 2. Knowledge Node TeX Schema

### 2.1 设计原则

- 每个知识节点是一个自包含的 `.tex` 文档
- 模板强制结构化：固定的元数据命令 + 必填的 sections
- `\abstract{}` 是 **mandatory** 且有 **硬 token 上限**（≤150 词），用于 context 投影
- 节点间引用通过 `\noderef[relation]{id}` 显式声明，可被确定性解析为图谱边
- 外部文献引用通过标准 `\cite{key}` 机制

### 2.2 节点模板

```latex
% === Sisyphus Knowledge Node ===
% 所有知识节点必须遵循此模板

% --- 元数据 ---
\nodeid{<uuid>}                          % 全局唯一 ID，由系统生成
\nodetype{<type>}                        % 节点类型（见 §2.3）
\confidence{<0.0-1.0>}                   % 置信度
\source{<ingestion|research>}            % 来源管线
\sourceref{<paper-node-id|session-id>}   % 来源论文节点 ID 或研究 session ID

% --- 摘要（必填，≤150 词）---
\begin{abstract}
<自包含的一段话，概括此节点的核心知识主张。
 必须无需阅读正文即可理解。>
\end{abstract}

% --- 正文 ---
\begin{document}

\section{Claim}
<核心知识主张的完整陈述。>

\section{Evidence}
<支撑证据、推理依据或实验结果。
 引用已有节点：\noderef[supports]{node-xxx}
 引用外部文献：\cite{bibtex-key}>

\section{Context}          % 可选
<补充上下文，如适用条件、假设前提等。>

\section{Formal}           % 可选，仅 theorem/lemma/equation 类型
<形式化表述，如数学公式。>

\end{document}
```

### 2.3 节点类型

| 类型 | 说明 | 典型来源 |
|------|------|---------|
| `hypothesis` | 假说，待进一步验证的主张 | Research Session |
| `fact` | 已确认的事实 | Paper Ingestion / Research |
| `inference` | 推理结论 | Research Session |
| `definition` | 概念定义 | Paper Ingestion / Research |
| `theorem` | 定理（含 lemma, corollary, proposition） | Paper Ingestion |
| `method` | 方法论、算法、技术方案 | Paper Ingestion / Research |
| `result` | 实验结果、观测数据 | Paper Ingestion |
| `observation` | 观察、洞察 | Research Session |
| `source_paper` | 论文元数据节点（特殊类型，见 §2.5） | Paper Ingestion |

### 2.4 引用规范

#### 节点间引用：`\noderef`

```latex
\noderef[<relation>]{<node-id>}
```

可用的 relation 类型：

| Relation | 语义 | 示例 |
|----------|------|------|
| `supports` | 当前节点支持目标节点 | 实验结果支持某假说 |
| `contradicts` | 当前节点与目标矛盾 | 新发现推翻旧结论 |
| `extends` | 当前节点扩展目标 | 推广定理到更一般情形 |
| `depends_on` | 当前节点依赖目标 | 推理依赖某定义 |
| `derived_from` | 当前节点从目标推导 | 推论从定理推导 |
| `proves` | 当前节点证明目标 | 证明过程 → 定理 |
| `evaluates` | 当前节点评估目标 | 实验结果评估某方法 |
| `formalizes` | 当前节点形式化目标 | 数学表述 → 直觉描述 |

**解析规则**：Application 层解析 `.tex` 内容，提取所有 `\noderef[rel]{id}`，自动生成对应的图谱边：

```
\noderef[supports]{node-abc} → Edge: current_node --supports--> node-abc
```

#### 外部文献引用：`\cite`

```latex
\cite{bibtex-key}
```

引用键指向 source_paper 节点中记录的 BibTeX entry。Application 层解析 `\cite{key}` 后，生成 `cites` 边指向对应的 source_paper 节点。

### 2.5 Source Paper 节点（特殊类型）

论文摄取时，首先为整篇论文创建一个 `source_paper` 节点，记录元数据：

```latex
\nodeid{paper-2f8a3b}
\nodetype{source_paper}
\confidence{1.0}
\source{ingestion}

% --- 论文元数据 ---
\papertitle{Attention Is All You Need}
\authors{Vaswani, A. and Shazeer, N. and Parmar, N. et al.}
\venue{NeurIPS 2017}
\arxivid{1706.03762}

\begin{abstract}
The dominant sequence transduction models are based on complex recurrent or
convolutional neural networks... We propose a new simple network architecture,
the Transformer, based solely on attention mechanisms...
\end{abstract}

\begin{document}
\section{Claim}
This paper introduces the Transformer architecture...

\section{Evidence}
Original paper abstract and metadata.

\section{Bibliography}
\bibentry{vaswani2017attention}{Vaswani et al., Attention Is All You Need, NeurIPS 2017}
\bibentry{bahdanau2015attention}{Bahdanau et al., Neural Machine Translation by Jointly Learning to Align and Translate, ICLR 2015}
% ... 所有 BibTeX entries
\end{document}
```

从该论文提取的所有知识节点通过 `\sourceref{paper-2f8a3b}` 指向此论文元数据节点。

### 2.6 内容约束

| 字段 | 约束 | 理由 |
|------|------|------|
| `\abstract{}` | **必填**，≤150 词 | Context 投影的数据源，必须精简且自包含 |
| `\section{Claim}` | **必填**，≤300 词 | 核心主张，不能过长 |
| `\section{Evidence}` | **必填**，≤500 词 | 支撑材料 |
| `\section{Context}` | 可选，≤300 词 | 补充信息 |
| `\section{Formal}` | 可选，≤200 词 | 形式化表述 |
| 整个节点 | **≤1200 词**（不含元数据行） | 确保单节点不会占用过多 context |

### 2.7 完整示例

#### 示例 1：从论文提取的 theorem 节点

```latex
\nodeid{node-7c3d9e}
\nodetype{theorem}
\confidence{0.98}
\source{ingestion}
\sourceref{paper-2f8a3b}

\begin{abstract}
The Transformer model achieves state-of-the-art BLEU scores on English-to-German
and English-to-French translation tasks while requiring significantly less training
time than previous recurrent and convolutional architectures.
\end{abstract}

\begin{document}

\section{Claim}
The Transformer architecture, based solely on attention mechanisms without
recurrence or convolution, achieves 28.4 BLEU on the WMT 2014
English-to-German translation task, improving over the existing best results
by over 2 BLEU. On English-to-French, it achieves 41.0 BLEU, surpassing
all previously published single models.

\section{Evidence}
Experimental results reported in Table 2 of \noderef[evaluates]{node-8d4e1f}
demonstrate consistent improvements across both language pairs. The model was
trained for 3.5 days on 8 P100 GPUs, compared to weeks of training for
previous state-of-the-art models \cite{wu2016google}.

\section{Formal}
\text{BLEU}_{en \to de} = 28.4, \quad \text{BLEU}_{en \to fr} = 41.0

\end{document}
```

#### 示例 2：研究 session 产出的 inference 节点

```latex
\nodeid{node-a1b2c3}
\nodetype{inference}
\confidence{0.75}
\source{research}
\sourceref{session-5f6g7h}

\begin{abstract}
Self-attention's quadratic complexity with respect to sequence length limits
direct application of Transformers to long documents. Sparse attention patterns
that reduce complexity to O(n sqrt(n)) maintain comparable performance for
sequences up to 8192 tokens.
\end{abstract}

\begin{document}

\section{Claim}
The quadratic memory and time complexity of self-attention (O(n^2)) in the
original Transformer architecture creates a practical bottleneck for processing
long sequences. Sparse attention variants can reduce this to sub-quadratic
complexity while preserving most of the model's representational capacity.

\section{Evidence}
This inference is derived from the attention mechanism design described in
\noderef[derived_from]{node-7c3d9e} and supported by the experimental findings
in \noderef[supports]{node-d4e5f6} which demonstrate that structured sparsity
patterns achieve 95%+ of dense attention performance on long-range benchmarks.

\section{Context}
This limitation is particularly relevant for Sisyphus's research workflow
where knowledge graphs may contain thousands of nodes requiring long-context
summarization.

\end{document}
```

---

## 3. Knowledge Node Skill

### 3.1 职责

Knowledge Node Skill 是一个 Aevatar Skill（SKILL.md），被注册为 `IAgentTool` 供所有相关 Agent 使用。它的核心职责：

1. **格式约束** — 教 LLM 使用正确的 TeX 节点模板
2. **内容约束** — 约束各字段的 token 上限
3. **引用规范** — 教 LLM 使用 `\noderef[relation]{id}` 语法
4. **质量标准** — abstract 必须自包含、claim 必须明确、evidence 必须有出处

### 3.2 执行流程

```
Agent 激活时 → SkillDiscovery 扫描目录 → 发现 knowledge_node SKILL.md
    → SkillToolAdapter 包装为 IAgentTool → 注册到 ToolManager

Agent 收到任务 → LLM 决定调用 skill_knowledge_node tool
    → ExecuteAsync() 返回 SKILL.md 全文
    → LLM 读取模板规范
    → LLM 按规范生成 .tex 节点
```

### 3.3 Skill 应用场景

| 场景 | 调用者 | Skill 作用 |
|------|--------|-----------|
| 论文摄取 | Paper Ingestion Extractor Agent | 将论文段落转化为标准 .tex 节点 |
| 研究循环 | Researcher Agent | 将推理产出转化为标准 .tex 节点 |
| 知识更新 | 未来 Review Agent | 更新已有节点时遵循同一模板 |

### 3.4 Skill 文件设计

```
apps/sisyphus/skills/knowledge_node/SKILL.md
```

Skill 内容应包含：

1. §2 的完整 TeX 模板
2. 各字段的约束规则（必填性、token 上限）
3. `\noderef` 语法和可用 relation 类型
4. 正例和反例
5. 针对不同场景（ingestion vs research）的具体指引

---

## 4. 系统架构

### 4.1 整体数据流

```
用户上传 .tex 文件 / tar.gz 项目归档
     │
     ▼
┌─────────────────────────────────────────────────────────────┐
│  Sisyphus Host                                               │
│                                                               │
│  ┌───────────────────────┐    ┌──────────────────────────┐  │
│  │  Application Layer     │    │  Orleans Silo              │  │
│  │                        │    │                            │  │
│  │  • 文件接收 & 归档解压  │    │  paper_ingestion          │  │
│  │  • TeX AST 解析        │    │  (WorkflowGAgent)         │  │
│  │  • 分块（respecting    │───▶│    ├─ extractor           │  │
│  │    环境边界）           │    │    │  (读取 chunk,         │  │
│  │  • Chunk Manifest 生成 │    │    │   调用 Knowledge      │  │
│  │  • 上传 chunks 到      │    │    │   Node Skill,         │  │
│  │    Chrono Storage      │    │    │   产出 .tex 节点)      │  │
│  │  • 触发 Workflow       │    │    │                       │  │
│  │                        │◀───│    └─ dag_writer           │  │
│  │  • 接收完成事件        │    │       (解析 .tex 节点,      │  │
│  │  • 解析 \noderef       │    │        写入 Chrono Graph)  │  │
│  │    生成 edges          │    │                            │  │
│  └───────────────────────┘    └──────────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
         │                              │
         ▼                              ▼
  Chrono Storage                 NyxId MCP Server
  (原始文件 + chunks)                   │
                                        ▼
                                 Chrono Graph Service
                                 (Neo4j: 节点 + 边)
```

### 4.2 与现有 Workflow 的关系

Paper Ingestion 是第 3 个 WorkflowGAgent：

| # | Workflow | 职责 | 产出 |
|---|---------|------|------|
| 1 | `sisyphus_research` | 自主研究循环 | 标准 `.tex` 知识节点 |
| 2 | `sisyphus_maker` | 多角度验证 | pass/fail 判定 |
| 3 | **`paper_ingestion`** | **论文摄取** | **标准 `.tex` 知识节点（同一 schema）** |

关键设计点：**两条管线产出的节点格式完全相同**。Research 节点和 Ingestion 节点可以互相 `\noderef` 引用，不存在格式兼容问题。

### 4.3 角色设计

相比 v1 的 4 个角色（Chunker, Extractor, Mapper, DAG Writer），v2 精简为 **2+1 个角色**：

| 角色 | 职责 | 工具 | 适用场景 |
|------|------|------|---------|
| **Analyzer** | 分析大型 TeX 项目结构 → 生成 Processing Manifest | Chrono Storage (读项目文件), tex-bulk-load Skill | 仅大型项目（多文件 TeX 工程） |
| **Extractor** | 读取 chunk/PU → 调用 Knowledge Node Skill → 产出标准 `.tex` 节点 | Chrono Storage (读 chunk), knowledge-tex Skill |  所有场景 |
| **DAG Writer** | 接收 `.tex` 节点 → 写入 Chrono Graph | Chrono Graph (写节点/边) | 所有场景 |

**为什么可以精简：**

- **Chunker 移到 Application 层** — 分块是确定性的 AST 操作，不需要 LLM
- **Mapper 角色被 Skill 取代** — v1 中 Mapper 负责"将提取内容映射到 DAG schema"，现在 Knowledge Node Skill 直接约束 Extractor 的输出格式，不再需要额外的映射步骤
- **边的生成移到 Application 层** — `\noderef` 的解析是确定性的正则匹配，不需要 LLM
- **Analyzer 仅在大型项目时激活** — 单文件论文直接进入 Extractor 流程，无需项目分析。大型项目（多文件 TeX 工程）需要 Analyzer 先理解项目拓扑并生成 Processing Manifest（详见 §14）

---

## 5. Paper Ingestion Workflow

### 5.1 端到端流程

```
┌──────────────────────────────────────────────────────────────────┐
│ Phase 1: Application Layer (确定性，无 LLM)                       │
│                                                                   │
│  上传 .tex / tar.gz                                               │
│       │                                                           │
│       ▼                                                           │
│  [TeX Project Resolver] 解压归档，发现 main entry                  │
│       │                                                           │
│       ▼                                                           │
│  [TeX AST Parser] 解析 AST，展开 \input/\include                  │
│       │                                                           │
│       ▼                                                           │
│  [Chunking Service] 按 section 边界分块，生成 Chunk Manifest       │
│       │                                                           │
│       ▼                                                           │
│  [Upload] chunks + manifest → Chrono Storage                      │
│       │                                                           │
│       ▼                                                           │
│  [Source Paper Node] 创建 source_paper 节点 → Chrono Graph         │
│       │                                                           │
│       ▼                                                           │
│  触发 paper_ingestion Workflow                                    │
└──────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌──────────────────────────────────────────────────────────────────┐
│ Phase 2: Agent Layer (LLM + Skill)                                │
│                                                                   │
│  for each chunk in manifest:                                      │
│       │                                                           │
│       ▼                                                           │
│  [Extractor] 读取 chunk → 调用 Knowledge Node Skill               │
│       │       → 产出 1-N 个标准 .tex 节点                          │
│       │                                                           │
│       ▼                                                           │
│  [DAG Writer] 接收 .tex 节点                                      │
│       │       → 写入节点到 Chrono Graph                            │
│       │       → 返回创建的 node IDs                                │
│       │                                                           │
│       └───→ 下一个 chunk                                          │
│                                                                   │
└──────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌──────────────────────────────────────────────────────────────────┐
│ Phase 3: Application Layer (确定性，无 LLM)                       │
│                                                                   │
│  [Edge Resolver] 扫描所有已创建节点的 .tex 内容                    │
│       │          解析 \noderef[rel]{id} → 生成 edges               │
│       │          解析 \cite{key} → 生成 cites edges               │
│       │                                                           │
│       ▼                                                           │
│  [Batch Edge Writer] 批量写入 edges → Chrono Graph                │
│       │                                                           │
│       ▼                                                           │
│  完成                                                             │
└──────────────────────────────────────────────────────────────────┘
```

### 5.2 为什么分三个 Phase

| Phase | 层 | LLM? | 理由 |
|-------|---|------|------|
| 1 | Application | 否 | TeX 解析、分块、元数据提取都是确定性操作 |
| 2 | Agent | **是** | 知识提取需要语义理解（"这段话是假说还是已证结论？"） |
| 3 | Application | 否 | `\noderef` 解析是正则匹配，确定性生成 edges |

关键优势：**Phase 3 解耦了边的生成**。在 v1 中，LLM 既要提取知识又要判断关系类型——两个认知负载叠加。v2 中，LLM 只负责在写 `.tex` 时选择正确的 `\noderef[relation]{id}`，而实际的 edge 创建是 Application 层的确定性操作。

### 5.3 Workflow YAML

```yaml
name: paper_ingestion
description: >
  Parse TeX academic papers into standardized .tex knowledge nodes
  and write them to the knowledge graph.

roles:
  - id: extractor
    name: Knowledge Extractor
    system_prompt: |
      You are a knowledge extraction specialist. Your task is to read
      chunks of academic TeX documents and extract structured knowledge
      claims as standardized .tex knowledge nodes.

      ## Graph IDs

      Two Graph IDs are provided in the "Original Context" block:
      - `Read Graph ID: <uuid>` — for reading existing graph state
      - `Write Graph ID: <uuid>` — passed to DAG Writer (not your concern)
      - `Paper Node ID: <uuid>` — the source_paper node for this paper

      ## Tools

      You have the Knowledge Node Skill tool which defines the exact
      .tex template format. ALWAYS invoke it before generating nodes.

      You also have access to Chrono Storage tools via NyxId MCP to
      read chunks:
      - `chrono-storage-service__get_object` — read a chunk by storage key

      ## Workflow

      For each chunk you receive:
      1. Read the chunk content from Chrono Storage using the provided storage_key
      2. Invoke the Knowledge Node Skill to load the .tex template specification
      3. Identify all knowledge claims in the chunk (theorems, hypotheses,
         definitions, methods, results, observations)
      4. For each claim, generate a standardized .tex node following the template
      5. Use \noderef[relation]{id} to reference:
         - Other nodes created in this session (use temp IDs: t0, t1, ...)
         - The source paper node: \sourceref{<paper-node-id>}
      6. Ensure every node has a ≤150 word abstract
      7. Output all generated .tex nodes as a JSON array

      ## Output Format
      {
        "nodes": [
          {
            "temp_id": "t0",
            "tex_content": "\\nodeid{...}\n\\nodetype{...}\n..."
          }
        ],
        "saturation": false
      }

      ## Constraints
      - Each node ≤1200 words (excluding metadata lines)
      - Abstract ≤150 words, mandatory
      - 1-8 nodes per chunk (quality over quantity)
      - Do NOT create nodes for trivial content (acknowledgements, formatting)
    connectors:
      - nyxid_mcp
    skills:
      - knowledge_node

  - id: dag_writer
    name: DAG Writer
    system_prompt: |
      You are a knowledge graph writer. You receive .tex knowledge nodes
      from the Extractor and write them to Chrono Graph.

      ## Graph IDs

      Two Graph IDs are provided in the "Original Context" block:
      - `Read Graph ID: <uuid>` — not used by you
      - `Write Graph ID: <uuid>` — use this for ALL write operations

      ## Tools

      Via NyxId MCP:
      - `chrono-graph-service__post_api_graphs_by_graphid_nodes` — create nodes
      - `chrono-graph-service__get_api_graphs_by_graphid_nodes` — check existing

      ## Workflow

      For each batch of .tex nodes from the Extractor:
      1. For each node, create a graph node with properties:
         - type: extracted from \nodetype{}
         - title: extracted from first line of \section{Claim}
         - abstract: extracted from \begin{abstract}...\end{abstract}
         - tex_content: the full .tex source
         - confidence: extracted from \confidence{}
         - source: "ingestion"
         - source_paper_id: extracted from \sourceref{}
      2. Map temp IDs (t0, t1, ...) to created node UUIDs
      3. Report all created node IDs

      ## Output Format
      {
        "created_nodes": [
          {"temp_id": "t0", "node_id": "<uuid>", "title": "..."}
        ],
        "total_created": N
      }
    connectors:
      - nyxid_mcp

steps:
  # --- 初始化 ---
  - id: init
    type: assign
    parameters:
      chunk_index: 0
      total_chunks: ${total_chunks}
      paper_node_id: ${paper_node_id}
      created_node_registry: "{}"

  # --- 分块提取循环 ---
  - id: extraction_loop
    type: while
    condition: chunk_index < total_chunks
    steps:

      # Extractor: 读取 chunk + 提取知识 + 产出 .tex 节点
      - id: extract
        type: llm_call
        role: extractor
        parameters:
          prompt_prefix: |
            Process chunk {{chunk_index}} of {{total_chunks}}.
            Paper Node ID: {{paper_node_id}}
            Chunk storage key: {{chunk_manifest[chunk_index].storage_key}}
            Section path: {{chunk_manifest[chunk_index].section_path}}

            Previously created nodes in this session:
            {{created_node_registry}}

            Extract knowledge claims from this chunk as standardized .tex nodes.

      # DAG Writer: 写入节点
      - id: write_nodes
        type: llm_call
        role: dag_writer
        parameters:
          prompt_prefix: |
            Write the following .tex knowledge nodes to the graph using
            the Write Graph ID from the Original Context block:

      # 更新注册表
      - id: update_registry
        type: assign
        parameters:
          created_node_registry: merge(created_node_registry, write_nodes.output.created_nodes)
          chunk_index: chunk_index + 1

  # --- 完成 ---
  - id: finalize
    type: assign
    parameters:
      status: "completed"
      total_nodes_created: len(created_node_registry)
```

### 5.4 Temp ID 解析

Extractor 在处理单个 chunk 时，可能产出多个相互引用的节点（如 theorem + proof）。由于节点尚未写入图谱，没有真实 UUID。

**策略**：

1. Extractor 使用 temp IDs：`t0`, `t1`, `t2`, ...
2. `.tex` 中引用 temp ID：`\noderef[proves]{t0}`
3. DAG Writer 创建节点后获得真实 UUID
4. `created_node_registry` 维护 `{temp_id → real_uuid}` 映射
5. Phase 3 的 Edge Resolver 在解析 `\noderef` 时，先查 registry 替换 temp ID 为真实 UUID

### 5.5 跨 Chunk 引用

论文中后面的章节经常引用前面的内容（如 "as shown in Theorem 1"）。

**策略**：

1. 每个 chunk 的提取 prompt 包含 `created_node_registry` — Extractor 知道之前已创建了哪些节点
2. Extractor 可以直接用已创建节点的真实 UUID：`\noderef[depends_on]{node-7c3d9e}`
3. 如果引用指向尚未处理的 chunk（前向引用），Extractor 使用占位符 `\noderef[depends_on]{pending:theorem_main}` — Phase 3 的 Edge Resolver 尝试匹配

---

## 6. TeX 解析与分块（Application 层）

### 6.1 TeX Project Resolver

处理两种输入形式：

| 输入 | 处理 |
|------|------|
| 单个 `.tex` 文件 | 直接作为输入 |
| `tar.gz` 归档 | 内存解压 → 发现 main entry → 解析 `\input{}`/`\include{}` 引用 |

**Main Entry 发现规则**（tar.gz 场景）：

1. 找到包含 `\documentclass{...}` 的 `.tex` 文件
2. 多个候选时：优先根目录文件，按文件名排序（main > paper > thesis > document）
3. 再平局：选最大文件

**接口设计**：

```csharp
public interface ITexFileResolver
{
    string GetMainContent();
    string? Resolve(string inputPath, string referringFilePath);
    IReadOnlyList<string> GetAllFilePaths();
}
```

**安全约束**：

| 约束 | 阈值 | 动作 |
|------|------|------|
| 路径穿越（`..` 或绝对路径） | 0 容忍 | Hard reject |
| 符号链接 | 0 容忍 | Hard reject |
| 非白名单后缀 | `.tex, .sty, .cls, .bst, .bib, .def` | Soft skip（跳过该文件） |
| 单文件 > 10 MB | 10 MB | Soft skip |
| 归档解压后总体积 > 200 MB | 200 MB | Hard reject |
| 文件总数 > 500 | 500 | Hard reject |
| 压缩比 > 100:1 | 100:1 | Hard reject（zip bomb 检测） |

### 6.2 AST 解析

从 TeX 源文件中提取结构化信息：

**提取目标**：

| AST 元素 | 用途 |
|----------|------|
| `\section{...}`, `\subsection{...}` | 分块边界 |
| `\begin{theorem}`, `\begin{lemma}`, `\begin{proof}` | 知识单元识别 |
| `\label{...}` | 节点唯一标识（TeX 内） |
| `\ref{...}`, `\eqref{...}` | 内部引用追踪 |
| `\cite{...}` | 外部文献引用 |
| `\input{...}`, `\include{...}` | 多文件展开 |
| `\title{...}`, `\author{...}` | 论文元数据 |

**解析策略**：轻量级正则 + 状态机，不需要完整的 TeX 编译器。只关注结构命令，忽略排版命令（`\textbf`, `\vspace` 等）。

### 6.3 分块策略

**目标**：将长文档拆分为 LLM 可处理的 chunk，同时保持语义完整性。

**分块规则**：

1. **主分割点**：`\chapter{}`, `\section{}` 边界
2. **次分割点**：如果单 section > token 上限，在 `\subsection{}` 边界再分
3. **不可分割单元**：`\begin{theorem}...\end{theorem}`, `\begin{proof}...\end{proof}`, `\begin{equation}...\end{equation}` — 绝不在环境内部分割
4. **Token 上限**：每 chunk ≤ 35,000 tokens（留 5,000 tokens 给 system prompt + skill 内容）

**Chunk Manifest Schema**：

```json
{
  "paper_id": "<uuid>",
  "source_file": "main.tex",
  "upload_type": "single_file | tar_gz_archive",
  "total_chunks": 4,
  "storage_bucket": "papers",
  "storage_prefix": "papers/{paper_id}/chunks/",
  "bibliography": {
    "storage_key": "papers/{paper_id}/bibliography.json",
    "entry_count": 42
  },
  "chunks": [
    {
      "chunk_id": "chunk_000",
      "index": 0,
      "section_path": "Abstract + 1. Introduction",
      "storage_key": "papers/{paper_id}/chunks/chunk_000.json",
      "token_estimate": 12000,
      "labels_defined": ["sec:intro", "fig:architecture"],
      "labels_referenced": ["sec:experiments", "thm:main"],
      "cites": ["vaswani2017attention", "devlin2019bert"]
    },
    {
      "chunk_id": "chunk_001",
      "index": 1,
      "section_path": "2. Related Work + 3. Method",
      "storage_key": "papers/{paper_id}/chunks/chunk_001.json",
      "token_estimate": 28000,
      "labels_defined": ["sec:method", "thm:main", "eq:loss"],
      "labels_referenced": ["sec:intro", "fig:architecture"],
      "cites": ["kingma2015adam", "he2016resnet"]
    }
  ]
}
```

### 6.4 容量估算

| 文档类型 | 页数 | Chunks | LLM Calls（Extractor + Writer） | 预计耗时 |
|---------|------|--------|-------------------------------|---------|
| 会议论文 | 8-12 | 1-2 | 2-4 | 1-3 min |
| 期刊论文 | 20-40 | 2-4 | 4-8 | 3-10 min |
| 综述论文 | 40-80 | 4-8 | 8-16 | 10-30 min |
| 博士论文 | 150-300 | 15-30 | 30-60 | 30-90 min |

---

## 7. DAG 存储设计

### 7.1 Neo4j 节点属性

Knowledge Node 在 Chrono Graph (Neo4j) 中的存储：

```
(:KnowledgeNode {
  id:              STRING    -- UUID, 主键
  type:            STRING    -- nodetype (hypothesis, fact, theorem, ...)
  title:           STRING    -- Claim section 首句（≤100 字符）
  abstract:        STRING    -- \begin{abstract}...\end{abstract} 内容
  tex_content:     STRING    -- 完整 .tex 源文件
  confidence:      FLOAT     -- 0.0-1.0
  source:          STRING    -- "ingestion" | "research"
  source_ref:      STRING    -- paper node ID 或 session ID
  word_count:      INT       -- 正文词数
  created_at:      DATETIME
})
```

**存储分层**：

| 字段 | 用途 | 查询频率 |
|------|------|---------|
| `id`, `type`, `title` | 索引、快速列表 | 极高 |
| `abstract` | **Context 投影**（Researcher 读取精简视图） | 高 |
| `tex_content` | 完整知识内容（深度阅读时） | 低 |
| `confidence`, `source`, `source_ref` | 过滤、溯源 | 中 |

**关键设计**：`abstract` 作为独立字段存储（而非嵌在 `tex_content` 里去解析），使得 Chrono Graph 可以提供一个**只返回 abstract 的轻量查询接口**，而无需返回完整 `tex_content`。

### 7.2 边类型

所有边从 `\noderef[relation]{id}` 确定性解析生成：

```
(:KnowledgeNode)-[:SUPPORTS]->(:KnowledgeNode)
(:KnowledgeNode)-[:CONTRADICTS]->(:KnowledgeNode)
(:KnowledgeNode)-[:EXTENDS]->(:KnowledgeNode)
(:KnowledgeNode)-[:DEPENDS_ON]->(:KnowledgeNode)
(:KnowledgeNode)-[:DERIVED_FROM]->(:KnowledgeNode)
(:KnowledgeNode)-[:PROVES]->(:KnowledgeNode)
(:KnowledgeNode)-[:EVALUATES]->(:KnowledgeNode)
(:KnowledgeNode)-[:FORMALIZES]->(:KnowledgeNode)
(:KnowledgeNode)-[:CITES]->(:KnowledgeNode)        -- \cite → source_paper 节点
(:KnowledgeNode)-[:SOURCE_OF]->(:KnowledgeNode)     -- source_paper → 子节点（反向）
```

### 7.3 Chrono Graph 新端点需求

为支持 Context 投影和高效查询，Chrono Graph 需要新增：

| 端点 | 用途 |
|------|------|
| `GET /api/graphs/{id}/nodes?fields=id,type,title,abstract` | 轻量列表（只返回投影字段） |
| `GET /api/graphs/{id}/nodes?titles=X,Y,Z` | 标题查重（DAG Writer 去重） |
| `GET /api/graphs/{id}/summary` | 返回所有节点的 `{id, type, title, abstract}` + 边统计 |

---

## 8. Context 管理策略

### 8.1 问题回顾

随着知识图谱增长，Researcher Agent 需要读取图谱现有知识来指导下一轮研究。如果直接发送全量 snapshot（所有节点的完整 `tex_content`），context 会迅速超限。

### 8.2 基于 TeX Schema 的解决方案

Knowledge Node Schema 中的 **mandatory `\abstract{}`（≤150 词）** 是解决 context 问题的核心机制。

**Context 投影层级**：

| 层级 | 内容 | Token 量/节点 | 使用场景 |
|------|------|-------------|---------|
| L0 — Index | `{id, type, title}` | ~10 tokens | 节点列表、概览 |
| L1 — Abstract | `{id, type, title, abstract}` | ~100 tokens | **Researcher 常规上下文** |
| L2 — Full | 完整 `tex_content` | ~800 tokens | 深度引用、细节查看 |

**Researcher 的 Context 组装策略**：

```
Researcher 每轮收到的 context =
  所有节点的 L1 投影（id + type + title + abstract）
  + 最近一轮创建的节点的 L2（完整 tex_content）
  + 研究主题 + system prompt + skill 内容
```

### 8.3 Token Budget 计算

| 组成部分 | Token 估算 | 说明 |
|---------|-----------|------|
| System prompt + Skill | ~3,000 | 固定开销 |
| 研究主题 + 上下文指令 | ~500 | 固定开销 |
| 全部节点 L1 投影（N 个节点） | N × 100 | **线性增长，但每节点 ≤100 tokens** |
| 最近创建节点 L2（5 个） | 5 × 800 = 4,000 | 固定（只看最新一轮） |
| LLM 输出空间 | ~4,000 | 预留给 LLM 的生成空间 |

**假设 128K context window**：

```
可用给 L1 投影的 tokens = 128,000 - 3,000 - 500 - 4,000 - 4,000 = 116,500
最大节点数 = 116,500 / 100 = ~1,165 个节点
```

即使保守估计（实际 context window 使用率 50%），也能支持 ~580 个节点的 L1 投影。按每轮 5 个节点计算，可以支持 **100+ 轮研究**，远超 Alpha 的 20 轮上限。

### 8.4 当节点超过 L1 投影容量时

对于极大规模图谱（1000+ 节点），引入 **Summary Node**：

```latex
\nodeid{summary-epoch-3}
\nodetype{summary}
\confidence{1.0}
\source{system}

\begin{abstract}
Epoch 3 summary (rounds 11-15): Research focused on sparse attention
mechanisms. Key findings: ... Established 23 new nodes covering ...
\end{abstract}

\begin{document}
\section{Claim}
This is an automated summary of rounds 11-15 of the research session.

\section{Evidence}
Summarizes nodes: \noderef[summarizes]{node-a1}, \noderef[summarizes]{node-a2}, ...
\end{document}
```

**Summary Node 层级**：

| 层级 | 触发条件 | 覆盖范围 |
|------|---------|---------|
| Round Summary | 每轮结束 | 该轮创建的 3-5 个节点 |
| Epoch Summary | 每 5 轮 | 5 个 Round Summaries |
| Global Summary | 每次 Epoch Summary 更新时 | 所有 Epoch Summaries |

Researcher 读取：`Global Summary + 最新 Epoch + 当前轮 raw nodes` — 总量恒定。

Summary Node 的生成可以用 `connector_call`（HTTP/CLI）调用轻量 LLM 或在 workflow 中插入一个 summarizer step。

---

## 9. 与研究循环的统一

### 9.1 Research Workflow 的变更

当前 `sisyphus_research.yaml` 中 Researcher 产出的是 JSON claims：

```json
{ "claims": [{ "title": "...", "content": "...", "type": "...", "evidence": "..." }] }
```

统一后，Researcher 同样调用 Knowledge Node Skill，产出标准 `.tex` 节点：

```
Researcher → 调用 Knowledge Node Skill → 产出 .tex 节点 → Verifier 验证 → DAG Builder 写入
```

**变更点**：

| 组件 | 当前 | 统一后 |
|------|------|--------|
| Researcher 输出 | JSON claims | `.tex` 节点（遵循 Knowledge Node Schema） |
| Verifier 输入 | JSON claims | `.tex` 节点（验证内容 + 格式合规性） |
| DAG Builder 输入 | JSON claims（需推断关系） | `.tex` 节点（`\noderef` 已声明关系） |
| DAG Builder 职责 | 推断关系 + 创建节点/边 | 只写节点；边由 Application 层解析 |
| Researcher Context | 全量 snapshot JSON | L1 投影（`abstract` 列表） |

### 9.2 互引用

Paper Ingestion 节点和 Research 节点使用完全相同的 `\noderef` 语法互引：

```latex
% Research 节点引用 Paper Ingestion 节点
\noderef[extends]{node-7c3d9e}     % 引用从论文提取的 theorem

% Paper Ingestion 节点被 Research 节点引用
% （无需修改 ingestion 节点，Research 节点的 \noderef 自动建立边）
```

---

## 10. API 设计

### 10.1 论文上传

```
POST /api/v2/papers/ingest
Content-Type: multipart/form-data

Fields:
  - file: .tex 文件或 .tar.gz 归档（必填）
  - session_id: 关联的研究 session ID（可选）
  - graph_id: 目标图谱 ID（可选，默认使用当前 session 的 write graph）

Response: 201 Created
{
  "ingestion_id": "<uuid>",
  "paper_node_id": "<uuid>",
  "status": "processing",
  "chunk_manifest": {
    "total_chunks": 4,
    "estimated_nodes": 15
  }
}
```

### 10.2 进度查询

```
GET /api/v2/papers/ingest/{ingestion_id}

Response: 200 OK
{
  "ingestion_id": "<uuid>",
  "paper_node_id": "<uuid>",
  "status": "processing | completed | failed",
  "progress": {
    "chunks_processed": 2,
    "chunks_total": 4,
    "nodes_created": 8
  },
  "created_nodes": [
    {"node_id": "<uuid>", "type": "theorem", "title": "..."},
    ...
  ]
}
```

### 10.3 WebSocket 实时进度

```
GET /ws/papers/ingest/{ingestion_id}

Events:
  { "event": "chunk_started", "chunk_index": 0, "section_path": "Abstract + 1. Introduction" }
  { "event": "nodes_extracted", "chunk_index": 0, "count": 3 }
  { "event": "nodes_written", "chunk_index": 0, "node_ids": ["..."] }
  { "event": "chunk_completed", "chunk_index": 0 }
  ...
  { "event": "edges_resolved", "count": 12 }
  { "event": "ingestion_completed", "total_nodes": 15, "total_edges": 12 }
```

---

## 11. Application Layer Services

### 11.1 服务清单

| 服务 | 职责 | 层 |
|------|------|---|
| `TexProjectResolver` | 归档解压、main entry 发现、`\input` 展开 | Application |
| `TexAstParser` | AST 解析（section/environment/label/ref/cite） | Application |
| `ChunkingService` | 按 section 边界分块，生成 Chunk Manifest | Application |
| `TexNodeParser` | 解析 `.tex` 节点中的 `\noderef`、`\cite`、`\abstract` | Application |
| `EdgeResolverService` | 扫描所有节点的 `\noderef` → 生成 edges | Application |
| `PaperIngestionService` | 编排 Phase 1 + 触发 Workflow + Phase 3 | Application |
| `PaperIngestionTriggerService` | 触发 `paper_ingestion` Workflow（类似 `WorkflowTriggerService`） | Application |

### 11.2 TexNodeParser — 核心解析器

负责从 `.tex` 节点内容中提取结构化数据（供 DAG Writer 和 Edge Resolver 使用）：

```csharp
public sealed class TexNodeParser
{
    /// 提取所有 \noderef[relation]{id}
    public IReadOnlyList<NodeReference> ParseNodeRefs(string texContent);

    /// 提取所有 \cite{key}
    public IReadOnlyList<string> ParseCitations(string texContent);

    /// 提取 \begin{abstract}...\end{abstract}
    public string? ParseAbstract(string texContent);

    /// 提取 \nodetype{...}
    public string? ParseNodeType(string texContent);

    /// 提取 \nodeid{...}
    public string? ParseNodeId(string texContent);

    /// 提取 \confidence{...}
    public float? ParseConfidence(string texContent);

    /// 验证节点是否符合 schema 约束（必填字段、token 上限）
    public ValidationResult Validate(string texContent);
}

public record NodeReference(string Relation, string TargetId);

public record ValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors,    // 硬错误（必须修复）
    IReadOnlyList<string> Warnings   // 软警告（建议修复）
);
```

**解析方式**：正则匹配，不需要完整 TeX 编译器。

关键正则：

```
\noderef:     \\noderef\[(\w+)\]\{([^}]+)\}
\cite:        \\cite\{([^}]+)\}
\abstract:    \\begin\{abstract\}([\s\S]*?)\\end\{abstract\}
\nodetype:    \\nodetype\{([^}]+)\}
\nodeid:      \\nodeid\{([^}]+)\}
\confidence:  \\confidence\{([^}]+)\}
```

---

## 12. 配置

```json
{
  "Sisyphus": {
    "PaperIngestion": {
      "ChunkTokenLimit": 35000,
      "ChunkTokenSafetyMargin": 0.05,
      "MaxFileSize": "10MB",
      "MaxArchiveSize": "200MB",
      "MaxArchiveFiles": 500,
      "MaxCompressionRatio": 100,
      "AllowedExtensions": [".tex", ".sty", ".cls", ".bst", ".bib", ".def"],
      "StorageBucket": "papers"
    },
    "KnowledgeNode": {
      "AbstractMaxWords": 150,
      "ClaimMaxWords": 300,
      "EvidenceMaxWords": 500,
      "ContextMaxWords": 300,
      "FormalMaxWords": 200,
      "TotalMaxWords": 1200,
      "NodesPerChunkMax": 8
    },
    "ContextProjection": {
      "L1MaxTokensPerNode": 120,
      "L2MaxNodesPerRound": 5,
      "SummaryNodeTrigger": 500,
      "EpochRounds": 5
    },
    "BulkLoad": {
      "PUTokenLimit": 35000,
      "PUMergeMinTokens": 2000,
      "MaxPUsPerProject": 200,
      "ContextLayers": {
        "PaperOverviewMaxTokens": 200,
        "DependencySummaryMaxTokens": 500,
        "SiblingSummaryMaxTokens": 300,
        "CompactRegistryMaxTokens": 500
      },
      "Checkpoint": {
        "Enabled": true,
        "StoragePrefix": "papers/{project_id}/checkpoints/",
        "AutoSaveAfterEveryPU": true
      },
      "FileClassification": {
        "MainBodyPatterns": ["sections/*.tex"],
        "AppendixPatterns": ["sections/appendices/*.tex"],
        "GeneratedPatterns": ["sections/generated/**/*.tex"],
        "SkipPatterns": ["*.sty", "*.cls", "*.bst", "*.def", "scripts/**", "data/**", "figures/**"]
      }
    }
  }
}
```

---

## 13. 实施路径

### Phase 1: Foundation（与 Alpha 同步）

| # | 任务 | 依赖 |
|---|------|------|
| 1 | 定义 Knowledge Node Skill（SKILL.md） | 无 |
| 2 | 实现 `TexNodeParser`（正则解析 `.tex` 节点） | 无 |
| 3 | 修改 Research Workflow：Researcher 产出 `.tex` 节点 | 1 |
| 4 | 修改 DAG Builder：写入 `.tex` 节点（abstract 作为独立字段） | 2 |
| 5 | 实现 Context 投影：Researcher 读取 L1 而非 full snapshot | 4 |

### Phase 2: Paper Ingestion

| # | 任务 | 依赖 |
|---|------|------|
| 6 | 实现 `TexProjectResolver`（归档解压、main entry 发现） | 无 |
| 7 | 实现 `TexAstParser`（AST 解析） | 无 |
| 8 | 实现 `ChunkingService`（分块 + Manifest） | 7 |
| 9 | 实现 `paper_ingestion` Workflow（Extractor + DAG Writer） | 1, 2 |
| 10 | 实现 `EdgeResolverService`（`\noderef` → edges） | 2 |
| 11 | 实现 `PaperIngestionService`（编排 Phase 1-3） | 6, 8, 9, 10 |
| 12 | 实现 API endpoints（上传、进度查询、WebSocket） | 11 |

### Phase 3: Scale

| # | 任务 | 依赖 |
|---|------|------|
| 13 | 实现 Summary Node 自动生成 | 5 |
| 14 | Chrono Graph 新端点（轻量查询、标题查重） | 4 |
| 15 | 批量论文上传支持 | 12 |

### Phase 4: Large Project Bulk Loading

| # | 任务 | 依赖 |
|---|------|------|
| 16 | 定义 tex-bulk-load Skill（SKILL.md） | 1 |
| 17 | 实现 `TexInclusionTreeBuilder`（递归 `\input`/`\include` 追踪，构建完整包含树） | 7 |
| 18 | 实现 `FileClassificationService`（按规则分类 main_body/appendix/generated/auxiliary/data/script/figure） | 17 |
| 19 | 实现 `ProcessingUnitBuilder`（按层级和 topic prefix 分组文件为 PU，含 token 估算和依赖拓扑排序） | 17, 18 |
| 20 | 增强 `paper_ingestion` Workflow：添加 Analyzer 角色 + PU 循环替代 chunk 循环 | 9, 16, 19 |
| 21 | 实现 `PUContextAssembler`（构建分层 context：paper overview / context chain / dependency summaries / sibling summaries / compact registry） | 20 |
| 22 | 实现 `CheckpointService`（PU 级别检查点保存与恢复） | 20 |
| 23 | 实现 `PUSummaryGenerator`（PU 完成后生成摘要，供下游 PU context 注入） | 20, 21 |
| 24 | 增强 API：支持目录/tar.gz 项目上传 + 批量进度查询 | 12, 20 |

---

## 14. 大型项目批量摄取 (tex-bulk-load)

### 14.1 背景与挑战

§5 定义的标准 Paper Ingestion 管线假设输入是**单篇论文**（1-3 个 `.tex` 文件）。对于大型 TeX 工程项目（如专著、博士论文、多百页理论物理论文），管线面临以下挑战：

| 维度 | 标准论文 | 大型 TeX 工程 |
|------|---------|-------------|
| 文件数 | 1-3 个 `.tex` | **数百个 `.tex` 文件** |
| 结构 | 扁平 sections | **多层 `\input` 嵌套（3-4 级）** |
| 内容类型 | 纯论文 | **正文 + 附录 + 自动生成片段 + 数据 + 脚本 + 图表** |
| 大小 | 10-40 页 | **100+ MB 项目，300+ 页等价内容** |
| 参考文献 | 20-50 条 | **数千行 `.bib` 文件** |
| 预估节点数 | 5-15 个 | **100-400 个** |
| 处理时间 | 1-10 min | **1-3 小时** |

**核心问题**：

1. **项目拓扑理解** — 哪些文件是主体内容 vs 附录 vs 生成片段 vs 辅助文件？
2. **语义分组** — 不能按 token 上限机械分块，需要尊重论文层级结构（Part → Chapter → Section）
3. **处理顺序** — 后续章节引用前面定义的概念，需要按依赖关系排序
4. **大规模 Context 管理** — 处理第 50 个块时，不可能把前 49 个块产出的 200+ 节点全部塞进 Extractor prompt
5. **容错与恢复** — 2 小时的处理过程中间断电/失败，不能从头重来

`tex-bulk-load` Skill 解决这些问题。

### 14.2 架构定位

```
┌──────────────────────────────────────────────────────────────────────┐
│  paper_ingestion Workflow (增强版)                                     │
│                                                                       │
│  ┌───────────────────────────────┐                                   │
│  │  Phase 0: Project Analysis     │  ← NEW (仅大型项目)               │
│  │  Analyzer 角色                 │                                   │
│  │  tex-bulk-load Skill           │                                   │
│  │                                │                                   │
│  │  • 扫描项目 → 包含树           │                                   │
│  │  • 分类文件                    │                                   │
│  │  • 构建 Processing Units       │                                   │
│  │  • 输出 Processing Manifest    │                                   │
│  └────────────┬──────────────────┘                                   │
│               │ manifest                                              │
│               ▼                                                       │
│  ┌───────────────────────────────┐                                   │
│  │  Phase 1: PU Extraction Loop   │  ← 替代原 chunk 循环              │
│  │  Extractor 角色                │                                   │
│  │  knowledge-tex Skill           │                                   │
│  │                                │                                   │
│  │  for each PU in manifest:      │                                   │
│  │    • 注入分层 context           │                                   │
│  │    • 提取知识节点               │                                   │
│  │    • DAG Writer 写入            │                                   │
│  │    • 保存 checkpoint            │                                   │
│  │    • 生成 PU summary            │                                   │
│  └────────────┬──────────────────┘                                   │
│               │                                                       │
│               ▼                                                       │
│  ┌───────────────────────────────┐                                   │
│  │  Phase 2: Edge Resolution      │  ← 与标准管线相同                  │
│  │  Application 层                │                                   │
│  └───────────────────────────────┘                                   │
└──────────────────────────────────────────────────────────────────────┘
```

**关键设计点**：`tex-bulk-load` 不替代标准管线，而是在管线**上游**增加一个分析阶段。单文件论文跳过 Phase 0 直接进入 Phase 1，大型项目由 Analyzer 先生成 Processing Manifest 再进入 Phase 1。

### 14.3 Processing Unit (PU)

#### 14.3.1 概念

Processing Unit（处理单元）是 tex-bulk-load 的核心抽象。PU 取代了标准管线中的 "chunk"，区别如下：

| 维度 | Chunk（标准管线） | PU（批量摄取） |
|------|-----------------|---------------|
| 划分依据 | Token 上限（35K tokens） | **论文语义层级**（Part/Section/Topic） |
| 内容 | 单文件的一段连续文本 | **1-N 个相关 `.tex` 文件的集合** |
| 上下文 | `created_node_registry`（全量 flat） | **分层 context injection**（§14.5） |
| 元数据 | `section_path`, `token_estimate` | **priority, depends_on, context_chain, context_hint, expected_node_types** |

#### 14.3.2 PU 分组规则

```
Rule 1 — 层级优先分组：
  每个 Part 文件 + 其直接 \input 子文件 = 1 个 PU
  如果 Part 包含的子文件总 tokens > 35K，按 \section 边界拆分为子 PU

Rule 2 — 附录按 topic prefix 分组：
  共享数字前缀的文件组成一个 PU：
    08_*.tex (K4 audits, 11 files) → PU "K4 Protocol Audits"
    30_*.tex (quantum measurement, 8 files) → PU "Quantum Measurement & Born Rule"
    36_*.tex (Yang-Mills, 6 files) → PU "Continuum Yang-Mills from Holonomy"

Rule 3 — 生成内容按子目录/命名模式分组：
    generated/sigma_summary_*.tex → PU "Sigma Summary Tables"
    generated/mass_spectrum_*.tex → PU "Mass Spectrum Closure Tables"
    generated/neutrino_*.tex → PU "Neutrino Audit Tables"

Rule 4 — Token 预算：
  PU > 35K tokens → 在 \section 边界拆分为子 PU
  PU < 2K tokens → 与同优先级相邻 PU 合并

Rule 5 — 原子单元不可分割：
  \begin{theorem}...\end{theorem}
  \begin{proof}...\end{proof}
  \begin{equation}...\end{equation}
  \begin{lemma}...\end{lemma}
```

#### 14.3.3 PU 优先级

| 优先级 | 分类 | 说明 | 处理顺序 |
|--------|------|------|---------|
| `critical` | 主体内容（Part I-VIII 正文） | 论文核心论点、定理、方法、结果 | 第一批 |
| `standard` | 附录（含原创定理/证明/推导） | 深度证明、扩展分析、独立模块 | 第二批 |
| `supplementary` | 生成内容（审计表、诊断表、闭合证据表） | 由脚本自动生成的结构化数据 | 第三批 |
| `skip` | 辅助文件（`.sty`, `.cls`, `.bst`, `.def`、脚本、原始数据、图表文件） | 不含知识主张 | 不处理 |

**注意**：`skip` 仅跳过不含知识主张的文件类型（格式定义、Python 脚本、原始 CSV 数据、图片文件）。所有 `.tex` 内容文件（包括生成的 `.tex` 片段）都会被摄取。

#### 14.3.4 PU Schema

```json
{
  "pu_id": "PU-07",
  "name": "Part V: Matter — Standard Model Interface",
  "priority": "critical",
  "depends_on": ["PU-00", "PU-03", "PU-05"],
  "context_chain": "Part V: Matter > §20: Standard Model Interface",
  "context_hint": "SM field labeling, gauge coupling derivation, Weinberg angle from protocol geometry, CKM/PMNS matrix closure",
  "file_list": [
    "sections/PE_matter.tex",
    "sections/I_20_standard_model_interface.tex",
    "sections/V_30_sm_field_labeling_closure.tex"
  ],
  "estimated_tokens": 32000,
  "expected_node_types": ["theorem", "definition", "result", "method"],
  "expected_node_count": "8-12",
  "labels_defined": ["sec:sm-interface", "thm:weinberg-angle", "eq:ckm-matrix"],
  "labels_referenced": ["sec:tick-calculus", "thm:resolution-folding", "eq:holonomy-connection"]
}
```

### 14.4 Processing Manifest

Analyzer 角色的输出是一个完整的 Processing Manifest，指导后续所有处理阶段：

```json
{
  "project_id": "<uuid>",
  "paper_title": "The Linear Universe: Geometric Enforcement of ...",
  "authors": "Haobo Ma (Auric)",
  "main_entry": "main.tex",
  "total_tex_files": 632,
  "bibliography_entries": 150,

  "processing_units": [
    {
      "pu_id": "PU-00",
      "name": "Front Matter & Paper Metadata",
      "priority": "critical",
      "depends_on": [],
      "context_chain": "Front Matter",
      "context_hint": "Paper title, authors, abstract, keywords, notation conventions",
      "file_list": ["sections/00_frontmatter.tex"],
      "estimated_tokens": 3500,
      "expected_node_types": ["source_paper"],
      "expected_node_count": 1
    },
    {
      "pu_id": "PU-04",
      "name": "Part II: Tick-first Ontology",
      "priority": "critical",
      "depends_on": ["PU-00", "PU-03"],
      "context_chain": "Part II: Tick-first",
      "context_hint": "HPA readout dynamics, cut-and-project bridge, golden angle phyllotaxis, tick calculus",
      "file_list": [
        "sections/PB_tick_first.tex",
        "sections/C_10_hpa_readout.tex",
        "sections/C_13_embedding_projection.tex",
        "sections/C_14_cut_and_project_bridge.tex",
        "sections/I_04_golden_angle_phyllotaxis.tex",
        "sections/I_05_tick_calculus.tex"
      ],
      "estimated_tokens": 28000,
      "expected_node_types": ["definition", "theorem", "method"],
      "expected_node_count": "5-8"
    }
  ],

  "skip_list": {
    "auxiliary": ["*.sty", "*.cls", "*.bst", "*.def"],
    "scripts": ["scripts/**"],
    "data": ["data/**"],
    "figures": ["figures/**"]
  },

  "bibliography_summary": {
    "storage_key": "papers/{project_id}/bibliography.json",
    "entry_count": 150,
    "top_cited_keys": ["vaswani2017...", "..."]
  },

  "estimates": {
    "total_pus": 86,
    "critical_pus": 11,
    "standard_pus": 30,
    "supplementary_pus": 45,
    "skipped_files": 507,
    "estimated_nodes": "250-350",
    "estimated_llm_calls": "170-260",
    "estimated_duration": "90-180 min"
  },

  "checkpoints": {
    "enabled": true,
    "storage_prefix": "papers/{project_id}/checkpoints/"
  }
}
```

### 14.5 大规模 Context 管理

标准管线通过 `created_node_registry`（全量 temp_id → UUID 映射）为 Extractor 提供上下文。在 86 个 PU 的场景下（产出 ~295 个节点），全量 registry 会占用 ~8,000 tokens，挤压 PU 内容空间。

tex-bulk-load 引入**分层 Context 注入**：

#### 14.5.1 Context 层级

```
┌──────────────────────────────────────────────────────────┐
│ Layer 1: Paper Overview                   (~200 tokens)  │
│   source_paper 节点的 abstract + 论文结构大纲              │
├──────────────────────────────────────────────────────────┤
│ Layer 2: Context Chain                     (~30 tokens)  │
│   "Part V: Matter > §20: SM Interface > §20.3: Gauge"   │
├──────────────────────────────────────────────────────────┤
│ Layer 3: Dependency PU Summaries         (~500 tokens)   │
│   当前 PU depends_on 的所有 PU 的 summary                 │
│   （非全量 registry，仅依赖链上的摘要）                     │
├──────────────────────────────────────────────────────────┤
│ Layer 4: Sibling PU Summaries            (~300 tokens)   │
│   同一 Part 下最近完成的 PU 的 summary                     │
├──────────────────────────────────────────────────────────┤
│ Layer 5: Compact Node Registry           (~500 tokens)   │
│   仅依赖链上的节点: [{id, type, title}]                    │
│   不含 abstract，不含非依赖节点                             │
├──────────────────────────────────────────────────────────┤
│ Layer 6: Current PU Content           (~25,000 tokens)   │
│   当前 PU 所有 .tex 文件的完整内容                          │
├──────────────────────────────────────────────────────────┤
│ Layer 7: knowledge-tex Skill           (~3,000 tokens)   │
│   节点模板和格式规范                                       │
├──────────────────────────────────────────────────────────┤
│ TOTAL                                 ~29,530 tokens     │
│ LLM 输出空间                            ~5,470 tokens    │
└──────────────────────────────────────────────────────────┘
```

#### 14.5.2 PU Summary

每个 PU 完成后，dag_writer 产出一段 **PU Summary** — 该 PU 所有创建节点的一段话摘要。此摘要被注入到下游 PU 的 Layer 3/4 中，替代全量 registry。

```json
{
  "pu_id": "PU-05",
  "summary": "Established resolution folding 64→21 (theorem, confidence 0.97), kernel-view computation guide (method), and adelic prime orbit module (theorem). Key result: the 21-dimensional folded representation preserves all physical observables via explicit projection operators.",
  "node_ids": ["node-a1b2", "node-c3d4", "node-e5f6"],
  "node_index": [
    {"id": "node-a1b2", "type": "theorem", "title": "Resolution folding 64→21"},
    {"id": "node-c3d4", "type": "method", "title": "Kernel-view computation guide"},
    {"id": "node-e5f6", "type": "theorem", "title": "Adelic prime orbit module"}
  ]
}
```

#### 14.5.3 与 §8 Context 管理策略的关系

§8 定义的 L0/L1/L2 投影层级用于 **Research Session 的 Researcher Agent** 读取已有图谱。本节定义的分层 Context 注入用于 **Ingestion 的 Extractor Agent** 处理大型项目。两套机制互补：

| 机制 | 用途 | 核心策略 |
|------|------|---------|
| §8 L0/L1/L2 投影 | Researcher 读取已有图谱 | abstract 作为精简投影 |
| §14.5 分层 Context | Extractor 处理大型项目 | 依赖链摘要 + compact registry |

### 14.6 增强后的 Workflow YAML

```yaml
name: paper_ingestion
description: >
  Parse TeX academic papers into standardized .tex knowledge nodes
  and write them to the knowledge graph.
  Supports both single-file papers and large multi-file TeX projects.

roles:
  # --- NEW: Analyzer（仅大型项目激活）---
  - id: analyzer
    name: Project Analyzer
    system_prompt: |
      You are a LaTeX project structure analyst. Your task is to analyze
      a TeX project and produce a Processing Manifest that guides the
      knowledge extraction pipeline.

      ## Tools

      - Chrono Storage tools to read uploaded project files
      - tex-bulk-load skill for analysis methodology

      ## Workflow

      1. Invoke tex-bulk-load skill to load analysis methodology
      2. Read the main .tex entry point
      3. Recursively trace \input{}/\include{} → build full inclusion tree
      4. Classify all files:
         - main_body: files included from Part/Chapter files
         - appendix: files under appendix structure
         - generated: files in generated/ directories
         - auxiliary: .sty, .cls, .bst, .def
         - data: .json, .csv, .dat files
         - script: .py, .sh files
         - figure: .pdf, .png, .eps, .jpg files
      5. Group files into Processing Units following the paper's hierarchy
         - Respect Part/Chapter/Section boundaries
         - Bundle related appendices by topic prefix
         - Bundle generated fragments by naming pattern
      6. Assign priorities (critical/standard/supplementary/skip)
      7. Compute dependency ordering via topological sort
      8. Generate context_chain and context_hint for each PU
      9. Parse bibliography → extract entry count
      10. Output the Processing Manifest JSON

      ## Output Format
      {
        "manifest": <ProcessingManifest JSON per §14.4>
      }
    connectors:
      - nyxid_mcp
    skills:
      - tex_bulk_load

  # --- Extractor（增强：支持 PU context）---
  - id: extractor
    name: Knowledge Extractor
    system_prompt: |
      You are a knowledge extraction specialist. Your task is to read
      chunks of academic TeX documents and extract structured knowledge
      claims as standardized .tex knowledge nodes.

      ## Graph IDs

      Two Graph IDs are provided in the "Original Context" block:
      - `Read Graph ID: <uuid>` — for reading existing graph state
      - `Write Graph ID: <uuid>` — passed to DAG Writer (not your concern)
      - `Paper Node ID: <uuid>` — the source_paper node for this paper

      ## Context (Bulk Mode)

      In bulk mode, your prompt includes layered context:
      - Paper Overview: high-level summary of the entire paper
      - Context Chain: where this PU sits in the paper hierarchy
      - Dependency Summaries: summaries of PUs this content builds upon
      - Sibling Summaries: summaries of recently processed PUs in the same Part
      - Compact Node Registry: [{id, type, title}] of relevant prior nodes
      Use these to correctly assign \noderef references to existing nodes.

      ## Tools

      You have the Knowledge Node Skill tool which defines the exact
      .tex template format. ALWAYS invoke it before generating nodes.

      You also have access to Chrono Storage tools via NyxId MCP to
      read chunks:
      - `chrono-storage-service__get_object` — read a chunk by storage key

      ## Workflow

      For each chunk/PU you receive:
      1. Read the content from Chrono Storage using the provided storage_key(s)
      2. Invoke the Knowledge Node Skill to load the .tex template specification
      3. Identify all knowledge claims (theorems, hypotheses,
         definitions, methods, results, observations)
      4. For each claim, generate a standardized .tex node following the template
      5. Use \noderef[relation]{id} to reference:
         - Other nodes created in this batch (use temp IDs: t0, t1, ...)
         - Previously created nodes from the Compact Node Registry
         - The source paper node: \sourceref{<paper-node-id>}
      6. Ensure every node has a ≤150 word abstract
      7. Output all generated .tex nodes as a JSON array

      ## Output Format
      {
        "nodes": [
          {
            "temp_id": "t0",
            "tex_content": "\\nodeid{...}\n\\nodetype{...}\n..."
          }
        ],
        "pu_summary": "<one paragraph summarizing all nodes created in this PU>",
        "saturation": false
      }

      ## Constraints
      - Each node ≤1200 words (excluding metadata lines)
      - Abstract ≤150 words, mandatory
      - 1-8 nodes per chunk (quality over quantity)
      - Do NOT create nodes for trivial content (acknowledgements, formatting)
      - Always include pu_summary in output (used for downstream context)
    connectors:
      - nyxid_mcp
    skills:
      - knowledge_tex

  # --- DAG Writer（不变）---
  - id: dag_writer
    name: DAG Writer
    system_prompt: |
      You are a knowledge graph writer. You receive .tex knowledge nodes
      from the Extractor and write them to Chrono Graph.

      ## Graph IDs

      Two Graph IDs are provided in the "Original Context" block:
      - `Read Graph ID: <uuid>` — not used by you
      - `Write Graph ID: <uuid>` — use this for ALL write operations

      ## Tools

      Via NyxId MCP:
      - `chrono-graph-service__post_api_graphs_by_graphid_nodes` — create nodes
      - `chrono-graph-service__get_api_graphs_by_graphid_nodes` — check existing

      ## Workflow

      For each batch of .tex nodes from the Extractor:
      1. For each node, create a graph node with properties:
         - type: extracted from \nodetype{}
         - title: extracted from first line of \section{Claim}
         - abstract: extracted from \begin{abstract}...\end{abstract}
         - tex_content: the full .tex source
         - confidence: extracted from \confidence{}
         - source: "ingestion"
         - source_paper_id: extracted from \sourceref{}
      2. Map temp IDs (t0, t1, ...) to created node UUIDs
      3. Report all created node IDs

      ## Output Format
      {
        "created_nodes": [
          {"temp_id": "t0", "node_id": "<uuid>", "title": "..."}
        ],
        "total_created": N
      }
    connectors:
      - nyxid_mcp

steps:
  # --- Phase 0: Project Analysis（仅大型项目）---
  - id: analyze
    type: llm_call
    role: analyzer
    condition: is_bulk_project == true
    parameters:
      prompt_prefix: |
        Analyze the TeX project uploaded at storage prefix:
        {{storage_prefix}}
        Main entry file: {{main_entry}}
        Total files: {{total_files}}
        Produce a Processing Manifest following the tex-bulk-load methodology.

  # --- Phase 0b: Manifest Fallback（单文件论文）---
  - id: simple_manifest
    type: assign
    condition: is_bulk_project == false
    parameters:
      manifest: build_simple_manifest(chunks)

  # --- Phase 0c: Source Paper Node ---
  - id: create_source_paper
    type: llm_call
    role: dag_writer
    parameters:
      prompt_prefix: |
        Create the source_paper node from the following metadata:
        Title: {{manifest.paper_title}}
        Authors: {{manifest.authors}}
        ...

  # --- Phase 1: PU Extraction Loop ---
  - id: init_loop
    type: assign
    parameters:
      pu_index: 0
      total_pus: len(manifest.processing_units)
      node_registry: "{}"
      pu_summaries: "{}"

  - id: pu_loop
    type: while
    condition: pu_index < total_pus
    steps:

      # 跳过 skip 优先级的 PU
      - id: check_skip
        type: assign
        condition: manifest.processing_units[pu_index].priority == "skip"
        parameters:
          pu_index: pu_index + 1
          __continue: true

      # 构建分层 context
      - id: build_context
        type: assign
        parameters:
          current_pu: manifest.processing_units[pu_index]
          pu_context: build_pu_context(
            manifest,
            pu_index,
            node_registry,
            pu_summaries,
            source_paper_abstract
          )

      # Extractor: 读取 PU → 提取知识节点
      - id: extract
        type: llm_call
        role: extractor
        parameters:
          prompt_prefix: |
            {{pu_context}}

            ---

            Process PU {{pu_index}}/{{total_pus}}: "{{current_pu.name}}"
            Priority: {{current_pu.priority}}
            Context Chain: {{current_pu.context_chain}}
            Context Hint: {{current_pu.context_hint}}
            Files: {{current_pu.file_list}}

            Extract knowledge claims as standardized .tex nodes.
            Include a pu_summary in your output.

      # DAG Writer: 写入节点
      - id: write_nodes
        type: llm_call
        role: dag_writer
        parameters:
          prompt_prefix: |
            Write the following .tex knowledge nodes to the graph using
            the Write Graph ID from the Original Context block:

      # 更新 registry + PU summary + checkpoint
      - id: update_state
        type: assign
        parameters:
          node_registry: merge(node_registry, write_nodes.output.created_nodes)
          pu_summaries: merge(pu_summaries, {
            current_pu.pu_id: {
              "summary": extract.output.pu_summary,
              "node_index": write_nodes.output.created_nodes
            }
          })
          pu_index: pu_index + 1

      # 保存 checkpoint
      - id: save_checkpoint
        type: connector_call
        connector: nyxid_mcp
        tool: chrono-storage-service__put_object
        parameters:
          key: "{{manifest.checkpoints.storage_prefix}}state.json"
          body: checkpoint_state(pu_index, node_registry, pu_summaries)

  # --- Phase 2: Edge Resolution ---
  - id: finalize
    type: assign
    parameters:
      status: "completed"
      total_nodes_created: len(node_registry)
```

### 14.7 `build_pu_context` 函数

Application 层的确定性函数，为每个 PU 构建分层 context：

```csharp
public sealed class PUContextAssembler
{
    /// 构建分层 context 注入内容
    public string BuildContext(
        ProcessingManifest manifest,
        int puIndex,
        Dictionary<string, string> nodeRegistry,
        Dictionary<string, PUSummary> puSummaries,
        string sourcePaperAbstract)
    {
        var pu = manifest.ProcessingUnits[puIndex];
        var sb = new StringBuilder();

        // Layer 1: Paper Overview (~200 tokens)
        sb.AppendLine("## Paper Overview");
        sb.AppendLine(sourcePaperAbstract);
        sb.AppendLine();

        // Layer 2: Context Chain (~30 tokens)
        sb.AppendLine($"## Current Position: {pu.ContextChain}");
        sb.AppendLine();

        // Layer 3: Dependency PU Summaries (~500 tokens)
        sb.AppendLine("## Dependency Context");
        foreach (var depId in pu.DependsOn)
        {
            if (puSummaries.TryGetValue(depId, out var depSummary))
                sb.AppendLine($"- [{depId}] {depSummary.Summary}");
        }
        sb.AppendLine();

        // Layer 4: Sibling PU Summaries (~300 tokens)
        sb.AppendLine("## Recent Sibling Context");
        var siblings = GetRecentSiblings(manifest, puIndex, puSummaries, maxCount: 3);
        foreach (var sib in siblings)
            sb.AppendLine($"- [{sib.PuId}] {sib.Summary}");
        sb.AppendLine();

        // Layer 5: Compact Node Registry (~500 tokens)
        sb.AppendLine("## Available Nodes for Reference");
        var relevantNodes = GetDependencyChainNodes(pu.DependsOn, puSummaries);
        foreach (var node in relevantNodes)
            sb.AppendLine($"- {node.Id} ({node.Type}): {node.Title}");

        return sb.ToString();
    }
}
```

### 14.8 检查点与恢复

#### 14.8.1 Checkpoint 数据结构

每个 PU 完成后保存 checkpoint 到 Chrono Storage：

```json
{
  "project_id": "proj-xxx",
  "manifest_version": "1.0",
  "last_completed_pu": "PU-12",
  "pu_status": {
    "PU-00": {"status": "completed", "nodes_created": 1, "summary": "..."},
    "PU-01": {"status": "completed", "nodes_created": 3, "summary": "..."},
    "PU-12": {"status": "completed", "nodes_created": 6, "summary": "..."},
    "PU-13": {"status": "pending"}
  },
  "node_registry": {
    "t0@PU-00": "node-real-uuid-1",
    "t0@PU-01": "node-real-uuid-2"
  },
  "pu_summaries": {
    "PU-00": {"summary": "...", "node_index": [...]},
    "PU-01": {"summary": "...", "node_index": [...]}
  },
  "total_nodes_created": 52,
  "last_checkpoint_at": "2026-03-04T10:30:00Z"
}
```

#### 14.8.2 恢复流程

```
恢复摄取任务:
  1. 加载 checkpoint → 跳过所有 status=completed 的 PU
  2. 从 checkpoint 恢复 node_registry 和 pu_summaries
  3. 定位 last_completed_pu + 1 → 设为 pu_index
  4. 继续 PU 循环
```

#### 14.8.3 API 支持

```
POST /api/v2/papers/ingest/{ingestion_id}/resume

Response: 200 OK
{
  "ingestion_id": "<uuid>",
  "resumed_from_pu": "PU-13",
  "already_completed": 12,
  "remaining": 74,
  "status": "processing"
}
```

### 14.9 Application Layer 新增服务

| 服务 | 职责 | 层 |
|------|------|---|
| `TexInclusionTreeBuilder` | 递归追踪 `\input`/`\include`，构建完整包含树 | Application |
| `FileClassificationService` | 按规则分类文件为 main_body/appendix/generated/auxiliary/data/script/figure | Application |
| `ProcessingUnitBuilder` | 按层级和 topic prefix 分组文件为 PU，含 token 估算和依赖拓扑排序 | Application |
| `PUContextAssembler` | 构建分层 context injection（§14.7） | Application |
| `CheckpointService` | PU 级别检查点保存与恢复 | Application |
| `PUSummaryGenerator` | PU 完成后生成摘要，存入 pu_summaries | Application |

### 14.10 tex-bulk-load Skill 文件

```
apps/sisyphus/skills/tex-bulk-load/SKILL.md
```

Skill 内容包含：

1. §14.3 的完整 PU 分组规则
2. §14.4 的 Processing Manifest schema
3. 文件分类规则和 pattern 匹配示例
4. 依赖拓扑排序算法说明
5. context_chain 和 context_hint 生成指引
6. 针对不同项目结构（专著、博士论文、多百页论文）的具体示例

### 14.11 示例：the-omega 论文项目

以 `2025_z128_standard_model_stable_sector_hpa_omega` 为例展示完整处理流程。

#### 14.11.1 项目规模

| 指标 | 值 |
|------|-----|
| 总文件数 | 1,169 |
| `.tex` 文件 | 632 |
| 生成 `.tex` 片段 | 421 |
| 附录 `.tex` 文件 | 160 |
| 数据文件 | 94 |
| 图表文件 | 146 |
| Python 脚本 | 286 |
| 参考文献 (`references.bib`) | 2,256 行 |
| 项目总大小 | 137 MB |
| 论文结构 | 8 Parts + 附录，通过 `\input{}` 嵌套 3-4 级 |

#### 14.11.2 Analyzer 输出（Processing Manifest 摘要）

```
Phase 0 产出:
  总 PU 数: ~86
    critical (主体 8 Parts):     11 PUs
    standard (附录各 topic):     30 PUs
    supplementary (生成内容):     45 PUs
    skip (脚本/数据/图表/辅助):  507 files skipped

  PU 示例:
    PU-00: Front Matter (00_frontmatter.tex)
           → source_paper node
    PU-01: Unified Spine (U_00_unified_spine.tex)
           → 2-3 nodes (paper overview, reading paths)
    PU-03: Part I Contract (PA_contract.tex + A.0-A.4)
           → 3-5 nodes (protocol contract, wish, motive, channels)
    PU-04: Part II Tick-first (PB + C_10-C_14, I_04-I_05)
           → 5-8 nodes (HPA readout, tick calculus, phyllotaxis)
    PU-07: Part V Matter (PE + I_20, V_30-V_33)
           depends_on: [PU-00, PU-03, PU-05]
           → 8-12 nodes (SM interface, mass spectrum, CKM/PMNS)
    PU-15: Appendix: K4 Protocol Audits (08_*.tex, 11 files)
           → 5-8 nodes (K4 delay, PDG leakage, falsification)
    PU-25: Appendix: Quantum Measurement (30_*.tex, 8 files)
           → 4-6 nodes (Born rule, decoherence, measurement)
    PU-60: Generated: Mass Spectrum Tables (generated/mass_spectrum_*.tex)
           → 2-4 nodes (closure evidence, PDG comparison)
```

#### 14.11.3 处理时间估算

| 阶段 | PU 数量 | 预估节点数 | 预估时间 |
|------|---------|-----------|---------|
| Phase 0: Analyzer | 1 次调用 | — | ~2 min |
| Phase 1a: Critical PUs | 11 | ~45 | ~30 min |
| Phase 1b: Standard PUs | 30 | ~100 | ~60 min |
| Phase 1c: Supplementary PUs | 45 | ~150 | ~45 min |
| Phase 2: Edge Resolution | — | ~600 edges | ~5 min |
| **Total** | **86 PUs** | **~295 nodes, ~600 edges** | **~142 min** |

#### 14.11.4 最终知识图谱

```
Source Paper Node: 1
  └── Knowledge Nodes: ~295
        ├── theorem: ~60 (resolution folding, holonomy, gauge connections, ...)
        ├── definition: ~45 (tick, CAP, HPA, protocol primitives, ...)
        ├── method: ~35 (kernel view, audit procedures, phyllotaxis, ...)
        ├── result: ~50 (mass spectrum, coupling unification, BLEU scores, ...)
        ├── fact: ~40 (SM field labeling, PDG values, ...)
        ├── observation: ~30 (limitations, audit findings, ...)
        ├── hypothesis: ~20 (open conjectures, candidates, ...)
        └── inference: ~15 (cross-module connections, ...)

  Edges: ~600
        ├── depends_on: ~180
        ├── derived_from: ~120
        ├── supports: ~100
        ├── proves: ~60
        ├── extends: ~50
        ├── evaluates: ~40
        ├── formalizes: ~30
        └── cites: ~20
```
