# 审计评分卡：`feature/app-services` vs `dev`

| 项目 | 值 |
|---|---|
| 审计日期 | `2026-03-27` |
| 审计范围 | 当前分支相对 `dev` 的增量变更 |
| 审计方法 | `git diff` 热区审查 + 关键代码路径复核 + 构建/门禁/测试验证 |
| 变更规模 | `1183 files changed, +168376 / -45014` |
| 提交数 | `231` |
| 重点关注 | `scope-first` 服务绑定与调用、`GAgentService`、`Workflow/Scripting`、Projection 主链、console-web 新增面 |

---

## 1. 客观验证结果

| 命令 | 结果 |
|---|---|
| `dotnet build aevatar.slnx --nologo` | `PASS`，`0 error / 20 warnings` |
| `bash tools/ci/architecture_guards.sh` | `PASS` |
| `dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo --filter "FullyQualifiedName~ScopeBindingCommandApplicationServiceTests|FullyQualifiedName~DefaultServiceInvocationDispatcherTests|FullyQualifiedName~ServiceInvocationApplicationServiceTests|FullyQualifiedName~ServiceInvocationResolutionServiceTests"` | `PASS`，`45` 个测试通过 |
| `dotnet test test/Aevatar.GAgentService.Integration.Tests/Aevatar.GAgentService.Integration.Tests.csproj --nologo --filter "FullyQualifiedName~ScopeServiceEndpointsTests"` | `PASS`，`28` 个测试通过 |
| `dotnet test aevatar.slnx --nologo` | `未完成`。可见输出中的各项目测试均为通过/跳过，但命令未自然退出 |
| `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --nologo` | `未完成`。单独运行也停在 `Aevatar.Scripting.Core.Tests` 的 `testhost` |

补充说明：

1. `dotnet test aevatar.slnx --nologo` 运行期间，可见输出已覆盖大部分测试项目且均未报失败。
2. 但进程未退出；随后通过 `ps` 可观察到 `vstest.console.dll` 与 `testhost.dll` 挂在 `Aevatar.Scripting.Core.Tests` 上，说明全量验证不能记为完整通过。

---

## 2. 整体评分

| 维度 | 权重 | 得分 | 扣分 | 说明 |
|---|---:|---:|---:|---|
| 分层与依赖反转 | 20 | 15 | -5 | `scope-first` 新端点默认允许未认证访问，宿主边界存在权限绕过 |
| CQRS 与统一投影链路 | 20 | 19 | -1 | 主链路整体清晰，但新 invoke 路径把可预期业务错误直接暴露成未映射异常 |
| Projection 编排与状态约束 | 20 | 18 | -2 | `binding` 状态汇总对 serving target 的选择规则不符合语义优先级 |
| 读写分离与会话语义 | 15 | 14 | -1 | 读写职责总体正确，Scope/Run 路径保持 command/query 分离 |
| 命名语义与冗余清理 | 10 | 9 | -1 | 大量 rename/清理完成，但 `ScopeServiceEndpoints` 宿主类继续膨胀 |
| 可验证性（门禁/构建/测试） | 15 | 9 | -6 | build/guards 与定向测试通过，但全量测试命令不能完成 |
| **总计** | **100** | **84** | **-16** | |

**等级：`B+`**

---

## 3. 分模块评分

| 模块 | 分数 | 结论 |
|---|---:|---|
| `GAgentService` scope-first endpoints | 78 | 功能覆盖完整，但存在未认证访问与错误映射缺口 |
| `GAgentService` application/runtime | 86 | 服务绑定、分发、激活链路清晰，测试也相对完整 |
| `CQRS / Projection` | 92 | 主链路一致性与门禁质量较好，未见中间层事实态字典回潮 |
| `Workflow / Scripting` | 84 | 语义边界基本正确，但 `Aevatar.Scripting.Core.Tests` 悬挂影响可信度 |
| `console-web` | 88 | 体量大但测试跟进较多；本次未发现阻断后端契约的不一致点 |
| `Docs / CI Guards` | 91 | 架构守卫覆盖面持续扩大，文档同步明显 |

---

## 4. 主要发现

### 4.1 [P1] `scope-first` 服务端点默认允许未认证访问

**证据**

1. `src/platform/Aevatar.GAgentService.Hosting/Endpoints/ScopeServiceEndpoints.cs:27-47` 只注册路由，没有 `RequireAuthorization()`。
2. `src/platform/Aevatar.GAgentService.Hosting/Endpoints/ScopeEndpointAccess.cs:63-64` 在 `IsAuthenticated != true` 时直接 `return false`，等于“未认证不拦截”。
3. `test/Aevatar.GAgentService.Integration.Tests/ScopeServiceEndpointsTests.cs:31-76` 明确验证了未带认证信息的 `PUT /api/scopes/{scopeId}/binding` 能返回 `200 OK`。

**影响**

1. 新增的 `binding`、`activate`、`invoke`、`resume`、`signal`、`stop` 等 scope 级能力在启用了认证中间件的宿主里仍可被匿名调用。
2. 这不是“缺少 scope claim 时返回 403”的问题，而是“匿名请求直接绕过整套 scope guard”的问题。
3. 如果这些路由在 Mainnet Host 或其他多租户宿主暴露，这就是合并前必须处理的安全缺陷。

**建议**

1. 对 `/api/scopes/...` 路由组整体加 `RequireAuthorization()`，再保留当前 scope claim 校验。
2. 或者将 `ScopeEndpointAccess` 改为“未认证即拒绝”，不要把匿名访问当成默认放行路径。

### 4.2 [P2] 普通 scope invoke 请求会把客户端错误直接放大成 `500`

**证据**

1. `src/platform/Aevatar.GAgentService.Hosting/Endpoints/ScopeServiceEndpoints.cs:464-495` 的 `HandleInvokeAsync` 没有错误映射。
2. `src/platform/Aevatar.GAgentService.Hosting/Serialization/ServiceJsonPayloads.cs:8-15` 会因空 `payloadTypeUrl` 或非法 `payloadBase64` 直接抛异常。
3. `src/platform/Aevatar.GAgentService.Application/Services/ServiceInvocationResolutionService.cs:29-51` 会对“服务不存在 / endpoint 不存在 / 无 serving target / artifact 缺失”抛 `InvalidOperationException`。
4. `src/Aevatar.Bootstrap/Hosting/WebApplicationBuilderExtensions.cs:74-99` 没有全局异常到 `4xx` 的统一映射。

**影响**

1. 对于用户可预期的坏请求，当前行为更接近“宿主内部异常”，而不是稳定、诚实的 API 契约。
2. 新增 scope-first 路径把这类失败面直接暴露给外部调用方，前端会收到 `500` 而不是可处理的 `400/404`。

**建议**

1. 在 `HandleInvokeAsync` 对 `InvalidOperationException` / `FormatException` 做显式 `400/404` 映射。
2. 或在宿主层统一引入 problem-details/异常映射中间件，但要确保业务错误不会被统统包装成 `500`。

### 4.3 [P2] `binding` 摘要可能显示错误的 serving target

**证据**

1. `src/platform/Aevatar.GAgentService.Hosting/Endpoints/ScopeServiceEndpoints.cs:919-934` 按 `AllocationWeight` 再按 `ServingState` 的字典序选择一个 target。
2. `ServingState` 是业务语义状态，不是可按字符串字典序排序的展示字段。

**影响**

1. 当同一 `revision` 同时存在 `Active / Paused / Draining / Disabled` 等多个 target 时，返回结果可能优先选中字符串更“靠后”的状态，而不是语义上最应该展示的 target。
2. 这会让 `/api/scopes/{scopeId}/binding` 的 `deploymentId / primaryActorId / servingState` 误导控制台和排障流程。

**建议**

1. 改为显式状态优先级，例如 `Active > Paused > Draining > Disabled > Unspecified`。
2. 如果接口真正想表达“当前主 target”，应基于 serving 语义而不是字典序推断。

---

## 5. 阻断项

1. **必须修复** `scope-first` 路由的未认证访问问题，然后再评估是否可以合并。
2. **必须解释或修复** `Aevatar.Scripting.Core.Tests` 的悬挂问题；当前分支不能被标记为“全量测试通过”。

---

## 6. 加分项

1. `dotnet build aevatar.slnx --nologo` 与 `bash tools/ci/architecture_guards.sh` 均通过，说明主干架构守卫仍然有效。
2. `ScopeBindingCommandApplicationService`、`DefaultServiceInvocationDispatcher`、`ScopeServiceEndpoints` 都补了定向单测/集成测试，新增路径不是“裸功能落地”。
3. 大规模分支合并后，Projection/Workflow/Scripting 主链没有出现明显的 `query-time replay`、中间层事实态字典、或双投影主链回退。

---

## 7. 改进优先级

| 优先级 | 项目 | 说明 |
|---|---|---|
| P1 | 收紧 `/api/scopes/...` 认证准入 | 先保证匿名请求无法修改/调用 scope 资源 |
| P1 | 定位 `Aevatar.Scripting.Core.Tests` 悬挂 | 让 `dotnet test aevatar.slnx --nologo` 可自然完成 |
| P2 | 给 scope invoke 路径补业务错误映射 | 把 `500` 收敛为稳定 `4xx` 契约 |
| P2 | 修正 `binding` 摘要 target 选择规则 | 用显式 serving state 排序替代字典序 |

---

## 8. 非扣分观察项

1. Projection、Workflow、Scripting、Foundation 的大批量重构整体仍满足当前仓库的架构门禁，没有看到明显的反向依赖或 query-time priming 回退。
2. `console-web` 体量很大，但本次抽查到的后端契约消费面基本跟得上，相关页面也补了测试。
3. `ScopeServiceEndpoints` 的耦合度与体积已经很高，当前还不是阻断缺陷，但继续增长会显著降低后续可维护性。

---

## 9. 结论

这条分支在架构主链、构建质量和门禁覆盖上整体是可读、可运行的，绝大多数重构方向也是正向的；但当前不能给高分，原因不是“代码量大”，而是有两类更实质的问题：一类是 `scope-first` 路由的真实权限绕过，另一类是验证层面无法证明“全量测试通过”。

综合评分：**`84/100`，`B+`**。如果只看架构主链会更高，但在修复未认证访问与测试悬挂之前，不建议把这条分支按“可安全合并”口径放行。
