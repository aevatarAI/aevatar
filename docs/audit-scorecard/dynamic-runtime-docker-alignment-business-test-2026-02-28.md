# Dynamic Runtime Docker 对齐业务仿真测试报告（2026-02-28）

## 1. 范围
- 目标：通过复杂多智能体业务链路测试，验证 Dynamic Runtime 与 Docker/Compose 关键语义的一致性。
- 测试项目：`test/Aevatar.DynamicRuntime.Application.Tests/Aevatar.DynamicRuntime.Application.Tests.csproj`
- 关键用例：`MultiAgentBusinessSimulation_ShouldAlignDockerLikeSemantics`

## 2. 业务场景（多 Agents）
- 服务拓扑（Compose）：
1. `gateway`（`hybrid`，1 副本）：模拟 LLM 意图解析入口。
2. `planner`（`event`，0 副本）：模拟 LLM 规划服务。
3. `worker`（`daemon`，2 副本）：模拟长期执行服务。
- 业务流：
1. `register -> activate` 三个服务（脚本存储于服务状态）。
2. `compose:apply` 建立 stack 与 service 拓扑。
3. `containers:create/start` 启动四个容器（含 worker 双副本）。
4. `exec` 串行执行 `gateway -> planner -> worker(并行2次)`。
5. `build-jobs:plan/validate/approve/execute` 触发 `worker` 新镜像发布与服务滚动更新。

## 3. Docker 语义对齐结果

### 3.1 已对齐
1. **Image digest 固定化（Pin by digest）**
- 容器创建时，`image_ref` 会解析为 `sha256:` digest 存储。
- `rollout` 与 `build execute` 结果也落到 digest，而非 tag。

2. **Build once, run many**
- `worker` 两个副本容器启动后使用同一 digest。
- 构建后新 digest 统一下发到 compose service，形成新一代运行版本。

3. **Compose 声明式收敛**
- `compose:apply` 后 `desired_generation == observed_generation`。
- `build execute` 自动触发目标 service 的 rollout 事件与状态收敛。

4. **Service Mode 基本语义**
- `event/hybrid` 服务建立 envelope subscription lease。
- `daemon` 服务不建立 event lease（符合模式边界）。

5. **Autonomous Build 主链路**
- `plan -> validate -> approve -> execute` 全链路可跑通。
- 发布后 image catalog 同步写入 `latest` 与 `buildJobId` tag。

6. **脚本服务化能力**
- 脚本定义以服务状态保存并可激活执行。
- 执行结果可模拟 LLM 输出（JSON 文本）并参与跨 agent 业务链路。

### 3.2 当前缺口（未完全对齐）
1. **真实 Reconcile 引擎**
- 当前为基础实现，缺少依赖拓扑排序、分批滚动策略、失败回滚策略验证。

2. **Envelope 实际投递到运行执行面**
- 目前验证了订阅与发布，但未形成完整“总线投递 -> run actor 消费 -> ack/retry”闭环。

3. **网络/资源/沙箱的生产级隔离**
- 当前为策略接口与默认实现，尚未进行容器级强隔离（例如 OS 级约束）验证。

4. **健康检查与 daemon 可用性SLO**
- 业务测试覆盖功能链路，但未覆盖 30 分钟持续可用性与自动恢复场景。

## 4. 结论评分（方案+实现快照）
- 语义对齐得分（本轮业务链路范围）：**89/100**
- 评分说明：
1. 核心控制面语义（image/build/compose/service mode）已基本打通。
2. 运行面高级能力（真实总线投递、强隔离、SLO 长稳）仍需后续实现与压测验证。

## 5. 复现命令
1. `dotnet test test/Aevatar.DynamicRuntime.Application.Tests/Aevatar.DynamicRuntime.Application.Tests.csproj --nologo`
2. 可重点过滤：
`dotnet test test/Aevatar.DynamicRuntime.Application.Tests/Aevatar.DynamicRuntime.Application.Tests.csproj --nologo --filter MultiAgentBusinessSimulation_ShouldAlignDockerLikeSemantics`

## 6. 对齐建议（下一步）
1. 落地 `Envelope Dispatch` 到 `Run` 的消费闭环，并补 `ack/retry/dedup` 端到端测试。
2. 将 `Compose Reconcile` 从基础实现升级为策略化滚动引擎（含失败回滚）。
3. 增加 `daemon/event/hybrid` 的 SLO 压测门禁测试并接入 `tools/ci/script_runtime_*_guards.sh`。
