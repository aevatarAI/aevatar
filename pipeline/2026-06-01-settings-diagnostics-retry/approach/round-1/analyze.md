# 演化 approach 状态

该记录是自动演化 approach gate 的对外状态事实；人工阅读中文说明，机器字段保持原样用于 grep 和恢复。

status_code=approach-analyze
thread_id=2026-06-01-settings-diagnostics-retry
inbox_ref=refs/inbox/2026-06-01-settings-diagnostics-retry
inbox_stem=2026-06-01-settings-diagnostics-retry
approach_round=1
artifact_base=/Users/potter/.local/state/fkst/runtime/Users-potter-Desktop-sbt_project-aevatar/pipeline/2026-06-01-settings-diagnostics-retry/approach/round-1
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

No previous meta_judge convergence context.

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
