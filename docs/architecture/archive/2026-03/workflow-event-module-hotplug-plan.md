# Aevatar Event Module 热插拔扩展方案（设计稿）

更新时间：2026-03-02  
状态：Proposed（仅文档，未实施）  
适用范围：`Aevatar.Workflow.Core` / `Aevatar.Foundation.Core` / `Aevatar.Workflow.Infrastructure`

## 1. 背景与问题

当前 Aevatar 已支持两类扩展能力：

- 启动期通过 `IWorkflowModulePack` 注册模块（内置 + 扩展 pack）。
- 运行期通过 `SetModules/RegisterModule` 动态装配“已存在于进程内”的模块实例。

但尚不支持“运行中安装新的模块二进制并立即生效”的完整热插拔链路。

用户提出的想法是：新增一个 `execute_dotnet_file` 类型的 event module，通过执行任意 dotnet 文件来扩展模块能力。

## 2. 结论先行

`execute_dotnet_file` 可以作为 **开发期实验能力**，但不适合直接作为生产级主方案。  
推荐方案是：**模块包（Module Package）+ 受控加载器 + 生命周期治理 + 分布式一致目录**。

一句话结论：**不执行“随机 dotnet file”，只执行“经过校验、签名、可审计、可回滚”的模块包。**

## 3. 为什么不建议“随机 dotnet file 执行”

### 3.1 安全不可控（最高风险）

- 任意代码执行（RCE）风险极高。
- 默认拿到宿主进程权限，容易访问文件系统、网络、环境变量和密钥。
- 很难做最小权限控制与审计归因。

### 3.2 分布式一致性风险

- 多节点部署下，不同节点可能执行不同文件版本，导致行为漂移。
- 无中心化版本事实源时，回放与重试结果不可重复。

### 3.3 生命周期与回滚困难

- 无标准安装/启用/禁用/卸载协议，失败后难以自动回滚。
- 旧 run 与新 run 的兼容边界不清晰，容易出现“运行中切版本”的非确定行为。

### 3.4 可观测性与治理不足

- 很难统一记录 `moduleId/version/hash/permissions`。
- 无法稳定建立“变更 -> 运行结果 -> 审计证据”链路。

### 3.5 性能与稳定性问题

- 每次执行都 `dotnet` 启进程会带来明显冷启动抖动。
- 异常退出、僵尸进程、资源泄漏难治理。

## 4. 可行方案（推荐）：Module Package Hot-Plug

## 4.1 目标

- 支持模块热安装、热启用、热禁用、热回滚。
- 保持 Aevatar 分层与事实源约束：运行事实放 Actor/分布式状态，不放中间层进程内字典。
- 保证“新能力可扩展”与“生产治理可验证”并存。

## 4.2 方案总览

- `Module Package`：模块以标准包发布（含 manifest、二进制、依赖、签名）。
- `Module Catalog`：分布式一致目录，记录已安装/启用模块版本。
- `Module Runtime Host`：受控加载执行环境，负责加载、隔离、卸载模块。
- `WorkflowModuleFactory`：从“启动期静态快照”改为“可查询动态目录”。
- `WorkflowGAgent`：每次装配模块时读取当前目录快照；运行中的 pipeline 不强制中断。

## 4.3 包格式（建议）

```text
<module-id>-<version>.aevmod.zip
  /manifest.json
  /bin/<tfm>/*.dll
  /deps/*
  /signature.sig
```

`manifest.json` 建议字段：

- `moduleId`：唯一标识（如 `acme.sentiment_score`）
- `version`：语义化版本
- `entryType`：实现 `IEventModule` 的类型名
- `aliases`：step type 别名
- `aevatarApiVersion`：兼容 API 版本
- `permissions`：能力声明（network/filesystem/secrets/tool）
- `sha256`：包摘要
- `publisher`：发布者标识

## 4.4 生命周期（必须具备）

1. `Install`：上传包并做结构校验、签名校验、兼容性校验。  
2. `Enable`：写入目录事实源，标记目标版本为 active。  
3. `Disable`：停止新 run 使用该版本，保留已运行实例直至 drain。  
4. `Rollback`：回切上一个稳定版本。  
5. `Uninstall`：仅在无 active/无引用后允许卸载。  

## 4.5 执行语义（与当前运行时兼容）

- 新 run：读取目录中的 active 版本装配模块。
- 旧 run：继续使用其启动时已装配版本，避免中途行为突变。
- `WorkflowValidator` 的 `known step types` 来自“内置 + 目录 active 模块”联合集合。

## 4.6 安全与治理基线

- 默认拒绝：未签名、未声明权限、版本不兼容的包一律拒绝启用。
- 最小权限：模块只拿到声明过的能力。
- 审计强制：每次执行记录 `moduleId/version/hash/runId/actorId/publisher`。
- 资源配额：CPU/内存/超时上限，防止恶意或异常模块拖垮宿主。

## 5. 与“execute dotnet file”想法的关系

## 5.1 可保留为开发模式（仅限本地）

可以保留一个 `execute_dotnet_file` 适配器用于本地快速试验，但必须满足：

- 默认关闭（需显式开关，例如 `AEVATAR_DEV_DOTNET_FILE_MODULE=true`）。
- 仅允许受信目录（allowlist 路径），禁止任意路径。
- 禁止生产环境启用（启动即硬拦截）。
- 每次执行带审计日志，且强制超时与资源限制。

## 5.2 生产主路径不走 file

生产环境统一使用 `Module Package`，不走“随机文件执行”。

## 6. 分阶段落地计划

## Phase 0（文档与接口冻结）

- 产物：本设计文档 + 核心接口草案。
- 目标：先冻结契约，避免后续频繁返工。

## Phase 1（最小可用热启用）

- 新增 `IEventModuleCatalog`（读写 active 模块清单）。
- `WorkflowModuleFactory` 改为查询 catalog，而非构造期冻结 map。
- 先支持“进程内已存在程序集”的启用/禁用切换。

## Phase 2（模块包与校验）

- 实现 package installer、签名校验、兼容校验。
- 新增 install/enable/disable/rollback/uninstall 管理 API（Host 层仅协议适配）。

## Phase 3（隔离执行与分布式一致）

- 引入受控加载器（可回收上下文或独立模块宿主进程）。
- 将目录事实源落到 Actor 持久态或分布式状态服务，保证跨节点一致。

## 7. 代码改造映射（建议）

- `Aevatar.Foundation.Abstractions`
  - 新增：`IEventModuleCatalog`、`ModuleDescriptor`、`ModuleLifecycleState`
- `Aevatar.Workflow.Core`
  - 调整：`WorkflowModuleFactory`（改为 catalog 驱动）
  - 调整：`WorkflowGAgent`（装配时读取 catalog 快照）
  - 调整：`WorkflowPrimitiveCatalog` / `WorkflowValidator`（已知 step type 来源扩展）
- `Aevatar.Workflow.Infrastructure`
  - 新增：`ModulePackageInstaller`、`ModuleSignatureVerifier`、`ModuleCatalogStore`
- `Aevatar.Workflow.Host.Api`
  - 新增：模块生命周期管理端点（安装/启用/禁用/回滚）

## 8. 验收标准（DoD）

- 可在不重启宿主的前提下启用新模块并被新 run 使用。
- 旧 run 行为不受新版本影响（无中途切换）。
- 禁用后新 run 不再使用该模块，且支持回滚到上一版本。
- 所有模块执行有可追溯审计记录。
- 架构门禁与测试通过（build/test/guard scripts）。

## 9. 风险与应对

- 风险：模块 API 演进导致兼容性断裂。  
  应对：引入 `aevatarApiVersion` 与兼容矩阵校验。

- 风险：模块加载失败影响运行链路。  
  应对：启用前预热校验 + 失败自动回滚。

- 风险：分布式节点版本漂移。  
  应对：目录事实源集中化，节点只读同一版本事实。

## 10. 参考

- PluginCore 仓库（热插拔与生命周期设计启发）：  
  https://github.com/yiyungent/PluginCore
- PluginCore `IPlugin` 生命周期接口：  
  https://github.com/yiyungent/PluginCore/blob/main/src/PluginCore.IPlugins/IPlugin.cs
- PluginCore 可回收加载上下文：  
  https://github.com/yiyungent/PluginCore/blob/main/src/PluginCore/lmplements/CollectibleAssemblyLoadContext.cs

