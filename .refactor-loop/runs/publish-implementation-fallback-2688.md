# Publish implementation fallback 2688

## State

- Branch: `refactor/iter2688-issue-2688`
- Integration base: `origin/feat/2026-07-10_scheduled-agent-key-credential` at `21107ccfadbb3a9097de3d94f978b5e4205bb2b5`
- Merge state: no active `MERGE_HEAD`; working tree had no Git-level unmerged paths.
- Fresh base status: integration base is already an ancestor of `HEAD` (`git merge-base --is-ancestor` returned 0).
- Recovery needed: `docs/adr/0037-scheduled-invocation-credential-source-model.md` still contained committed conflict marker text, so the fallback resolved the marker blocks in place.

## Changed Files

- `docs/adr/0037-scheduled-invocation-credential-source-model.md`
- `.refactor-loop/runs/publish-implementation-fallback-2688.md`

## Resolution

- Removed two stale conflict marker blocks from ADR-0037.
- Preserved the implementation/base intent that existing tag `4` remains assigned to `legacy_durable_sender_bearer_blocked`, while new writes use oneof source tags `5/6`.

## Verification

- `rg -n "^(<<<<<<<|=======|>>>>>>>)" docs/adr/0037-scheduled-invocation-credential-source-model.md` found no conflict markers.
- `bash tools/docs/lint.sh` passed: `docs lint: PASSED — 72 file(s) checked, 0 errors`.

## Unresolved Risk

- Full solution build/test was not run because this fallback only changed an ADR document and the marker artifact.

⟦AI:AUTO-LOOP⟧
PUBLISH_FALLBACK_DONE:2688:resolved
