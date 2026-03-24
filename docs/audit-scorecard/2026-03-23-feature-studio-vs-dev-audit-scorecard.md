# 2026-03-23 `feature/studio` vs `dev` 审计评分卡

## 1. 审计范围

- **分支**: `feature/studio` (`3b54e054`)
- **基线**: `dev`
- **审计方式**: 基于代码 diff、关键门禁、全量构建、前端类型检查、针对性测试与修复后复核
- **本次复核重点**:
  - `apps/aevatar-console-web`
  - `src/workflow/Aevatar.Workflow.Infrastructure`
  - `test/Aevatar.Workflow.Application.Tests`
  - `test/Aevatar.Workflow.Host.Api.Tests`
  - `tools/ci/*`
  - `demos/Aevatar.Demos.Workflow.Web/wwwroot`

---

## 2. 修复后验证结果

| 验证项 | 结果 | 备注 |
|---|---|---|
| `bash tools/ci/playground_asset_drift_guard.sh` | **PASS** | 守卫已改为在临时目录构建 CLI playground，再与 Demo Web 静态资源比对；干净 checkout 不再依赖预生成的 `tools/Aevatar.Tools.Cli/wwwroot/playground/*`。 |
| `bash tools/ci/architecture_guards.sh` | **PASS** | 全链路门禁通过，说明 playground 漂移问题修复后，后续 workflow / scripting / CQRS guard 也能继续执行并通过。 |
| `dotnet build aevatar.slnx --nologo` | **PASS** | `WorkflowRunActorResolverTests` 中遗留的旧类型名已修复，解决方案恢复可编译状态。 |
| `pnpm -C apps/aevatar-console-web tsc --noEmit` | **PASS** | Console 改为使用本地导航 shim，认证回调页移除了不存在的 `useModel` 依赖。 |
| `dotnet test test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj --nologo --filter WorkflowRunActorResolverTests` | **PASS** | 20/20 通过。 |
| `dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo --filter ChatQueryEndpointsTests` | **PASS** | 7/7 通过，覆盖新增 `/primitives` 与 `/graph-enriched` 端点。 |

> 结论：此前 5 个 review finding 已全部闭环，当前分支已恢复到“可构建、可静态检查、可执行主架构门禁”的状态。

---

## 3. 总评

| 总分 | 等级 | 结论 |
|---|---|---|
| **90 / 100** | **A-** | 阻塞级问题已清零，当前分支具备合入 `dev` 的基本工程条件；剩余扣分主要来自文档/默认端口口径尚未完全收敛，以及本次只做了针对性测试回归，尚未重新跑全量 `dotnet test`。 |

---

## 4. 八维评分

| 维度 | 分数 | 说明 |
|---|---:|---|
| 构建与门禁 | 18 / 20 | `dotnet build`、`playground_asset_drift_guard`、`architecture_guards` 均已通过。 |
| 前后端契约 | 14 / 15 | Console 新增的 `/api/primitives`、`/api/actors/{id}/graph-enriched` 已补齐到后端正式路由，并有测试覆盖。 |
| 分层与架构 | 14 / 15 | 修复未破坏 `Domain / Application / Infrastructure / Host` 方向，且把 guard 收敛到可验证路径。 |
| 类型与静态质量 | 9 / 10 | `aevatar-console-web` 的 TypeScript 红线已清除。 |
| 测试与可验证性 | 8 / 10 | 针对修复点的测试已覆盖并通过，但尚未重新跑完整 `dotnet test aevatar.slnx --nologo`。 |
| 文档与运维一致性 | 8 / 10 | 评分卡已更新，但仓库内仍存在部分 `5000` 端口示例，和顶层约束不完全一致。 |
| 安全与身份边界 | 9 / 10 | 本轮修复未引入新的身份/边界退化。 |
| 可维护性 | 10 / 10 | review 指出的阻塞问题都以直接、可验证的方式收口，没有保留临时兜底。 |

**总分**: `18 + 14 + 14 + 9 + 8 + 8 + 9 + 10 = 90 / 100`

---

## 5. 已完成修复

### F1. 全量构建恢复

| | |
|---|---|
| **文件** | `test/Aevatar.Workflow.Application.Tests/WorkflowRunActorResolverTests.cs` |
| **修复** | 将残留的 `InMemoryWorkflowDefinitionRegistry` 替换为当前抽象体系下的 `InMemoryWorkflowDefinitionCatalog`。 |
| **验证** | `dotnet build aevatar.slnx --nologo`、`WorkflowRunActorResolverTests` 定向测试均已通过。 |

### F2. Playground asset drift 守卫恢复为“干净 checkout 可验证”

| | |
|---|---|
| **文件** | `tools/ci/playground_asset_drift_guard.sh`, `demos/Aevatar.Demos.Workflow.Web/wwwroot/app.js`, `demos/Aevatar.Demos.Workflow.Web/wwwroot/app.css` |
| **修复** | 守卫改为先在临时目录构建 CLI playground，再和 Demo Web 静态资源比对；同时把 Demo 静态资源同步到当前 CLI frontend 产物。 |
| **验证** | `bash tools/ci/playground_asset_drift_guard.sh`、`bash tools/ci/architecture_guards.sh` 均已通过。 |

### F3. Console TypeScript 类型问题修复

| | |
|---|---|
| **文件** | `apps/aevatar-console-web/src/app.tsx`, `apps/aevatar-console-web/src/pages/**`, `apps/aevatar-console-web/src/shared/navigation/history.ts`, `apps/aevatar-console-web/src/pages/auth/callback/index.tsx` |
| **修复** | 用本地 navigation shim 替代仓库当前 `@umijs/max` 版本不提供的 `history`；认证回调页移除不存在的 `useModel` 依赖，改为基于现有会话存储与跳转逻辑处理回调。 |
| **验证** | `pnpm -C apps/aevatar-console-web tsc --noEmit` 通过。 |

### F4. Console 缺失后端接口补齐

| | |
|---|---|
| **文件** | `src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/ChatQueryEndpoints.cs`, `test/Aevatar.Workflow.Host.Api.Tests/ChatQueryEndpointsTests.cs` |
| **修复** | 新增 `/primitives` 与 `/actors/{actorId}/graph-enriched` 两条查询路由，并补齐 DTO 映射与测试。 |
| **验证** | `ChatQueryEndpointsTests` 定向测试 7/7 通过。 |

---

## 6. 剩余风险

### R1. 默认端口文档口径仍未完全统一

| | |
|---|---|
| **文件示例** | `tools/Aevatar.Tools.Cli/README.md`, `src/workflow/Aevatar.Workflow.Sdk/README.md`, `src/workflow/Aevatar.Workflow.Sdk/Options/AevatarWorkflowClientOptions.cs` |
| **问题** | 仓库顶层规则要求默认示例避免 `5000/5050`，当时仍有若干 CLI / SDK 文档与默认值沿用旧默认端口。 |
| **影响** | 不阻塞当前修复合入，但会持续制造 README、示例代码与仓库约束之间的不一致。 |
| **建议** | 作为后续单独清理项，统一切到仓库当前文档口径（如 `5100`）并同步 Host / SDK / README。 |

---

## 7. 审计结论

本轮 review 指出的 5 个问题已经全部修复，分支状态相较第一次审计有实质性改善：

- `build` 已恢复
- `architecture guards` 已恢复
- Console `tsc` 已恢复
- 新 Console 依赖的两条查询链路已有后端正式契约

当前结论从“**不建议直接合入**”更新为“**可以进入合入准备阶段**”。如果要进一步把评分推到 `90+` 以上的稳定档，下一步应优先补跑全量测试，并统一仓库内 `5000` 端口相关的默认文档/SDK 口径。
