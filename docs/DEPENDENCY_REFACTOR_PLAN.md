# Aevatar 依赖与命名空间重构计划（DIP / 接口依赖 / 复数命名）

## 1. 目标与标准

本计划用于把当前仓库重构为标准化的解决方案拆分方式，满足以下硬性规则：

1. 依赖反转（DIP）：高层模块不依赖低层实现，只依赖抽象。
2. 依赖接口而非实现：业务与编排层只引用 `interface`/契约，不直接 `new` 具体实现。
3. 集合类命名使用复数：项目名与命名空间中的“集合语义段”统一使用复数。
4. 命名空间应体现项目边界：避免跨层共享同一根命名空间造成边界模糊。

---

## 2. 当前依赖现状（src）

当前项目引用关系（按 `.csproj`）：

```text
Aevatar.Abstractions -> <none>
Aevatar.Core -> Aevatar.Abstractions
Aevatar.Runtime -> Aevatar.Core
Aevatar.AI -> Aevatar.Core
Aevatar.Cognitive -> Aevatar.AI
Aevatar.AI.MEAI -> Aevatar.AI
Aevatar.AI.LLMTornado -> Aevatar.AI
Aevatar.AI.MCP -> Aevatar.AI
Aevatar.AI.Skills -> Aevatar.AI
Aevatar.Api -> Aevatar.Runtime, Aevatar.Cognitive, Aevatar.Config, Aevatar.AGUI, Aevatar.AI.MEAI, Aevatar.AI.MCP
Aevatar.Gateway -> Aevatar.Runtime, Aevatar.Cognitive, Aevatar.Config, Aevatar.AI.MEAI, Aevatar.AI.LLMTornado, Aevatar.AI.MCP, Aevatar.AI.Skills
```

---

## 3. 不符合标准项（全量清单）

## 3.1 命名与命名空间（复数规则）

1. `Aevatar.AI.LLMTornado` 未使用集合复数命名。
- 当前：`Aevatar.AI.LLMTornado`
- 建议：`Aevatar.AI.LLMProviders.Tornado`
- 证据：`src/Aevatar.AI.LLMTornado/Aevatar.AI.LLMTornado.csproj:6`、`src/Aevatar.AI.LLMTornado/ServiceCollectionExtensions.cs:9`

2. `Aevatar.AI.MEAI` 未归入复数集合命名。
- 当前：`Aevatar.AI.MEAI`
- 建议：`Aevatar.AI.LLMProviders.MEAI`
- 证据：`src/Aevatar.AI.MEAI/Aevatar.AI.MEAI.csproj:6`、`src/Aevatar.AI.MEAI/ServiceCollectionExtensions.cs:9`

3. `Aevatar.AI.MCP` 建议归入工具提供者复数集合。
- 当前：`Aevatar.AI.MCP`
- 建议：`Aevatar.AI.ToolProviders.MCP`（或若只承载 Connector 实现则迁入 `Aevatar.Connectors.Providers.MCP`）
- 证据：`src/Aevatar.AI.MCP/Aevatar.AI.MCP.csproj:6`、`src/Aevatar.AI.MCP/MCPConnector.cs:8`

4. LLM 抽象命名空间使用单数 `LLM`。
- 当前：`Aevatar.AI.LLM`
- 建议：`Aevatar.AI.LLMProviders`（或 `Aevatar.AI.Abstractions.LLMProviders`）
- 证据：`src/Aevatar.AI/LLM/ILLMProvider.cs:6`、`src/Aevatar.AI/LLM/ILLMProviderFactory.cs:6`

5. `Aevatar.Abstractions`/`Aevatar.Core`/`Aevatar.Runtime` 共用根命名空间 `Aevatar`，边界不清晰。
- 当前：三个项目的 `RootNamespace` 都是 `Aevatar`
- 建议：分别规范为 `Aevatar.Abstractions`、`Aevatar.Core`、`Aevatar.Runtime`
- 证据：`src/Aevatar.Abstractions/Aevatar.Abstractions.csproj:7`、`src/Aevatar.Core/Aevatar.Core.csproj:6`、`src/Aevatar.Runtime/Aevatar.Runtime.csproj:6`

6. Runtime 内部集合命名空间含单数语义（如 `Aevatar.Actor`）。
- 当前：`Aevatar.Actor`
- 建议：`Aevatar.Runtime.Actors`
- 证据：`src/Aevatar.Runtime/Actor/LocalActor.cs:9`

## 3.2 DIP 与“依赖接口而非实现”

7. API 启动流程直接实例化配置实现类。
- 当前：`new AevatarSecretsStore()`
- 建议：通过 `ISecretsStore`（或 `ISecretProvider`）接口注入。
- 证据：`src/Aevatar.Api/Program.cs:40`

8. Provider 注册 API 暴露具体工厂类型（`Action<MEAILLMProviderFactory>` / `Action<TornadoLLMProviderFactory>`）。
- 当前：宿主必须依赖具体工厂实现。
- 建议：统一为 `Action<ILLMProviderRegistry>`（接口）。
- 证据：`src/Aevatar.AI.MEAI/ServiceCollectionExtensions.cs:26`、`src/Aevatar.AI.LLMTornado/ServiceCollectionExtensions.cs:25`

9. Connector 装配使用具体实现 `new HttpConnector/CliConnector/MCPConnector`，并在多个宿主重复。
- 当前：`Aevatar.Api` 与 `samples/maker` 重复 switch + new。
- 建议：引入 `IConnectorFactory`/`IConnectorFactories`，按类型解析实现。
- 证据：`src/Aevatar.Api/ConnectorRegistration.cs:34`、`src/Aevatar.Api/ConnectorRegistration.cs:55`、`src/Aevatar.Api/ConnectorRegistration.cs:79`、`samples/maker/Program.cs:167`、`samples/maker/Program.cs:191`、`samples/maker/Program.cs:218`

10. Workflow 编排对具体 Agent/Module 强耦合。
- 当前：
  - `runtime.CreateAsync<RoleGAgent>(...)`
  - `if (m is WorkflowLoopModule loop) ...`
- 建议：
  - 用 `IRoleAgentFactory`/`IAgentTypeResolver`
  - 用 `IWorkflowModule` 能力接口替代具体类型判断
- 证据：`src/Aevatar.Cognitive/WorkflowGAgent.cs:112`、`src/Aevatar.Cognitive/WorkflowGAgent.cs:161`

11. `Aevatar.Cognitive` 直接依赖完整 `Aevatar.AI`（含实现），不是纯契约依赖。
- 当前：`Aevatar.Cognitive -> Aevatar.AI`
- 建议：`Aevatar.Cognitive -> Aevatar.AI.Abstractions`，实现层留在 Host/Adapter。
- 证据：`src/Aevatar.Cognitive/Aevatar.Cognitive.csproj:9`

12. `Aevatar.AI.MEAI`/`Aevatar.AI.LLMTornado`/`Aevatar.AI.MCP`/`Aevatar.AI.Skills` 对 `Aevatar.AI` 全量依赖过重。
- 当前：Provider/Tool 适配项目依赖包含运行逻辑的 `Aevatar.AI`。
- 建议：仅依赖 `Aevatar.AI.Abstractions`（LLM/Tool 契约）。
- 证据：`src/Aevatar.AI.MEAI/Aevatar.AI.MEAI.csproj:9`、`src/Aevatar.AI.LLMTornado/Aevatar.AI.LLMTornado.csproj:9`、`src/Aevatar.AI.MCP/Aevatar.AI.MCP.csproj:9`、`src/Aevatar.AI.Skills/Aevatar.AI.Skills.csproj:9`

13. API/Gateway 项目直接引用具体 Provider 工程，导致编译期耦合。
- 当前：`Aevatar.Api` 和 `Aevatar.Gateway` 的 `.csproj` 直接引用 `Aevatar.AI.MEAI`/`Aevatar.AI.MCP`/`Aevatar.AI.LLMTornado`/`Aevatar.AI.Skills`。
- 建议：通过组合层（Bootstrap/Composition）或插件加载降低耦合；宿主只依赖抽象与组合入口。
- 证据：`src/Aevatar.Api/Aevatar.Api.csproj:10`、`src/Aevatar.Api/Aevatar.Api.csproj:11`、`src/Aevatar.Gateway/Aevatar.Gateway.csproj:11`、`src/Aevatar.Gateway/Aevatar.Gateway.csproj:12`、`src/Aevatar.Gateway/Aevatar.Gateway.csproj:13`、`src/Aevatar.Gateway/Aevatar.Gateway.csproj:14`

## 3.3 与既有配置约定不一致

14. 配置键已采用复数集合语义 `LLMProviders:*`，代码工程命名未对齐。
- 证据：`tools/Aevatar.Tools.Config/Program.cs:155`、`src/Aevatar.Config/AevatarSecretsStore.cs:49`

---

## 4. 目标分层（重构后）

建议统一为 5 层（仅允许“由外向内”依赖）：

1. `Abstractions`（契约层）
- 仅接口、DTO、proto 契约
- 不依赖任何实现层

2. `Core`（核心规则层）
- 纯框架核心行为
- 仅依赖 `Abstractions`

3. `Application/Orchestration`（编排层）
- 工作流编排、模块调度
- 仅依赖抽象，不直接依赖 Provider 实现

4. `Adapters/Providers`（适配层）
- LLMProviders / ToolProviders / ConnectorProviders 具体实现
- 只面向接口输出能力

5. `Hosts`（宿主层）
- Api/Gateway/Samples
- 做组合，不承载业务逻辑

---

## 5. 目标命名规范（含复数规则）

统一规则：

1. 项目名：`Aevatar.<LayerOrDomain>.<CollectionPlural>.<Item>`
2. 集合语义段使用复数：`LLMProviders`、`ToolProviders`、`ConnectorProviders`、`Actors`、`Modules`
3. `namespace` 与项目名前缀对齐，避免跨项目共用同一根命名空间。

建议重命名映射：

1. `Aevatar.AI.MEAI` -> `Aevatar.AI.LLMProviders.MEAI`
2. `Aevatar.AI.LLMTornado` -> `Aevatar.AI.LLMProviders.Tornado`
3. `Aevatar.AI.MCP` -> `Aevatar.AI.ToolProviders.MCP`（含 Connector 时评估拆分）
4. `Aevatar.AI.Skills` -> `Aevatar.AI.ToolProviders.Skills`
5. `Aevatar.AI.LLM` -> `Aevatar.AI.LLMProviders`（建议迁入抽象项目）
6. `Aevatar.Actor` -> `Aevatar.Runtime.Actors`
7. `Aevatar.DependencyInjection` -> `Aevatar.Runtime.DependencyInjection`
8. `Aevatar.Routing` -> `Aevatar.Runtime.Routing`
9. `Aevatar.Streaming` -> `Aevatar.Runtime.Streaming`

---

## 6. 分阶段重构计划

## Phase 0：基线冻结

1. 冻结当前行为并补充回归测试快照（Api、Workflow、Connector、Provider）。
2. 生成当前依赖图（`dotnet list reference` + 架构检查脚本）。

## Phase 1：抽象先行（DIP 落地前置）

1. 新增 `Aevatar.AI.Abstractions`，迁移：
- `ILLMProvider`
- `ILLMProviderFactory`
- `LLMRequest/LLMResponse`（若被 Provider 直接依赖）
- `IAgentTool`（若 ToolProvider 需使用）
2. `Aevatar.Cognitive`、`Aevatar.AI.MEAI`、`Aevatar.AI.LLMTornado`、`Aevatar.AI.MCP`、`Aevatar.AI.Skills` 改为依赖 `Aevatar.AI.Abstractions`。

## Phase 2：命名与 namespace 标准化

1. 项目重命名为复数集合结构（见第 5 节映射）。
2. 全量替换 `namespace` 与 `using`。
3. 旧命名保留过渡兼容层（`[Obsolete]` forwarding 扩展/包装）1 个迭代周期。

## Phase 3：Provider 注册接口化

1. 引入 `ILLMProviderRegistry`（接口），替代具体工厂暴露。
2. 把以下签名改为接口：
- `AddMEAIProviders(Action<ILLMProviderRegistry>)`
- `AddTornadoProviders(Action<ILLMProviderRegistry>)`
3. Provider 工厂实现内部保持具体实现，但宿主侧仅见接口。

## Phase 4：Connector 创建反转

1. 新增 `IConnectorFactory`（按 `type` 创建 Connector）。
2. `ConnectorRegistration` 与 `samples/maker` 改为依赖工厂接口，不再直接 `new` 具体 Connector。
3. 去重：抽取共享装配组件（例如 `Aevatar.Connectors.Registration`）。

## Phase 5：Workflow 编排解耦

1. 引入 `IRoleAgentFactory`，替代 `runtime.CreateAsync<RoleGAgent>`。
2. 引入 `IWorkflowOrchestrationModule`（或 `IWorkflowAwareModule`），替代 `WorkflowLoopModule` 具体类型判断。
3. `Aevatar.Cognitive` 仅依赖编排接口，不依赖具体 Agent 实现类型。

## Phase 6：Host 组合层收敛

1. 新增 `Aevatar.Bootstrap`（或 `Aevatar.Composition`）统一注册入口。
2. `Aevatar.Api`、`Aevatar.Gateway` 仅引用：
- 抽象层
- 核心层
- 组合层
3. Provider 工程作为可选插件接入（显式配置或反射加载）。

## Phase 7：收尾与清理

1. 删除旧命名与兼容转发代码。
2. 同步更新 `README`/`docs`/样例/测试命名。
3. 完成架构守卫（CI 检查依赖方向和命名规则）。

---

## 7. 验收标准（Definition of Done）

1. 不再存在宿主/编排层对实现类型的直接依赖（除组合层外）。
2. Provider/Connector/Tool 的集合类项目与 namespace 均为复数命名。
3. `Aevatar.Cognitive` 与 Provider 项目仅依赖抽象层。
4. 通过依赖检查脚本验证不存在逆向依赖。
5. 现有集成测试全部通过，行为与重构前一致。

建议 CI 守卫命令（示例）：

```bash
rg -n "new (HttpConnector|CliConnector|MCPConnector|MEAILLMProviderFactory|TornadoLLMProviderFactory)" src/Aevatar.Api src/Aevatar.Cognitive
rg -n "Action<(MEAILLMProviderFactory|TornadoLLMProviderFactory)>" src
```

---

## 8. 风险与缓解

1. 大规模命名迁移导致 API/文档破坏：
- 缓解：提供一轮兼容层并标记弃用。

2. 依赖拆分引发循环或编译失败：
- 缓解：先抽象、后迁移实现、最后切宿主依赖。

3. 行为回归（尤其 Workflow + Connector）：
- 缓解：先补集成测试，再分阶段迁移。

---

## 9. 建议执行顺序（最小风险）

1. 先做 Phase 1（抽象拆分）+ Phase 3（Provider 接口化）。
2. 再做 Phase 4（Connector 工厂化）+ Phase 5（Workflow 解耦）。
3. 最后做 Phase 2（命名空间与项目重命名）+ Phase 6/7（组合层与收尾）。

该顺序可以把“行为改造”和“命名迁移”解耦，降低一次性变更风险。
