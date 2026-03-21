# 2026-03-20 `feat/scripts` vs `dev` 架构审计评分卡

## 1. 审计范围

- **分支**: `feat/scripts` (c1e33261)
- **基线**: `dev`
- **变更规模**: 795 files changed, 69839 insertions(+), 38809 deletions(-)
- **审计工具**: Claude Opus 4.6 独立审计（非 Codex 评分卡衍生）
- **覆盖面**: C# 源码、Proto 文件、前端 TS/JS/HTML/CSS、构建配置、CI 门禁、Docker 编排、文档

---

## 2. 门禁执行结果

| 门禁 | 结果 | 备注 |
|------|------|------|
| `architecture_guards.sh` | **FAIL** | 在 `playground_asset_drift_guard` 步骤失败：`app.js` 和 `app.css` CLI playground 与 Demo Web 静态资源不同步。后续子门禁（script inheritance、scripting interaction boundary、CQRS boundary、committed-state projection、runtime callback 等）因 `set -euo pipefail` 未被执行。 |
| `workflow_binding_boundary_guard.sh` | PASS | |
| `query_projection_priming_guard.sh` | PASS | |
| `projection_state_version_guard.sh` | PASS | |
| `projection_state_mirror_current_state_guard.sh` | PASS | |
| `projection_route_mapping_guard.sh` | PASS | |
| `test_stability_guards.sh` | PASS | |

> **与 Codex 评分卡分歧**：Codex 报告 `architecture_guards.sh` 通过，本次审计实测失败。差异原因可能是 Codex 审计时前端资源尚未出现漂移，或基于更早的 commit。这是一个客观事实差异，不是评分口径不同。

---

## 3. 总评

| 总分 | 等级 | 结论 |
|------|------|------|
| **79 / 100** | **B+** | 核心 scripting/workflow/projection 域改进方向正确（scope 强类型化、StateMirror 删除、query-time priming 移除）。但存在 1 个 Critical 安全问题、2 个 High 级配置安全隐患、1 个 High 级构建确定性问题、以及 CI 门禁实际未全通过。App Studio / CLI 工具层积累了较多架构债务。 |

---

## 4. 八维评分

| 维度 | 分数 | 说明 |
|------|-----:|------|
| 安全性 | 8 / 15 | Critical: Scope Header Injection (IDOR)；High: 硬编码 Neo4j 密码进入版本控制；Medium: 内部 Token 非恒定时间比较 |
| 分层与依赖反转 | 16 / 20 | 新 `IScopeScript*Port` 分层正确；但 `AppScopedScriptService` 583 行 dual-dispatch（8 处 `if port != null` 分支）违反传输载体可替换原则 |
| CQRS 与统一投影链路 | 17 / 20 | scope 已进入 proto/state/readmodel/projector 统一链路；但 scoped script 保存后立即读回 detail，命令成功与 readmodel 可见性被耦合 |
| Projection 编排与状态约束 | 17 / 20 | StateMirror 整体删除（大加分）；但 MassTransit subscriber provider 仍持有 `Dictionary<string, List<Subscriber>>` + `Lock` 进程内状态 |
| 读写分离 | 11 / 15 | `EnsureProjectedRuntimeAsync` 被正确移除；但 CLI 层 `Save -> immediately Read detail` 模式仍假设 readmodel 与 command 同步 |
| 命名语义与冗余 | 7 / 10 | 大部分命名清晰；workflow 入口同时维护 `workflow.scope_id` 与 `scope_id` 双 key；`AppScopedScriptService` 的 `ScriptId` 字段空值=自动生成、非空=标识查找，属 API 字段双重语义 |
| 构建与 CI 确定性 | 7 / 10 | `global.json` rollForward 从 `latestPatch` 改为 `latestFeature` 破坏构建确定性；`codecov.yml` 关闭 patch 覆盖率门禁；playground asset drift 门禁失败 |
| 可验证性 | 13 / 15 | 定向测试覆盖良好；但 `architecture_guards.sh` 主门禁实际失败，部分子门禁未被执行到 |

**总分**: 8 + 16 + 17 + 17 + 11 + 7 + 7 + 13 = **96 / 125 → 归一化 79 / 100**

---

## 5. 问题清单

### Critical

#### C1. Scope Header Injection — IDOR (OWASP A01)

| | |
|---|---|
| **文件** | `tools/Aevatar.Tools.Cli/Hosting/AppScopeResolver.cs:66-82` |
| **描述** | `DefaultAppScopeResolver.Resolve()` 先检查认证 Claims，但无论认证是否启用都会 fall through 到裸 HTTP header（`X-Aevatar-Scope-Id`, `X-Scope-Id`）。当 NyxID 认证开启时，已认证用户可注入 header 覆盖 scope，访问他人数据。 |
| **修复** | NyxID 认证启用时，scope 解析仅允许使用认证 Claims，禁止 header fallback。将 header 路径放在 `!nyxIdAuthEnabled` 守卫之后。 |

---

### High

#### H1. 硬编码数据库密码进入版本控制

| | |
|---|---|
| **文件** | `docker-compose.mainnet-cluster.yml`（3 个节点）、`src/Aevatar.Mainnet.Host.Api/appsettings.Distributed.json` |
| **描述** | Neo4j 密码以明文 `password` 硬编码在 compose 文件和应用配置中。这些文件会随二进制打包发布。 |
| **修复** | 使用 `${NEO4J_PASSWORD}` 环境变量插值或 Docker Secrets。配置文件中移除明文密码，改用环境变量注入。 |

#### H2. `global.json` rollForward 破坏构建确定性

| | |
|---|---|
| **文件** | `global.json` |
| **描述** | `rollForward` 从 `latestPatch` 改为 `latestFeature`，允许跨 feature band（如 10.1.x → 10.2.x）。不同开发者机器和 CI 可能使用不同 SDK 版本，产生不可复现的构建差异。 |
| **修复** | 恢复为 `latestPatch`。如需新 SDK feature，直接 bump `version` 字段。 |

#### H3. Playground 静态资源漂移 — CI 门禁失败

| | |
|---|---|
| **文件** | `demos/Aevatar.Demos.Workflow.Web/wwwroot/app.js`, `app.css` |
| **描述** | CLI playground 与 Demo Web 的 `app.js`/`app.css` 不同步，`playground_asset_drift_guard.sh` 检测到漂移并失败。由于 `set -euo pipefail`，后续多个子门禁未被执行。 |
| **修复** | 重新从 CLI Frontend 构建并同步到 Demo Web wwwroot，或反向同步。确保 `architecture_guards.sh` 全量通过。 |

---

### Medium

#### M1. 内部 Token 非恒定时间比较

| | |
|---|---|
| **文件** | `tools/Aevatar.Tools.Cli/Hosting/NyxIdAppAuthentication.cs:340-348` |
| **描述** | `IsTrustedInternalRequest` 使用 `string.Equals(token, credentials.Token, StringComparison.Ordinal)` 进行 token 比较，非恒定时间。虽然 token 为 32 字节随机 hex（实际风险低），但违反安全编码最佳实践。 |
| **修复** | 使用 `CryptographicOperations.FixedTimeEquals(...)` 替换。 |

#### M2. `AppScopedScriptService` dual-dispatch 违反传输载体可替换原则

| | |
|---|---|
| **文件** | `tools/Aevatar.Tools.Cli/Hosting/AppScopedScriptService.cs`（583 行，8 处 if-else 分支） |
| **描述** | 每个方法都有 `if (_port != null) { 走本地端口 } else { 走 HTTP }` 的双分派模式。这违反了"上层依赖投递契约，不依赖具体载体"的原则。`AppScopedWorkflowService`（438 行）也有类似模式。 |
| **修复** | 抽取统一的 `IScopedScriptServicePort` 接口，本地模式和远程模式各提供一个实现，通过 DI 注入而非运行时 if-else 切换。 |

#### M3. MassTransit Subscriber Provider 进程内 Dictionary + Lock

| | |
|---|---|
| **文件** | `src/Aevatar.Foundation.Runtime.Streaming.Implementations.MassTransit/Streaming/MassTransitActorEventSubscriptionProvider.cs:11-13` |
| **描述** | `private readonly Lock _gate` + `private readonly Dictionary<string, List<Subscriber>> _subscribers` 在 singleton 基础设施服务中作为事实态。虽属 Infrastructure 层且有 `IAsyncDisposable` 清理，但仍是进程内 actorId → context 映射。 |
| **修复** | 如需保留，显式标注为 allowed exception 并加入 CI allowlist。长期应迁移到 lease/session 句柄模式。 |

#### M4. `ScriptId` API 字段双重语义

| | |
|---|---|
| **文件** | `AppScopeScriptSaveRequest`（通过 `AppStudioEndpoints.cs` 暴露） |
| **描述** | `ScriptId` 字段空值表示"自动生成新 ID"，非空表示"按此 ID 查找已有 script"。一个字段承载"创建"和"更新"两种语义，违反 API 字段单一语义原则。 |
| **修复** | 拆分为 `POST /scripts`（创建，无需 ID）和 `PUT /scripts/{scriptId}`（更新，ID 在路径中）。或在 request body 中显式增加 `operation: create | update` 字段。 |

#### M5. Command 成功与 ReadModel 新鲜度耦合

| | |
|---|---|
| **文件** | `AppScopedScriptService.SaveAsync`、`AppStudioEndpoints.cs` 保存链路 |
| **描述** | Script 保存成功后立即读回 detail 作为响应。命令的 accepted ACK 被当作 readmodel 已物化的保证，违反"ACK 语义必须诚实"和"readmodel 可以最终一致"原则。 |
| **修复** | 保存成功后返回 command receipt（含 commandId + accepted 状态）。前端通过轮询或 WebSocket 推送获取最终物化结果。 |

#### M6. `AppStudioEndpoints.cs` 939 行单文件膨胀

| | |
|---|---|
| **文件** | `tools/Aevatar.Tools.Cli/Hosting/AppStudioEndpoints.cs` |
| **描述** | 近千行的单一端点注册文件，混合了 script 管理、workflow 管理、evolution、catalog、runtime snapshot 等多种 capability 的路由定义。难以审查和维护。 |
| **修复** | 按 capability 拆分为 `ScriptEndpoints.cs`、`WorkflowEndpoints.cs`、`EvolutionEndpoints.cs` 等，使用 ASP.NET `MapGroup` 组织。 |

#### M7. `codecov.yml` 关闭 Patch 覆盖率门禁

| | |
|---|---|
| **文件** | `codecov.yml` |
| **描述** | 新增 `patch: status: off`，关闭了变更行覆盖率检查。这使得新代码可以无测试覆盖地合入。 |
| **修复** | 如仅针对自动生成代码，使用 `ignore` 路径排除而非全局关闭。否则恢复 patch 覆盖率检查。 |

---

### Low

#### L1. `ProjectionObservationSubscriber` 使用 `SemaphoreSlim`

| | |
|---|---|
| **文件** | `src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionObservationSubscriber.cs:6` |
| **描述** | `SemaphoreSlim _gate` 守护流订阅的 attach/detach 生命周期。如果由异步回调调用，违反"无锁优先"原则。 |

#### L2. 6 个近空 Projection Port 类

| | |
|---|---|
| **文件** | `src/platform/Aevatar.GAgentService.Projection/Orchestration/Service*ProjectionPort.cs`（6 个文件） |
| **描述** | 每个文件仅一行 `EnsureProjectionAsync(...)` → `EnsureProjectionCoreAsync(...)`，是为 DI 类型区分而存在的纯转发壳。虽然模式上合理，但接近"不保留无效层"的边界。 |

#### L3. 前端 React 单文件 5998 行

| | |
|---|---|
| **文件** | `tools/Aevatar.Tools.Cli/Frontend/src/App.tsx` |
| **描述** | 整个 Studio UI 在一个 React 组件文件中。不影响后端架构，但维护困难。 |

#### L4. CI 门禁弱化 — 文件存在性静默跳过

| | |
|---|---|
| **文件** | `tools/ci/architecture_guards.sh` |
| **描述** | 新增 `[ -f ... ]` 检查使得 `ProjectionSubscriptionRegistry` 字典守卫在文件被删除时静默通过，削弱了门禁效力。 |

#### L5. CI 脚本中硬编码密码

| | |
|---|---|
| **文件** | `tools/ci/distributed_3node_smoke.sh`、`distributed_mixed_version_smoke.sh`、`orleans_3node_real_env_smoke.sh` |
| **描述** | `NEO4J_PASSWORD="password"` 硬编码。CI 脚本可接受，但应加注释说明仅限 CI/本地开发。 |

#### L6. CLI 工具版本跳跃

| | |
|---|---|
| **文件** | `tools/Aevatar.Tools.Cli/Aevatar.Tools.Cli.csproj` |
| **描述** | 版本从 `0.0.1` 直接跳到 `1.0.3`，跳过了中间版本。 |

---

## 6. 关键加分项

| # | 描述 | 价值 |
|---|------|------|
| +1 | **StateMirror 整体删除** — 删除 `Aevatar.CQRS.Projection.StateMirror` 项目，消除了 JSON 序列化违规和双轨投影风险 | 显著降低架构债 |
| +2 | **`EnsureProjectedRuntimeAsync` 移除** — 从 `ScriptBehaviorRuntimeCapabilities` 中删除内联 projection priming | 修复 query-time priming 违规 |
| +3 | **Scope 强类型化贯通** — `scope_id` 从 bag/header 升级为 proto field，贯穿 scripting/workflow/projection/readmodel 全链路 | 核心语义强类型化典范 |
| +4 | **`ProjectReadModel` → `BuildReadModel` 语义重构** — 将 per-event 投影改为 state-level 投影，API 签名更准确 | 减少 readmodel 重算复杂度 |
| +5 | **Proto 新文件设计规范** — `projection_scope_messages.proto` 全部使用强类型字段、typed enum、typed sub-message，无 bag | 模范 proto 设计 |
| +6 | **新增 CI 门禁** — scripting readmodel projection、query port splits 等新门禁覆盖新增链路 | 可验证性提升 |

---

## 7. 与 Codex 评分卡的主要分歧

| 维度 | Codex (92/A) | 本次 (79/B+) | 分歧原因 |
|------|-------------|-------------|----------|
| 门禁通过 | 全部 PASS | `architecture_guards.sh` FAIL | Codex 可能基于更早 commit 审计，playground asset drift 在后续提交中引入 |
| 安全审计 | 未覆盖 | 发现 C1 Critical (IDOR) + H1 (hardcoded credentials) | Codex 评分卡未包含安全维度 |
| 构建确定性 | 未覆盖 | 发现 H2 (`rollForward` 问题) | Codex 未审计 `global.json` 变更 |
| 前端/配置 | 未覆盖 | 覆盖 Docker/codecov/CI 配置 | 本次审计范围更广 |
| 代码膨胀 | 提及但未扣分 | M6 (939 行端点) + M2 (dual-dispatch) | 本次更严格评判工具层架构债 |

---

## 8. 合并建议

### 必须修复（阻断合并）
1. **C1** — Scope Header Injection IDOR
2. **H3** — Playground asset drift 导致 CI 主门禁失败

### 强烈建议修复
3. **H1** — 从版本控制中移除硬编码密码
4. **H2** — 恢复 `global.json` rollForward 为 `latestPatch`

### 建议跟进（可在后续 PR 处理）
5. **M2** — Dual-dispatch 抽取统一接口
6. **M4** — ScriptId 双重语义拆分
7. **M5** — Command/ReadModel 新鲜度解耦
8. **M6** — 端点文件拆分
9. **M7** — 恢复 patch 覆盖率或精确排除
