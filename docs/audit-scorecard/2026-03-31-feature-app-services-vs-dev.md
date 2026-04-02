# 审计评分卡：`feature/app-services` vs `dev`

| 项目 | 值 |
|---|---|
| 审计日期 | `2026-03-31` |
| 审计分支 | `feature/app-services` |
| 审计基线 | `dev` |
| 审计提交 | `cae36b3e` |
| 审计范围 | `dev...HEAD` 的已提交增量 |
| 审计方法 | `HEAD` 干净 worktree 复验 + 门禁/构建校验 + 高风险模块人工审查 |
| 变更规模 | `1395 files changed, +203628 / -45113` |
| 提交数 | `285` |
| 审计说明 | 当前工作区有未提交改动；本次验证和代码阅读均在 `/tmp/aevatar-review` 的 `HEAD` worktree 完成，避免把脏工作区混入结论 |

---

## 1. 客观验证结果

| 命令 | 结果 |
|---|---|
| `git diff --shortstat dev...HEAD` | `1395 files changed, 203628 insertions(+), 45113 deletions(-)` |
| `dotnet test test/Aevatar.Tools.Cli.Tests/Aevatar.Tools.Cli.Tests.csproj --nologo` | `PASS`，`70` 个测试通过 |
| `pnpm -C tools/Aevatar.Tools.Cli/Frontend exec tsc -b` | `PASS` |
| `pnpm -C tools/Aevatar.Tools.Cli/Frontend exec vite build --outDir /tmp/aevatar-review/.tmp-playground` | `PASS`，但 `app.js` 体积约 `4.39 MB`，触发 chunk size warning |
| `bash tools/ci/playground_asset_drift_guard.sh` | `FAIL`，生成的 playground `app.js` 与 demo `wwwroot/app.js` 不一致 |
| `bash tools/ci/architecture_guards.sh` | `FAIL`，query/projection/workflow 相关子守卫通过，但最终被 `playground_asset_drift_guard.sh` 阻断 |

补充说明：

1. `CLI` 定向后端测试、`tsc -b` 和 `vite build` 都通过，说明这轮分支的显式回归不在基础编译链，而在运行路径与交付一致性。
2. `dotnet test` 输出中仍然给出 [AppScopedWorkflowService.cs](/Users/chronoai/Code/aevatar/src/Aevatar.Studio.Application/AppScopedWorkflowService.cs#L114) 的 `CS8602` 告警，这和下面的人工审查发现一致。
3. 前端校验前在干净 worktree 里执行了 `pnpm install --no-frozen-lockfile`，因为 [tools/Aevatar.Tools.Cli/Frontend](/Users/chronoai/Code/aevatar/tools/Aevatar.Tools.Cli/Frontend) 当前没有 `pnpm-lock.yaml`。

---

## 2. 整体评分

| 维度 | 权重 | 得分 | 扣分 | 说明 |
|---|---:|---:|---:|---|
| 正确性与运行安全 | 25 | 11 | -14 | 存在可触发的空引用路径，以及 scope/global 资源边界混淆 |
| 架构约束符合度 | 20 | 12 | -8 | 主干 projection/query 守卫通过，但新引入的 external link 运行态突破了 Actor 回调只发信号的约束 |
| 构建与门禁可通过性 | 20 | 14 | -6 | `tsc` / `vite build` 通过，但 playground 资产一致性门禁失败，仍是合并阻断项 |
| 测试覆盖与回归信号 | 15 | 9 | -6 | `CLI` 测试通过，但这次抓到的几个关键问题没有对应覆盖 |
| 可维护性 | 10 | 6 | -4 | 变更面极大，前端 bundle 偏大，关键宿主类耦合度高 |
| 交付卫生 | 10 | 6 | -4 | demo 产物与源码不一致，分支相对 `dev` 的体量也显著放大审查风险 |
| **总计** | **100** | **58** | **-42** | |

**等级：`C`**

**合并建议：`不建议直接合并`**

---

## 3. 主要发现

### 3.1 [P1] `AppScopedWorkflowService` 在 artifact fallback 路径上存在可触发的空引用

**证据**

1. [AppScopedWorkflowService.cs](/Users/chronoai/Code/aevatar/src/Aevatar.Studio.Application/AppScopedWorkflowService.cs#L105) 到 [AppScopedWorkflowService.cs](/Users/chronoai/Code/aevatar/src/Aevatar.Studio.Application/AppScopedWorkflowService.cs#L115) 进入 fallback 后，只检查了 `_workflowQueryPort != null`，却直接执行 `await _serviceLifecycleQueryPort?.GetServiceAsync(...)`。
2. 在 C# 里对 `Task?` 做 `await`，当左值为 `null` 时会在运行时抛 `NullReferenceException`；这也是本次 `dotnet test` 构建阶段给出 `CS8602` 的原因。
3. [StudioHostingServiceCollectionExtensions.cs](/Users/chronoai/Code/aevatar/src/Aevatar.Studio.Hosting/StudioHostingServiceCollectionExtensions.cs#L37) 到 [StudioHostingServiceCollectionExtensions.cs](/Users/chronoai/Code/aevatar/src/Aevatar.Studio.Hosting/StudioHostingServiceCollectionExtensions.cs#L44) 明确通过 `GetService<IServiceLifecycleQueryPort>()` 可选注入这个依赖，所以空值不是理论路径，而是容器允许出现的真实配置。

**影响**

1. 当 binding projection 里还没有 `WorkflowYaml`，且当前宿主没有注册 `IServiceLifecycleQueryPort` 时，打开 workflow 详情会从“回退读取 artifact”直接变成 500。
2. 这个故障发生在 Studio 读取链路，而不是后台管理链路，用户触达概率高。

**建议**

1. 把 fallback 条件改成显式要求 `_serviceLifecycleQueryPort != null`，或者在空值时直接跳过这一步。
2. 补一个覆盖“`binding.WorkflowYaml` 为空且 lifecycle query port 未注册”场景的单测到 [AppScopedWorkflowServiceTests.cs](/Users/chronoai/Code/aevatar/test/Aevatar.Tools.Cli.Tests/AppScopedWorkflowServiceTests.cs#L11)。

### 3.2 [P1] `Config Explorer` 用 scope 视图包装了全局 workflows/scripts API，容易读错或误操作跨 scope 资源

**证据**

1. [ConfigExplorerPage.tsx](/Users/chronoai/Code/aevatar/tools/Aevatar.Tools.Cli/Frontend/src/config-explorer/ConfigExplorerPage.tsx#L16) 到 [ConfigExplorerPage.tsx](/Users/chronoai/Code/aevatar/tools/Aevatar.Tools.Cli/Frontend/src/config-explorer/ConfigExplorerPage.tsx#L17) 以 `scopeId` 初始化 store，并在 [FileTree.tsx](/Users/chronoai/Code/aevatar/tools/Aevatar.Tools.Cli/Frontend/src/config-explorer/FileTree.tsx#L31) 到 [FileTree.tsx](/Users/chronoai/Code/aevatar/tools/Aevatar.Tools.Cli/Frontend/src/config-explorer/FileTree.tsx#L49) 把当前 scope 渲染为根目录。
2. 但 [useConfigStore.ts](/Users/chronoai/Code/aevatar/tools/Aevatar.Tools.Cli/Frontend/src/config-explorer/useConfigStore.ts#L73) 到 [useConfigStore.ts](/Users/chronoai/Code/aevatar/tools/Aevatar.Tools.Cli/Frontend/src/config-explorer/useConfigStore.ts#L82) 拉取 workflows/scripts 时调用的是全局接口：`api.workspace.listWorkflows()` 和 `api.app.listScripts(true)`。
3. 相应的 API 定义也确实是全局路径：[api.ts](/Users/chronoai/Code/aevatar/tools/Aevatar.Tools.Cli/Frontend/src/api.ts#L251) 到 [api.ts](/Users/chronoai/Code/aevatar/tools/Aevatar.Tools.Cli/Frontend/src/api.ts#L253) 走 `/workspace/workflows`，而 [api.ts](/Users/chronoai/Code/aevatar/tools/Aevatar.Tools.Cli/Frontend/src/api.ts#L685) 到 [api.ts](/Users/chronoai/Code/aevatar/tools/Aevatar.Tools.Cli/Frontend/src/api.ts#L686) 走 `/app/scripts`。
4. 选中 workflow 后，[useConfigStore.ts](/Users/chronoai/Code/aevatar/tools/Aevatar.Tools.Cli/Frontend/src/config-explorer/useConfigStore.ts#L160) 到 [useConfigStore.ts](/Users/chronoai/Code/aevatar/tools/Aevatar.Tools.Cli/Frontend/src/config-explorer/useConfigStore.ts#L169) 仍然使用全局 `api.workspace.getWorkflow(wfId)`，没有把 `scopeId` 带入读取路径。

**影响**

1. 当前 UI 语义是“我正在看这个 scope 的 storage”，但 workflows/scripts 实际上不是按 scope 过滤出来的，容易让用户把全局资源误认为当前 scope 资源。
2. 一旦不同 scope 下存在同名 workflow/script，当前页面会出现“列表、预览、跳转 Studio”三者都没有明确作用域的错误匹配风险。

**建议**

1. 如果 workflows/scripts 本来就应该是 scope-scoped，就改成真正的 scope API。
2. 如果它们设计上就是全局资源，就不要把它们挂在当前 scope 根目录下，至少要在 UI 上单独标明全局命名空间。

### 3.3 [P1] playground 生成产物与 demo 静态资源已发生漂移，当前分支过不了仓库自带门禁

**证据**

1. [playground_asset_drift_guard.sh](/Users/chronoai/Code/aevatar/tools/ci/playground_asset_drift_guard.sh#L70) 到 [playground_asset_drift_guard.sh](/Users/chronoai/Code/aevatar/tools/ci/playground_asset_drift_guard.sh#L89) 会先重新构建 CLI 前端，再把生成的 `app.js` 与 demo 的 [app.js](/Users/chronoai/Code/aevatar/demos/Aevatar.Demos.Workflow.Web/wwwroot/app.js#L1) 做字节级比较。
2. 本次复验里，生成文件 `/tmp/aevatar-review/.tmp-playground/app.js` 与 demo 文件大小分别是 `4,395,061` 和 `4,394,343` 字节，SHA-256 分别是 `6f3bfe04f1dc7453157080854a2e6aa72ea792275e2195e40467acd09fcd8bd8` 与 `d70b106fbd144e37ab85a7b8ba560774690a54dd702e4fb1a49d87163d27e0f6`。
3. `bash tools/ci/playground_asset_drift_guard.sh` 与 `bash tools/ci/architecture_guards.sh` 都因此失败。

**影响**

1. 这是明确的合并阻断项，不是风格问题。
2. demo Web 和 CLI playground 不再共享同一份源码生成结果，后续一旦出现“demo 能复现 / CLI 不能复现”会明显增加排障成本。

**建议**

1. 重新生成并提交 demo `wwwroot` 资源，或者明确取消这条同构约束并同步修改 guard。
2. 在这条 guard 通过前，不要把当前 demo 视为 `HEAD` 行为的可信镜像。

### 3.4 [P2] External link 运行态直接在回调线程和 `Task.Run` 里改 Actor 状态，和仓库的 Actor 化执行约束冲突

**证据**

1. [ExternalLinkManager.cs](/Users/chronoai/Code/aevatar/src/Aevatar.Foundation.Core/ExternalLinks/ExternalLinkManager.cs#L122) 到 [ExternalLinkManager.cs](/Users/chronoai/Code/aevatar/src/Aevatar.Foundation.Core/ExternalLinks/ExternalLinkManager.cs#L126) 用 `Task.Run` 启动重连循环。
2. 同一个文件里，[ExternalLinkManager.cs](/Users/chronoai/Code/aevatar/src/Aevatar.Foundation.Core/ExternalLinks/ExternalLinkManager.cs#L133) 到 [ExternalLinkManager.cs](/Users/chronoai/Code/aevatar/src/Aevatar.Foundation.Core/ExternalLinks/ExternalLinkManager.cs#L177) 直接在后台循环里修改 `ReconnectAttempt`、`IsConnected`，并发布 reconnect/connected 事件。
3. [ExternalLinkManager.cs](/Users/chronoai/Code/aevatar/src/Aevatar.Foundation.Core/ExternalLinks/ExternalLinkManager.cs#L205) 到 [ExternalLinkManager.cs](/Users/chronoai/Code/aevatar/src/Aevatar.Foundation.Core/ExternalLinks/ExternalLinkManager.cs#L252) 又在 transport 回调里直接修改 `IsConnected` / `IsClosed`。
4. 当前仓库里没有任何针对 `ExternalLink` / `WebSocketTransport` / `IExternalLinkAware` 的测试覆盖，`rg` 在 `test/` 下返回空结果。

**影响**

1. 这套实现把连接事实放在 actor 外围的后台线程里推进，而不是通过 self-message 回到 actor inbox 再推进，和仓库明确要求的“回调只发信号、业务推进内聚到 Actor 事件处理线程”相反。
2. 一旦和 `DeactivateAsync`、`DisconnectAsync` 或 actor 自身事件处理交叉，存在陈旧 reconnect 事件、状态竞态和重复推进的风险。

**建议**

1. transport 回调只负责投递内部事件，运行态变更和重连决策回到 actor 自身 inbox 里完成。
2. 至少补一组针对断连/重连/停机的并发行为测试，再考虑把这套能力向主干宿主开放。

---

## 4. 阻断项

1. 修掉 [AppScopedWorkflowService.cs](/Users/chronoai/Code/aevatar/src/Aevatar.Studio.Application/AppScopedWorkflowService.cs#L114) 的空引用路径，并补覆盖测试。
2. 明确 `Config Explorer` 的资源作用域：要么切 scope API，要么把 workflows/scripts 从当前 scope 根目录语义中拆出去。
3. 让 `bash tools/ci/playground_asset_drift_guard.sh` 恢复通过，重新对齐 demo 静态资源。

---

## 5. 加分项

1. `CLI` 后端定向测试通过，说明宿主/API 主链没有出现立即可见的编译或回归断裂。
2. `tsc -b` 与 `vite build` 当前都能过，说明前端源码本身不是“完全不可构建”的状态。
3. `architecture_guards.sh` 在进入 playground 资产比对前，query/projection/workflow 相关子守卫都已经通过，主干核心架构约束没有明显回退。

---

## 6. 结论

这条分支相对 `dev` 的改动体量已经到了“需要按高风险热区审计”的级别。本次复验里，最值得立刻处理的不是基础构建，而是三个更实的合并风险：Studio workflow 读取链里的空引用、Config Explorer 的 scope 边界混淆，以及 playground/demo 资产已经失配。再叠加 external link 子系统对 Actor 运行模型的突破，当前分支还不适合直接并入 `dev`。

综合评分：**`58/100`，`C`**。在上述阻断项修复前，不建议合并。
