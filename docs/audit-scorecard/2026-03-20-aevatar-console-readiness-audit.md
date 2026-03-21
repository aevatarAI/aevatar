# Aevatar Backend + Console 上线就绪度审计

## 1. 结论

当前仓库的后端核心架构是成熟的，但 `aevatar-console-web` 仍处于 `Console Shell + Studio sidecar + Configuration API` 的过渡态。

结论分两层：

- 后端主链：可以继续向生产形态演进，分层、CQRS、Projection、Actor Runtime 的治理是成立的。
- Console 产品：当前不建议按仓库现状直接对外正式上线，更适合内测、演示、受控内网部署。

当前总体判断：

- `后端核心架构成熟度`：`较高`
- `Console 功能覆盖度`：`较高`
- `Console 上线就绪度`：`中低`
- `当前仓库形态是否可直接正式上线`：`否`

## 2. 审计范围

本次审计覆盖：

- `src/` 下后端能力与宿主
- `tools/Aevatar.Tools.Cli` 中 `aevatar app` sidecar / studio host
- `tools/Aevatar.Tools.Config` 本地配置宿主
- `apps/aevatar-console-web`
- 关键文档、构建、测试、架构守卫

## 3. 后端全景

仓库当前共有 `65` 个 `.csproj`，主干可以概括为 6 个能力族：

| 能力族 | 项目数 | 说明 |
|---|---:|---|
| `Foundation.*` | 12 | Actor Runtime、Abstractions、Hosting、Projection、Persistence |
| `CQRS.*` | 10 | Command/Query/Projection Core 与 Provider |
| `workflow/*` | 12 | Workflow Domain / Application / Infrastructure / Host / Extensions |
| `platform/*` | 12 | GAgentService 平台能力、治理、投影、宿主 |
| `Scripting.*` | 6 | Script 定义、运行、Projection、Hosting |
| `AI.*` | 7 | LLM Provider、Tool Provider、AI Core、Projection |

主要宿主面：

- `src/workflow/Aevatar.Workflow.Host.Api`
- `src/Aevatar.Mainnet.Host.Api`
- `tools/Aevatar.Tools.Cli -- app`
- `tools/Aevatar.Tools.Config`

当前宿主语义：

- `Workflow.Host.Api`：Workflow runtime/query 的能力隔离宿主
- `Mainnet.Host.Api`：目标统一入口，叠加 Workflow + GAgentService + 分布式运行时能力
- `aevatar app`：Studio Host / BFF，支持 embedded 或 proxy
- `Tools.Config`：本地配置 UI，明确是 localhost-only

## 4. Console 功能面

`aevatar-console-web` 当前已有真实实现页面，不是空壳。

主导航：

- `Overview`
- `Workflows`
- `Studio`
- `Primitives`
- `Runs`
- `Actors`
- `Observability`
- `Settings`

页面规模也说明它不是 demo：

| 页面 | 文件行数 |
|---|---:|
| `overview` | 612 |
| `workflows` | 2055 |
| `primitives` | 476 |
| `runs` | 2219 |
| `actors` | 1367 |
| `observability` | 361 |
| `settings` | 3636 |
| `studio` | 3766 |

## 5. Console 到后端的真实依赖

### 5.1 Console Shell

| 页面 | 主要 API 依赖 | 当前判断 |
|---|---|---|
| `Overview` | `listWorkflows` `listWorkflowCatalog` `listAgents` `getCapabilities` | 基本成立 |
| `Workflows` | `listWorkflowCatalog` `getWorkflowDetail` | 基本成立 |
| `Runs` | `/api/chat` `/api/workflows/resume` `/api/workflows/signal` `getActorSnapshot` | 基本成立 |
| `Actors` | `getActorSnapshot` `getActorTimeline` `getActorGraphEnriched` `getActorGraphSubgraph` `getActorGraphEdges` | 有硬缺陷 |
| `Observability` | 无新增后端 API，仅外链跳转 | 成立 |

### 5.2 Console Studio / Settings

| 页面 | 主要 API 依赖 | 当前判断 |
|---|---|---|
| `Studio` | `/api/app/*` `/api/auth/*` `/api/workspace/*` `/api/editor/*` `/api/executions/*` `/api/roles/*` `/api/connectors/*` `/api/settings/*` | 依赖 sidecar/BFF |
| `Settings` | `/api/configuration/*` + Console workflow/capability 查询 | 依赖本地配置宿主 |

### 5.3 额外隐藏依赖

Console 代码中还存在这些前端预期接口：

- `/api/primitives`
- `/api/llm/status`
- `/api/workflow-authoring/parse`
- `/api/workflow-authoring/workflows`

但在当前仓库的后端路由定义中，没有找到对应的显式 endpoint 注册。

这说明：

- console 前端能力面已经超出当前统一宿主显式暴露的 API 面
- 某些页面/交互可能依赖历史实现、sidecar、或尚未完成的统一入口收敛

## 6. 实际验证结果

本次执行结果如下：

| 验证项 | 命令 | 结果 |
|---|---|---|
| 后端编译 | `dotnet build aevatar.slnx --nologo` | 通过 |
| 后端测试 | `dotnet test aevatar.slnx --nologo` | 失败，`1` 个真实失败用例 |
| 架构守卫 | `bash tools/ci/architecture_guards.sh` | 通过 |
| Console 生产打包 | `cd apps/aevatar-console-web && pnpm build` | 通过 |
| Console Jest | `cd apps/aevatar-console-web && pnpm test --runInBand` | 通过，`29 suites / 105 tests` |
| Console TypeScript | `cd apps/aevatar-console-web && pnpm tsc` | 失败 |

补充说明：

- 分布式/Kafka/Garnet/Elasticsearch/Neo4j 相关测试有多项 `SKIP`
- `pnpm build` 通过不代表前端门禁全绿，因为 `build` 没有包含 `tsc`

## 7. 阻塞项

### P0. Console `Actors` 页与后端接口契约不一致

现象：

- 前端默认请求 `/api/actors/{actorId}/graph-enriched`
- 当前 `Workflow Host API` 只显式提供：
  - `/api/actors/{actorId}`
  - `/api/actors/{actorId}/timeline`
  - `/api/actors/{actorId}/graph-edges`
  - `/api/actors/{actorId}/graph-subgraph`

影响：

- Actor Explorer 不是“某个高级选项失效”，而是默认查询路径就会打到不存在的接口
- 线上用户进入 Actors 查询会直接看到错误态

证据：

- `apps/aevatar-console-web/src/pages/actors/index.tsx`
- `apps/aevatar-console-web/src/shared/api/consoleApi.ts`
- `src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/ChatQueryEndpoints.cs`

### P0. Studio 与 Settings 仍绑定本地化宿主

现象：

- `console-web` 开发文档明确依赖三套后端：
  - `Workflow Host API`
  - `Configuration API`
  - `Studio sidecar`
- `Configuration API` 文档明确声明 `localhost only`
- `aevatar app` sidecar 默认也是本地工具宿主语义

影响：

- 当前不是“单个可部署 console + 单个统一后端”形态
- 是“前端 + 两个本地化宿主 + 一个 workflow host”的组合
- 对外正式上线时，部署拓扑、认证、网络暴露面、反向代理策略都还未收口

证据：

- `apps/aevatar-console-web/README.md`
- `apps/aevatar-console-web/config/proxy.ts`
- `tools/Aevatar.Tools.Config/README.md`
- `tools/Aevatar.Tools.Cli/Hosting/AppToolHost.cs`

### P0. 全仓仍有真实集成测试失败

失败用例：

- `Aevatar.Integration.Tests.ClaimReplayTests.Should_recompile_from_definition_source_without_external_repository`

失败原因：

- `ProjectionScriptDefinitionSnapshotPort` 在读取脚本定义快照时抛出
  `Script definition snapshot not found`

影响：

- scripting / projection / read-model 链路存在真实断点
- 这属于业务事实链路问题，不是单纯测试脆弱性

证据：

- `test/Aevatar.Integration.Tests/ClaimReplayTests.cs`
- `src/Aevatar.Scripting.Projection/ReadPorts/ProjectionScriptDefinitionSnapshotPort.cs`

### P0. Console 类型检查未通过

现象：

- `pnpm build` 通过
- `pnpm tsc` 失败
- 失败集中在 `Studio` 测试代码

影响：

- 前端质量门禁当前是红的
- 团队后续改动容易继续积累类型债
- CI 若补齐 `tsc` 门禁，会立即阻断交付

证据：

- `apps/aevatar-console-web/package.json`
- `apps/aevatar-console-web/src/pages/studio/components/StudioShell.test.tsx`
- `apps/aevatar-console-web/src/pages/studio/index.test.tsx`

### P1. Console 依赖的部分 API 在统一宿主中未见显式实现

前端存在直接调用：

- `/api/primitives`
- `/api/llm/status`
- `/api/workflow-authoring/parse`
- `/api/workflow-authoring/workflows`

但当前仓库后端显式路由中未找到对应注册。

影响：

- `Primitives` 页可能无法在当前统一宿主上闭环
- `yaml/playground` 等路径可能依赖历史或旁路宿主
- Mainnet “唯一后端 API 面”的目标尚未完全落地

### P1. Mainnet 生产形态是支持的，但不是默认落地状态

现状：

- 运行时默认 provider 仍是 `InMemory`
- Orleans/Kafka/Garnet/Elasticsearch/Neo4j 配置在单独的 `appsettings.Distributed.json`

影响：

- 当前默认启动不能代表生产部署
- 需要显式切换到分布式配置并跑真实基础设施验证

证据：

- `src/Aevatar.Foundation.Runtime.Hosting/AevatarActorRuntimeOptions.cs`
- `src/Aevatar.Mainnet.Host.Api/appsettings.Distributed.json`

### P2. Console 认证异常收敛未完全完成

现象：

- 错误处理中 `REDIRECT` 分支仍是 `TODO`

影响：

- 登录过期、无权限、部分后端 showType 返回 redirect 时，体验不稳定

证据：

- `apps/aevatar-console-web/src/requestErrorConfig.ts`

## 8. 为什么当前不建议直接正式上线

不是因为它“功能少”，而是因为它同时命中以下 4 类问题：

- 有真实接口错位
- 有真实集成测试失败
- 有本地化宿主依赖未服务化
- 有工程质量门禁未绿

这四类问题叠加后，意味着当前更适合：

- 内部演示
- 小范围内测
- 受控环境试运行

不适合：

- 对外 SLA 型正式服务
- 依赖统一产品壳稳定运行的多租户交付
- 将 console 作为唯一官方线上入口直接开放给外部用户

## 9. 推荐整改顺序

### Phase 1: 收口硬阻塞

目标：

- 先把“明显会炸”的问题收掉

任务：

- 修复 `Actors` 页与 `graph-enriched` 契约错位
- 修复 `ClaimReplayTests` 失败
- 修复 `pnpm tsc` 错误
- 明确 `/api/primitives` `/api/llm/status` `/api/workflow-authoring/*` 的统一宿主归属

预计范围：

- `前端 API / 页面`
- `Workflow query endpoint`
- `Scripting projection/read-port`
- `Studio test typing`

复杂度：

- `中`

### Phase 2: 收敛部署拓扑

目标：

- 让 console 变成可解释的正式部署单元

任务：

- 决定 `Studio` 能力是否继续依赖 `aevatar app` sidecar
- 若继续依赖，则把 sidecar 正式服务化并纳入统一部署
- 若不继续依赖，则将 `workspace/editor/executions/roles/connectors/settings` API 统一并入 Mainnet 或正式 BFF
- 处理 `Configuration API` 的线上替代方案，不能继续依赖 localhost-only 宿主

复杂度：

- `大`

### Phase 3: 验证生产运行形态

目标：

- 用真实基础设施确认不是“只在 InMemory 里正确”

任务：

- 启用 Mainnet distributed 配置
- 验证 Orleans + Kafka + Garnet + Elasticsearch + Neo4j
- 跑 smoke / cluster / persistence 脚本
- 补线上部署文档和回滚策略

复杂度：

- `大`

## 10. 最终建议

当前建议口径：

- `后端`：可以继续按生产架构推进
- `console`：可以继续作为主前端方向演进
- `正式上线`：暂缓，先完成 `Phase 1 + Phase 2`

更准确的说法不是：

- “系统不能用”

而是：

- “核心架构已经成型，但产品集成和部署收敛还没完成”

这也是当前最真实的项目状态。
