# TODOS

## P1 — Critical Path

### [TODO-001] 指定跨产品首次体验 owner
- **What:** 一个人负责第一个外部用户从注册到跑通首个自动化的完整路径
- **Why:** 用户旅程穿越 NyxID + Aevatar + Ornn，产品边界不变但需要端到端视角
- **Effort:** S (human) → S (CC)
- **Depends on:** 无
- **Source:** CEO Review 2026-04-05, Codex challenge #3

## P2 — Important

### [TODO-002] Agent 心跳与成本可见性
- **What:** Console 显示 agent 实时状态 (alive/sleeping/error) + LLM API 调用次数和累计成本
- **Why:** 解决行业痛点 #3（成本不透明），让用户"看到" agent 活着，建立信任
- **Effort:** S (human) → S (CC)
- **Depends on:** NyxID 获得首批用户后优先补齐
- **Source:** CEO Review 2026-04-05, 扩展提案 #2

### [TODO-003] 每日摘要报告
- **What:** 每天早上发一份 agent 活动摘要（处理了多少事件、创建了多少 issue、成本多少）
- **Why:** "关上笔记本后发生了什么"的最直接证明，核心价值承诺的可见化
- **Effort:** S (human) → S (CC)
- **Depends on:** Agent 心跳基础设施 (TODO-002)
- **Source:** CEO Review 2026-04-05, 扩展提案 #7

## P3 — Future

### [TODO-004] 自然语言→YAML 生成
- **What:** Console 中自然语言描述→LLM 生成 YAML workflow
- **Why:** Ornn→YAML 路径的具体实现，降低 workflow 创建门槛
- **Effort:** M (human) → S (CC)
- **Depends on:** YAML schema validation + 参数化 recipe 验证。Pre-alpha 阶段 schema 变动会让生成的 YAML 快速失效。
- **Source:** CEO Review 2026-04-05, Codex challenge 建议延迟
