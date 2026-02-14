# Aevatar 项目组织重构总计划（项目名 / Namespace / 项目依赖）

## 1. 目标

本计划只聚焦“项目组织”三件事，并优先完成：

1. 项目名规范化（语义清晰、集合名复数化、分层可识别）。
2. Namespace 规范化（与项目边界一致，避免跨项目混用根命名空间）。
3. 项目依赖规范化（严格 DAG，依赖抽象而不是实现）。

约束：

1. 先完成组织重构，再进入功能级重构。
2. 组织重构阶段不引入业务行为变化（只改结构与依赖）。
3. 每一阶段都必须保持可编译、可测试、可回滚。

---

## 2. 设计原则（最佳实践基线）

1. `AssemblyName` = `RootNamespace` = 项目名（测试项目除外但建议同构）。
2. 一项目一职责（契约、核心、适配器、宿主分离）。
3. 依赖方向单向：外层 -> 内层，不允许反向引用。
4. Provider/Adapter 属于“集合”，项目段使用复数：
`LLMProviders`、`ToolProviders`、`ConnectorProviders`。
5. 宿主项目（Api/Gateway/Sample）只做组合，不承载框架核心逻辑。
6. 通过自动化规则守护组织约束（CI 失败即阻断合并）。

---

## 3. 目标组织结构（To-Be）

```text
src/
├── Aevatar.Foundation.Abstractions
├── Aevatar.Foundation.Core
├── Aevatar.Foundation.Runtime
├── Aevatar.AI.Abstractions
├── Aevatar.AI.Core
├── Aevatar.AI.LLMProviders.MEAI
├── Aevatar.AI.LLMProviders.Tornado
├── Aevatar.AI.ToolProviders.MCP
├── Aevatar.AI.ToolProviders.Skills
├── Aevatar.Workflows.Core
├── Aevatar.Configuration
├── Aevatar.Presentation.AGUI
├── Aevatar.Bootstrap
├── Aevatar.Hosts.Api
└── Aevatar.Hosts.Gateway
```

说明：

1. `Aevatar.Bootstrap` 作为组合根，集中管理可选 Provider 注册。
2. `Hosts.*` 通过 Bootstrap 组装依赖，避免直接耦合多个 Provider 工程。
3. `AI.Abstractions` 提供 LLM/Tool 契约，Provider 只依赖它。

---

## 4. 项目映射（As-Is -> To-Be）

1. `Aevatar.Abstractions` -> `Aevatar.Foundation.Abstractions`
2. `Aevatar.Core` -> `Aevatar.Foundation.Core`
3. `Aevatar.Runtime` -> `Aevatar.Foundation.Runtime`
4. `Aevatar.AI` -> `Aevatar.AI.Core`
5. `Aevatar.AI.MEAI` -> `Aevatar.AI.LLMProviders.MEAI`
6. `Aevatar.AI.LLMTornado` -> `Aevatar.AI.LLMProviders.Tornado`
7. `Aevatar.AI.MCP` -> `Aevatar.AI.ToolProviders.MCP`
8. `Aevatar.AI.Skills` -> `Aevatar.AI.ToolProviders.Skills`
9. `Aevatar.Cognitive` -> `Aevatar.Workflows.Core`
10. `Aevatar.Config` -> `Aevatar.Configuration`
11. `Aevatar.AGUI` -> `Aevatar.Presentation.AGUI`
12. `Aevatar.Api` -> `Aevatar.Hosts.Api`
13. `Aevatar.Gateway` -> `Aevatar.Hosts.Gateway`
14. 新增：`Aevatar.AI.Abstractions`
15. 新增：`Aevatar.Bootstrap`

---

## 5. 目标依赖规则（必须满足）

## 5.1 允许依赖矩阵

1. `Foundation.Abstractions` -> 无
2. `Foundation.Core` -> `Foundation.Abstractions`
3. `Foundation.Runtime` -> `Foundation.Core`, `Foundation.Abstractions`
4. `AI.Abstractions` -> `Foundation.Abstractions`
5. `AI.Core` -> `AI.Abstractions`, `Foundation.Core`, `Foundation.Abstractions`
6. `Workflows.Core` -> `AI.Abstractions`, `Foundation.Core`, `Foundation.Abstractions`
7. `AI.LLMProviders.*` -> `AI.Abstractions`
8. `AI.ToolProviders.*` -> `AI.Abstractions`, `Foundation.Abstractions`
9. `Configuration` -> 无（或仅 `Foundation.Abstractions`）
10. `Presentation.AGUI` -> `Foundation.Abstractions`, `AI.Abstractions`
11. `Bootstrap` -> `AI.LLMProviders.*`, `AI.ToolProviders.*`, `AI.Core`, `Workflows.Core`, `Foundation.Runtime`, `Configuration`
12. `Hosts.*` -> `Bootstrap`, `Workflows.Core`, `Foundation.Runtime`, `Configuration`, `Presentation.AGUI`

## 5.2 明确禁止

1. `Workflows.Core` 直接依赖 `AI.LLMProviders.*` 或 `AI.ToolProviders.*`
2. `AI.Core` 直接依赖具体 Provider 工程
3. `Hosts.*` 直接依赖 `AI.LLMProviders.*`（通过 `Bootstrap` 间接引用）
4. 任意项目引用“同层实现项目”而非抽象

---

## 6. Namespace 规范

1. 根 namespace 固定为项目名（例如 `Aevatar.AI.LLMProviders.Tornado`）。
2. 子 namespace 与文件夹层级一致（例如 `...Tornado.Factories`）。
3. 不再使用跨项目共享根 namespace（例如多个项目都用 `Aevatar`）。
4. 复数规则：
`LLMProviders`、`ToolProviders`、`ConnectorProviders`、`Actors`、`Modules`。
5. Proto 文件 `option csharp_namespace` 必须迁移到新根 namespace。

---

## 7. 分阶段实施计划（组织优先）

## Phase 0：基线与保护

目标：
冻结当前行为，保证后续可回归。

任务：
1. 生成依赖基线图（当前 csproj 引用快照）。
2. 全量执行测试并保存结果快照。
3. 建立“组织重构分支”并锁定只做结构改动。

完成判据：
1. 基线文档可复现。
2. 测试基线可复现。

## Phase 1：抽象先拆（先解依赖，再改名字）

目标：
把核心契约从实现项目中剥离，先打通依赖反转。

任务：
1. 新建 `Aevatar.AI.Abstractions`。
2. 迁移 `ILLMProvider`、`ILLMProviderFactory`、LLM 请求/响应契约、Tool 契约（按依赖最小化拆分）。
3. `AI.LLMProviders.*`、`AI.ToolProviders.*`、`Workflows.Core` 改为仅依赖 `AI.Abstractions`。

完成判据：
1. Provider 工程不再引用 `Aevatar.AI` 实现工程。
2. `Workflows.Core` 不再因 LLM/Tool 实现产生编译期耦合。

## Phase 2：项目重命名与目录重排

目标：
统一项目名、AssemblyName、RootNamespace。

任务：
1. 按第 4 节映射重命名目录与 `.csproj`。
2. 更新 `aevatar.slnx`、`*.slnf`、README/docs 中路径引用。
3. 所有项目补全或统一 `<RootNamespace>` 与 `<AssemblyName>`。

完成判据：
1. 项目命名全量符合目标结构。
2. 解决方案可正常加载、编译。

## Phase 3：Namespace 全量迁移

目标：
代码 namespace 与新项目边界一致。

任务：
1. 批量替换 `namespace` 与 `using`。
2. 迁移 proto 的 `csharp_namespace`。
3. 测试项目 namespace 同步改名（例如 `Aevatar.Foundation.Core.Tests`）。

完成判据：
1. 无旧 namespace 引用残留。
2. IDE/编译器无命名冲突警告。

## Phase 4：项目依赖重连（DAG 固化）

目标：
形成稳定的目标依赖图，清理历史耦合。

任务：
1. 重写所有 `.csproj` 的 `ProjectReference`。
2. 引入 `Aevatar.Bootstrap`，宿主移除对具体 Provider 的直接引用。
3. 去掉重复 DI 注册与“宿主差异化装配”。

完成判据：
1. 依赖图满足第 5 节规则。
2. 无逆向依赖。

## Phase 5：兼容层与平滑迁移

目标：
对外 API 迁移不一次性破坏。

任务：
1. 为旧命名空间提供过渡层（`[Obsolete]` + forwarding wrappers）。
2. 设定兼容窗口（1-2 个迭代）后删除兼容层。

完成判据：
1. 内部调用完成新命名切换。
2. 兼容层仅保留必要入口，且有删除计划日期。

## Phase 6：守护与收尾

目标：
防止组织结构回退。

任务：
1. 增加架构测试（依赖方向断言）。
2. 增加命名规则检查（项目名、namespace、RootNamespace 对齐）。
3. 在 CI 中把上述检查设为必过门禁。

完成判据：
1. CI 可自动拦截违规依赖或命名。
2. 文档、脚本、模板全部切换到新结构。

---

## 8. 验收标准（Definition of Done）

1. 项目名、目录名、AssemblyName、RootNamespace 全量一致。
2. namespace 不再跨项目共享根 `Aevatar`（除明确约定的抽象层外）。
3. Provider 项目名与 namespace 使用复数集合段（`LLMProviders`/`ToolProviders`）。
4. 项目依赖满足第 5 节 DAG，且有自动化校验。
5. `dotnet build` 与 `dotnet test` 全绿。
6. 旧命名兼容层有明确移除版本与时间。

---

## 9. 风险与回滚

风险：
1. 大规模重命名引发引用雪崩。
2. namespace 迁移造成序列化/反射路径变更。
3. 宿主装配切换导致运行时依赖缺失。

回滚策略：
1. 每个 Phase 独立提交，失败可按阶段回退。
2. 先保留兼容层再切换调用，避免一步到位。
3. 每阶段完成后立即执行 build/test 与最小集成验证。

---

## 10. 执行顺序建议（先组织，再功能）

1. 先做 Phase 1 + Phase 2 + Phase 3 + Phase 4（组织完成里程碑）。
2. 组织完成并稳定后，再进入功能重构（性能、安全、模块行为优化）。

里程碑定义：
`M1（组织完成）` = 项目名 + namespace + 项目依赖 全量达标，并通过测试基线。
