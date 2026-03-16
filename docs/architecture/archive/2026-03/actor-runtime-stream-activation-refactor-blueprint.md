# GAgent 配置与初始化分离彻底重构蓝图（Zero-Compatibility）

## 1. 文档元信息
1. 状态：`Final`
2. 版本：`v8`
3. 日期：`2026-03-05`
4. 决策级别：`Architecture Breaking Change`
5. 范围：
   1. `Aevatar.Foundation.Abstractions`
   2. `Aevatar.Foundation.Core`
   3. `Aevatar.Foundation.Runtime.Implementations.Local`
   4. `Aevatar.Foundation.Runtime.Implementations.Orleans`
   5. `Aevatar.AI.Abstractions`
   6. `Aevatar.AI.Core`
   7. `Aevatar.Workflow.*`
   8. `Aevatar.Configuration / Host`

## 2. 问题定义
当前实现在语义上把三种不同概念混在一起：
1. 配置（Configuration）：跨实例共享或实例级可持续策略。
2. 初始化参数（Initialization Arguments）：创建或首次激活时的一次性输入。
3. 运行态派生结果（Effective Runtime View）：合并和归一化后的执行快照。

导致的问题：
1. `Configure*` 在使用上承担了大量 init 责任，语义漂移。
2. `app_config_* / AppConfigPayload` 成为通用字符串槽位，绕开业务强类型模型。
3. `GAgentBase<TState,TConfig>.ConfigureAsync` 形成事件旁路，弱化 EventSourcing 事实源。
4. workflow roles 字段既像 init 又像 config，行为边界不清。
5. 文档口径与代码口径不一致，容易造成实现分叉。

## 3. 最终架构决策（强制）
1. 框架层保持 Zero-Manifest，不引入任何 Manifest 替代物。
2. `Configuration` 与 `Init Args` 强制分离，禁止同一字段双语义。
3. 配置是长期策略，初始化参数是一次性输入。
4. `Init` 是否持久化由该 GAgent 内部领域逻辑决定，框架不做隐式持久化。
5. 业务配置必须强类型建模，不再使用通用 `app_config_*` 字符串槽位。
6. 删除 `AppConfigPayload` 及其相关事件通道。
7. 删除 `RoleGAgentExtensionContract` 这类通用 app-config/app-state 补丁入口。
8. `GAgentBase<TState,TConfig>.EffectiveConfig` 定义为运行态快照，不是事实源。
9. 实例覆盖只允许通过 `Command -> Event -> Apply(State)` 生效。
10. `GAgentBase<TState,TConfig>.ConfigureAsync` 作为公共旁路入口删除。
11. 基类继续负责 `class defaults + state overrides => effective config` 统一合并。
12. class defaults 由 Host/Infrastructure 提供，支持集群共享与无重启热更新。
13. `Core/Abstractions` 不直接依赖 `IConfiguration/IOptions*`。
14. workflow `roles` 默认解释为 Role 初始化规格（Role Initialization Spec）。
15. Role 初始化参数变更不走“配置补丁”，而走显式重初始化策略。
16. `DefaultModules` 不进入 Foundation 通用语义。
17. Local 激活索引仅保留在 Local Runtime 内部实现。
18. 不做兼容适配，不保留双写，不保留旧字段。

## 4. 目标模型

### 4.1 语义分层
1. Configuration 层：
   1. class-level defaults（跨实例共享）
   2. instance-level overrides（实例持久化策略）
2. Initialization 层：
   1. 仅在 create/first-activate/reinit 执行
   2. 是否入 state 由业务 init 逻辑显式决定
3. Runtime View 层：
   1. `EffectiveConfig` 为已合并快照
   2. 可被重算覆盖
   3. 不独立持久化

### 4.2 配置模型
1. 合并公式：`effective_config = Merge(class_defaults, state_overrides)`。
2. class defaults 来源：`.NET Configuration + Options + Distributed Config Source`。
3. instance overrides 来源：`TState`（由 EventSourcing 回放恢复）。
4. `EffectiveConfig` 为运行态缓存，只在主线程更新。
5. 热更新语义：
   1. provider reload 推进 class defaults 版本
   2. 实例在消息入口比较版本并重算
   3. 不要求重启节点

### 4.3 初始化模型
1. 每个支持初始化的 GAgent 定义强类型 init 命令/事件。
2. init 执行流程：
   1. 接收 `InitCommand`
   2. 校验与规范化
   3. 转换为领域事件
   4. 事件落库
   5. `Apply` 更新 state（如需要持久化）
3. init 幂等要求：
   1. 相同 init fingerprint 必须幂等
   2. 不同 fingerprint 由业务选择 reject 或 reinit
4. init 参数禁止自动进入框架级配置存储。

### 4.4 Workflow Role 模型
1. `workflow.roles` 是 Role 初始化规格，不是通用配置中心协议。
2. `WorkflowGAgent` 负责：
   1. 按 role spec 创建/获取子 role actor
   2. 发送 Role 初始化事件
3. role 字段分类：
   1. `id`：拓扑标识
   2. `name`：角色身份
   3. `system_prompt/provider/model/temperature/max_*`：初始化输入
   4. `event_modules/event_routes`：初始化装配输入
   5. `connectors`：workflow 步骤执行授权输入
4. Role 配置变更策略：
   1. 不再通过 `SetRoleAppConfigEvent` 补丁
   2. 使用显式 `ReinitializeRoleAgent` 或“销毁重建 role actor”

### 4.5 RoleGAgent 定位重置
1. `RoleGAgent` 只承担通用角色能力，不承载业务 app-config 槽位。
2. 业务扩展方式：
   1. 业务自定义 `GAgentBase<TState,TConfig>` 子类
   2. 使用强类型事件与强类型 state
3. 不再支持“框架层统一 JSON app_config 注入”的扩展模式。

## 5. 协议与模型重构

### 5.1 Foundation/Core
1. 删除公共入口：`GAgentBase<TState,TConfig>.ConfigureAsync(TConfig)`。
2. 保留并强化：
   1. `MergeEffectiveConfig(TConfig classDefaults, TState state)`
   2. `OnEffectiveConfigChangedAsync(TConfig config, CancellationToken ct)`
3. 禁止基类做配置独立持久化。
4. 保留 `IAgentClassDefaultsProvider<TConfig>`，但实现归属 Host/Infrastructure。

### 5.2 AI 协议重构（`ai_messages.proto`）
删除：
1. `AppConfigPayload`
2. `SetRoleAppConfigEvent`
3. `SetRoleAppStateEvent`
4. `AIAgentConfigOverrides` 中通用 app-config 字段
5. `RoleGAgentState` 中通用 app-state/app-config 扩展字段

新增或替换：
1. `InitializeRoleAgentEvent`（初始化专用）
2. `RoleInitializationState`（如需持久化初始化结果）
3. 业务扩展事件由业务包定义，不进入 `Aevatar.AI.Abstractions` 通用协议

### 5.3 AI Core 重构
1. 删除 `RoleGAgentExtensionContract`。
2. 删除 `AIAgentConfig` 中 `AppConfigJson/AppConfigCodec/AppConfigSchemaVersion`。
3. `RoleGAgent` 改为仅处理：
   1. 初始化事件
   2. 聊天执行事件
   3. 必要的角色生命周期事件
4. 业务 app state/config 迁移到业务自定义 GAgent。

### 5.4 Workflow Core 重构
1. `CreateRoleAgentConfigureEnvelope(...)` 替换为 `CreateRoleAgentInitEnvelope(...)`。
2. workflow reconfigure 时：
   1. 对 role init spec 差异做显式策略
   2. 禁止静默覆盖已初始化 role 的内部策略状态
3. `connectors` 保持在 workflow 执行域，不写入 role 通用 config。

## 6. 生命周期语义

### 6.1 Create
1. runtime 创建 actor。
2. 注入 class defaults provider 与基础依赖。
3. 不做任何隐式 init 参数持久化。

### 6.2 Activate
1. EventSourcing 回放恢复 `TState`。
2. 基类执行 `class defaults + state overrides` 合并。
3. 生成运行态 `EffectiveConfig` 快照。

### 6.3 Initialize
1. 由业务显式触发初始化命令。
2. 业务决定哪些 init 参数写入 state。
3. 初始化完成后，若 state 变化则触发 config 重算。

### 6.4 Reinitialize
1. 仅通过显式 reinit 命令或 actor 重建。
2. 不允许通过通用 config patch 旁路重配。

### 6.5 Class Defaults Hot Reload
1. 配置中心发布新版本。
2. 节点加载新快照。
3. 实例在主线程入口检测版本前进并重算。

## 7. 不兼容删除清单
1. `AppConfigPayload`
2. `SetRoleAppConfigEvent`
3. `SetRoleAppStateEvent`
4. `RoleGAgentExtensionContract`
5. `AIAgentConfig.AppConfigJson`
6. `AIAgentConfig.AppConfigCodec`
7. `AIAgentConfig.AppConfigSchemaVersion`
8. `GAgentBase<TState,TConfig>.ConfigureAsync(TConfig)` 公共旁路
9. 文档中所有 `app_config_*` 与“零派生通用 app-config 槽位”叙述

## 8. 实施工作包（WBS）

### WP1：契约与基类瘦身
1. 删除 Foundation/Core 配置旁路接口。
2. 收敛 `EffectiveConfig` 语义为 runtime view。
3. 补齐 fail-fast 校验与注释。

### WP2：AI 协议重建
1. 重写 `ai_messages.proto`。
2. 删除通用 app-config/app-state 事件。
3. 引入初始化专用协议。

### WP3：AI Core 改造
1. 重构 `RoleGAgent` 事件处理模型。
2. 删除 extension contract 与通用 patch 代码。
3. 调整 `AIGAgentBase` 配置字段与合并逻辑。

### WP4：Workflow Role 初始化改造
1. `WorkflowGAgent` 改用 role init 事件。
2. 引入 role reinit 策略（reject/recreate）。
3. 保持 connectors 在 workflow 执行域内传递。

### WP5：Host/Infrastructure 配置控制面
1. 建立 class defaults 的版本化发布。
2. 完成节点热更新链路。
3. 提供灰度与回滚机制。

### WP6：测试与门禁
1. 删除旧 `app_config_*` 断言测试。
2. 新增 init 幂等/reinit/重建测试。
3. 新增守卫禁止通用 app-config 槽位回流。
4. 新增守卫禁止公共 `ConfigureAsync(TConfig)` 旁路回流。

### WP7：文档收敛
1. 更新 `ROLE.md`、`WORKFLOW.md`、`FOUNDATION.md`。
2. 删除所有与 `AppConfigPayload` 相关示例。
3. 保证“配置 vs 初始化”口径一致。

## 9. 代码级落点清单
1. `src/Aevatar.Foundation.Core/GAgentBase.TState.TConfig.cs`
2. `src/Aevatar.AI.Abstractions/ai_messages.proto`
3. `src/Aevatar.AI.Abstractions/RoleGAgentExtensionContract.cs`（删除）
4. `src/Aevatar.AI.Core/AIGAgentBase.cs`
5. `src/Aevatar.AI.Core/RoleGAgent.cs`
6. `src/workflow/Aevatar.Workflow.Core/WorkflowGAgent.cs`
7. `docs/ROLE.md`
8. `docs/WORKFLOW.md`
9. `docs/FOUNDATION.md`

## 10. 验证矩阵
1. `dotnet restore aevatar.slnx --nologo`
2. `dotnet build aevatar.slnx --nologo`
3. `dotnet test aevatar.slnx --nologo`
4. `bash tools/ci/architecture_guards.sh`
5. `bash tools/ci/solution_split_guards.sh`
6. `bash tools/ci/solution_split_test_guards.sh`
7. `bash tools/ci/test_stability_guards.sh`

## 11. 验收标准（DoD）
1. 框架层不存在 `app_config_*` 通用语义。
2. `EffectiveConfig` 与 `Init Args` 边界可由代码结构直接看出。
3. 所有配置变更路径均可追溯到 state/event 或 class defaults。
4. workflow role 初始化采用显式 init 协议。
5. 业务扩展通过强类型 GAgent/State/Event 完成，不依赖 JSON 配置槽位。
6. 文档、代码、测试三者语义一致。

## 12. 非目标
1. 不提供旧协议迁移脚本。
2. 不保留兼容字段。
3. 不保留运行期兼容开关。
4. 不引入新的框架级“通用业务配置槽位”。
