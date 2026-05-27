# Convergence Policy

Team Lead uses this policy to decide whether an issue is approved, needs rework, or should be skipped.

## Protocol Filter

- `BLOCKED_PROTOCOL` after one retry → skip issue and record protocol failure.
- Missing changed-file scope check from CI runner → re-run CI runner once.

## Deduplicate

Group by file, merge issues within 5 lines. For browser-only failures, group by route + viewport + flow.

## Severity Filter

| Condition | Decision |
|---|---|
| CRITICAL or HIGH from any single reviewer | must fix |
| MEDIUM from 2+ reviewers | must fix |
| LOW | optional |
| CI FAILED | must fix |
| Browser QA FAILED | must fix |
| Browser QA `BLOCKED_MISSING_ENTRYPOINT` or `BLOCKED_MISSING_TEST_DATA` | must fix the implementer report or task wiring before approval |
| Browser QA `BLOCKED_LAUNCH` | one retry after checking dev server command; if still blocked, skip issue and record environment failure |
| Browser QA `BLOCKED_AUTH` or `BLOCKED_EXTERNAL_SERVICE` on protected-route user-visible changes | cannot approve without manual setup/evidence |
| Browser QA `BLOCKED_AUTH` or `BLOCKED_EXTERNAL_SERVICE` on non-visible refactor, type-only, or dead-code changes | may approve only with residual risk recorded |
| Browser QA `BLOCKED_AUTH` or `BLOCKED_EXTERNAL_SERVICE` on backend-contract-impact runtime flows | cannot approve without manual QA evidence or targeted tests |
| Changed files outside allowed frontend scope | must fix |

## Decision

- Must-fix AND `review_round < 3` → re-spawn implementer with fix list, then re-review
- Must-fix AND `review_round >= 3` → skip issue and record `issue_id / reason / attempts / last_failure_type / next_action`
- No must-fix → APPROVED → submit PR
