# GAgent 平台入口与装配重构蓝图（2026-03-13）

## 1. 文档元信息

- 状态：Completed
- 版本：R1
- 日期：2026-03-13
- 关联文档：
  - `AGENTS.md`
  - `docs/FOUNDATION.md`
  - `docs/SCRIPTING_ARCHITECTURE.md`
  - `docs/architecture/2026-03-13-gagent-protocol-series-closeout.md`
  - `src/Aevatar.Hosting/AevatarCapabilityHostExtensions.cs`
  - `src/workflow/extensions/Aevatar.Workflow.Extensions.Hosting/AevatarPlatformHostBuilderExtensions.cs`
  - `src/workflow/Aevatar.Workflow.Infrastructure/DependencyInjection/WorkflowCapabilityServiceCollectionExtensions.cs`
  - `src/Aevatar.Scripting.Hosting/CapabilityApi/ScriptCapabilityHostBuilderExtensions.cs`
  - `src/Aevatar.Scripting.Hosting/DependencyInjection/ServiceCollectionExtensions.cs`

## 2. 一句话结论

本次已经完成 `entry + composition` 重构收口：

1. 统一平台入口
2. 明确静态装配
3. 明确动态装配
4. 保持来源无关通信

## 3. 背景

`gagent-protocol` phase 1-5 已经完成：

1. `workflow / scripting / 静态 GAgent` 已经可以通过 `actorId + typed protobuf protocol` 互相通信
2. Host / Application 已经不再允许按 `actorId` 字面模式做来源分支
3. forward-only upgrade 也已经有显式验收

在本轮重构开始前，开发体验存在一个明显问题：

`能力存在，不等于入口清晰`

调用者当时容易困惑：

1. 到底应该加哪个 Host 扩展
2. `workflow` 和 `script` 是能力、入口，还是实现来源
3. 哪些东西应该在启动时装好，哪些应该在运行时动态创建
4. 静态 `GAgent` 应该如何纳入统一组合面

## 4. 当前问题分析

## 4.1 Host 入口 API 语义过胖

重构前入口最典型的是：

- `AddWorkflowCapabilityWithAIDefaults(...)`

它的真实职责并不只是 “加 workflow”：

1. 加 AI features
2. 加 workflow capability
3. 可选加 script capability
4. 加 workflow projection provider
5. 加 workflow AI projection extension

这导致命名和职责不一致：

1. 名字像 `workflow`
2. 实际像 “默认平台装配器”

## 4.2 Capability 注册与平台装配混在一起

当前代码里至少有三层：

1. `AddAevatarCapability(...)`
2. `AddWorkflowCapability(...)`
3. `AddScriptCapability(...)`

但它们既像：

1. endpoint capability 注册器
2. service bundle 注册器
3. 平台入口扩展

这三层边界不够清楚。

## 4.3 静态装配与动态装配没有显式模型

现在仓库里已经同时存在两种完全不同的装配语义：

### 静态装配

启动时决定：

1. 用哪种 runtime
2. 开哪些 capability family
3. 注册哪些 projection provider
4. 注册哪些内建静态 agent 类型
5. 加哪些默认 endpoint

### 动态装配

运行时决定：

1. upsert workflow definition
2. upsert script definition
3. create actor / spawn runtime
4. link actor
5. 替换某个服务当前绑定的依赖 actorId

当前这两类操作虽然在代码里客观存在，但没有一个统一的术语和文档模型。

## 4.4 “来源” 与 “能力” 容易被混淆

现在最容易混淆的几个词：

1. workflow
2. script
3. static gagent

它们在不同语境下分别可能表示：

1. capability family
2. implementation source
3. host endpoint set
4. runtime actor shape

这会直接导致入口设计失真。

## 5. 设计原则

下一阶段入口与装配重构必须遵守：

1. 不再重构消息协议内核
2. 不再把 workflow/script/static 统一成新的来源对象模型
3. 只重构平台入口和装配语义
4. 静态装配与动态装配必须显式分层
5. 平台入口只负责平台能力启用，不负责业务实例绑定
6. 运行期 actor 绑定只能由 actor-owned state 或 typed command 维护
7. 对调用方继续保持 source-agnostic

## 6. 目标模型

## 6.1 平台入口只负责静态装配

建议引入统一根入口，例如：

- `AddAevatarPlatform(...)`

它只表达部署期/启动期装配，不表达任何业务实例绑定。

它应当负责：

1. runtime provider
2. capability family 开关
3. projection/read-model provider
4. endpoint bundle 挂载
5. observability / AI feature / maker 之类平台级扩展

它不应当负责：

1. 具体 workflow definition
2. 具体 script definition
3. 具体 actor 实例创建
4. 某个服务当前依赖哪个 workflow actor / script actor

## 6.2 静态装配与动态装配分离

### 静态装配（Deployment-Time Composition）

在 Host / DI / Program 层完成。

包括：

1. `UseLocalRuntime / UseOrleansRuntime`
2. `UseWorkflowCapability`
3. `UseScriptingCapability`
4. `UseAI`
5. `UseMaker`
6. `UseWorkflowProjection`
7. `UseScriptProjection`

### 动态装配（Runtime Composition）

在 actor command / application service / capability API 层完成。

包括：

1. `UpsertWorkflowDefinition`
2. `BindWorkflowRunDefinition`
3. `UpsertScriptDefinition`
4. `SpawnScriptRuntime`
5. `CreateStaticAgent`
6. `ConfigureServiceBindings`
7. `ReplaceServiceDependency`

## 6.3 通信面继续统一

入口重构后，通信模型不变：

1. `actorId` 仍然是不透明地址
2. `workflow / scripting / static` 仍然通过 typed protobuf protocol 通信
3. `SendToAsync / publication` 不再暴露来源判断
4. reply/query 仍然通过 typed request/reply 或 projection observation 获得结果

## 7. 推荐 API 形态

## 7.1 平台根入口

最终落地形态：

```csharp
builder.AddAevatarPlatform(options =>
{
    options.EnableMakerExtensions = true;
});
```

关键点：

1. 这是平台能力开关
2. 不出现 `workflow with AI defaults` 这种 fat convenience 命名
3. script 不再通过 workflow 入口顺带挂上
4. Workflow Host 直接使用 `builder.AddAevatarPlatform()`

## 7.2 capability registration 收窄成 bundle

当前：

1. `AddWorkflowCapability(...)`
2. `AddScriptCapability(...)`

建议收敛成更诚实的命名：

1. `AddWorkflowCapabilityBundle(...)`
2. `AddScriptingCapabilityBundle(...)`

它们只负责：

1. service registration
2. endpoint mapping registration

不负责：

1. 平台默认组合策略
2. AI 默认值
3. Mainnet 默认值

## 7.3 Mainnet / Workflow Host 应只做组合

当前宿主组合：

### Workflow Host

```csharp
builder.AddAevatarPlatform(options =>
{
});
```

### Mainnet Host

```csharp
builder.AddAevatarPlatform(options =>
{
    options.EnableMakerExtensions = true;
});
```

当前 `Program.cs` 已收口成组合根，不再混合注册脚本与 Maker 细节。

## 8. 动态装配建议

## 8.1 服务状态应显式持有依赖绑定

例如某个长期服务状态 actor，如果依赖：

1. 一个 workflow actor
2. 一个 scripting runtime actor
3. 一个静态 GAgent

那么这些依赖关系应显式持有在服务状态里，例如：

1. `workflow_actor_id`
2. `scripting_actor_id`
3. `evaluator_actor_id`

替换实现时，改的是这份绑定，而不是 Host。

## 8.2 动态替换通过 typed command 完成

例如：

1. `ReplaceScriptingImplementationRequested`
2. `ReplaceWorkflowImplementationRequested`
3. `RebindServiceDependenciesRequested`

由服务状态 actor 自己决定：

1. 新请求走新绑定
2. 历史状态保留
3. 旧 run 留旧实现

## 8.3 静态 GAgent 也纳入运行期装配面

静态 `GAgent` 不应被当成“只能在启动时写死”的特殊对象。

对运行期来说，它和 workflow/script 一样都是 actor target：

1. 可以被 `CreateAgent`
2. 可以被 `Link`
3. 可以被某个服务状态 actor 作为依赖绑定保存

差别只在实现来源，不在调用协议。

## 9. 非目标

本轮不做：

1. 不重做 `EnvelopeRoute`
2. 不重做 Foundation runtime
3. 不新增 workflow/script/static 来源枚举
4. 不要求把所有 capability API 统一成同一 HTTP shape
5. 不要求热替换正在运行的旧 run

## 10. 实施结果

## P1. 入口命名与职责收窄

1. 已新增统一平台根入口 `AddAevatarPlatform(...)`
2. 已删除旧的 `AddWorkflowCapabilityWithAIDefaults(...)`
3. Mainnet / Workflow Host 已切到新入口

验收：

1. `Program.cs` 中不再出现 workflow 顺带装 script 的隐式组合
2. Host 代码只描述平台能力组合

## P2. capability bundle 收窄

1. `WorkflowCapabilityHostBuilderExtensions` 已收窄为 workflow bundle 注册
2. `ScriptCapabilityHostBuilderExtensions` 已收窄为 script bundle 注册
3. platform root 已负责默认组合策略

验收：

1. workflow bundle 不再负责 AI defaults
2. script bundle 不再通过 workflow wrapper 间接启用

## P3. 动态装配模型文档化与样例化

1. 已为长期服务状态 actor 定义 binding state 文档模型
2. 已提供替换一种实现后继续运行的正式样例
3. 已补 runtime composition 文档

验收：

1. 至少一组测试覆盖 “静态 + workflow + scripting 服务状态 + 替换一种实现”

## 11. 验证结果

已执行：

1. `dotnet build aevatar.slnx --nologo`
2. Host 注册相关 targeted tests
3. `bash tools/ci/architecture_guards.sh`
4. `bash tools/ci/test_stability_guards.sh`

补充覆盖：

1. integration tests 已覆盖 cross-source runtime composition
2. Host 注册相关 targeted tests 已覆盖平台入口与 bundle 注册

## 12. 结论

当前系统已经完成入口与装配重构：

1. 一个统一平台入口
2. 一套显式静态装配模型
3. 一套显式动态装配模型
4. 一个继续保持 source-agnostic 的统一通信面

workflow、scripting、静态 `GAgent` 现在已经从“能互通的三种实现来源”，进一步收口成“能被清晰组合的同一平台能力单元”。
