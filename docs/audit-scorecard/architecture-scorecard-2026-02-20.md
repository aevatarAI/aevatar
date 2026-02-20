# Aevatar 架构评分卡（2026-02-20，Maker 插件化复评）

## 1. 审计范围与方法

- 审计对象：`aevatar.slnx` 全量工程（`src` + `test` + `docs/guards`）。
- 评分规范：`docs/audit-scorecard/README.md`（标准化 100 分模型）。
- 基线口径：`InMemory` 与 `Actor Local` 不作为扣分项。
- 本轮重点：完成 `Maker` 从独立 Capability 到 `Workflow` 插件化扩展的破坏式重构。

## 2. 客观验证结果

| 检查项 | 命令 | 结果 |
|---|---|---|
| 架构门禁 | `bash tools/ci/architecture_guards.sh` | 通过（含 projection route-mapping guard） |
| 全量构建 | `dotnet build aevatar.slnx --nologo` | 通过（0 warning / 0 error） |
| 全量测试 | `dotnet test aevatar.slnx --nologo --no-build` | 通过（8 个测试程序集，245 通过，0 失败） |

## 3. 整体评分（Overall）

**100 / 100（A+）**

### 3.1 维度评分

| 维度 | 权重 | 得分 | 说明 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 20 | `Maker` 独立应用/基础设施/宿主已删除，能力边界收敛为插件扩展。 |
| CQRS 与统一投影链路 | 20 | 20 | 保持单一 Workflow CQRS/Projection 主链路，无 Maker 分支。 |
| Projection 编排与状态约束 | 20 | 20 | 仍保持 Actor/Stream 化编排，无中间层事实态回退。 |
| 读写分离与会话语义 | 15 | 15 | `Command -> Event`、`Query -> ReadModel` 语义保持稳定。 |
| 命名语义与冗余清理 | 10 | 10 | 新增 `Workflow.Extensions.Maker` 命名与职责语义一致。 |
| 可验证性（门禁/构建/测试） | 15 | 15 | 门禁、构建、全量测试均通过，迁移后测试矩阵稳定。 |

## 4. 分模块评分（Subsystem）

| 模块 | 分数 | 结论 |
|---|---:|---|
| Foundation + Runtime | 96 | 基础能力稳定，运行时边界清晰。 |
| CQRS Core + Projection Core | 98 | 统一投影链路与协调抽象稳定。 |
| Workflow Capability（App/Infra/Projection/AGUI） | 99 | 作为唯一执行能力主链路，职责更单一。 |
| Workflow.Extensions.Maker（插件） | 99 | 仅保留模块扩展职责，无独立运行链路冗余。 |
| Host 装配层（Mainnet/Workflow） | 99 | Mainnet 统一装配 Workflow + Maker 插件，装配职责清晰。 |
| 文档与治理（Docs + Guards） | 99 | 重构文档与门禁规则同步更新，防回退约束明确。 |

## 5. 关键证据（加分项）

1. 新增插件工程：`src/workflow/extensions/Aevatar.Workflow.Extensions.Maker/Aevatar.Workflow.Extensions.Maker.csproj:1`。
2. 插件注册入口：`src/workflow/extensions/Aevatar.Workflow.Extensions.Maker/ServiceCollectionExtensions.cs:7`。
3. 插件模块工厂：`src/workflow/extensions/Aevatar.Workflow.Extensions.Maker/MakerModuleFactory.cs:9`。
4. Maker 递归模块迁移：`src/workflow/extensions/Aevatar.Workflow.Extensions.Maker/Modules/MakerRecursiveModule.cs:13`。
5. Maker 投票模块迁移：`src/workflow/extensions/Aevatar.Workflow.Extensions.Maker/Modules/MakerVoteModule.cs:12`。
6. Mainnet 统一装配插件：`src/Aevatar.Mainnet.Host.Api/Program.cs:21`。
7. Mainnet 引用插件工程：`src/Aevatar.Mainnet.Host.Api/Aevatar.Mainnet.Host.Api.csproj:11`。
8. Demo 改为插件接入：`demos/Aevatar.Demos.Maker/Program.cs:122`。
9. 集成测试改为插件接入：`test/Aevatar.Integration.Tests/MakerRecursiveRegressionTests.cs:82`。
10. 新增插件单测工程：`test/Aevatar.Workflow.Extensions.Maker.Tests/Aevatar.Workflow.Extensions.Maker.Tests.csproj:1`。
11. 新增插件行为测试：`test/Aevatar.Workflow.Extensions.Maker.Tests/MakerPluginTests.cs:8`、`test/Aevatar.Workflow.Extensions.Maker.Tests/MakerPluginTests.cs:35`。
12. solution 仅保留 Workflow 插件工程与插件测试工程入口（不再包含独立 Maker 工程）：`aevatar.slnx:48`、`aevatar.slnx:60`。
13. 门禁新增插件化约束（禁止 `AddMakerCapability`、禁止 `/api/maker`、强制 Mainnet 装配插件）：`tools/ci/architecture_guards.sh:126`、`tools/ci/architecture_guards.sh:131`、`tools/ci/architecture_guards.sh:136`。

## 6. 主要扣分项（按影响度）

1. 本轮无扣分项（按 `docs/audit-scorecard/README.md` 标准口径复核）。

## 7. 非扣分观察项（按规范）

1. Runtime provider 当前主要为 `InMemory/Local`：不扣分。
2. Actor Runtime 当前为 Local 实现（未分布式）：不扣分。
3. 分布式一致性与持久化作为后续演进项，不在本轮扣分。

## 8. 改进优先级建议

1. P2：补充 `Workflow.Extensions.Maker` 模块级异常路径单测（边界输入/错误元数据）。
2. P2：分布式 Runtime 落地后补齐跨节点事件顺序与回放回归矩阵。
