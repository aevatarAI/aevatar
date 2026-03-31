# 审计评分卡：`feature/app-services` vs `dev`

| 项目 | 值 |
|---|---|
| 审计日期 | `2026-03-31` |
| 审计分支 | `feature/app-services` |
| 审计基线 | `dev` |
| 审计提交 | `ec8c78c1` |
| 审计范围 | `dev...HEAD` 的已提交增量 |
| 审计方法 | `git diff` 热区抽样 + 门禁/构建/测试验证 + 关键文件人工复核 |
| 变更规模 | `1312 files changed, +187625 / -45098` |
| 提交数 | `271` |
| 审计说明 | 工作区当前存在未提交改动；凡是可能受脏工作区影响的结论，均已用 `HEAD` 快照在临时目录里复现确认 |

---

## 1. 客观验证结果

| 命令 | 结果 |
|---|---|
| `git diff --shortstat dev...HEAD` | `1312 files changed, 187625 insertions(+), 45098 deletions(-)` |
| `dotnet build aevatar.slnx --nologo` | `PASS`，`0 error / 6 warnings` |
| `dotnet test test/Aevatar.Tools.Cli.Tests/Aevatar.Tools.Cli.Tests.csproj --nologo` | `PASS`，`70` 个测试通过 |
| `dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo` | `PASS`，`309` 个测试通过 |
| `bash tools/ci/test_stability_guards.sh` | `FAIL`，命中 `test/Aevatar.Tools.Cli.Tests/AppPlaygroundHostTests.cs:39` 的 `Task.Delay(100)` |
| `bash tools/ci/architecture_guards.sh` | `FAIL`，前置的 query/projection/workflow 子守卫通过，但在 CLI playground 资源一致性阶段退出 |
| `HEAD` 快照下执行 `pnpm -C <tmp-frontend> exec tsc -b` | `FAIL`，`tools/Aevatar.Tools.Cli/Frontend/src/App.tsx` 存在多处 `TS6133` 未使用符号 |
| `HEAD` 快照下执行 `vite build` 后比较 demo 资源 | `FAIL`，生成的 `app.js` 与 `demos/Aevatar.Demos.Workflow.Web/wwwroot/app.js` 不一致；`index.html` 一致 |

补充说明：

1. `dotnet build` 与两组后端定向测试都通过，说明主干后端运行路径没有出现立即可见的编译或单测回归。
2. 当前阻断项主要集中在 `tools/Aevatar.Tools.Cli` 的前端构建链和新增 playground 测试的稳定性约束。
3. 为排除工作区脏文件干扰，CLI 前端相关验证均对 `HEAD` 版本的 `tools/Aevatar.Tools.Cli/Frontend/src/App.tsx` 做了单独复现。

---

## 2. 整体评分

| 维度 | 权重 | 得分 | 扣分 | 说明 |
|---|---:|---:|---:|---|
| 分层与依赖反转 | 20 | 18 | -2 | 抽样的 `Workflow/GAgentService/Projection` 边界总体仍符合仓库约束 |
| CQRS 与统一投影链路 | 15 | 14 | -1 | 已执行的 query/projection/workflow 相关守卫均通过 |
| 构建与门禁可通过性 | 25 | 10 | -15 | CLI 前端 `tsc` 失败、demo 资源漂移、测试稳定性门禁失败，都是合并阻断项 |
| 测试可信度 | 15 | 11 | -4 | 两组关键测试通过，但新增测试使用轮询等待，破坏仓库强制门禁 |
| 可维护性 | 15 | 12 | -3 | CLI 前端主文件过大，且当前存在未接线/未消费的死代码；生产构建输出 `app.js` 约 `4.39 MB` |
| 文档与可验证性 | 10 | 9 | -1 | 文档更新覆盖面很大，但生成资产与源码不一致，削弱可验证性 |
| **总计** | **100** | **74** | **-26** | |

**等级：`B-`**

**合并建议：`暂不建议合并`**

---

## 3. 分模块评分

| 模块 | 分数 | 结论 |
|---|---:|---|
| `tools/Aevatar.Tools.Cli/Frontend` | 56 | 当前是主要风险面；严格类型检查不过，且生成产物与 demo 静态资源不一致 |
| `tools/Aevatar.Tools.Cli` 后端/宿主 | 80 | 定向单测通过，主机与命令路径没有直接编译/单测失败 |
| `Workflow Host / Projection` | 88 | 构建通过、定向测试通过、专项架构守卫无新增回退 |
| `Docs / Architecture` | 90 | 文档同步范围广，架构说明与落地记录充足 |

---

## 4. 主要发现

### 4.1 [P1] CLI 前端在 `HEAD` 上无法通过严格 TypeScript 构建

**证据**

1. [tools/ci/playground_asset_drift_guard.sh](/Users/chronoai/Code/aevatar/tools/ci/playground_asset_drift_guard.sh#L70) 到 [tools/ci/playground_asset_drift_guard.sh](/Users/chronoai/Code/aevatar/tools/ci/playground_asset_drift_guard.sh#L72) 会先执行 `pnpm ... exec tsc -b`。
2. [tools/Aevatar.Tools.Cli/Frontend/tsconfig.json](/Users/chronoai/Code/aevatar/tools/Aevatar.Tools.Cli/Frontend/tsconfig.json#L17) 到 [tools/Aevatar.Tools.Cli/Frontend/tsconfig.json](/Users/chronoai/Code/aevatar/tools/Aevatar.Tools.Cli/Frontend/tsconfig.json#L21) 打开了 `noUnusedLocals` 和 `noUnusedParameters`。
3. `HEAD` 快照复现时，`tools/Aevatar.Tools.Cli/Frontend/src/App.tsx` 在以下位置产生 `TS6133`：
   [App.tsx](/Users/chronoai/Code/aevatar/tools/Aevatar.Tools.Cli/Frontend/src/App.tsx#L702),
   [App.tsx](/Users/chronoai/Code/aevatar/tools/Aevatar.Tools.Cli/Frontend/src/App.tsx#L720),
   [App.tsx](/Users/chronoai/Code/aevatar/tools/Aevatar.Tools.Cli/Frontend/src/App.tsx#L879),
   [App.tsx](/Users/chronoai/Code/aevatar/tools/Aevatar.Tools.Cli/Frontend/src/App.tsx#L1005),
   [App.tsx](/Users/chronoai/Code/aevatar/tools/Aevatar.Tools.Cli/Frontend/src/App.tsx#L1006),
   [App.tsx](/Users/chronoai/Code/aevatar/tools/Aevatar.Tools.Cli/Frontend/src/App.tsx#L2993),
   [App.tsx](/Users/chronoai/Code/aevatar/tools/Aevatar.Tools.Cli/Frontend/src/App.tsx#L3016),
   [App.tsx](/Users/chronoai/Code/aevatar/tools/Aevatar.Tools.Cli/Frontend/src/App.tsx#L3033),
   [App.tsx](/Users/chronoai/Code/aevatar/tools/Aevatar.Tools.Cli/Frontend/src/App.tsx#L3249),
   [App.tsx](/Users/chronoai/Code/aevatar/tools/Aevatar.Tools.Cli/Frontend/src/App.tsx#L3287)。

**影响**

1. `playground_asset_drift_guard.sh` 在真正比较资源前就会失败，CLI playground 的静态资源一致性无法进入下一步验证。
2. 这不是 lint 风格问题，而是显式打开的编译约束被当前提交破坏；任何依赖 `tsc -b` 的本地或 CI 路径都会被阻断。

**建议**

1. 删除未接线的 provider 管理辅助函数与状态，或把它们真正接入 UI。
2. 在修复前，不要把 `tools/Aevatar.Tools.Cli/Frontend` 当作“已验证可发布”的前端入口。

### 4.2 [P1] demo playground 的已提交静态资源与前端源码生成结果不一致

**证据**

1. [tools/ci/playground_asset_drift_guard.sh](/Users/chronoai/Code/aevatar/tools/ci/playground_asset_drift_guard.sh#L74) 到 [tools/ci/playground_asset_drift_guard.sh](/Users/chronoai/Code/aevatar/tools/ci/playground_asset_drift_guard.sh#L77) 明确要求把 CLI 前端生成的 `app.js` 与 demo `wwwroot/app.js` 做字节级比较。
2. 使用 `HEAD` 快照前端源码单独 `vite build` 后，生成的 `app.js` 与 [demos/Aevatar.Demos.Workflow.Web/wwwroot/app.js](/Users/chronoai/Code/aevatar/demos/Aevatar.Demos.Workflow.Web/wwwroot/app.js#L1) 比较结果为不一致。
3. 复现时两者大小分别为 `4,393,504` 字节与 `4,300,027` 字节，SHA-256 分别为 `932b752499ab7ddb92e4181a4190b5b8be85d1f9cc492313518881de38a5a75b` 与 `ffbfce527a1bee260ca85adebfdcbf6ae8ac3431e5f4700a42df8fd95970568b`。

**影响**

1. 即使先修掉 `tsc -b` 的未使用符号问题，这条 guard 仍会在资源比对阶段失败。
2. demo Web 与 CLI playground 不再共享同一份可复现前端产物，后续排障时会出现“源码行为”和“演示行为”不一致的问题。

**建议**

1. 重新生成并提交与 `HEAD` 前端源码一致的 demo `wwwroot` 资源。
2. 如果两者不再要求完全同构，应先修改 guard 与文档，而不是让仓库处于“规则要求一致、实际提交不一致”的状态。

### 4.3 [P1] 新增 playground host 测试违反强制测试稳定性门禁

**证据**

1. [test/Aevatar.Tools.Cli.Tests/AppPlaygroundHostTests.cs](/Users/chronoai/Code/aevatar/test/Aevatar.Tools.Cli.Tests/AppPlaygroundHostTests.cs#L28) 到 [test/Aevatar.Tools.Cli.Tests/AppPlaygroundHostTests.cs](/Users/chronoai/Code/aevatar/test/Aevatar.Tools.Cli.Tests/AppPlaygroundHostTests.cs#L40) 通过循环 + `Task.Delay(100)` 轮询 `/api/health` 等待 host 启动。
2. 仓库显式要求测试里禁止随意引入 `Task.Delay(...)` 轮询等待；`bash tools/ci/test_stability_guards.sh` 当前已经因此直接失败。

**影响**

1. 当前分支无法通过仓库强制测试门禁，属于明确的合并阻断项。
2. 该测试即便偶然“能跑过”，也仍然会把 host 启动时序的不确定性引入测试结果，和仓库要求的确定性同步点方向相反。

**建议**

1. 改成 `TaskCompletionSource`、`Channel`、显式就绪信号或更窄的宿主探针，而不是用固定睡眠轮询。
2. 若确实无法避免跨进程最终一致性探测，至少先补充 `tools/ci/test_polling_allowlist.txt` 与变更说明；但从当前场景看，优先级仍应是改成确定性同步。

---

## 5. 阻断项

1. 修复 `tools/Aevatar.Tools.Cli/Frontend/src/App.tsx` 的 `TS6133` 未使用符号，恢复 `pnpm ... tsc -b` 可通过。
2. 重新对齐 `demos/Aevatar.Demos.Workflow.Web/wwwroot/app.js` 与 CLI 前端 `HEAD` 源码生成产物，或同步修改 guard 与约束。
3. 移除 `AppPlaygroundHostTests` 中的 `Task.Delay` 轮询等待，恢复 `bash tools/ci/test_stability_guards.sh`。

---

## 6. 加分项

1. `dotnet build aevatar.slnx --nologo` 成功，说明后端主解空间没有新增编译断裂。
2. `Aevatar.Tools.Cli.Tests` 与 `Aevatar.Workflow.Host.Api.Tests` 的定向回归都通过，说明非前端主链并未出现直接单测回退。
3. `architecture_guards.sh` 在进入 CLI playground 资源阶段前，query/projection/workflow 相关子守卫均已通过，说明仓库最核心的架构约束没有明显回潮。

---

## 7. 非阻断观察项

1. [tools/Aevatar.Tools.Cli/Frontend/src/App.tsx](/Users/chronoai/Code/aevatar/tools/Aevatar.Tools.Cli/Frontend/src/App.tsx#L1) 当前约 `6395` 行，已经把多块工作台能力挤进单文件；这不是本次唯一 blocker，但会持续拉高前端回归成本。
2. `vite build` 会给出 chunk 过大警告；当前 `app.js` 生成体量约 `4.39 MB`，后续最好切分路由或按功能拆包。
3. `src/Aevatar.NyxId.Chat/Aevatar.NyxId.Chat.csproj` 在 `dotnet build` 中出现 `NU1510`，虽然不阻断，但说明依赖清理还有尾项。

---

## 8. 结论

这条分支相对 `dev` 的改动规模非常大，但从这次抽样审计结果看，真正的阻断点并不在 `Workflow/Projection` 主链，而是集中在 `tools/Aevatar.Tools.Cli` 的前端发布链和新增 playground 测试的稳定性约束上。后端构建与定向测试给出的信号总体是正向的，但当前还不能把这条分支按“可安全合并”口径放行。

综合评分：**`74/100`，`B-`**。在修复前端 `tsc` 失败、demo 资源漂移和测试稳定性门禁失败之前，不建议合并到 `dev`。
