# 2026-03-24 `feature/studio` vs `dev` 自动评分卡

## 1. 审计范围

- **分支**: `feature/studio`
- **基线**: `dev` (`0c4a2efb34e5a7bc017a38fae1ce1169aa4cb83e`)
- **比较口径**: `dev` 对比当前工作树，包含已提交分支内容与未提交本地改动
- **分支关系**: `feature/studio` 相对 `dev` **领先 216 个提交**，`dev` 相对当前分支领先 `0` 个提交
- **当前工作树额外改动**: 10 个 `GAgentService` 测试文件变更，其中 6 个已修改、4 个新增

## 2. 变更规模概览

### 2.1 Diff 规模

| 指标 | 数值 |
|---|---:|
| 变更文件数 | 1121 |
| 新增行数 | 153141 |
| 删除行数 | 44771 |

### 2.2 主要改动面

- `apps/aevatar-console-web`：新增完整 Console Web 工程与大量页面、API client、测试
- `src/Aevatar.Studio.*`：新增 Studio `Domain / Application / Infrastructure / Hosting`
- `tools/Aevatar.Tools.Cli`：新增独立前端、CLI host 与静态资源构建链路
- `src/platform/Aevatar.GAgentService*` 与 `test/Aevatar.GAgentService*`：治理、作用域、服务查询/命令路径扩展
- `docs/` 与 `tools/ci/`：新增/调整文档、守卫和开发辅助脚本

### 2.3 代表性大文件

| 文件 | 行数 |
|---|---:|
| `tools/Aevatar.Tools.Cli/Frontend/src/App.tsx` | 5998 |
| `apps/aevatar-console-web/src/pages/studio/components/StudioWorkbenchSections.tsx` | 5535 |
| `apps/aevatar-console-web/src/pages/studio/index.tsx` | 3766 |

## 3. 客观验证结果

| 验证项 | 结果 | 备注 |
|---|---|---|
| `bash tools/ci/architecture_guards.sh` | **PASS** | worktree diff mode 下通过，`projection` / `binding boundary` / `playground drift` 等守卫均通过 |
| `bash tools/ci/test_stability_guards.sh` | **PASS** | 当前测试改动未引入轮询等待违规 |
| `dotnet build aevatar.slnx --nologo` | **PASS** | 全量构建通过，存在 `16` 条 warning，但无 error |
| `pnpm -C apps/aevatar-console-web tsc --noEmit` | **PASS** | Console Web 类型检查通过 |
| `dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo` | **PASS** | `239/239` 通过 |
| `dotnet test test/Aevatar.GAgentService.Integration.Tests/Aevatar.GAgentService.Integration.Tests.csproj --nologo --filter "FullyQualifiedName~GovernanceEndpointsTests|FullyQualifiedName~ScopeScriptEndpointsTests|FullyQualifiedName~ScopeWorkflowEndpointsTests|FullyQualifiedName~ServiceEndpointsTests"` | **PASS** | `57/57` 通过 |

## 4. 总评

| 总分 | 等级 | 结论 |
|---|---|---|
| **86 / 100** | **A-** | 当前分支已经达到“可构建、关键门禁通过、核心回归路径可验证”的工程水位；但分支跨度过大，且 Studio/Console/CLI 出现明显的文件肥大、组合根耦合和文档口径漂移，继续堆叠需求会明显抬高合并与维护风险。 |

## 5. 六维评分

| 维度 | 分数 | 说明 |
|---|---:|---|
| 分层与依赖反转 | 17 / 20 | 新增 `Aevatar.Studio.Domain/Application/Infrastructure/Hosting`，总体方向符合顶层分层约束；但 `StudioCapabilityExtensions` 组合根已明显偏重。 |
| CQRS 与统一投影链路 | 18 / 20 | 架构门禁通过，未见新的 query-time priming / route mapping 退化；`GAgentService` 相关查询与命令路径已有测试回归。 |
| Projection 编排与状态约束 | 18 / 20 | `architecture_guards` 整体通过，说明核心投影约束仍守住；未发现新的中间层事实态字典或 actorId 反查生命周期模式。 |
| 读写分离与会话语义 | 13 / 15 | 当前抽样到的治理、scope、service 测试路径稳定；但 Studio host 侧开始聚合较多应用服务，边界需要继续收敛。 |
| 命名语义与冗余清理 | 7 / 10 | 命名总体尚可，但仍残留 `5000` 端口示例，且有重复 `using` 与超大文件，说明清理不到位。 |
| 可验证性 | 13 / 15 | build、前端类型检查、关键守卫、定向测试均通过；但没有重新执行 `dotnet test aevatar.slnx --nologo`，全量回归证据还不完整。 |

**总分**: `17 + 18 + 18 + 13 + 7 + 13 = 86 / 100`

## 6. 分模块评分

| 模块 | 分数 | 结论 |
|---|---:|---|
| `Aevatar.Studio.*` | 84 / 100 | 分层结构已经成形，但 Hosting 组合根和服务实现的复杂度开始积压。 |
| `apps/aevatar-console-web` | 82 / 100 | 功能面很全，`tsc` 通过；主要问题是页面文件过大，后续演化成本高。 |
| `tools/Aevatar.Tools.Cli` | 80 / 100 | playground/build 链路已被守卫验证，但 CLI 文档与默认 URL 仍违背端口约束。 |
| `Aevatar.GAgentService*` | 89 / 100 | 当前工作树新增/修改的测试全部通过，治理与 scope/service 相关路径可信度较高。 |
| `Workflow/CQRS Guards` | 91 / 100 | 相对 `dev` 的大规模改动下仍能保持架构门禁绿灯，这是本分支最强的正向信号之一。 |

## 7. 主要加分项

### A1. 大规模改动下仍维持核心门禁通过

- 证据: `bash tools/ci/architecture_guards.sh` 全量通过
- 说明: 在 `1121` 个文件的改动规模下，`projection route mapping`、`binding boundary`、`committed-state projection`、`playground asset drift` 等守卫仍为绿色，说明这条分支没有把核心架构纪律打穿。

### A2. Studio 能力按层拆分，而不是直接塞进单一 Host 项目

- 证据: `src/Aevatar.Studio.Domain`、`src/Aevatar.Studio.Application`、`src/Aevatar.Studio.Infrastructure`、`src/Aevatar.Studio.Hosting`
- 说明: 相对直接在 API/Host 层堆功能，这种拆分更符合仓库的 `Domain / Application / Infrastructure / Host` 约束。

### A3. 当前工作树中的 `GAgentService` 测试变更是可执行的

- 证据: `GAgentService.Tests` `239/239` 通过，`GAgentService.Integration.Tests` 定向 `57/57` 通过
- 说明: 当前未提交的 10 个测试文件修改不是“只改断言不跑验证”的状态，至少治理、scope、service 相关主路径都被实跑过。

## 8. 主要扣分项

### D1. 分支跨度已经明显超出正常 feature 分支的可审阅上限

- 证据: `git rev-list --count dev..HEAD = 216`
- 证据: `git diff --shortstat dev = 1121 files changed, 153141 insertions(+), 44771 deletions(-)`
- 影响: 单次 review 和合并冲突成本都很高，也让局部回归结果难以代表整体质量。

### D2. Studio/Console/CLI 前端出现明显的超大文件堆积

- 证据: `tools/Aevatar.Tools.Cli/Frontend/src/App.tsx` `5998` 行
- 证据: `apps/aevatar-console-web/src/pages/studio/components/StudioWorkbenchSections.tsx` `5535` 行
- 证据: `apps/aevatar-console-web/src/pages/studio/index.tsx` `3766` 行
- 影响: 可维护性、测试粒度和后续重构成本都会持续恶化，这类文件现在已经不是“稍后再拆”的量级。

### D3. Studio Hosting 组合根耦合偏重，且 warning 噪音已经出现

- 证据: `src/Aevatar.Studio.Hosting/StudioCapabilityExtensions.cs:23` 的 `AddStudioCapability` 在一个方法里直接拼装 controllers、scope resolver、workflow/script 服务、generation 服务
- 证据: `dotnet build` 产出 `CA1506`，指出 `AddStudioCapability` 与 `41` 个类型耦合
- 证据: `src/Aevatar.Studio.Hosting/Controllers/ConnectorsController.cs:1-7` 存在重复 `using Microsoft.AspNetCore.Http;`
- 证据: `src/Aevatar.Studio.Application/Studio/Services/ExecutionService.cs:571` 在 async 方法中使用 `reader.EndOfStream`，触发 `CA2024`
- 影响: 这类问题不阻塞构建，但会持续降低 Studio 子系统的可维护性和代码卫生。

### D4. 文档与 CLI 默认示例仍然违反仓库端口约束

- 证据: `tools/Aevatar.Tools.Cli/Commands/Chat/ChatCommand.cs:72` 当时仍提示旧默认端口
- 证据: `tools/Aevatar.Tools.Cli/README.md:59` 当时仍使用旧默认端口
- 证据: `docs/2026-03-20-workflow-call-practice-guide.md:14` 当时仍沿用旧默认端口示例
- 影响: 仓库顶层规则明确禁止把 `5000/5050` 作为 Web API 默认端口；当前分支仍然把旧示例继续扩散，文档与治理口径不一致。

## 9. 结论与建议

当前分支相对 `dev` 的整体状态，可以判断为“**大分支、但工程质量基本过线**”。如果只是问是否已经低于合入门槛，答案是否定的：构建、架构守卫、Console 类型检查、`GAgentService` 当前测试回归都已经给出了正向证据。

但如果问是否适合继续在这个分支上无节制叠加需求，答案也是否定的。下一步最值得优先做的不是继续扩功能，而是：

1. 先补跑 `dotnet test aevatar.slnx --nologo`，把全量回归证据补齐。
2. 收敛 `StudioCapabilityExtensions` 和几处超大前端文件，把组合根与页面拆回更小的可测试单元。
3. 一次性清掉 `5000` 端口示例，避免继续违背仓库顶层约束。

## 10. 同日修复跟进

- 已拆分 `src/Aevatar.Studio.Hosting/StudioCapabilityExtensions.cs` 的注册职责，新增 `src/Aevatar.Studio.Hosting/StudioHostingServiceCollectionExtensions.cs` 承载 hosting core、bridge、authoring 注册。
- 已修复 `src/Aevatar.Studio.Application/Studio/Services/ExecutionService.cs` 的 async `EndOfStream` 读取问题，并清理 `Studio.Hosting` 内重复 `using`。
- 已将 CLI / SDK / 文档中的旧默认端口示例统一切换到 `5100`，仓库中不再残留 `localhost:5000/5050` 示例。
- 修复后复核结果：`dotnet build aevatar.slnx --nologo` 为 `0 warning / 0 error`，`bash tools/ci/architecture_guards.sh` 通过，`pnpm -C apps/aevatar-console-web tsc --noEmit` 通过，`dotnet test test/Aevatar.Workflow.Sdk.Tests/Aevatar.Workflow.Sdk.Tests.csproj --nologo` 通过。
