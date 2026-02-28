# AI Script Runtime 架构方案复评评分卡（2026-02-28，v3）

## 1. 审计范围与方法

1. 审计对象：
- `docs/architecture/ai-script-runtime-implementation-change-plan.md`（v1.2）
- `docs/architecture/dynamic-gagent-csharp-script-runtime-requirements.md`（v3.1）
- `tools/ci/architecture_doc_consistency_guards.sh`
- `tools/ci/script_runtime_perf_guards.sh`
- `tools/ci/script_runtime_availability_guards.sh`
- `tools/ci/script_runtime_resilience_guards.sh`
2. 评分规范：`docs/audit-scorecard/README.md`（标准 100 分模型）。
3. 审计类型：方案质量复评（文档一致性、治理闭环、可验证性）。
4. 对比对象：`docs/audit-scorecard/ai-script-runtime-architecture-scorecard-2026-02-28-v2.md`（91/100）。

## 2. 客观验证结果

| 检查项 | 命令 | 结果 |
|---|---|---|
| 架构守卫 | `bash tools/ci/architecture_guards.sh` | 通过（projection-route / closed-world / run-id guard 全通过） |
| 文档一致性守卫 | `bash tools/ci/architecture_doc_consistency_guards.sh` | 通过 |
| 性能守卫 | `bash tools/ci/script_runtime_perf_guards.sh` | 跳过（当前无 Script Runtime 项目） |
| 可用性守卫 | `bash tools/ci/script_runtime_availability_guards.sh` | 跳过（当前无 Script Runtime 项目） |
| 韧性守卫 | `bash tools/ci/script_runtime_resilience_guards.sh` | 跳过（当前无 Script Runtime 项目） |
| 新守卫接入 CI 工作流 | `rg -n "architecture_doc_consistency_guards\|script_runtime_.*_guards" .github/workflows tools/ci/README.md` | 仅命中 `tools/ci/README.md`，未命中 `.github/workflows` |

## 3. 整体评分（Overall）

**95 / 100（A+）**

> 相比 v2（91 分），本次核心提升来自：4 个守卫脚本资产落地、`capability-only` Host 策略冻结、Projection lifecycle 接口化、需求/实施文档 SLO 口径对齐。

## 4. 维度评分

| 维度 | 权重 | 得分 | 说明 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 20 | Host 策略从“可选”收敛为 `capability-only`，分层边界更稳定。 |
| CQRS 与统一投影链路 | 20 | 19 | 单一投影主链路口径清晰，Query/ReadModel 路径完整。 |
| Projection 编排与状态约束 | 20 | 19 | lease/session/checkpoint 端口合同已补齐，Actor 化语义明确。 |
| 读写分离与会话语义 | 15 | 15 | `Idempotency-Key + If-Match + ETag + 冲突码` 协议完整。 |
| 命名语义与冗余清理 | 10 | 10 | Docker 术语、Adapter-only、Host 承载口径统一。 |
| 可验证性（门禁/构建/测试） | 15 | 12 | 脚本已落地，但工作流未接入且非功能守卫当前为 skip。 |

## 5. 分模块评分（方案视角）

| 模块 | 分数 | 结论 |
|---|---:|---|
| 控制面（Image/Container/Run） | 95 | 领域边界和生命周期定义完整、可回放语义清晰。 |
| 执行面（RoleGAgent + Adapter-only） | 96 | 复用主干能力且避免脚本接管 Actor 生命周期，风险低。 |
| Projection 编排 | 94 | 已有 ownership/lifecycle 接口合同，进入可实施状态。 |
| 安全与沙箱 | 94 | 编译/装载/网络/配额合同化充分。 |
| 治理与门禁 | 90 | 守卫脚本已补齐，但 CI 自动执行尚未闭环。 |

## 6. 关键证据（加分项）

1. Host 单路径冻结：`docs/architecture/ai-script-runtime-implementation-change-plan.md:43-47`。
2. 4 个守卫资产落地声明：`docs/architecture/ai-script-runtime-implementation-change-plan.md:61-66`。
3. Projection 生命周期接口合同：`docs/architecture/ai-script-runtime-implementation-change-plan.md:247-282`。
4. 实施文档验收矩阵包含性能/可用性/韧性守卫：`docs/architecture/ai-script-runtime-implementation-change-plan.md:368-383`。
5. 需求文档验收矩阵与 SLO 同步：`docs/architecture/dynamic-gagent-csharp-script-runtime-requirements.md:219-245`。
6. CI 脚本目录已登记 4 个新守卫：`tools/ci/README.md:15-18`。

## 7. 扣分项（按影响度）

### 7.1 [Medium] 新增守卫尚未接入 GitHub CI 工作流

**影响**
守卫目前主要依赖人工或本地执行，未形成 PR 级自动阻断闭环。

**证据**
1. 命令结果：`rg -n "architecture_doc_consistency_guards|script_runtime_.*_guards" .github/workflows tools/ci/README.md` 仅命中 `tools/ci/README.md`。

**扣分**：-3

### 7.2 [Medium] 非功能守卫当前以 skip 通过，SLO 仍未形成真实门禁压力

**影响**
性能/可用性/韧性守卫在当前仓库状态下尚未验证真实阈值，只验证了脚本框架存在。

**证据**
1. `bash tools/ci/script_runtime_perf_guards.sh` 输出：`Script runtime projects not found; perf guard skipped.`  
2. `bash tools/ci/script_runtime_availability_guards.sh` 输出：`Script runtime projects not found; availability guard skipped.`  
3. `bash tools/ci/script_runtime_resilience_guards.sh` 输出：`Script runtime projects not found; resilience guard skipped.`

**扣分**：-2

## 8. 阻断项修复准入标准（Blocking Gate）

本次复评无 Blocking 项。上一轮 4 个核心问题已全部关闭。

## 9. 改进优先级建议

### P1
1. 将 4 个新增守卫接入 `.github/workflows/ci.yml` 的 `fast-gates` 或 script-runtime 专项 job。
2. 在 Script Runtime 项目落地后，补齐 metrics 产物管道（`artifacts/script-runtime/*.txt`）并将 3 个非功能守卫从 skip 变为强制。

### P2
1. 为 `IScriptProjectionSessionPort/DispatchPort/CheckpointPort` 增加契约测试模板，绑定到 script 子解测试。
2. 在后续代码实施阶段补齐“冲突码 -> HTTP 状态码”契约测试，避免协议漂移。

## 10. 结论

该方案已达到 **A+（95）** 水平，核心架构缺陷已清零。剩余工作集中在“把新增治理资产并入 CI 自动执行路径”和“让非功能守卫从脚本存在性校验升级为真实 SLO 阈值校验”。
