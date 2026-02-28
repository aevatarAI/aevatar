# AI Script Runtime 架构方案评分卡（2026-02-28）

## 1. 审计范围与方法

1. 审计对象：
- `docs/architecture/ai-script-runtime-implementation-change-plan.md`
- `docs/architecture/dynamic-gagent-csharp-script-runtime-requirements.md`
2. 评分规范：`docs/audit-scorecard/README.md`（标准化 100 分模型）。
3. 评分口径：本次评分针对“架构方案质量与可实施性”，不是代码落地质量。
4. 本轮重点：
- Docker/OCI 语义对齐程度（Image/Registry/Container/Exec）
- `Adapter-only + RoleGAgent 执行复用` 方案一致性
- Actor 化事实源与统一 Projection 链路

## 2. 客观验证结果

| 检查项 | 命令 | 结果 |
|---|---|---|
| 架构门禁 | `bash tools/ci/architecture_guards.sh` | 通过（含 projection route-mapping / closed-world / run-id guard） |

说明：当前为方案阶段，尚无 `Aevatar.AI.Script.*` 代码与测试项目可执行。

## 3. 整体评分（Overall）

**85 / 100（A-）**

## 4. 维度评分

| 维度 | 权重 | 得分 | 说明 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 18 | 已明确 script 体系与 workflow 解耦、Host 仅组合（证据见 §6）；但 Host 落地形态“独立 Host 或 capability”并存，仍有边界漂移空间。 |
| CQRS 与统一投影链路 | 20 | 17 | 方案明确统一投影主链路与 Command/Query 分离；但尚缺 Script 事件到 read model 的最小字段合同与失败补偿语义。 |
| Projection 编排与状态约束 | 20 | 16 | 已坚持 Actor 化事实源与禁止中间层映射；但 lease/session、ownership、跨节点一致性策略未细化到接口级。 |
| 读写分离与会话语义 | 15 | 13 | Image/Container/Run 拆分清晰，生命周期完整；但 run 幂等键、并发冲突策略、tag 漂移防护落地机制仍偏原则层。 |
| 命名语义与冗余清理 | 10 | 8 | Docker 名词体系统一度高；但两份方案文档对 `IRoleAgent` 兼容模式存在冲突（Adapter-only vs Native+Adapter）。 |
| 可验证性（门禁/构建/测试） | 15 | 13 | 已给出 guards+tests 验证矩阵；但部分测试项目仍为规划路径，且缺少 SLO/容量/故障注入验证项。 |

## 5. 分模块评分（方案视角）

| 模块 | 分数 | 结论 |
|---|---:|---|
| 控制面（Image/Container/Run） | 86 | 领域边界清晰，Docker 语义贴合度高。 |
| 执行面（RoleGAgent 复用 + Adapter） | 90 | 技术路线务实，复用现有 AI 内核风险最低。 |
| 运行时（Compiler/Sandbox/IOC） | 82 | 方向正确，但关键隔离细节仍需接口化落地。 |
| API 与发布治理 | 84 | 生命周期 API 完整，但版本兼容策略需补齐。 |
| 投影与查询 | 83 | 明确统一链路，缺少 reducer/read model 合同细目。 |
| 文档一致性 | 78 | 主要问题是同一决策在两份文档中未完全收敛。 |

## 6. 关键证据（加分项）

1. 明确 `Adapter-only` 决策：`docs/architecture/ai-script-runtime-implementation-change-plan.md:12-15`。
2. 明确执行面复用 `RoleGAgent`：`docs/architecture/ai-script-runtime-implementation-change-plan.md:17-20`。
3. 明确 Docker 控制面语义（Image/Container/Exec/Registry）：`docs/architecture/ai-script-runtime-implementation-change-plan.md:22-26`。
4. 明确事实源 Actor 化约束：`docs/architecture/ai-script-runtime-implementation-change-plan.md:28-31`。
5. 明确 script 项目禁依赖 workflow：`docs/architecture/ai-script-runtime-implementation-change-plan.md:75-80` 与 `docs/architecture/dynamic-gagent-csharp-script-runtime-requirements.md:52-54`。
6. 明确统一投影主链路：`docs/architecture/ai-script-runtime-implementation-change-plan.md:61-63` 与 `docs/architecture/dynamic-gagent-csharp-script-runtime-requirements.md:31-32`。
7. API 生命周期完整（image/container/run）：`docs/architecture/ai-script-runtime-implementation-change-plan.md:167-182`。

## 7. 主要扣分项（按影响度）

### 7.1 [High] 决策不一致：`Adapter-only` 与 `Native+Adapter` 并存

**影响**
同一方案出现双口径，会直接导致实现分叉（代码生成、测试门禁、发布策略都不一致）。

**证据**
1. `Adapter-only`：`docs/architecture/ai-script-runtime-implementation-change-plan.md:12-15`。
2. `Native+Adapter`：`docs/architecture/dynamic-gagent-csharp-script-runtime-requirements.md:71-74`。
3. WBS 仍写“双模式契约”：`docs/architecture/dynamic-gagent-csharp-script-runtime-requirements.md:203-205`。

**扣分**：-4

### 7.2 [Medium] 跨节点一致性与冲突解决策略未接口化

**影响**
Image tag 更新、container 启停并发、run 重入在分布式场景下可能出现竞态；当前仅有原则描述，缺少冲突解算协议。

**证据**
1. Registry 状态描述停留在 map 结构：`docs/architecture/ai-script-runtime-implementation-change-plan.md:86-89`。
2. 生命周期与 API 已定义但未定义并发冲突码与幂等键：`docs/architecture/ai-script-runtime-implementation-change-plan.md:167-182`、`:246-250`。

**扣分**：-3

### 7.3 [Medium] 沙箱边界缺少“可执行级”技术条款

**影响**
仅描述“白名单+配额”不足以避免实现偏差；需明确 `AssemblyLoadContext` 卸载、反射限制、网络/文件系统策略注入点。

**证据**
1. 安全条款为策略级描述：`docs/architecture/dynamic-gagent-csharp-script-runtime-requirements.md:150-155`。
2. 编译与运行时章节未给具体隔离机制接口：`docs/architecture/ai-script-runtime-implementation-change-plan.md:147-163`。

**扣分**：-3

### 7.4 [Low] 验证矩阵缺少性能与可用性准入

**影响**
功能测试可通过但可能在高并发或长会话场景失稳，缺少上线前容量/延迟/回收指标门槛。

**证据**
当前验证矩阵仅含架构守卫与单测命令：`docs/architecture/ai-script-runtime-implementation-change-plan.md:236-244`。

**扣分**：-2

## 8. 阻断项修复准入标准（Blocking Gate）

### B-1：文档决策口径必须单一化（合并前必须完成）
1. 统一为 `Adapter-only` 或统一为 `Native+Adapter`，不得双轨并存。
2. 统一更新以下文件并交叉引用：
- `docs/architecture/ai-script-runtime-implementation-change-plan.md`
- `docs/architecture/dynamic-gagent-csharp-script-runtime-requirements.md`
3. 在 CI 中新增“方案一致性检查”（最小可先用 `rg` 规则防止关键词冲突）。

## 9. 改进优先级建议

### P1（本周建议完成）
1. 修复双口径：冻结 `IRoleAgent` 兼容模式为单一路线并更新全部 WBS 与验收条目。
2. 增加并发与幂等协议：定义 `idempotency_key`、`etag/version`、冲突错误码（如 `IMAGE_TAG_CONFLICT`、`CONTAINER_STATE_CONFLICT`）。
3. 补齐沙箱技术合同：新增 `IScriptSandboxPolicy`、`IScriptAssemblyLoadPolicy`、`IScriptResourceQuotaPolicy` 接口。

### P2（实现前完成）
1. 增加非功能验收：启动时延、首 token 延迟、并发 run 上限、容器回收时间窗口。
2. 在 `tools/ci/architecture_guards.sh` 增加 script 专项守卫：
- 禁止 `Aevatar.AI.Script.*` 引用 `src/workflow/*`
- 禁止中间层 `container/run/session` 事实态字典字段
3. 增加“Adapter 兼容合同测试”模板，要求每个 script entrypoint 都通过。

## 10. 结论

该方案已经具备较强的工程落地潜力，尤其是 `Adapter-only + RoleGAgent 执行复用 + Docker 语义控制面` 组合，方向正确且风险可控。当前从 **A 提升到 A+** 的关键不在新增概念，而在“决策口径统一 + 并发冲突协议 + 沙箱技术合同”三项治理闭环。
