# AI Script Runtime 架构方案复评评分卡（2026-02-28，v5）

## 1. 审计范围与方法

1. 审计对象：
- `docs/architecture/ai-script-runtime-implementation-change-plan.md`（v1.4）
- `docs/architecture/dynamic-gagent-csharp-script-runtime-requirements.md`（v3.3）
- `tools/ci/architecture_doc_consistency_guards.sh`
2. 评分规范：`docs/audit-scorecard/README.md`。
3. 评分模式：**方案评分模式（Architecture Plan Scoring）**。
4. 口径约束：仅评估架构方案与文档质量；实施状态（代码未落地、测试/CI 未接入、守卫 skip）仅记录风险，不作为扣分项。
5. 对比对象：`docs/audit-scorecard/ai-script-runtime-architecture-scorecard-2026-02-28-v4.md`（95/100）。

## 2. 最小一致性验证（方案评分）

| 检查项 | 命令 | 结果 |
|---|---|---|
| 文档一致性守卫 | `bash tools/ci/architecture_doc_consistency_guards.sh` | 通过 |
| 关键术语冲突扫描 | `rg -n "独立 API|独立 Host|Adapter-only|Native 模式|双模式|service_mode|daemon|event|hybrid" docs/architecture/ai-script-runtime-implementation-change-plan.md docs/architecture/dynamic-gagent-csharp-script-runtime-requirements.md` | 未发现与 `Adapter-only` / Host 冻结策略冲突的阻断性表达 |

## 3. 整体评分（Overall）

**100 / 100（A+）**

结论：在“方案评分模式”下，当前版本架构文档已形成完整、闭环、可验证的目标架构表达；v4 中两项扣分均属实施态问题，本轮按新口径转为非扣分观察项。

## 4. 维度评分

| 维度 | 权重 | 得分 | 说明 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 20 | Host capability-only、控制面/执行面边界、Actor 事实源边界表达完整。 |
| CQRS 与统一投影链路 | 20 | 20 | Build/Compose/Service/Run 事件统一进入单一 Projection Pipeline。 |
| Projection 编排与状态约束 | 20 | 20 | `Image/Compose/Service/Container/Run` 事实归属、禁止中间层事实字典、Envelope 约束清晰。 |
| 读写分离与会话语义 | 15 | 15 | 幂等键、并发令牌、冲突码、run 生命周期与对账语义完整。 |
| 命名语义与冗余清理 | 10 | 10 | Docker/Compose 对齐术语统一，`Adapter-only` 与 `daemon/event/hybrid` 口径稳定。 |
| 可验证性（门禁/构建/测试） | 15 | 15 | 文档内已定义守卫、验收矩阵、SLO 阈值与 WBS 分期，方案可验证性充分。 |

## 5. 分模块评分（方案视角）

| 模块 | 分数 | 结论 |
|---|---:|---|
| 控制面（Build + Compose + Reconcile） | 100 | Autonomous Build 到 generation reconcile 主链路完整。 |
| 执行面（RoleGAgent 复用 + Adapter-only） | 100 | 与现有 AI 主干对齐且边界清晰。 |
| 服务模式（daemon/event/hybrid） | 100 | 长期服务与事件响应统一在同一编排模型内。 |
| 消息与一致性（Envelope + 幂等并发） | 100 | 协议字段、冲突语义、对账机制均具备可落地合同。 |
| 治理与门禁（文档层） | 100 | 守卫规则、验收矩阵、WBS、SLO 与回滚口径齐备。 |

## 6. 关键证据（加分项）

1. `Adapter-only` 冻结与双模式废弃：
- `docs/architecture/ai-script-runtime-implementation-change-plan.md:13`
- `docs/architecture/ai-script-runtime-implementation-change-plan.md:66`
- `docs/architecture/ai-script-runtime-implementation-change-plan.md:67`
2. Host 承载冻结（capability-only）：
- `docs/architecture/ai-script-runtime-implementation-change-plan.md:43`
- `docs/architecture/ai-script-runtime-implementation-change-plan.md:45`
3. Compose + Actor Reconcile + Envelope 主链路：
- `docs/architecture/ai-script-runtime-implementation-change-plan.md:48`
- `docs/architecture/ai-script-runtime-implementation-change-plan.md:101`
- `docs/architecture/dynamic-gagent-csharp-script-runtime-requirements.md:95`
4. daemon/event/hybrid 三态与模式约束：
- `docs/architecture/ai-script-runtime-implementation-change-plan.md:58`
- `docs/architecture/dynamic-gagent-csharp-script-runtime-requirements.md:230`
5. 幂等、并发与冲突协议：
- `docs/architecture/ai-script-runtime-implementation-change-plan.md:267`
- `docs/architecture/ai-script-runtime-implementation-change-plan.md:273`
- `docs/architecture/ai-script-runtime-implementation-change-plan.md:278`
6. Autonomous Build 闭环与 API/验收矩阵：
- `docs/architecture/ai-script-runtime-implementation-change-plan.md:53`
- `docs/architecture/ai-script-runtime-implementation-change-plan.md:492`
- `docs/architecture/dynamic-gagent-csharp-script-runtime-requirements.md:335`

## 7. 扣分项

本轮无扣分项。

## 8. 非扣分观察项（实施风险，仅记录）

1. 文档中的 `Planned` 状态、未落地代码与测试、CI 集成进度，属于实施管理问题，不纳入方案评分扣分。
2. 待实现项建议继续按 WBS 与验收矩阵推进，并在“实施评分模式”下单独复审。

## 9. 与 v4 的差异说明

1. v4 的两项扣分（CI 接入缺口、非功能守卫 skip）属于实施态问题。
2. 按当前强制口径（方案评分模式），上述问题转入“非扣分观察项”。
3. 因此总分由 **95 -> 100**。

## 10. 结论

当前 AI Script Runtime 架构方案在文档层达到 **A+（100/100）**，可以作为后续代码实施与里程碑验收的权威蓝图。
