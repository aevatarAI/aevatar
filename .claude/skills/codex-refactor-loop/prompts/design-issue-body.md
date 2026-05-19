# Auto-refactor loop: design decision needed for `${CLUSTER_ID}` / 需要人类设计决策

> **Bilingual issue / 双语 issue**. The English and 中文 sections are fully equivalent — read either one, do not need to cross-reference. Reply in either language; the controller treats both identically.
> 中英文章节内容完全等价，读任一即可；回复可用任一语言。

---

## Technical Context / 技术上下文

**Cluster YAML, evidence, and fix boundary below are language-neutral. The English and 中文 prose sections that follow each contain the full instructions independently.**

(以下 cluster YAML / 证据 / 修复边界为语言无关，下方两个章节各自完整。)

### Cluster spec (from `.refactor-loop/runs/audit-iter-${ITERATION}.md`)

${CLUSTER_YAML}

### Evidence / 证据

${CLUSTER_EVIDENCE}

### Fix boundary (audit's initial proposal) / 修复边界（audit 初步提议）

${CLUSTER_FIX_BOUNDARY}

---

## English (full)

This issue was opened automatically by the `codex-refactor-loop` skill during iter${ITERATION}. The loop's audit codex identified a real architectural violation but flagged it `requires_design: true` because the fix is not a mechanical refactor — it needs a human design decision before any code change.

The loop has **paused** on this cluster. Auto-implementation will resume only when this issue is either:

- **Labelled `auto-loop-resume`** with a comment containing the design decision (signals "implement using this design"), or
- **Closed without** `auto-loop-resume` label (signals "design rejected; do not implement this cluster").

Without one of those signals, the controller polls this issue every ~1 hour and surfaces new comments via PushNotification (first new comment only — no spam on subsequent comments).

### Decision checklist — please answer before adding `auto-loop-resume`

- [ ] **Pattern choice**: which of the audit's proposed fix shapes (or an alternative) should the implement codex use?
- [ ] **Proto schema impact**: if new typed fields are needed, sketch them here (proto messages + field numbers). If no proto change is needed, say so.
- [ ] **Backward compatibility**: how should existing persisted state / wire format be handled? Options include `reserved` field numbers, type aliases, schema migrations, or accepted resets.
- [ ] **Scope split**: should this remain a single cluster, or split into N PRs? If split, sketch the cluster ids.
- [ ] **Test surface**: what behavior MUST be exercised by tests beyond the audit's `verification_hints`?
- [ ] **Out-of-scope guard rails**: anything the implement codex must NOT touch (e.g., a related concern that belongs to a separate issue)?

### Auto-loop behavior

- Controller polls this issue on every wakeup. When pending design issues are the only remaining work, the cadence is ~1 hour.
- The first new comment after the issue opens triggers a PushNotification to the controller operator. Subsequent comments do not re-notify; they are surfaced only via the next manual `/loop` invocation or the next sweep.
- Adding the `auto-loop-resume` label triggers the resumption flow: controller materialises an implement prompt with this issue's latest comment prepended verbatim under `## Design decision (from issue #${ISSUE_NUMBER})`, dispatches the implement codex, posts a confirmation comment back on this issue, and closes the issue automatically once the resulting PR opens.
- Closing the issue without the `auto-loop-resume` label is treated as "design rejected; cluster permanently deferred"; controller moves the cluster to `clusters_failed` with reason `design-rejected:closed`.

cc: @loning (auto-loop operator)

---

## 中文（完整）

本 issue 由 `codex-refactor-loop` skill 在 iter${ITERATION} 自动开启。Audit codex 识别出真实的架构违反，但因为修复不是机械重构、需要人类设计决策才能动代码，所以被标记为 `requires_design: true`。

Loop 在此 cluster 上**已暂停**。Auto-implementation 仅在以下任一条件下恢复：

- **加 `auto-loop-resume` 标签**，并在评论里给出设计决策（信号意义："按此设计实施"），或
- **不加 `auto-loop-resume` 标签直接关闭 issue**（信号意义："设计被拒绝；不实施此 cluster"）。

如未给出上述任一信号，controller 每约 1 小时轮询本 issue，发现新评论时通过 PushNotification 推送一次提示（仅首次新评论触发推送，后续评论不再重复提示，避免打扰）。

### 决策清单 — 加 `auto-loop-resume` 之前请回答

- [ ] **采用的模式**：audit 提议的修复 shape 中选哪一个？或另有替代方案？
- [ ] **Proto schema 影响**：如需新增 typed field，请列出 proto messages + field numbers；如无 proto 改动，请明确说明。
- [ ] **向后兼容**：现有持久态 / wire format 如何处理？可选项包括 `reserved` 字段号、类型 alias、schema migration、可接受的重置等。
- [ ] **Scope 拆分**：保持单 cluster，还是拆成 N 个 PR？若拆分，给出 cluster id 草案。
- [ ] **测试面**：在 audit 的 `verification_hints` 之外，**必须**被测试覆盖的行为有哪些？
- [ ] **越界禁地**：implement codex **不应**触碰的相邻关切（例如属于另一 issue 的范围）？

### Auto-loop 行为

- Controller 每次唤醒都会轮询本 issue。当待回复的 design issue 是仅剩的工作时，节奏约为 1 小时一次。
- Issue 打开后首次出现的新评论会触发一次 PushNotification 通知 controller 运维者；后续评论不再重复通知，需要靠下次手动 `/loop` 调用或下次 sweep 才被处理。
- 添加 `auto-loop-resume` 标签触发恢复流程：controller 把本 issue 最新评论原样作为 `## Design decision (from issue #${ISSUE_NUMBER})` 段拼到 implement prompt 前面，dispatch implement codex，在本 issue 上回评确认，PR 打开后自动关闭本 issue。
- 不加 `auto-loop-resume` 标签直接关闭 issue 会被判定为"设计被拒绝；cluster 永久搁置"；controller 把该 cluster 移到 `clusters_failed`，原因记为 `design-rejected:closed`。

cc: @loning（auto-loop 运维者）
