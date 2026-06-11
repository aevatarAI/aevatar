## implement #1369 run report

### Changed files

- `.claude/skills/codex-refactor-loop/scripts/dev_sync_daemon.py`
  - Removed the detached `subprocess.Popen` + `nohup ... & disown` self-spawn path from `dispatch_codex()`.
  - Kept prompt/log/worktree materialization and now appends one `dispatch-codex` line to `.refactor-loop/.controller-pending-events.log`.
  - Pending event includes `action`, `prompt`, `log`, `worktree`, `add_dir`, `timeout`, and `issue_or_pr`.
  - Updated sync PR body wording so conflicts/CI fixes are described as daemon pending event plus controller dispatch.

- `.claude/skills/codex-refactor-loop/scripts/spawn-codex.sh`
  - Added optional `--execution-id <id>`.
  - Generates a stable id when omitted.
  - Emits `ACCEPTED: execution_id=<id> ack_stage=accepted prompt=<path> log=<path> timeout=<s>` on accepted start.
  - Writes `.refactor-loop/markers/<execution_id>.running.json` and `<execution_id>.done.json` with `execution_id`, `ack_stage`, `prompt`, `log`, `timeout`, `started_at`, `done_at`, and `exit_code`.
  - Preserves legacy `base`, `log_path`, `verdict`, `SPAWN`, and `DONE` fields/banners for existing readers.

- `.claude/skills/codex-refactor-loop/SKILL.md`
  - Updated the spawn section with `ACCEPTED` receipt and `execution_id` marker semantics.
  - Updated Phase 6 dev-sync daemon notes so DIRTY/CI-fail paths are pending-event materialization, not daemon self-dispatch.
  - Updated daemon/controller responsibility table and anti-pattern wording.

### Smoke tests

- `python3 -c 'import ast; ast.parse(open(".claude/skills/codex-refactor-loop/scripts/dev_sync_daemon.py").read())'`: pass.
- `bash -n .claude/skills/codex-refactor-loop/scripts/spawn-codex.sh`: pass.
- `bash .claude/skills/codex-refactor-loop/scripts/spawn-codex.sh --help 2>&1 | head -20 || true`: completed with existing legacy `unknown flag: --help` output.
- `spawn-codex.sh --dry-run --execution-id issue1369-smoke`: printed `ACCEPTED` receipt with the provided execution id.
- Fake local `codex` smoke: `spawn-codex.sh --execution-id issue1369-marker-smoke` wrote `<id>.done.json` with matching `execution_id`, `ack_stage=accepted`, `timeout=3600`, `done_at`, `exit_code=7`, and preserved `verdict`.

### Deviation

- `SCOPE_EXTEND` was printed before touching worktree-local copies of the three skill files because the task simultaneously required implementation in `/Users/auric/aevatar-wt-issue1369-impl` and listed absolute scope paths under `/Users/auric/aevatar`.
- `SCOPE_EXTEND` was printed before creating this required run artifact because the artifact path is outside the three strict scope paths but explicitly required by procedure step 9.

⟦AI:AUTO-LOOP⟧
