# Aevatar 项目规范与最佳实践差距审计

## 范围

本审计覆盖 `src/`、`test/`、`samples/`、`tools/`、解决方案与构建配置，关注以下维度：

1. 项目名与命名空间规范（含复数语义、层级边界）。
2. 标准工程实践（安全、可维护性、可测试性、配置一致性）。
3. 与“依赖抽象/可组合架构”目标的一致性。

说明：本文件是全局“问题清单”，依赖重构的详细实施路线已在 `docs/DEPENDENCY_REFACTOR_PLAN.md`。

---

## 结论概览

发现的问题可分为三类：

1. 严重（应优先改）：安全与生命周期错误、契约实现偏差、核心命名边界混乱。
2. 重要（应在近期迭代改）：同步阻塞、字符串路由、配置与功能不一致、测试覆盖缺口。
3. 一般（规划改进）：工程基线与工具链标准化不足。

---

## 严重问题（P0/P1）

## 1. 密钥写入明文，和“安全存储”目标不一致

问题：
`AevatarSecretsStore` 读取支持加密格式，但写入始终走明文 `SavePlaintext()`。

证据：
- `src/Aevatar.Config/AevatarSecretsStore.cs:80`
- `src/Aevatar.Config/AevatarSecretsStore.cs:88`
- `src/Aevatar.Config/AevatarSecretsStore.cs:153`

影响：
API Key/Secrets 落盘为明文，生产环境存在泄露风险。

建议：
实现对称的加密写回（至少提供配置开关，默认加密），并使用原子写入与文件权限收敛。

## 2. Agent 重复激活（Create 后再次 Activate）

问题：
Runtime 创建 Actor 时已经执行 `ActivateAsync`，但上层再次调用 `ActivateAsync`。

证据：
- Runtime 已激活：`src/Aevatar.Runtime/Actor/LocalActorRuntime.cs:61`
- API 再次激活：`src/Aevatar.Api/Endpoints/ChatEndpoints.cs:91`、`src/Aevatar.Api/Endpoints/ChatEndpoints.cs:101`
- Sample 再次激活：`samples/maker/Program.cs:258`、`samples/maker/Program.cs:264`

影响：
可能导致 Hook 重复注册、状态重复加载、行为不可预测。

建议：
统一生命周期入口：`CreateAsync` 负责激活，上层仅设置初始状态/配置，不重复调用 `ActivateAsync`。

## 3. IStream 泛型契约与实现行为不一致

问题：
接口声明为“可订阅任意 `IMessage` 类型”，实现实际仅支持 `EventEnvelope`。

证据：
- 契约：`src/Aevatar.Abstractions/IStream.cs:24`
- 实现仅处理 `EventEnvelope`：`src/Aevatar.Runtime/Streaming/InMemoryStream.cs:61`

影响：
接口语义误导调用方，破坏可替换性与可预期性。

建议：
二选一：
1) 收敛接口为 `EventEnvelope` 专用；
2) 实现真正的泛型消息分发。

## 4. 项目名与 namespace 边界不规范（你指出的重点）

问题：
核心层存在大量“项目名/RootNamespace/代码 namespace”不一致，且集合语义未统一复数命名。

关键证据：
- `Aevatar.Abstractions` 项目 `RootNamespace` 为 `Aevatar`：`src/Aevatar.Abstractions/Aevatar.Abstractions.csproj:7`
- `Aevatar.Core` 项目 `RootNamespace` 为 `Aevatar`：`src/Aevatar.Core/Aevatar.Core.csproj:6`
- `Aevatar.Runtime` 项目 `RootNamespace` 为 `Aevatar`：`src/Aevatar.Runtime/Aevatar.Runtime.csproj:6`
- Runtime 代码散落在 `Aevatar.Actor` / `Aevatar.Routing` / `Aevatar.Streaming` 等非 `Aevatar.Runtime.*` 前缀：`src/Aevatar.Runtime/Actor/LocalActor.cs:9`、`src/Aevatar.Runtime/Routing/EventRouter.cs:6`、`src/Aevatar.Runtime/Streaming/InMemoryStream.cs:9`
- Provider 集合仍用单体命名：`src/Aevatar.AI.MEAI/Aevatar.AI.MEAI.csproj:6`、`src/Aevatar.AI.LLMTornado/Aevatar.AI.LLMTornado.csproj:6`

影响：
分层边界模糊、可维护性差、重构成本持续升高。

建议：
按复数集合规范重命名（示例）：
1. `Aevatar.AI.MEAI` -> `Aevatar.AI.LLMProviders.MEAI`
2. `Aevatar.AI.LLMTornado` -> `Aevatar.AI.LLMProviders.Tornado`
3. `Aevatar.AI.MCP` -> `Aevatar.AI.ToolProviders.MCP`
4. Runtime 全量收敛到 `Aevatar.Runtime.*`

---

## 重要问题（P1/P2）

## 5. 同步阻塞异步调用（潜在死锁点）

证据：
- `src/Aevatar.AI/RoleGAgentFactory.cs:59`

建议：
将 `ApplyConfig` 改为异步链路，避免 `GetAwaiter().GetResult()`。

## 6. Fire-and-forget 持久化（异常不可观测）

证据：
- `src/Aevatar.Core/GAgentBase.cs:177`
- `src/Aevatar.Core/GAgentBase.cs:184`

建议：
显式 await 或引入可观测后台队列，并记录失败。

## 7. 事件类型判断大量依赖 `TypeUrl.Contains(...)` 字符串匹配

证据（示例）：
- `src/Aevatar.Cognitive/Modules/LLMCallModule.cs:33`
- `src/Aevatar.Cognitive/Modules/WorkflowLoopModule.cs:43`
- `src/Aevatar.Gateway/ChatEndpoints.cs:70`

影响：
脆弱、难维护、易误匹配。

建议：
统一使用类型安全判定（消息 Descriptor/封装 helper）。

## 8. MCP/Skills 注册扩展与文档承诺不一致（功能未闭环）

现状：
`AddMCPTools` / `AddSkills` 仅注册 options/manager/discovery，没有落到 Agent 工具注册流程。

证据：
- MCP 仅注册：`src/Aevatar.AI.MCP/ServiceCollectionExtensions.cs:27`
- Skills 仅注册：`src/Aevatar.AI.Skills/ServiceCollectionExtensions.cs:41`

建议：
补齐 Tool 注入桥接（Host 启动时发现 -> 绑定到 Agent 工具管理器），或下调文档承诺。

## 9. MCP 配置字段未完整生效

问题：
`MCPServerConfig.Environment` 已定义，但建立连接时未使用。

证据：
- 定义字段：`src/Aevatar.AI.MCP/MCPServerConfig.cs:20`
- 创建 transport 未传 environment：`src/Aevatar.AI.MCP/MCPClientManager.cs:34`

建议：
在 transport/options 中传递环境变量，保证配置一致性。

## 10. API 组合配置重复/缺失并存

问题 A（重复）：
`AddAevatarCognitive()` 已注册 `IEventModuleFactory`，API 再次显式注册同实现。

证据：
- 默认注册：`src/Aevatar.Cognitive/ServiceCollectionExtensions.cs:21`
- API 再注册：`src/Aevatar.Api/Program.cs:31`

问题 B（缺失）：
Gateway 只注册 `IEventModuleFactory`，未调用 `AddAevatarCognitive()`，默认 `IConnectorRegistry` 不存在。

证据：
- `src/Aevatar.Gateway/Program.cs:24`

建议：
收敛为统一组合入口，避免宿主间行为偏差。

## 11. 安全默认值偏宽

问题：
API 直接 `AllowAnyOrigin/AnyMethod/AnyHeader`。

证据：
- `src/Aevatar.Api/Program.cs:88`

建议：
按环境分级（dev 放开、prod 白名单），并补鉴权中间件。

## 12. 依赖清单显著膨胀（治理成本高）

审计结果：
- `Directory.Packages.props` 声明包：215
- 实际项目直接引用包：23
- 未被直接引用：193

证据：
- 文件：`Directory.Packages.props`

建议：
按实际域拆分 CPM，移除当前解空间无关依赖，避免误升级和安全扫描噪音。

## 13. 测试覆盖存在结构性空白

现状：
测试项目仅直接覆盖 `Abstractions`、`Api`、`Core/Runtime`、`Cognitive/Config`，以下项目无直接测试引用：
`Aevatar.AI`、`Aevatar.AI.MEAI`、`Aevatar.AI.LLMTornado`、`Aevatar.AI.MCP`、`Aevatar.AI.Skills`、`Aevatar.AGUI`、`Aevatar.Gateway`。

证据：
- `test/Aevatar.Abstractions.Tests/Aevatar.Abstractions.Tests.csproj:23`
- `test/Aevatar.Api.Tests/Aevatar.Api.Tests.csproj:11`
- `test/Aevatar.Core.Tests/Aevatar.Core.Tests.csproj:11`
- `test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj:11`

建议：
补 Provider/Tool/Host 级契约测试与最小集成测试。

## 14. 配置模型与运行行为有偏差

问题：
`connectors.json` 支持 `retry` 字段，但运行时仅从 step 参数读取 retry，配置级 retry 未生效。

证据：
- 配置字段：`src/Aevatar.Config/AevatarConnectorConfig.cs:14`
- 运行时读取 step 参数：`src/Aevatar.Cognitive/Modules/ConnectorCallModule.cs:30`

建议：
定义优先级：`step.retry > connector.retry > default`，并落实代码。

---

## 一般问题（P2/P3）

## 15. 使用预览语言/平台特性作为默认基线

证据：
- `LangVersion=preview`：`Directory.Build.props:8`
- 多项目 `TargetFramework=net10.0`（示例：`src/Aevatar.Core/Aevatar.Core.csproj:3`）

建议：
若目标为稳定生产，建议 LTS 基线或明确“预览分支”策略。

## 16. 工程规范基线文件缺失

现状：
仓库根目录未发现 `.editorconfig`、`global.json`、CI 工作流配置。

影响：
团队一致性、SDK 漂移控制、自动质量门禁不足。

建议：
补齐：
1. `.editorconfig`
2. `global.json`
3. CI（build + test + analyzers + architecture checks）

## 17. 未使用/重复能力存在

问题：
`SecretManager` 当前无调用，且与 `AevatarSecretsStore` 职责重叠。

证据：
- 定义：`src/Aevatar.AI/Secrets/SecretManager.cs:14`
- 代码检索无调用点（仅定义处）

建议：
删除未使用实现，或收敛为单一密钥服务抽象。

---

## 验证状态

1. 已执行静态审计与代码扫描。
2. 尝试执行 `dotnet test aevatar.slnx --nologo`，在当前环境出现 `MSB1025 + SocketException (13) Permission denied`（MSBuild 命名管道权限问题），未能得到有效测试通过基线。

---

## 推荐优先顺序

1. 先修安全与生命周期：问题 1、2、3、11。
2. 再修命名与抽象边界：问题 4、5、6、7。
3. 然后补功能闭环与一致性：问题 8、9、10、14。
4. 最后做工程治理：问题 12、13、15、16、17。
