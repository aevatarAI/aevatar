# Auto-refactor loop: design decision needed for `${CLUSTER_ID}` / 需要人类设计决策

> **Bilingual issue / 双语 issue**. English first, 中文在后。Maintainers may reply in either language; the controller treats both identically.

---

## English

This issue was opened automatically by the `codex-refactor-loop` skill during iter${ITERATION}. The loop's audit codex identified a real architectural violation but flagged it `requires_design: true` because the fix is not a mechanical refactor — it needs a human design decision before any code change.

The loop has **paused** on this cluster. Auto-implementation will resume only when this issue is either:

- **Labelled `auto-loop-resume`** with a comment containing the design decision (signals "implement using this design"), or
- **Closed without** `auto-loop-resume` label (signals "design rejected; do not implement this cluster").

Without one of those signals, the controller polls this issue every ~1 hour and surfaces new comments via PushNotification (first new comment only, no spam).

### Cluster spec (from `.refactor-loop/runs/audit-iter-${ITERATION}.md`)

${CLUSTER_YAML}

### Evidence

${CLUSTER_EVIDENCE}

### Fix boundary (audit's initial proposal)

${CLUSTER_FIX_BOUNDARY}

### Decision checklist (please answer before adding `auto-loop-resume`)

- [ ] **Pattern choice**: which of the audit's proposed fix shapes (or an alternative) should the implement codex use?
- [ ] **Proto schema impact**: if new typed fields are needed, sketch them here (proto messages + field numbers). If no proto change, say so.
- [ ] **Backward compatibility**: how should existing persisted state / wire format be handled? (Reserve, alias, drop with reset, etc.)
- [ ] **Scope split**: should this be one cluster or split into N PRs? If split, sketch the cluster ids.
- [ ] **Test surface**: what behavior MUST be exercised by tests beyond the audit's `verification_hints`?
- [ ] **Out-of-scope guard rails**: anything the implement codex must NOT touch (e.g., a related concern that's a separate issue)?

### Auto-loop behavior

- Controller polls this issue on every wakeup (~1h cadence when no other work is active).
- First new comment after issue open → PushNotification to controller operator.
- `auto-loop-resume` label → controller materializes implement prompt with this issue's latest comment prepended verbatim as `## Design decision (from issue #${ISSUE_NUMBER})`, dispatches implement codex, posts confirmation back on this issue, and closes after PR opens.
- Issue closed without `auto-loop-resume` label → controller treats as "design rejected; cluster permanently deferred".

cc: @loning (auto-loop operator)

---

## 中文

本 issue 由 `codex-refactor-loop` skill 在 iter${ITERATION} 自动开启。Audit codex 识别出真实架构违反，但标记 `requires_design: true` —— 修复不是机械重构，需要人类设计决策才能动代码。

Loop **已暂停**此 cluster。auto-implementation 仅在以下任一情况恢复：

- **加 `auto-loop-resume` 标签**，并在评论里给出设计决策（表示"按此设计实施"），或
- **关闭 issue 但不加** `auto-loop-resume` 标签（表示"拒绝设计；不实施此 cluster"）。

如未给出上述信号，controller 每 ~1 小时轮询此 issue，发现新评论时通过 PushNotification 提示一次（不重复推送）。

### Cluster 定义（出处：`.refactor-loop/runs/audit-iter-${ITERATION}.md`）

见上方 English section 的 `Cluster spec` 块。

### 修复边界（audit 初步提议）

见上方 English section 的 `Fix boundary` 块。

### 决策清单（加 `auto-loop-resume` 前请回答）

- [ ] **采用的模式**：audit 提议的几种修复 shape 中选哪一个？或另有替代？
- [ ] **Proto schema 影响**：如需新增 typed field，列出 proto messages + field numbers；如无 proto 改动，说明即可。
- [ ] **向后兼容**：现有持久态 / wire format 如何处理？（reserve 字段号、加 alias、reset 老数据等）
- [ ] **Scope 拆分**：单 cluster 还是拆 N 个 PR？拆则给出 cluster id 草案。
- [ ] **测试面**：audit 的 `verification_hints` 之外，**必须**被测试覆盖的行为有哪些？
- [ ] **越界禁地**：implement codex **不应**碰的相邻关切（如属于另一 issue 的范围）？

### Auto-loop 行为

- Controller 每次 wakeup 都轮询本 issue（仅剩 design 待回时按 ~1h 心跳）。
- 首次发现新评论 → PushNotification 通知 operator（不重复）。
- 加 `auto-loop-resume` 标签 → controller 把最新评论作为 `## Design decision (from issue #${ISSUE_NUMBER})` 段拼到 implement prompt 前面，dispatch implement codex，在 issue 上回评确认，PR 开后关闭 issue。
- Issue 不加标签直接关闭 → controller 判定"设计拒绝；cluster 永久搁置"。

cc: @loning（auto-loop 运维者）
