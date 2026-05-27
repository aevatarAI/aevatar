#!/usr/bin/env python3
"""dev_sync_daemon.py — 通用双向 PR 同步 daemon.

每 TICK 周期处理两方向(forward: SOURCE→TARGET, reverse: TARGET→SOURCE):
  1. count_ahead == 0 → skip
  2. 没 open sync PR → 推 sync branch + 开 PR base=对方 + enable auto-merge
  3. 有 open sync PR + mergeStateStatus:
       - DIRTY  → 派 codex 在独立 worktree 解冲突,push 回 PR head
       - BEHIND → gh api update-branch(GitHub 自动 merge base into PR head)
       - 任意 CI fail → 派 codex 在独立 worktree 修 CI errors,push 回 PR head
       - clean    → 等 GitHub auto-merge

冲突解决与 CI 修复都是同 codex,差别在 prompt action(resolve-conflict / fix-ci)。
daemon 不参与 admin bypass / direct push;一切走 PR。
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

# ===== config =====

MAIN_REPO = Path(os.environ.get("REPO_ROOT", "/Users/auric/aevatar"))
TICK = int(os.environ.get("TICK", "120"))
SOURCE = os.environ.get("SOURCE", "dev")
TARGET = os.environ.get("TARGET", "auto-refact-dev")
REPO_FULL = os.environ.get("REPO_FULL", "aevatarAI/aevatar")

FORWARD_PREFIX = "chore/sync-dev-to-trunk-"
REVERSE_PREFIX = "chore/sync-trunk-to-dev-"
FORWARD_WT = Path(os.environ.get("FORWARD_WT", "/Users/auric/aevatar-wt-sync-forward"))
REVERSE_WT = Path(os.environ.get("REVERSE_WT", "/Users/auric/aevatar-wt-sync-reverse"))

SPAWN_CODEX = MAIN_REPO / ".claude" / "skills" / "codex-refactor-loop" / "scripts" / "spawn-codex.sh"
LOGS_DIR = MAIN_REPO / ".refactor-loop" / "logs"
PROMPTS_DIR = MAIN_REPO / ".refactor-loop" / "prompts"

# ===== utils =====

def log(msg: str) -> None:
    ts = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    print(f"[{ts}] {msg}", flush=True)
    sys.stdout.flush()


def run(cmd: list[str], cwd: Path | None = None, timeout: int = 60):
    return subprocess.run(cmd, cwd=str(cwd) if cwd else None,
                          capture_output=True, text=True, timeout=timeout)


def fetch(cwd: Path = MAIN_REPO) -> None:
    run(["git", "fetch", "origin", "--quiet"], cwd=cwd)


def count_ahead(base: str, head: str, cwd: Path = MAIN_REPO) -> int:
    r = run(["git", "rev-list", "--count", f"{base}..{head}"], cwd=cwd)
    try:
        return int(r.stdout.strip() or "0")
    except ValueError:
        return 0


def find_open_pr(base: str, head_prefix: str) -> tuple[int | None, str | None]:
    r = run(["gh", "pr", "list", "--state", "open", "--base", base,
             "--json", "number,headRefName"], cwd=MAIN_REPO)
    if r.returncode != 0:
        return None, None
    try:
        prs = json.loads(r.stdout)
    except json.JSONDecodeError:
        return None, None
    for p in prs:
        if p.get("headRefName", "").startswith(head_prefix):
            return p["number"], p["headRefName"]
    return None, None


def pr_state(pr_num: int) -> dict:
    r = run(["gh", "pr", "view", str(pr_num), "--json",
             "number,headRefName,mergeStateStatus,mergeable,statusCheckRollup"],
            cwd=MAIN_REPO)
    if r.returncode != 0:
        return {}
    try:
        return json.loads(r.stdout)
    except json.JSONDecodeError:
        return {}


def has_failing_check(state: dict) -> bool:
    for c in state.get("statusCheckRollup") or []:
        if (c.get("conclusion") or c.get("state") or "").upper() in {"FAILURE", "CANCELLED", "TIMED_OUT"}:
            return True
    return False


def codex_in_flight(label: str) -> bool:
    """label = 'forward' / 'reverse'. ps grep worktree path(codex -C <wt> 在命令行可见)."""
    wt = FORWARD_WT if label == "forward" else REVERSE_WT
    r = run(["bash", "-c",
             f"ps -ef | grep 'codex exec' | grep -- '-C {wt}' | grep -v grep | wc -l"])
    try:
        return int(r.stdout.strip()) > 0
    except ValueError:
        return False


def ensure_worktree(wt: Path, branch: str) -> bool:
    """Ensure worktree exists at wt, on `branch` (tracking origin/<branch> if exists)."""
    if not wt.exists():
        wt.parent.mkdir(parents=True, exist_ok=True)
        r = run(["git", "worktree", "add", str(wt), branch], cwd=MAIN_REPO)
        if r.returncode != 0:
            r = run(["git", "worktree", "add", "-b", branch, str(wt), f"origin/{branch}"], cwd=MAIN_REPO)
            if r.returncode != 0:
                log(f"FAIL ensure_worktree {wt} on {branch}: {r.stderr.strip()[:200]}")
                return False
    # ensure on the right branch
    cur = run(["git", "rev-parse", "--abbrev-ref", "HEAD"], cwd=wt)
    if cur.stdout.strip() != branch:
        run(["git", "fetch", "origin", "--quiet"], cwd=wt)
        r = run(["git", "checkout", branch], cwd=wt)
        if r.returncode != 0:
            r = run(["git", "checkout", "-B", branch, f"origin/{branch}"], cwd=wt)
            if r.returncode != 0:
                log(f"FAIL checkout {branch} in {wt}: {r.stderr.strip()[:200]}")
                return False
    run(["git", "fetch", "origin", "--quiet"], cwd=wt)
    run(["git", "reset", "--hard", f"origin/{branch}"], cwd=wt)
    return True

# ===== core =====

def create_sync_pr(source: str, target: str, branch_prefix: str, ahead: int, label: str) -> None:
    ts = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S")
    new_branch = f"{branch_prefix}{ts}"
    r = run(["git", "push", "origin",
             f"refs/remotes/origin/{source}:refs/heads/{new_branch}"], cwd=MAIN_REPO)
    if r.returncode != 0:
        log(f"{label}: FAIL push sync branch {new_branch}: {r.stderr.strip()[:200]}")
        return
    log(f"{label}: pushed {new_branch} ({ahead} commits)")
    body = (f"daemon 双向同步 `{source}` → `{target}` 共 {ahead} commits。"
            f"CI 绿后 GitHub auto-merge。冲突或 CI fail 由 daemon 派 codex 在独立 worktree 解决。"
            f"\n\n⟦AI:AUTO-LOOP⟧\n")
    r = run(["gh", "pr", "create", "--base", target, "--head", new_branch,
             "--title", f"chore: {source} → {target} 自动同步 ({ahead} commits)",
             "--body", body], cwd=MAIN_REPO)
    if r.returncode != 0:
        log(f"{label}: FAIL pr create: {r.stderr.strip()[:200]}")
        return
    pr_url = r.stdout.strip()
    pr_num = pr_url.rsplit("/", 1)[-1]
    log(f"{label}: opened PR {pr_url}")
    run(["gh", "pr", "edit", pr_num, "--add-label", "auto-loop"], cwd=MAIN_REPO)
    r = run(["gh", "pr", "merge", pr_num, "--merge", "--delete-branch", "--auto"], cwd=MAIN_REPO)
    if r.returncode == 0:
        log(f"{label}: enabled auto-merge PR #{pr_num}")
    else:
        log(f"{label}: WARN enable auto-merge fail: {r.stderr.strip()[:120]}")


def dispatch_codex(wt: Path, pr_num: int, sync_branch: str, source: str, target: str,
                   action: str, label: str) -> None:
    """action: 'resolve-conflict' | 'fix-ci'. 派 codex 在 wt 上 work + push 回 sync_branch."""
    if not ensure_worktree(wt, sync_branch):
        return
    ts = int(time.time())
    PROMPTS_DIR.mkdir(parents=True, exist_ok=True)
    LOGS_DIR.mkdir(parents=True, exist_ok=True)
    prompt_file = PROMPTS_DIR / f"sync-{label}-{action}-{ts}.md"
    log_file = LOGS_DIR / f"sync-{label}-codex-{action}-{ts}.log"

    if action == "resolve-conflict":
        body = f"""# {label} sync — 解冲突 PR #{pr_num}

worktree: `{wt}`(已 checkout `{sync_branch}` + reset to `origin/{sync_branch}`)
sync 方向: `{source}` → `{target}`(PR base=`{target}`)

## 任务

1. `git fetch origin && git merge origin/{target}`(把 target tip merge 进 PR head)
2. 解所有冲突(prefer 业务正确而非机械合并;若不确定保 source 端)
3. `dotnet build aevatar.slnx --nologo` 确认编译过
4. `git add -A && git commit -m "resolve conflict {source} ↔ {target}\\n\\n⟦AI:AUTO-LOOP⟧"`
5. `git push origin HEAD:{sync_branch}`(更新 PR head)

## 红线

- ❌ 禁止改 sync 范围外文件(只解冲突 + 必要 build fix)
- ❌ 禁止 force push / git branch / gh pr 操作
- ❌ 禁止动外部 repo
- ❌ 严禁写 `Auric` / `@auric` / `@Auric`

## 完成 marker(末行 echo)

- `SYNC_RESOLVE_DONE:{pr_num}:files=<N>`
- `SYNC_RESOLVE_BLOCKED:{pr_num}:<reason>`

所有 AI 生成的对外内容必须末尾独立一行加 sentinel `⟦AI:AUTO-LOOP⟧`。
"""
    else:  # fix-ci
        body = f"""# {label} sync — 修 CI fail PR #{pr_num}

worktree: `{wt}`(已 checkout `{sync_branch}` + reset to `origin/{sync_branch}`)
sync 方向: `{source}` → `{target}`(PR base=`{target}`)

## 任务

1. `gh pr checks {pr_num}` 查 fail 的 check
2. `gh run view <run-id> --log-failed` 看具体 fail 原因
3. 修代码(build / test / lint / coverage / guard)
4. `dotnet build aevatar.slnx --nologo && dotnet test aevatar.slnx --nologo --no-build --no-restore` verify
5. `git add -A && git commit -m "fix CI: <short>\\n\\n⟦AI:AUTO-LOOP⟧"`
6. `git push origin HEAD:{sync_branch}`

## 红线

- ❌ 禁止 `[Skip]` / disable 测试换绿
- ❌ 禁止 `Task.Delay`-based test pacing
- ❌ 禁止改无关代码
- ❌ 禁止 force push / git branch / gh pr 操作
- ❌ 禁止动外部 repo
- ❌ 严禁写 `Auric` / `@auric` / `@Auric`

## 完成 marker(末行 echo)

- `SYNC_FIX_CI_DONE:{pr_num}:checks=<N>`
- `SYNC_FIX_CI_BLOCKED:{pr_num}:<reason>`

所有 AI 生成的对外内容必须末尾独立一行加 sentinel `⟦AI:AUTO-LOOP⟧`。
"""
    prompt_file.write_text(body)
    log(f"{label}: dispatch codex({action}) PR #{pr_num} wt={wt} prompt={prompt_file.name}")
    subprocess.Popen(
        ["bash", "-lc",
         f"nohup {SPAWN_CODEX} --cd {wt} --add-dir {MAIN_REPO} "
         f"--prompt {prompt_file} --log {log_file} --timeout 5400 "
         f">/dev/null 2>&1 & disown"],
        cwd=str(MAIN_REPO))


def trigger_update_branch(pr_num: int, label: str) -> None:
    r = run(["gh", "api", "-X", "PUT",
             f"repos/{REPO_FULL}/pulls/{pr_num}/update-branch"], cwd=MAIN_REPO)
    if r.returncode == 0:
        log(f"{label}: PR #{pr_num} BEHIND → update-branch triggered")
    else:
        log(f"{label}: WARN update-branch PR #{pr_num} fail: {r.stderr.strip()[:120]}")


def sync_direction(source: str, target: str, wt: Path, branch_prefix: str, label: str) -> None:
    fetch()
    ahead = count_ahead(f"origin/{target}", f"origin/{source}")
    if ahead == 0:
        return

    pr_num, sync_branch = find_open_pr(target, branch_prefix)

    if pr_num is None:
        create_sync_pr(source, target, branch_prefix, ahead, label)
        return

    state = pr_state(pr_num)
    mss = (state.get("mergeStateStatus") or "").upper()

    if mss == "DIRTY":
        if not codex_in_flight(label):
            dispatch_codex(wt, pr_num, sync_branch or "", source, target, "resolve-conflict", label)
        else:
            log(f"{label}: PR #{pr_num} DIRTY + codex in-flight,等")
        return

    if mss == "BEHIND":
        trigger_update_branch(pr_num, label)
        return

    if has_failing_check(state):
        if not codex_in_flight(label):
            dispatch_codex(wt, pr_num, sync_branch or "", source, target, "fix-ci", label)
        else:
            log(f"{label}: PR #{pr_num} CI fail + codex in-flight,等")
        return

    log(f"{label}: PR #{pr_num} clean(mss={mss or 'UNKNOWN'}),等 GitHub auto-merge")


def tick() -> None:
    # forward 总跑(dev → trunk)
    sync_direction(SOURCE, TARGET, FORWARD_WT, FORWARD_PREFIX, "forward")
    # reverse 仅当 trunk 完全包含 dev(trunk 没落后 dev 任何 commit)时才开 PR
    # per loning: 先保证 dev → trunk OK,trunk superset of dev 时,才开 rollup 回 dev
    fetch()
    trunk_behind_dev = count_ahead(f"origin/{TARGET}", f"origin/{SOURCE}")
    if trunk_behind_dev == 0:
        sync_direction(TARGET, SOURCE, REVERSE_WT, REVERSE_PREFIX, "reverse")
    else:
        log(f"reverse: gate — trunk 落后 dev {trunk_behind_dev} commits,reverse 暂停直到 forward 完")


def main() -> None:
    log(f"dev_sync_daemon started: TICK={TICK}s SOURCE={SOURCE} TARGET={TARGET} "
        f"forward_wt={FORWARD_WT} reverse_wt={REVERSE_WT}")
    while True:
        try:
            tick()
        except Exception as e:
            log(f"ERROR tick: {e}")
        time.sleep(TICK)


if __name__ == "__main__":
    main()
