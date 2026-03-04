# GAgent 配置最佳实践蓝图（Final）

## 1. 文档元信息
- 状态：`Proposed`
- 版本：`v3`
- 日期：`2026-03-05`
- 范围：`Aevatar.Foundation.* / Aevatar.AI.* / workflow/*`
- 决策级别：`Architecture Breaking Change`

## 2. 结论（针对本次问题）
1. `GAgent` 必须支持“增量配置（patch）”。
2. 增量配置不应“覆盖 Manifest 配置”；最佳实践是**Manifest 不再承载业务配置语义**。
3. 业务配置唯一事实源为 `ConfigProfile` 事件流（`BaseSnapshot + IncrementalPatches`）。
4. 运行时使用 `RunConfigLease(profile_id, revision)` 固定配置版本，禁止运行中漂移（仅固定引用，不生成 run 独立配置文件）。

## 3. 当前问题（需要被彻底替换）
1. `Manifest.ConfigJson` 与事件状态并存，形成双事实源。
2. 回放恢复不闭环（无 manifest 时仅能恢复部分配置）。
3. 模块路由恢复依赖运行时重配，非纯事件同态恢复。
4. 配置作用域混叠（实例级/共享级/run 级边界不清）。

## 4. 最终架构原则（强制）
1. 单一事实源：业务配置只来自 `ConfigProfile` 事件流状态。
2. 读写分离：`Command -> Domain Event -> Projection -> Query`。
3. 运行固定性：run 启动时签发 lease，执行期间 revision 不可变。
4. 严格分层：`Domain / Application / Infrastructure / Host`。
5. 依赖反转：上层仅依赖配置端口抽象，不依赖具体存储。
6. 无中间层事实态：禁止 `instance_id -> config` 进程内字典作为权威状态。

## 5. 领域模型（最终态）

### 5.1 ConfigProfile（共享配置聚合，唯一事实源）
聚合：`ConfigProfileGAgent`

主键：
1. `profile_scope`（如 `role-agent`）
2. `profile_id`

状态：
1. `latest_revision`
2. `revisions`（有序不可变元信息）
3. `status`（`Active/Archived`）
4. `effective_config`（该 revision 的确定性物化结果）

修订模型：
1. `BaseSnapshot`：完整配置快照。
2. `IncrementalPatch`：仅携带变更字段，生成下一 revision。
3. 每次发布 patch 都产出新 revision，不在原 revision 上原地改写。

### 5.2 InstanceConfigBinding（实例绑定聚合）
存放于业务 Actor 状态，仅保存引用，不保存共享配置正文：
1. `bound_profile_id`
2. `revision_policy`（`TrackLatest` / `Pinned`）
3. `pinned_revision`（`Pinned` 时必填）

### 5.3 RunConfigLease（运行租约）
run 开始时签发：
1. `run_id`
2. `profile_id`
3. `revision`
4. `issued_at`

约束：
1. 同一 `run_id` 全程使用同一 revision。
2. 配置更新仅影响后续新 run。
3. run 仅持有配置引用（`profile_id + revision`），不复制/落盘独立配置文件。

## 6. 配置协议（最终态）

### 6.1 命令
1. `CreateConfigProfileCommand`
2. `PublishConfigBaseSnapshotCommand`
3. `PublishConfigPatchCommand`
4. `ArchiveConfigProfileCommand`
5. `BindInstanceConfigProfileCommand`
6. `PinInstanceConfigRevisionCommand`
7. `IssueRunConfigLeaseCommand`

### 6.2 事件
1. `ConfigProfileCreatedEvent`
2. `ConfigBaseSnapshotPublishedEvent`
3. `ConfigPatchPublishedEvent`
4. `ConfigProfileArchivedEvent`
5. `InstanceConfigProfileBoundEvent`
6. `InstanceConfigRevisionPinnedEvent`
7. `RunConfigLeaseIssuedEvent`

### 6.3 Patch 语义（强制）
1. Patch 采用“显式字段变更”模型，不使用字段复用和隐式语义。
2. 每个字段只表达一个业务含义（例如 `provider_name` 仅表示 provider，不承载引用/内联双语义）。
3. Patch 应支持三态：`NoChange` / `Set(value)` / `Clear`。
4. 同一 revision 的 effective config 结果必须可重放、可验证、可哈希。

## 7. 分层职责

### 7.1 Domain
1. Profile 修订状态机。
2. Instance 绑定状态机。
3. Lease 不变量校验。

### 7.2 Application
1. 配置发布编排（snapshot/patch/revision）。
2. 绑定与 pin 编排。
3. run 入场 lease 签发与校验。

### 7.3 Infrastructure
1. 事件存储实现。
2. 投影到统一 Projection Pipeline。
3. ReadModel 实现（InMemory/Distributed）。

### 7.4 Host
1. 配置管理 API（command/query 分离）。
2. Workflow/Chat API 仅传 `profile_id`/`revision`/`lease`，不拼装业务配置。

## 8. 对现有模型的硬性替换要求
1. 删除 `GAgentBase<TState, TConfig>` 配置持久化模型。
2. 禁止业务配置读写 `Manifest.ConfigJson`。
3. 删除 `RoleGAgentState.app_config_*` 作为共享配置事实源的职责。
4. 删除 `SetRoleAppConfigEvent`（由配置域 patch 事件替代）。
5. `ConfigureRoleAgentEvent` 收敛为实例元信息与配置引用语义，不再承载共享配置正文。

## 9. Manifest 职责（重定义）
`AgentManifest` 仅保留框架元数据：
1. `agent_type_name`
2. `module_names`
3. `metadata`

禁止：
1. 禁止将业务配置放入 `ConfigJson`。
2. 禁止用 manifest 参与业务配置恢复与覆盖逻辑。

## 10. 关键执行链路（最终态）
1. 配置发布：`Host API -> Application -> ConfigProfileGAgent -> Events -> Projection -> ReadModel`
2. run 启动：`WorkflowGAgent -> Application -> Resolve(binding + revision) -> Issue lease`
3. role 执行：`RoleGAgent` 按 lease 读取固定 revision 的配置快照执行。
4. 配置变更：只影响未来 lease，不影响已有 run。
5. run 级不创建独立配置文件，仅记录固定 revision 引用用于可重放与审计。

## 11. 验证矩阵
| ID | 验证目标 | 命令 | 通过标准 |
|---|---|---|---|
| V1 | 架构守卫 | `bash tools/ci/architecture_guards.sh` | 无违例 |
| V2 | 投影路由守卫 | `bash tools/ci/projection_route_mapping_guard.sh` | 无违例 |
| V3 | 分片构建 | `bash tools/ci/solution_split_guards.sh` | 全绿 |
| V4 | 分片测试 | `bash tools/ci/solution_split_test_guards.sh` | 全绿 |
| V5 | 全量测试 | `dotnet test aevatar.slnx --nologo` | 全绿 |
| V6 | 轮询等待守卫 | `bash tools/ci/test_stability_guards.sh` | 无违例 |

## 12. Final DoD
1. 代码中不存在业务配置对 `Manifest.ConfigJson` 的读写。
2. 代码中不存在共享配置正文存储在业务 Actor 状态中的路径。
3. 运行路径全部显式携带 `RunConfigLease(profile_id, revision)`。
4. 配置更新路径仅剩 `ConfigProfile` 命令链路。
5. 统一投影链路可查询 `Profile / Revision / Binding / Lease`。
6. 文档、测试、门禁全部同步并通过。

## 13. 非目标
1. 不保留旧协议兼容层。
2. 不保留双事实源兜底行为。
3. 不引入“Manifest 配置覆盖优先级”规则。
