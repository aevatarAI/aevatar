# 任务：审计 aevatar 仓库中违反软件工程哲学的位置，输出可并行重构的 cluster 列表

你正在 `$REPO_ROOT` 仓库中以无人值守方式工作。这不是探索性对话——必须输出一个**结构化、可被另一个 codex 进程消费**的产物。

## 上下文（必读）

1. `CLAUDE.md` —— 顶级架构约束；所有"违反"必须能在这里找到对应的"强制"条款。
2. `AGENTS.md` —— 协作规范（如有）。
3. `docs/canon/` —— 权威架构参考。
4. `docs/audit-scorecard/` —— 历史审计；作为已知未修复问题的起点。
5. 当前 git 分支：通过 `git branch --show-current` 获取。

## 目标

识别 **3~6 个独立的重构 cluster**。每个 cluster 必须满足：

- **独立性**：与其它 cluster 文件/目录交集 ≤ 5%。
- **边界可控**：单 cluster 改动文件数 ≤ 30。
- **不新增功能**：只清理违反哲学的位置。
- **明确归因**：能说清"当前错误模式"和"重构遵循的设计原则"（后者可直接写进代码注释）。

## 优先扫描方向（CLAUDE.md 强制条款）

1. 跨层耦合 / 反向依赖。
2. 中间层进程内事实状态（Dictionary 持有 entity/run/session 上下文）。
3. Actor 按技术功能拆分（`*WriteActor` / `*ReadActor` / `IXxxStore` 命名）。
4. Query/Command 边界混淆（query path 出现 replay、actor 侧读、generic request-reply）。
5. Projection 双轨 / 多包络（除 `EventEnvelope` 外的二层包络）。
6. 同步阻塞调用（`GetAwaiter().GetResult()`、回调直接改运行态）。
7. JSON / 自定义字符串用于事实存储（违反"统一 Protobuf"）。
8. 字符串路由 / 反射魔法（`TypeUrl.Contains(...)`）。
9. 空转发抽象 / 兼容空壳。
10. 测试不足或被 `[Skip]` 标记；轮询等待未在 allowlist。

## 工作流

1. 先读 `docs/audit-scorecard/`，把已识别但未修复的问题作为候选起点。
2. 用 `rg` / `grep` 抽样验证 2~3 个真实命中；已修复的跳过。
3. 按"同一目录 / 同一概念边界"聚合成 cluster。
4. 按"风险/收益"排序——优先 risk 低、leverage 高、有现成测试覆盖。

## 输出契约

**必须写入**：`$REPO_ROOT/.refactor-loop/runs/audit-iter-{{iteration}}.md`

```markdown
---
schema: refactor-audit-v1
iteration: {{iteration}}
generated_at: <ISO8601>
total_clusters: <N>
---

# Cluster 1: <短标题>

- **id**: cluster-001-<slug>
- **violation_clause**: CLAUDE.md 中对应强制条款原文摘录
- **old_pattern**: 当前错误模式（一句话 + 代表性文件:行号 ×2）
- **new_principle**: 重构后的设计原则（一句话，可直接写进代码注释）
- **scope_paths**:
  - <path1>
  - <path2>
- **estimated_files**: <数字>
- **risk**: low | medium | high
- **leverage**: low | medium | high
- **dependencies**: []  # 其它 cluster id
- **test_gap**: <描述测试覆盖缺口；如无写 "covered">
- **implementation_hints**: |
    - 第一步：...
- **verification_hints**: |
    - 需要跑的脚本：...
    - 需要新增的测试断言：...

# Cluster 2: ...
```

## 红线

- 禁止改任何代码。
- 禁止把"想加的功能"伪装成 cluster。
- 禁止依赖外部仓库改动（NyxID/chrono-* 等）。
- 末尾打印 `AUDIT_DONE:<output-path>:<total_clusters>` 便于外部检测。

开始执行。完成后退出。
