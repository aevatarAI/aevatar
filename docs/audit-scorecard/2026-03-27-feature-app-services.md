# 架构审计评分卡：feature/app-services

| 项目 | 值 |
|---|---|
| 审计日期 | 2026-03-27 |
| 审计范围 | `feature/app-services` 分支相对 `dev` 全量增量 |
| 审计方法 | diff 增量审查 + 构建验证 + 架构规则逐项对照 |
| 变更规模 | 1183 files, +167 743 / −45 014 lines |
| 提交数 | ~140 commits |
| 主要变更域 | 前端 console-web 新建（~2528 TS/TSX 文件）、后端 scope-first 绑定 / projection 重构 / Kafka provider / 架构门禁增强 |

---

## 1. 客观验证结果

| 命令 | 结果 |
|---|---|
| `dotnet build aevatar.slnx --nologo` | **PASS** (0 errors, 8 warnings) |
| 架构门禁脚本存在性 | 31 scripts in `tools/ci/`, 全部可执行 |
| `GetAwaiter().GetResult()` 扫描 | **未发现** |
| `TypeUrl.Contains(...)` 扫描 | **未发现** |
| 中间层 ID→事实态字典扫描 | **未发现违规**（详见 §5） |
| query-time IEventStore 读取扫描 | **未发现** |
| JSON 序列化用于事实存储扫描 | **未发现**（旧 JSON converter 已删除） |

---

## 2. 整体评分

| 维度 | 权重 | 得分 | 扣分 | 说明 |
|---|---:|---:|---:|---|
| 分层与依赖反转 | 20 | 19 | −1 | 前端 `readError()` 重复实现（Minor） |
| CQRS 与统一投影链路 | 20 | 20 | 0 | 投影走 `EventEnvelope` 统一链路，旧 JSON converter 已清理 |
| Projection 编排与状态约束 | 20 | 19 | −1 | `CachedScriptBehaviorArtifactResolver` ConcurrentDictionary 缓存需文档化升级路径（Medium） |
| 读写分离与会话语义 | 15 | 15 | 0 | Application Service 正确走 ReadPort 查询，Command 走 Port 投递 |
| 命名语义与冗余清理 | 10 | 10 | 0 | 多项 rename 完成（Registry→Catalog, InMemory→Configured），死代码已删除 |
| 可验证性（门禁/构建/测试） | 15 | 14 | −1 | 前端测试覆盖良好（29+ test 文件），后端新增架构测试；但分支未跑全量 CI guard（Medium） |
| **总计** | **100** | **97** | **−3** | |

**等级：A+**

---

## 3. 分模块评分

| 模块 | 分数 | 结论 |
|---|---:|---|
| GAgentService (scope-first binding) | 98 | `ScopeBindingCommandApplicationService` 分层清晰，Port 注入正确，测试验证命令顺序 |
| CQRS Projection | 97 | 统一 `EventEnvelope` 物化，`ScriptReadModelProjector` 重构为 `ICurrentStateProjectionMaterializer`，旧 JSON 序列化已清理 |
| Kafka Provider | 95 | lock 用于 transport 基础设施（offset tracking）属合理场景，单元测试已补充 |
| Workflow | 96 | `WorkflowDefinitionRegistry→Catalog` rename 完成，`ImmutableDictionary` 替换可变集合 |
| Scripting | 95 | `CachedScriptBehaviorArtifactResolver` 使用 `ConcurrentDictionary<string, Lazy<>>` 作为编译缓存，已有注释标注升级路径 |
| Studio Host (Endpoints) | 97 | 端点仅做宿主与组合，业务编排委托 Application Service |
| 前端 (console-web) | 94 | 全新模块，类型安全好（Decoder 模式），测试覆盖到位，`readError()`/`JSON_HEADERS` 重复是唯一 DRY 问题 |
| Architecture Tests & Guards | 98 | 新增 `ForbiddenPatternTests`（中间层字典、IEventStore query 依赖），门禁覆盖全面 |
| Docs & Guards | 96 | 31 个 CI guard 脚本，架构文档同步更新 |

---

## 4. 关键证据（加分项）

### 4.1 架构测试新增（强正面）

- `test/Aevatar.Architecture.Tests/Rules/ForbiddenPatternTests.cs` 新增：
  - `MiddleLayer_ShouldNot_Declare_IdMappingDictionaries()` — 禁止中间层 ID→事实态字典
  - `QueryReadPorts_ShouldNot_DependOn_IEventStore()` — 禁止 query-time replay
  - `SerializationConstraintTests` — 核心层禁止 `System.Text.Json.JsonSerializer`

### 4.2 状态管理正向重构

- `ReloadableLLMProviderFactory`: `lock` → `ImmutableDictionary + Interlocked.CompareExchange` (lock-free)
- `ToolCallModule`: `Dictionary + SemaphoreSlim` → `ConcurrentDictionary<string, Lazy<Task>>`
- `MakerRecursiveModule`: 单例状态 → workflow execution context 作用域
- `ScriptReadModelMaterializationCompiler`: 删除 lock + Dictionary 缓存
- `RunManager` with `ConcurrentDictionary` 事实态：**已删除**

### 4.3 命名语义清理

- `InMemoryServiceRevisionArtifactStore` → `ConfiguredServiceRevisionArtifactStore`
- `InMemoryConnectorRegistry` → `ConfiguredConnectorRegistry`
- `WorkflowDefinitionRegistry` → `WorkflowDefinitionCatalog`
- `ChatRequestEvent.metadata` → `headers`（命令头语义），`command_id` 提升为 typed field

### 4.4 前端类型安全

- `Decoder<T>` 模式实现运行时类型验证，大小写兼容 C# PascalCase
- `encodeURIComponent()` 一致使用防止注入
- `authFetch()` 正确注入 Bearer token，无硬编码 URL

---

## 5. 扣分项

### 5.1 前端 `readError()` 重复实现 — Medium (−1)

**证据**: `runtimeRunsApi.ts`, `scriptsApi.ts`, `scopesApi.ts` 各含独立 `readError()` 实现。

**影响**: 维度 1（分层与依赖反转）— 重复的错误解析逻辑未提取到 `http/client.ts` 共享层。

**修复建议**: 提取到 `shared/http/client.ts`，P2 优先级。

### 5.2 `CachedScriptBehaviorArtifactResolver` 缓存升级路径 — Medium (−1)

**证据**: `src/Aevatar.Scripting.Infrastructure/Compilation/CachedScriptBehaviorArtifactResolver.cs:9` — `ConcurrentDictionary<string, Lazy<ScriptBehaviorArtifact>>`。

**影响**: 维度 3（Projection 编排与状态约束）— 编译缓存在多节点部署时需分布式替换。代码注释已标注，但无 tracking issue。

**修复建议**: 创建 tracking issue 记录升级路径，P2 优先级。不属于当前阻断项（基线豁免 §1 — InMemory 实现可替换）。

### 5.3 全量 CI guard 未在分支验证 — Medium (−1)

**证据**: 构建通过，但 `bash tools/ci/architecture_guards.sh` 全量结果未在分支 CI 中记录。

**影响**: 维度 6（可验证性）— 合并前建议完整跑一轮。

**修复建议**: 合并前执行全量 `architecture_guards.sh` 并记录结果，P1 优先级。

---

## 6. 阻断项

**无阻断项。** 所有扣分均为 Medium 级别。

---

## 7. 改进优先级建议

| 优先级 | 项目 | 说明 |
|---|---|---|
| P1 | 合并前跑全量 `architecture_guards.sh` | 确保所有门禁通过 |
| P2 | 提取前端 `readError()` 到共享层 | DRY，减少维护成本 |
| P2 | `CachedScriptBehaviorArtifactResolver` 升级 tracking issue | 记录分布式缓存演进路径 |
| P2 | 前端 `JSON_HEADERS` 常量去重 | 提取到 `shared/http/constants.ts` |

---

## 8. 非扣分观察项（基线口径）

1. **InMemory 实现保留**: `CachedScriptBehaviorArtifactResolver` 编译缓存、Elasticsearch index lifecycle manager — 均满足"可替换、定位清晰"前提，不扣分。
2. **Lock 用于基础设施层**: Kafka provider offset tracking、Elasticsearch index init — 属于 transport/storage 基础设施，非 actor 业务状态，不扣分。
3. **ProjectReference 保留**: 当前阶段基线，模块边界清晰，不扣分。

---

## 9. 总结

`feature/app-services` 分支是一次高质量的大规模变更，涵盖后端 scope-first 绑定架构、projection 统一链路重构、并发状态管理 lock-free 改造、以及全新前端 console-web 模块。**核心架构约束零违规**，多项正向重构（删除事实态字典、rename 语义对齐、新增架构门禁测试）显著提升了代码库质量。唯一需要在合并前完成的是全量 CI guard 验证。

**最终评级：A+ (97/100)**
