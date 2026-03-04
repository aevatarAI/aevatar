# Actor Runtime 消息激活重构蓝图（Final）

## 1. 文档元信息
- 状态：`Proposed`
- 版本：`v1`
- 日期：`2026-03-05`
- 范围：`Aevatar.Foundation.Abstractions / Foundation.Runtime / Bootstrap / Orleans Runtime`
- 决策级别：`Architecture Breaking Change`

## 2. 背景与问题
当前框架层把“恢复（restore）”定义为通用能力：
1. `IActorRuntime` 暴露 `RestoreAllAsync`。
2. 默认宿主启动时注册 `ActorRestoreHostedService` 并调用 `RestoreAllAsync`。

该设计与“Actor 默认由消息触发激活”存在冲突，且把实现细节上提到了框架契约层。

## 3. 关键结论（最终决策）
1. 框架默认语义统一为：**消息驱动激活（message/stream activated）**。
2. 框架抽象层不再定义 `RestoreAllAsync`。
3. 启动时全量恢复不再是宿主默认行为。
4. 若某 runtime 需要恢复策略，必须在**实现内部**完成，不上浮到 `Abstractions` 与 `Host`。

## 4. 当前基线（代码事实）
1. `IActorRuntime` 包含 `RestoreAllAsync`。
   - 证据：`src/Aevatar.Foundation.Abstractions/IActorRuntime.cs`
2. 默认 host 通过 `ActorRestoreHostedService` 调用 runtime restore。
   - 证据：`src/Aevatar.Bootstrap/Hosting/ActorRestoreHostedService.cs`
   - 证据：`src/Aevatar.Bootstrap/Hosting/WebApplicationBuilderExtensions.cs`
3. Local runtime 实现了 manifest 扫描恢复。
   - 证据：`src/Aevatar.Foundation.Runtime/Actor/LocalActorRuntime.cs`
4. Orleans runtime 的 `RestoreAllAsync` 为 no-op，实际依赖 Grain 惰性激活。
   - 证据：`src/Aevatar.Foundation.Runtime.Implementations.Orleans/Actors/OrleansActorRuntime.cs`
   - 证据：`src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/RuntimeActorGrain.cs`

## 5. 目标架构

### 5.1 抽象层（Foundation.Abstractions）
1. `IActorRuntime` 仅保留生命周期与拓扑能力：
   - `Create / Destroy / Get / Exists / Link / Unlink`
2. 删除 `RestoreAllAsync`。

### 5.2 宿主层（Bootstrap）
1. 删除 `ActorRestoreHostedService`。
2. 删除 `EnableActorRestoreOnStartup` 及 `ActorRuntime:RestoreOnStartup`。
3. 默认启动不做“框架级恢复编排”。

### 5.3 Runtime 实现层（Local / Orleans）
1. Orleans：保持现有惰性激活语义。
2. Local：实现内部惰性 materialize（按需创建），不依赖 host restore。
3. 实现层可使用 `IAgentManifestStore` 作为运行时元数据来源，但这是 runtime 内部机制，不是框架契约。

## 6. Local Runtime 目标行为（强制）
1. `GetAsync(actorId)`：
   - 若 actor 在内存，直接返回。
   - 若不在内存且 manifest 可解析类型，则内部创建并激活后返回。
2. `ExistsAsync(actorId)`：
   - 基于“内存存在或可从 manifest materialize”给出一致语义。
3. `DestroyAsync(actorId)`：
   - 保持当前销毁与 manifest 清理语义。
4. 不提供对外 `RestoreAll` 入口。

## 7. Manifest 职责边界（重申）
1. Manifest 是 runtime 元数据登记，不是框架恢复编排入口。
2. Manifest 使用场景只允许在 runtime 实现内部（如 Local 惰性 materialize）。
3. 宿主与应用层不直接驱动“全量扫描恢复”。

## 8. 变更清单（按模块）

### 8.1 Foundation.Abstractions
1. 修改 `IActorRuntime`：删除 `RestoreAllAsync`。
2. 更新 Abstractions README 中对 runtime 能力描述。

### 8.2 Foundation.Runtime（Local）
1. 删除/内收 `RestoreAllAsync` 公共语义。
2. 在 `GetAsync/ExistsAsync` 内实现按需 materialize。
3. 补充相关日志与容错（类型解析失败 fail-fast + warning）。

### 8.3 Foundation.Runtime.Implementations.Orleans
1. 删除接口适配残留（`RestoreAllAsync` 实现）。
2. 保持 Grain `OnActivateAsync -> InitializeAgentInternalAsync` 路径不变。

### 8.4 Bootstrap
1. 删除 `ActorRestoreHostedService` 文件与注册逻辑。
2. 清理 host options 中 restore 相关配置项。

### 8.5 Tests
1. 删除/改写所有 `RestoreAllAsync` 相关断言。
2. 新增 Local 惰性 materialize 测试：
   - manifest 存在 -> `GetAsync` 首次命中自动激活。
   - manifest 缺失/类型不可解析 -> 返回 null 或抛约定异常（需统一约定）。
3. 保持 Orleans 现有惰性激活用例通过。

## 9. 实施工作包（WBS）
1. `WP1`：移除抽象层 `RestoreAllAsync`。
2. `WP2`：清理 Bootstrap restore hosted service 与配置项。
3. `WP3`：Local runtime 按需 materialize 实现与单测。
4. `WP4`：Orleans runtime 接口收敛与回归。
5. `WP5`：文档与守卫同步。

## 10. 风险与治理
1. 风险：Local runtime 首次访问延迟增加。  
   治理：materialize 过程可观测化（metrics + structured logs）。
2. 风险：manifest 污染导致类型解析失败。  
   治理：fail-fast + 明确错误信息 + 运维审计脚本。
3. 风险：历史测试依赖 `RestoreAll` 语义。  
   治理：统一改造为“首次访问触发激活”的行为断言。

## 11. 验证矩阵
| ID | 验证目标 | 命令 | 通过标准 |
|---|---|---|---|
| V1 | Foundation 构建 | `dotnet build aevatar.foundation.slnf --nologo` | 全绿 |
| V2 | Bootstrap 测试 | `dotnet test test/Aevatar.Bootstrap.Tests/Aevatar.Bootstrap.Tests.csproj --nologo` | 全绿 |
| V3 | Runtime Hosting 测试 | `dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj --nologo` | 全绿 |
| V4 | Workflow Host API 测试 | `dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo` | 全绿 |
| V5 | 架构守卫 | `bash tools/ci/architecture_guards.sh` | 无违例 |

## 12. Final DoD
1. `IActorRuntime` 不再包含 `RestoreAllAsync`。
2. 默认 host 不再注册 Actor restore hosted service。
3. Local/Orleans 均在实现层维持各自激活策略，框架层无恢复编排逻辑。
4. 测试与文档全部同步，门禁通过。

## 13. 非目标
1. 不在本次重构中改动业务层 workflow 编排语义。
2. 不新增第二套 runtime 抽象接口。
3. 不保留对旧 `RestoreAll` 入口的兼容适配。
