# AI Script Runtime 架构方案复评评分卡（2026-02-28，v2）

## 1. 审计范围与方法

1. 审计对象：
- `docs/architecture/ai-script-runtime-implementation-change-plan.md`（v1.1）
- `docs/architecture/dynamic-gagent-csharp-script-runtime-requirements.md`（v3.0）
2. 评分规范：`docs/audit-scorecard/README.md`（标准化 100 分模型）。
3. 审计类型：方案质量复评（文档一致性、可实施性、可验证性）。
4. 与上一版对比对象：`docs/audit-scorecard/ai-script-runtime-architecture-scorecard-2026-02-28.md`（85/100）。

## 2. 客观验证结果

| 检查项 | 命令 | 结果 |
|---|---|---|
| 架构门禁 | `bash tools/ci/architecture_guards.sh` | 通过（projection-route / closed-world / run-id guard 全通过） |
| 新增 CI 脚本存在性 | `for f in ...; do [ -f "$f" ] ...; done` | 4/4 缺失（`architecture_doc_consistency_guards.sh`、`script_runtime_perf_guards.sh`、`script_runtime_availability_guards.sh`、`script_runtime_resilience_guards.sh`） |

## 3. 整体评分（Overall）

**91 / 100（A）**

> 相比上一版（85），本次主要提升来自：`Adapter-only` 口径统一、并发/幂等协议接口化、沙箱技术合同化、SLO 验收门槛补齐。

## 4. 维度评分

| 维度 | 权重 | 得分 | 说明 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 19 | 已明确 Script 体系禁依赖 workflow、Host 仅组合（证据见 §6）。 |
| CQRS 与统一投影链路 | 20 | 18 | Command/Query 与统一投影链路定义清晰；但 read model 字段最小合同仍偏抽象。 |
| Projection 编排与状态约束 | 20 | 18 | Actor 事实源、禁止中间层映射、回调事件化均明确；ownership/lease 接口还未细化。 |
| 读写分离与会话语义 | 15 | 14 | `Idempotency-Key + If-Match + ETag + 冲突码` 已落地到协议层。 |
| 命名语义与冗余清理 | 10 | 10 | Docker 术语与 `Adapter-only` 决策已统一，无双口径冲突。 |
| 可验证性（门禁/构建/测试） | 15 | 12 | 验证矩阵完整，但多项“必跑脚本”尚未实际存在。 |

## 5. 分模块评分（方案视角）

| 模块 | 分数 | 结论 |
|---|---:|---|
| 控制面（Image/Container/Run） | 90 | 生命周期与状态边界清晰，Docker 语义对齐好。 |
| 执行面（RoleGAgent 复用 + Adapter-only） | 94 | 路线稳定且复用现有内核，演进风险显著降低。 |
| 一致性协议（幂等/并发/冲突） | 91 | 已从原则升级到接口与错误码层。 |
| 沙箱与运行时治理 | 89 | 接口合同完善，但需代码与测试证明可执行性。 |
| API 与发布治理 | 90 | 生命周期 API+回滚策略完整。 |
| 验证与门禁治理 | 82 | 设计完善，但脚本门禁尚未落地成文件。 |

## 6. 关键证据（加分项）

1. `Adapter-only` 冻结决策：`docs/architecture/ai-script-runtime-implementation-change-plan.md:13-16`。
2. 文档口径统一修复：`docs/architecture/ai-script-runtime-implementation-change-plan.md:45-47`；需求文档一致：`docs/architecture/dynamic-gagent-csharp-script-runtime-requirements.md:71-74`。
3. 幂等/并发协议：`docs/architecture/ai-script-runtime-implementation-change-plan.md:34-37`、`:133-143`。
4. 固定冲突错误码：`docs/architecture/ai-script-runtime-implementation-change-plan.md:144-150`。
5. 一致性接口合同：`docs/architecture/ai-script-runtime-implementation-change-plan.md:152-169`。
6. 沙箱接口合同（5 策略）：`docs/architecture/ai-script-runtime-implementation-change-plan.md:194-223`。
7. 一致性 Header 约定：`docs/architecture/ai-script-runtime-implementation-change-plan.md:247-250`。
8. SLO 与性能/可用性验收：`docs/architecture/ai-script-runtime-implementation-change-plan.md:316-340`。

## 7. 主要扣分项（按影响度）

### 7.1 [Medium] 验证矩阵中的关键 CI 脚本尚未落地

**影响**
方案写明了“必跑守卫”，但文件尚不存在，短期内无法形成自动化阻断能力。

**证据**
1. 文档声明需运行：`docs/architecture/ai-script-runtime-implementation-change-plan.md:321`、`:328-330`。
2. 实际检查缺失：
- `tools/ci/architecture_doc_consistency_guards.sh`
- `tools/ci/script_runtime_perf_guards.sh`
- `tools/ci/script_runtime_availability_guards.sh`
- `tools/ci/script_runtime_resilience_guards.sh`

**扣分**：-3

### 7.2 [Medium] Projection 编排细节仍缺接口级定义

**影响**
虽然主链路明确，但 Script 运行域的 projection ownership/lease 生命周期尚未形成具体端口合同，后续实现可能各自解释。

**证据**
1. 只定义“接入统一 projection pipeline”：`docs/architecture/ai-script-runtime-implementation-change-plan.md:83-84`、`:286-289`。
2. 未见 Script 专属 projection lifecycle/ownership 接口列表。

**扣分**：-3

### 7.3 [Low] Host 承载形态仍保留可选分支

**影响**
“独立 Host 或 capability 挂现有 host”并存，可能导致环境间差异。

**证据**
`docs/architecture/ai-script-runtime-implementation-change-plan.md:73`（`src/Aevatar.AI.Script.Host.Api` 可选）。

**扣分**：-1

### 7.4 [Low] 需求文档 SLO 口径尚未同步增强

**影响**
实施文档已有 SLO，需求文档验收矩阵仍偏功能与结构，跨文档追踪不够紧。

**证据**
1. 实施文档有 SLO：`docs/architecture/ai-script-runtime-implementation-change-plan.md:332-340`。
2. 需求文档验收矩阵未同步性能/可用性门槛：`docs/architecture/dynamic-gagent-csharp-script-runtime-requirements.md:219-229`。

**扣分**：-2

## 8. 阻断项修复准入标准（Blocking Gate）

本次复评无新增阻断项；上一版阻断项（决策双口径）已关闭。

## 9. 改进优先级建议

### P1
1. 新建并接入 4 个 CI 脚本（文档一致性、性能、可用性、韧性）。
2. 为 Script 运行域补齐 projection lifecycle/ownership 接口（与现有 CQRS projection 端口对齐）。
3. 固化 Host 承载策略（推荐先 capability-only，再决定是否保留独立 Host）。

### P2
1. 将实施文档 SLO 同步回需求文档验收矩阵，形成单一验收口径。
2. 增加“冲突码到 HTTP 状态码映射表”与契约测试模板。

## 10. 结论

该方案已从“方向正确但治理不完整”升级为“架构可实施且风险可控”的 A 档方案。当前主要剩余问题集中在“把文档承诺的门禁与非功能验收真正落地为 CI 资产”。这部分完成后，方案可进入 A+ 区间。
