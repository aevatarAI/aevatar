# GAgent 协议优先最小化实施文档（2026-03-12）

## 1. 文档元信息

- 状态：Proposed
- 版本：R1
- 日期：2026-03-12
- 关联文档：
  - `docs/architecture/2026-03-12-gagent-implementation-source-unification-blueprint.md`
  - `docs/architecture/2026-03-12-gagent-protocol-first-implementation-plan.md`
  - `AGENTS.md`
- 文档定位：
  - 本文只定义“第一阶段先改最核心部分”的最小实施方案。
  - 本文不追求一步到位统一所有入口、所有来源、所有模块。
  - 本文目标是先把最关键、最可验证、最能降低后续架构漂移的点做对。

## 2. 一句话目标

先不统一“实现来源模型”，只先统一三件最核心的事：

1. 用同一套协议 contract tests 治理静态 `GAgent`、workflow、scripting
2. 把通用 actor 通信能力从 `Scripting` 私有层上移到公共层
3. 给 workflow 增加最小通用 actor 通信面，避免继续靠专用步骤硬编码互通

## 3. 为什么先做这三件

这三件是最核心的，因为它们直接决定后续架构会往哪个方向走。

### 3.1 如果不先做 protocol contract tests

问题：

1. 大家会继续凭感觉讨论“来源是否统一”
2. 但没有任何机制证明静态/workflow/script 是否真的是同协议实现
3. 最终会退回到“看内部实现猜行为”

结论：

1. 先用 contract tests 固定行为边界
2. 再谈抽象和统一才有根据

### 3.2 如果不先上移通用 actor 通信能力

问题：

1. 通用能力会继续被误认为是 `Scripting` 私有能力
2. workflow 迟早会复制一套类似接口
3. 最后变成两套 actor 通信抽象并存

结论：

1. 先把最共性的 `Publish/SendTo/Create/Link` 能力中立化

### 3.3 如果不先给 workflow 通用 actor 通信面

问题：

1. workflow 继续通过专用步骤与外部 actor 交互
2. 跨来源互通会不断写成业务特例
3. module pack 会继续吸收不该吸收的能力

结论：

1. 先补最小的通用 actor 通信步骤
2. 把 workflow 拉回“编排面”，而不是“能力容器”

## 4. 最小范围

本轮只做以下内容：

### 4.1 必做

1. 建立一个跨来源协议 contract test 样本
2. 抽出通用 actor 通信端口
3. workflow 增加最小通用 actor 通信模块
4. 补最小守卫，禁止继续解析 `actorId` 或在 Host 理解来源

### 4.2 不做

1. 不统一所有创建入口
2. 不统一 definition schema
3. 不统一 workflow/script/static 初始化协议
4. 不做热替换存量 run
5. 不重写全部 workflow 专用步骤
6. 不把所有 scripting façade 一次性全拆完

## 5. 第一阶段具体改造项

## 5.1 改造项 A：建立第一个协议 contract test

### 目标

选一个最小但真实的协议，分别给出：

1. 静态 `GAgent` 实现
2. workflow 实现
3. scripting 实现

然后用同一套测试验证它们行为一致。

### 推荐协议

优先选最简单的请求-响应协议，不要一上来选复杂工作流。

建议：

1. 一个简单 `command`
2. 一个简单 `reply/completion`
3. 一个简单 `query`

例如“接收输入字符串，返回规范化输出并投影一个只读快照”这类最小协议。

### 验证内容

必须验证：

1. 三种实现都能接收同一 command
2. 三种实现都发出同一 completion/reply 语义
3. 三种实现都能被同一 query 语义读取
4. 三种实现都写入同一 read model 语义

### 代码落点建议

1. `test/Aevatar.Integration.Tests/`
2. 若需要拆共享 contract fixtures，可新建 `test/Aevatar.ProtocolContract.Tests/`

### 完成标准

1. 第一组 cross-source contract tests 通过
2. 团队后续讨论“是否同协议”时，统一以这套测试为准，而不是看来源

## 5.2 改造项 B：上移通用 actor 通信端口

### 目标

将当前 `Scripting` 中的通用 actor 通信能力抽成中立公共抽象。

当前候选：

1. `src/Aevatar.Scripting.Core/Ports/IGAgentRuntimePort.cs`
2. `src/Aevatar.Scripting.Infrastructure/Ports/RuntimeGAgentRuntimePort.cs`

### 最小改法

第一步不要大改行为，只做中立化：

1. 抽到公共项目或中立命名空间
2. 保持最小接口面不变
3. 让 workflow 能直接复用

### 允许保留的方法

最小稳定面只保留：

1. `PublishAsync`
2. `SendToAsync`
3. `CreateAsync`
4. `DestroyAsync`
5. `LinkAsync`
6. `UnlinkAsync`

### 禁止扩张

第一阶段不要新增：

1. 来源 kind
2. binding schema
3. 大而全 source registry
4. workflow/script 私有逻辑字段

### 完成标准

1. 通用 actor 通信能力不再挂在 `Scripting` 私有语义下
2. workflow 侧可引用同一抽象

## 5.3 改造项 C：给 workflow 增加最小通用 actor 通信面

### 目标

让 workflow 能以通用方式和任意协议兼容 actor 通信，而不是继续增加专用步骤。

### 第一阶段最小模块

只建议先做两个：

1. `gagent_send`
2. `gagent_query`

原因：

1. 这两个已经足够覆盖最核心的跨来源互通
2. `create/link/unlink` 可以放到下一阶段

### 模块职责

`gagent_send`

1. 接收目标 `actorId`
2. 接收 typed payload
3. 通过公共 actor 通信端口发送

`gagent_query`

1. 接收目标 `actorId`
2. 接收 typed query payload
3. 等待 typed reply
4. 将结果写回 workflow execution state

### 禁止事项

1. 不要把业务协议硬编码进模块名
2. 不要在模块内部做来源判断
3. 不要通过 `actorId` 前缀猜 workflow/script/static

### 完成标准

1. workflow 能通过通用模块和静态/script actor 完成一个真实协议交互

## 5.4 改造项 D：补最小治理守卫

### 目标

防止第一阶段刚做完，后面又漂回去。

### 最小守卫

建议先补以下静态守卫：

1. 禁止在 Host/Application 解析 `actorId`
2. 禁止新增以 workflow/script/static 来源名区分的 actor 通信抽象
3. 禁止 capability 私造平行 observation 主链

### 完成标准

1. 新增代码无法再靠来源字符串判断逻辑分支

## 6. 这轮不该先动的部分

以下内容都重要，但不应作为第一刀：

1. 统一 instance creator
2. 统一 definition snapshot/query contract
3. 重写全部 scripting orchestration façade
4. 重写全部 workflow steps
5. Mainnet 全量组装改造

原因不是它们不重要，而是：

1. 这些改动面太大
2. 若没有先用 contract tests 和通用 actor 通信面把主方向钉住，后面很容易越改越散

## 7. 推荐实施顺序

按顺序执行，不要并行扩散：

1. 先做协议 contract test 样本
2. 再上移通用 actor 通信端口
3. 再补 workflow 的 `gagent_send/gagent_query`
4. 再补最小守卫

原因：

1. 先有行为基线
2. 再抽共性
3. 再扩 workflow 互通面
4. 最后用守卫固定成果

## 8. 验收标准

第一阶段完成后，必须满足：

1. 至少有一个真实协议同时被静态/workflow/script 两种以上来源实现，并通过同一套 contract tests。
2. 通用 actor 通信能力已经中立化，不再挂在 `Scripting` 私有层。
3. workflow 已具备最小通用 actor 通信能力，不需要继续为互通造专用步骤。
4. Host/Application 中不能再通过 `actorId` 猜来源。

## 9. 收束性结论

第一阶段不要试图“一把梭”统一全部来源模型。  
先做最关键的三件事：

1. 用 contract tests 统一行为判断标准
2. 用公共 actor 通信端口统一跨来源调用面
3. 用 workflow 最小通用通信模块统一编排侧互通面

只要这三件先做对，后续再谈创建入口、definition 统一、更多模块和 façade 拆分，才会有稳定主线。
