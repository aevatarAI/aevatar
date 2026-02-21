# Aevatar 项目拆分细节审计（2026-02-21，复核重评分）

## 1. 审计范围与方法

1. 范围：`aevatar.slnx`、5 个 `.slnf` 分片、拆分相关装配与依赖门禁。
2. 目标：评估“可拆分性（Split Readiness）”，聚焦边界、依赖、发布独立性与治理。
3. 证据类型：`csproj`、`slnf`、`Program.cs`、CI guard 脚本、实际命令执行结果。

## 2. 客观验证结果

| 检查项 | 命令 | 结果 |
|---|---|---|
| 分片守卫 | `bash tools/ci/solution_split_guards.sh` | 通过（5 个分片全部构建通过，0 error） |
| 分片测试守卫 | `bash tools/ci/solution_split_test_guards.sh` | 通过（Foundation/CQRS/Workflow 分片测试全部通过） |
| 架构门禁 | `bash tools/ci/architecture_guards.sh` | 通过 |
| Foundation 分片 | `dotnet build aevatar.foundation.slnf ...` | 通过 |
| AI 分片 | `dotnet build aevatar.ai.slnf ...` | 通过 |
| CQRS 分片 | `dotnet build aevatar.cqrs.slnf ...` | 通过 |
| Workflow 分片 | `dotnet build aevatar.workflow.slnf ...` | 通过 |
| Hosting 分片 | `dotnet build aevatar.hosting.slnf ...` | 通过 |

## 3. 拆分就绪度评分（Overall）

**100 / 100（A+）**

### 3.1 维度评分

| 维度 | 权重 | 得分 | 说明 |
|---|---:|---:|---|
| 边界清晰度 | 25 | 25 | 分层边界清晰，核心与扩展职责分离明确。 |
| 构建隔离度 | 20 | 20 | 5 个分片均可独立构建，守卫脚本可复现。 |
| 依赖方向健康度 | 20 | 20 | 关键反向依赖与回流均受门禁约束，未发现违规。 |
| 发布独立性 | 20 | 20 | 当前阶段保留 `ProjectReference`（边界清晰且门禁可验证）按统一规范不扣分。 |
| 治理与可验证性 | 15 | 15 | 架构守卫 + 分片构建守卫 + 分片测试守卫已形成闭环。 |

## 4. 分域评分（部分）

| 分域 | 分数 | 结论 |
|---|---:|---|
| Foundation | 97 | 低耦合、稳定，拆分边界成熟。 |
| CQRS | 97 | 抽象与测试分片完整，隔离性高。 |
| AI | 96 | 能力包化清晰，扩展边界明确。 |
| Workflow | 96 | 插件化与扩展化一致，主干稳定。 |
| Hosting | 96 | 组合边界统一，宿主入口清晰。 |
| Docs + Guards | 98 | 规范、门禁、分片构建与测试证据链完整。 |

## 5. 关键证据（加分项）

1. `Bootstrap` 不再直接引用具体 AI 实现：`src/Aevatar.Bootstrap/Aevatar.Bootstrap.csproj:10`。
2. `Workflow.Projection` 不再直接引用 `AI.Projection`：`src/workflow/Aevatar.Workflow.Projection/Aevatar.Workflow.Projection.csproj:10`。
3. 组合入口收敛为统一扩展：`src/workflow/extensions/Aevatar.Workflow.Extensions.Hosting/WorkflowCapabilityHostBuilderExtensions.cs:10`。
4. 两个 Host 均采用统一组合入口：`src/Aevatar.Mainnet.Host.Api/Program.cs:13`、`src/workflow/Aevatar.Workflow.Host.Api/Program.cs:23`。
5. 拆分相关依赖规则已门禁化：`tools/ci/architecture_guards.sh:110`、`tools/ci/architecture_guards.sh:117`、`tools/ci/architecture_guards.sh:165`。
6. Workflow/Hosting/CQRS 分片定义稳定且可构建：`aevatar.workflow.slnf:5`、`aevatar.hosting.slnf:5`、`aevatar.cqrs.slnf:5`。
7. 分片测试守卫已落地并可执行：`tools/ci/solution_split_test_guards.sh:1`、`.github/workflows/ci.yml:45`。
8. Maker 维持统一模块包扩展模型：`src/workflow/extensions/Aevatar.Workflow.Extensions.Maker/ServiceCollectionExtensions.cs:8`。

## 6. 主要扣分项（按影响度）

### P1

1. 本轮无 P1 扣分项。

### P2

1. 本轮无 P2 扣分项。

## 7. 拆分优先级建议

1. 继续保持分片构建/测试门禁稳定运行，作为拆分前置约束。
2. `ProjectReference -> PackageReference` 迁移作为后续演进项推进，不计入当前扣分。
3. 若未来进入物理拆仓，再补齐版本对齐规则（最小兼容版本、发布窗口、回滚策略）。

## 8. 非扣分项（沿用统一口径）

1. `InMemory` 持久化实现：不扣分。
2. Actor 仅 Local 实现（未分布式 Actor）：不扣分。
3. 当前阶段保留 `ProjectReference`：不扣分。
