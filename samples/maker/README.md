# MAKER Workflow Sample

本示例实现了论文 [Solving a Million-Step LLM Task with Zero Errors](https://arxiv.org/html/2511.09030v1) 中描述的 MAKER 模式，用于论文/文本分析场景。

## 论文核心思想

MAKER（Maximal Agentic decomposition, first-to-ahead-by-K Error correction, and Red-flagging）包含三个核心组件：

1. **MAD（Maximal Agentic Decomposition）**：把大任务拆分成尽可能小的子任务，每个子任务由一个专注的 microagent 处理
2. **First-to-ahead-by-k Voting**：对每个子任务，让多个 agent 独立求解，通过投票选出正确答案——得票最高的候选领先第二名 >= k 票即获胜
3. **Red-Flagging**：丢弃"有问题"的响应（过长、格式错误等），因为这些往往意味着 LLM 推理过程出了偏

论文证明：通过极端分解 + 投票纠错，即使用小模型也能在百万步任务中实现零错误。

## 本 Sample 的映射

```
论文概念                    → 本 Sample 的实现
────────────────────────────────────────────────────
Atomicity decision         → recursive stage: atomic vote (ATOMIC/DECOMPOSE)
MAD recursive decompose    → recursive stage: coordinator decomposition + child recursion
First-to-ahead-by-k vote   → recursive stages: atomic/decompose/solve/compose 都走 maker_vote
Recursive composition      → recursive stage: compose vote across child solutions
Framework connector call   → final step: connector_call（可选后处理/外部调用）
```

## 执行流程

```
用户提交论文文本
  │
  ▼
WorkflowGAgent (maker-root)
  │
  ├─ Step 1: solve_root (maker_recursive)
  │    ├─ atomic vote: 判断是 ATOMIC 还是 DECOMPOSE
  │    ├─ if ATOMIC:
  │    │    └─ solve vote: 3 workers 独立求解后投票
  │    └─ if DECOMPOSE:
  │         ├─ decompose vote: 多路分解候选投票
  │         ├─ child recursion: 对每个子任务递归执行 maker_recursive
  │         └─ compose vote: 汇总子结果并投票
  │
  └─ Step 2: connector_post (connector_call)
       通过框架内置 connector 执行可选后处理（MCP/HTTP/CLI）
```

## 目录结构

```
samples/maker/
├── Aevatar.Samples.Maker.csproj  # 可运行 Console 项目
├── Program.cs                    # 入口：装配运行时、加载 YAML、执行 workflow
├── workflows/
│   └── maker_analysis.yaml       # MAKER 工作流定义
├── connectors/
│   └── maker.connectors.json     # 示例命名 connector 配置
├── roles/
│   ├── coordinator.yaml          # 协调者角色配置
│   └── worker.yaml               # 分析 Worker 角色配置
└── README.md                     # 本文件
```

## 运行方式

```bash
# 在仓库根目录
cd samples/maker
dotnet run
```

每次执行都会自动在仓库根目录生成本次运行报告：

- `artifacts/maker/maker-run-<timestamp>.json`
- `artifacts/maker/maker-run-<timestamp>.html`

JSON 包含完整时间线与结构化步骤数据；HTML 用于可视化查看执行细节（包含 `atomic / decompose / recursion / vote / red_flag` 等阶段）。

`maker_analysis.yaml` 包含两个关键步骤：

- `solve_root`：`maker_recursive`，实现原子性判断 + 递归分解 + 递归组合。
- `connector_post`：`connector_call`，可选后处理。

- 默认策略：`on_missing=skip`，没有配置 connector 时不影响主流程输出。
- 配置后可调用框架级 connector（MCP/HTTP/CLI），并把 `connector.name/type/status_code/exit_code/duration_ms` 写入元数据与报告。

如需接入真实 LLM，取消 `Program.cs` 中 LLM Provider 配置的注释并设置 API Key：

```bash
export OPENAI_API_KEY="sk-..."
dotnet run
```

## 框架层新增

本 sample 依赖框架原语 + sample 专属原语：

### ConnectorCallModule (`connector_call`)

框架内置 connector 调用模块，用统一契约触发外部能力：

- 输入：`StepRequestEvent.Input`
- 路由：`parameters.connector` 选择命名 connector
- 容错：支持 `on_missing=skip` / `on_error=continue`
- 观测：统一输出到 `StepCompletedEvent.Metadata`

### MakerVoteModule (`maker_vote`, sample-scoped)

实现论文 Algorithm 2 的投票逻辑：

- 从 `\n---\n` 分隔的候选中提取答案
- **Red-Flagging**：丢弃超过 `max_response_length` 阈值的响应
- **分组计票**：按内容精确匹配分组
- **First-to-ahead-by-k**：得票最高者领先 >= k 票即获胜
- 候选不足时降级为 majority vote

参数：
- `k`：领先票数阈值（默认 2）
- `max_response_length`：红旗阈值（默认 2000 字符）

### ForEachModule (`foreach`)

通用迭代模块，遍历列表中的每项并执行子流程：

- 按 `delimiter` 分隔输入（默认 `\n---\n`）
- 对每项发布类型为 `sub_step_type` 的子步骤（默认 `parallel`）
- 收齐所有子步骤结果后合并输出

参数：
- `delimiter`：分隔符（默认 `\n---\n`）
- `sub_step_type`：子步骤类型（默认 `parallel`）
- `sub_target_role`：子步骤目标角色
- `sub_param_*`：传递给子步骤的参数（去掉 `sub_param_` 前缀）

> 当前递归流程已改为 `maker_recursive`，`foreach` 仍是通用框架能力，不再是 MAKER 主流程必需路径。

### MakerRecursiveModule (`maker_recursive`, sample-scoped)

实现递归 MAKER 主流程：

- 原子性投票：判断当前任务是否可直接求解
- 非原子时递归分解：生成子任务并递归调用自身
- 原子/组合阶段都做 first-to-ahead-by-k 投票
- 每个节点输出 `maker.*` 元数据（`depth/stage/atomic_decision/...`）

## 与论文的关键对应

| 论文 Section | 概念 | 本 Sample |
|---|---|---|
| 3.1 | Maximal Agentic Decomposition | `maker_recursive` 递归分解 |
| 3.2 | First-to-ahead-by-k Voting | `maker_vote`（原子/分解/组合阶段） |
| 3.3 | Red-Flagging | `max_response_length` 长度过滤 |
| Algorithm 2 | do_voting | `MakerVoteModule.HandleAsync` |
| Algorithm 3 | get_vote | `ParallelFanOutModule` + `maker_vote` |
