# 演化 approach 状态

该记录是自动演化 approach gate 的对外状态事实；人工阅读中文说明，机器字段保持原样用于 grep 和恢复。

status_code=approach-analyze
thread_id=2026-06-01-settings-diagnostics-final
inbox_ref=refs/inbox/2026-06-01-settings-diagnostics-final
inbox_stem=2026-06-01-settings-diagnostics-final
approach_round=2
artifact_base=/Users/potter/.local/state/fkst/runtime/Users-potter-Desktop-sbt_project-aevatar/pipeline/2026-06-01-settings-diagnostics-final/approach/round-2
meta_judge_artifact=
phase=design-solving
human=
reason_code=approach-analyze
trigger_category=
seconds=
budget=
round=
max_rounds=

说明：若 reason_code 指向 converge-budget，seconds 与 budget 是收敛预算证据；若指向 converge-round-cap，round 与 max_rounds 是轮次上限证据；若指向 meta-judge-escalate，trigger_category 是升级分类证据。
当前状态说明：请按 status_code、reason_code 与机器字段判读该状态；details 保留原始事实文本，便于 grep 与恢复。

This round derives the shared problem frame from the inbox body, existing artifacts, and the current recovery entrypoint.

## previous meta judge

previous_decision=converge
previous_meta_judge_artifact=/Users/potter/.local/state/fkst/runtime/Users-potter-Desktop-sbt_project-aevatar/pipeline/2026-06-01-settings-diagnostics-final/approach/round-1/meta_judge.md
convergence_question=Should the Diagnostics implementation stay inline in `apps/aevatar-console-web/src/pages/settings/index.tsx` plus `index.test.tsx`, or should it split into settings-local `diagnosticsReport.ts`/`diagnosticsContent.tsx` helpers, given the shared requirement to keep the copy bundle explicitly allowlisted and non-secret?

Previous meta_judge summary:
# Meta judge
thread=2026-06-01-settings-diagnostics-final
approach_round=1
@fkst-record approach-meta
field decision string converge
field kind string implementation-granularity
field trigger_category string
@fkst-end
## Solver verdicts
- minimal: propose
- structural: propose
- delete: propose
## Decision
三个 solver 都同意只在 `apps/aevatar-console-web` 内做 Settings 本地 Diagnostics tab，复用现有 auth session、`studioApi.getUserConfig`、`studioApi.getUserConfigModels`、runtime helper 与客户端已暴露 env allowlist，不新增后端 API、不改 runtime contract、不中断现有 LLM/Account 行为，也没有触发 host protected path 或 trusted-boundary 升级；但三者在文件边界、是否新增 settings-local helper/component、以及 LOC 成本上没有达到 3/3 同一实现框架，因此不能直接 consensus，下一轮应收敛到“inline 两文件实现”还是“拆出本地 diagnostics helper/content”的窄问题。
## Framing agreement check
- Same boundary: yes
- Same files: no - minimal/delete 只编辑 `settings/index.tsx` 与 `settings/index.test.tsx`，structural 还新增 `settings/diagnosticsReport.ts` 与 `settings/diagnosticsContent.tsx`
- LOC variance <=30%: no - estimates are +180/-20, +260/-20, and +520/-20; max/min add lines ratio is 520/180
- Naming/migration agreement: no - all agree no migration and `diagnostics` section key, but structural names a new `SettingsDiagnosticsReport`/`buildSettingsDiagnosticsReport` local contract that the other two do not accept
- Failure/recovery agreement: yes
## If consensus
- Chosen framing:
- Implement plan:
- Implementation constraint:

## inbox

# Add Settings diagnostics for console environment readiness

## Scope
Improve `apps/aevatar-console-web/src/pages/settings`.

Add a third Settings section/tab named `Diagnostics` next to the existing `LLM` and `Account` sections.

The Diagnostics section should help an operator understand whether the console is ready to use without opening devtools or asking backend engineers.

Include compact, readable panels for:
- Auth session status: signed in / missing session / token expiry / token type.
- LLM defaults: effective route, default model, ready provider count, gateway URL.
- Runtime mode: runtime mode label, resolved runtime base URL, local/remote status when available from existing user config.
- Frontend environment: public path, relevant configured frontend env flags that are already exposed to the client.
- API/provider loading state and error summaries using existing `studioApi.getUserConfig` and `studioApi.getUserConfigModels`; do not add backend APIs.

Add a `Copy diagnostics` action that copies a Markdown support bundle to the clipboard. The bundle should include only non-secret values. Never copy access tokens, refresh tokens, API keys, or provider secrets.

## UX constraints
- Preserve the existing Ant Design Pro shell and Settings visual language.
- Keep the layout dense and operational, not decorative.
- Missing fields should render as `Unavailable` or `n/a`.
- The copy action must show success/failure feedback.
- The tab switch should preserve URL state with `?section=diagnostics`.
- Keyboard navigation must continue to work for the Settings tab rail.

## Tests
Update focused Settings tests for:
- diagnostics tab navigation and URL state
- auth session present and missing states
- provider/model readiness summary
- copy diagnostics excluding token/secret values
- existing LLM and Account tab behavior still passing

## Verification
Run:
- `pnpm --dir apps/aevatar-console-web test --runInBand settings`
- `pnpm --dir apps/aevatar-console-web tsc`


⟦AI:FKST⟧
