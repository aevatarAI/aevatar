# Branch Audit Scorecard: HEAD vs dev

**Date**: 2026-03-13
**Branch**: claude/tender-babbage (merged master baseline)
**Base**: dev
**Commits**: 177
**Files Changed**: 1,074 (109,172 insertions / 17,285 deletions)

---

## 总体评分

| 维度 | 分数 | 说明 |
|------|------|------|
| 架构质量 | 9/10 | 五阶段协议完整实现，边界清晰，EventRouter 清理彻底 |
| 代码质量 | 8/10 | 结构良好，中间件优先，错误处理完善；部分类偏大 |
| 测试覆盖 | 8/10 | 252 个测试文件变更（23%），Contract 测试完善；E2E 有缺口 |
| 文档质量 | 9/10 | 五阶段文档完整，收尾文档示范级；早期过时文档未清理 |
| 安全性 | 7/10 | 错误消息已脱敏，有超时保护；CLI Web 端点待审计 |
| 可操作性/运维 | 8/10 | Prometheus/Grafana 配置完备，CLI 工具新增，迁移脚本完善 |

### **综合得分：82 / 100**

---

## 变更摘要

### 核心架构变更
- **EventRouter 完全移除**：`IRouterHierarchyStore`、`InMemoryRouterStore` 已删除，拓扑状态内联到 `LocalActor`
- **TopologyAudience 统一**：`EventDirection.Up/Down` → `TopologyAudience.Parent/Children`，语义更明确
- **GAgentBase 精简**：模块清单持久化移出 Base，引入 `ICommittedStateEventPublisher`，增加持久化定时回调
- **Scripting.Core 新模块**：`ScriptRuntimeGAgent`（751行）+ `ScriptCatalogGAgent`（240行），Port-based 依赖注入

### GAgent 协议五阶段收尾
| 阶段 | 内容 | 状态 |
|------|------|------|
| Phase 1 | 跨源协议样本 + Foundation 边界收窄 + Workflow 发送能力 | ✅ 完成 |
| Phase 2 | Scripting Evolution Actor 所有权收敛 | ✅ 完成 |
| Phase 3 | Scripting Query/Observation 单链收敛 | ✅ 完成 |
| Phase 4 | EnvelopeRoute 正交语义重构 | ✅ 完成 |
| Phase 5 | 直接投递/发布/StateEvent 分层，Local 拓扑内联 | ✅ 完成 |
| Extra | Host/Mainnet 前向升级验证 | ✅ 完成 |

### 新增能力
- **Session Replay**：RoleGAgent 缓存已完成 LLM 响应，检测恢复场景
- **LLM 超时**：`ChatRequestEvent.TimeoutMs` 可配置超时，CancellationToken 协调
- **ToolCallLoop 中间件**：per-call 元数据跟踪（callId），最终调用独立路径，工具结果图片提取
- **Aevatar.Tools.Cli**：~6K 行新 CLI 工具，含 Browser Launcher、Web UI、Demo 端点
- **Failover LLM Provider**：新增带测试的故障转移 LLM 提供方

---

## 强项

1. **架构一致性**：五阶段实现连贯，Foundation 原语精简（`IActorRuntime`, `IActorDispatchPort`, `IEventPublisher`, `IEventContext`）
2. **中间件优先**：LLM/Tool/Command 管道均为 first-class hook，可组合
3. **Event-Sourcing 纪律**：状态通过 domain event 驱动，无直接突变
4. **错误处理**：`SanitizeFailureMessage()` 防止异常细节泄漏
5. **文档体系**：五阶段过程文档 + 收尾文档（`2026-03-13-gagent-protocol-series-closeout.md`）堪称示范

---

## 风险项

### 高优先级（建议发布前处理）

| 风险 | 严重度 | 建议 |
|------|--------|------|
| Session Replay 边缘用例（部分失败恢复） | 中 | 补充集成测试 + 状态机追踪验证 |
| CLI Web 端点安全性（`AppDemoPlaygroundEndpoints` ~2018行） | 中 | 发布前安全审计 |
| 工具结果 JSON 解析宽松（base64 内容无校验） | 中 | 添加媒体类型和大小限制 |
| Call ID 格式跨 RPC 边界一致性 | 中 | 格式校验测试 |

### 中优先级（下个迭代）

| 风险 | 建议 |
|------|------|
| LLM 超时 + 流式传输边缘条件 | 压力测试 |
| ScriptRuntimeGAgent 复杂度（751行） | 考虑拆分为更小单元 |
| `AIAgentConfigOverrides` 广播授权控制 | 审查授权逻辑 |
| 图片内容提取无大小上限 | 添加内存保护 |

---

## 测试覆盖详情

| 类别 | 关键文件 |
|------|----------|
| ToolCallLoop 单元测试 | `ToolCallLoopTests.cs` |
| Failover LLM Provider | `FailoverLLMProviderFactoryTests.cs` |
| 中间件管道 | `MiddlewarePipelineTests.cs`, `GenAIObservabilityMiddlewareTests.cs` |
| Session Replay 合约 | `RoleGAgentReplayContractTests.cs` |
| CQRS/Projection | `DefaultCommandInteractionServiceTests.cs`, `ProjectionOwnershipAndSessionHubTests.cs` |
| Proto 覆盖 | `FoundationAbstractionsProtoCoverageTests.cs` |
| EventEnvelope 语义 | `EventEnvelopeTests.cs` |

**测试文件占比**: 252 / 1,074 = **23.5%**（含 Contract 测试、Proto 覆盖测试）

---

## 安全评估

### 正面
- 错误消息脱敏（`SanitizeFailureMessage`）
- 失败标记使用前缀而非原始堆栈
- 请求身份跟踪（`LLMRequestMetadataKeys.*`）
- 超时保护防止 DoS

### 待改进
- CLI Demo Web 端点需安全审计（规模过大，2000+行）
- 工具结果 base64 解析需严格校验
- `AIAgentConfigOverrides` 广播需验证授权路径
- 建议为图片提取设置大小上限（防内存耗尽）

---

## 建议行动清单

- [ ] **P0** Session Replay 集成测试（含部分失败场景）
- [ ] **P0** Aevatar.Tools.Cli Web 端点安全审计
- [ ] **P1** 工具结果图片大小限制
- [ ] **P1** Call ID 跨边界格式验证测试
- [ ] **P1** LLM 超时 + 流式传输压力测试
- [ ] **P2** ScriptRuntimeGAgent 拆分方案设计
- [ ] **P2** 过时架构文档清理（早期 first-plan 文档）
- [ ] **P2** Session Replay 缓存淘汰策略文档化
