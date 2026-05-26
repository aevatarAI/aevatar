---
title: "Studio 产品重构 — 团队构建器"
status: DRAFT
owner: CEO
---

# Studio 产品重构 — 团队构建器

Generated on 2026-04-08
Repo: aevatarAI/aevatar
Related: [Console Web AI Teams 设计](2026-04-08-console-web-ai-teams.md)

## 定位

Studio 是**团队构建器**。它针对 Scope（= Team）工作。用户在 Studio 里构建和打造整个团队：定义成员（workflow/scripting）、分配角色、配置集成、测试运行。

```
Scope = Team
Studio = Scope 的编辑器
∴ Studio = 团队构建器
```

**在 Studio 中，用户可以**：
- 创建/编辑团队成员的行为定义（workflow 可视化编排）
- 编写脚本行为（C# scripting）
- 定义 Agent 角色（LLM prompt/model 配置）
- 配置集成（Telegram/HTTP/MCP 连接器）
- 测试运行整个团队的行为

**入口**：
- 团队详情页 → "高级编辑" Tab（进入当前 Scope 的 Studio）
- 团队成员表 → 单个成员的"编辑"链接（进入 Studio 并聚焦该成员的 workflow/script）
- "组建团队" → 进入空的 Studio 开始创建

## 当前状态

Studio 当前功能完整但缺乏产品语境：

| 当前名称 | 功能 | 问题 |
|---------|------|------|
| Workflows | 工作流列表 | 没有"属于哪个团队"的上下文 |
| Studio (Editor) | XYFlow 可视化工作流编辑 | 独立页面，跟团队脱节 |
| Scripts | Monaco C# 脚本编辑器 | 同上 |
| Roles | LLM 角色定义（prompt/model） | 叫"Roles"用户不理解 |
| Connectors | 外部连接器（HTTP/CLI/MCP） | 同上 |
| Settings | 运行时和 Provider 配置 | 同上 |
| Executions | 执行追踪和回放 | 没有跟团队关联 |

**核心问题**：Studio 作为独立页面存在，没有团队上下文。用户不知道自己在编辑"谁"。

## 重构方案

### 原则

1. **带上下文进入**：从团队/成员进入 Studio 时，自动加载该成员的 workflow/script
2. **术语重映射**：用团队语言重新包装，但不改底层功能
3. **保留全部能力**：XYFlow 编辑器、Monaco 编辑器、角色管理、连接器管理、执行测试全部保留
4. **不改底层代码**：只改导航入口和 UI 标签

### 术语重映射

| 当前 | 改为 | 原因 |
|------|------|------|
| Studio | 成员编辑器 / Agent Editor | 明确在编辑什么 |
| Workflows | 行为定义 | 工作流就是 agent 的行为逻辑 |
| Steps | 处理步骤 | 保留 |
| Roles | Agent 角色 | 每个 Role 定义一个 LLM 人格 |
| Connectors | 集成 / Integrations | 外部服务连接 |
| Executions | 测试运行 | 在编辑器内测试 agent 行为 |
| Settings | 编辑器设置 | 保留 |
| Scripts | 脚本行为 | C# 脚本定义 agent 行为 |

### 导航结构（Studio 内部）

从团队详情进入 Studio 后，Studio 内部保留独立的 Tab 导航：

```
┌─────────────────────────────────────────────────────────────┐
│  ← 返回团队: 客服团队                    [测试运行] [保存]  │
├─────────────────────────────────────────────────────────────┤
│  行为定义 │ 脚本行为 │ Agent 角色 │ 集成 │ 设置            │
├─────────────┬───────────────────────────────────────────────┤
│  定义列表    │  XYFlow 画布                │  属性面板      │
│  ─────────  │  ┌─────┐    ┌─────┐         │  ────────      │
│  接待流程    │  │step1│───→│step2│         │  步骤类型:     │
│  处理流程 ●  │  └─────┘    └──┬──┘         │  llm_call      │
│  跟进流程    │               │             │  目标角色:     │
│             │           ┌───▼──┐          │  处理专员      │
│             │           │step3 │          │  参数:         │
│             │           └──────┘          │  {...}         │
│             │                             │                │
│  [+ 新建]   │  [自动布局] [缩放] [适应]   │  [删除步骤]    │
└─────────────┴─────────────────────────────┴────────────────┘
```

### 上下文传递

从团队详情进入 Studio 时传递：
- `scopeId` — 当前团队（Scope）
- `memberId` — 当前成员（GAgent Actor ID，可选）
- `workflowId` — 该成员绑定的 workflow（如果是 workflow 类型）
- `scriptId` — 该成员绑定的 script（如果是 scripting 类型）

Studio 顶部显示上下文面包屑：
```
← 客服团队 / 处理专员 / 行为定义
```

### 页面详细设计

#### 1. 行为定义（Workflow Editor）

**保留当前所有功能**：
- XYFlow 画布：步骤节点 + 连接边
- 三栏布局：定义列表 | 画布 | 属性面板
- 属性面板三 Tab：步骤属性 | Agent 角色 | YAML

**改进**：
- 画布节点显示成员上下文（"此步骤由 处理专员 执行"）
- 步骤类型分类保留现有颜色编码：
  - 蓝色: 数据（transform, assign）
  - 紫色: 控制（guard, conditional, switch）
  - 粉色: AI（llm_call, tool_call）
  - 橙色: 组合（foreach, parallel）
  - 绿色: 集成（connector_call, emit）
  - 青色: 人工（human_input, human_approval）
- 执行状态覆盖：idle/active/waiting/completed/failed

#### 2. 脚本行为（Scripts Editor）

**保留当前所有功能**：
- Monaco C# 编辑器 + Proto 文件
- 多文件支持（文件树）
- 实时诊断
- AI 辅助生成（流式）
- Draft → Validate → Run → Promote 工作流

**改进**：
- 顶部显示"正在编辑: 跟进员 的脚本行为"
- 测试运行结果关联到成员的事件流

#### 3. Agent 角色（Roles）

当前的 Role 定义就是 LLM 的人格配置：
- Role ID + Name
- System Prompt（核心：定义 agent 的行为指令）
- Provider + Model（选择 LLM）
- Connectors（可用的工具）

**改进**：
- 重命名为"Agent 角色"
- 每个角色卡片显示关联的团队成员
- 从团队成员表点击"编辑角色"直接跳到这里

#### 4. 集成（Connectors）

三种连接器类型：
- **HTTP**：RESTful API 调用
- **CLI**：命令行工具
- **MCP**：Model Context Protocol 服务

**改进**：
- 重命名为"集成"
- 显示哪些团队成员使用了这个集成
- Telegram connector 应该在这里可见（团队的外部连接）

#### 5. 测试运行（Executions）

**保留当前所有功能**：
- Draft Run 执行（SSE 流式）
- XYFlow 画布执行状态装饰（节点高亮、边高亮）
- 执行日志时间线
- Human Input/Approval 交互

**改进**：
- 重命名为"测试运行"
- 测试结果与团队事件流关联
- "在团队中查看"按钮跳回团队详情的事件流 Tab

## 与团队详情的关系

```
团队详情 (/teams/:scopeId)
├── 概览 Tab         — SaaS 风格
├── 事件拓扑 Tab      — Platform 风格，只读查看 EventEnvelope 流转
├── 事件流 Tab        — Platform 风格，只读查看事件日志
├── 成员 Tab          — 表格，每个成员有"编辑"链接
│   └── 编辑 → 进入 Studio（带 scopeId + workflowId/scriptId 上下文）
├── 连接器 Tab        — 查看连接器绑定
└── 高级编辑 Tab      — 直接进入 Studio（带 scopeId 上下文）
```

**事件拓扑 vs Studio 画布**：
- 事件拓扑（团队详情）= 运行时视角，展示 EventEnvelope 在 agent 之间的实际流转
- Studio 画布 = 定义时视角，展示 workflow 内部的步骤编排逻辑
- 同样用 XYFlow，但数据源和语义不同

## 数据源

| 功能 | 现有 API | 文件 |
|------|---------|------|
| Workflow CRUD | `studioApi.*` | `src/shared/studio/api.ts` |
| Graph 构建 | `buildStudioGraphElements()` | `src/shared/studio/graph.ts` |
| 文档操作 | `insertStep*`, `connectStep*` 等 | `src/shared/studio/document.ts` |
| 执行流式 | `studioApi.startExecution()` (SSE) | `src/shared/studio/api.ts` |
| 脚本编辑 | `scriptsApi.*` | `src/shared/studio/scriptsApi.ts` |
| 角色 catalog | `studioApi.getRolesCatalog()` | `src/shared/studio/api.ts` |
| 连接器 catalog | `studioApi.getConnectorsCatalog()` | `src/shared/studio/api.ts` |

**全部已有。** Studio 重构不需要新 API。

## 实施范围

**做**：
1. 术语替换（Workflows → 行为定义, Roles → Agent 角色, Connectors → 集成, Executions → 测试运行）
2. 从一级导航移除，改为从团队详情进入
3. 顶部面包屑显示团队 + 成员上下文
4. 进入时自动加载对应 scopeId 的 workflow/script

**不做**：
- 不改 XYFlow 画布逻辑
- 不改 Monaco 编辑器
- 不改 API 层
- 不改步骤类型系统
- 不改执行/测试流程

## 预览

预览页面：[`2026-04-08-studio-redesign-preview.html`](2026-04-08-studio-redesign-preview.html)
