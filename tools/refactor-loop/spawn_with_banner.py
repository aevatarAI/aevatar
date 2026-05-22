#!/usr/bin/env python3
"""
spawn_with_banner.py — spawn-codex wrapper 自动 post GitHub status banner

per Auric 2026-05-20 "...也看不到运行状态. 你继续修 skills 吧. 然后需要写脚本你
可以写脚本":controller 派 codex 时常忘 post banner,导致 GitHub 上看不到运行
状态。Helper script enforce "spawn 必配 banner"。

Usage:
  python3 tools/refactor-loop/spawn_with_banner.py \\
    --cd <worktree> --prompt <prompt-file> --log <log-file> \\
    --timeout 5400 \\
    --banner-target <issue-or-pr-num> \\
    --banner-kind <issue|pr> \\
    --banner-role <test-add|fix|reviewer|implement|solver|judge|reflector> \\
    --banner-detail "<short context, 1 line>"
  [--add-dir <main-repo-path>]

Behavior:
- spawn-codex.sh 后台跑(nohup + disown)
- 立即 post banner 到 target issue/pr,format `## 📊 状态卡片 — <role> 派出`
- banner 含 codex 任务名 / timeout / "下一步自动会做" / 是否需要人介入
- 末尾 `🤖 controller status banner` + ⟦AI:AUTO-LOOP⟧ sentinel
- Exit 0 if both succeed;exit 非 0 + 错误信息 otherwise

⟦AI:AUTO-LOOP⟧
"""

import argparse
import os
import subprocess
import sys
import tempfile
from pathlib import Path

REPO_ROOT = Path(os.environ.get("REPO_ROOT", "/Users/auric/aevatar"))
SPAWN_CODEX = REPO_ROOT / ".claude" / "skills" / "codex-refactor-loop" / "scripts" / "spawn-codex.sh"

ROLE_NEXT_STEPS = {
    "test-add": "1. test-add 完成 marker `TEST_ADD_DONE:...`  2. controller 自动 commit + push  3. codecov 重测",
    "fix": "1. fix r<N> 完成 marker `FIX_DONE:...`  2. controller commit + push  3. 派 reviewer r<N+1>",
    "reviewer": "1. 三 reviewer 完成 verdict marker  2. controller 计算 consensus  3. unanimous → auto-merge / reject → fix r<N+1>",
    "implement": "1. implement 完成 marker `IMPLEMENT_DONE:<cluster>:<status>`  2. controller commit + push  3. open PR + 派 reviewer r1",
    "solver": "1. 三 solver `SOLVER_DONE:...`  2. controller 派 meta-judge r<N>  3. consensus → implement / converge → fresh round",
    "judge": "1. judge `META_JUDGE_DONE:...`  2. consensus → implement / converge → fresh round / escalate → reflector or 人介入",
    "reflector": "1. reflector `META_RESOLVED:<kind>`  2. controller 按 kind 路由(retry-fix / re-design / re-cluster / drop / escalate-human)",
}


def parse_args() -> argparse.Namespace:
    p = argparse.ArgumentParser()
    p.add_argument("--cd", required=True, help="cwd for codex(usually worktree)")
    p.add_argument("--prompt", required=True, help="codex prompt file")
    p.add_argument("--log", required=True, help="codex log file")
    p.add_argument("--timeout", type=int, required=True, help="codex timeout seconds(>=3600)")
    p.add_argument("--add-dir", default=None, help="extra dir for codex(usually main repo when cd is worktree)")
    p.add_argument("--banner-target", required=True, help="issue/PR number to post banner")
    p.add_argument("--banner-kind", choices=["issue", "pr"], required=True)
    p.add_argument("--banner-role", required=True, choices=list(ROLE_NEXT_STEPS.keys()))
    p.add_argument("--banner-detail", default="", help="one-line context for banner")
    return p.parse_args()


def post_banner(args: argparse.Namespace) -> int:
    role = args.banner_role
    next_step = ROLE_NEXT_STEPS[role]
    log_name = Path(args.log).name
    body = f"""## 📊 状态卡片 — {role} 派出

| 维度 | 值 |
|---|---|
| 阶段 | **派出 codex(role=`{role}`)** |
| codex log | `{log_name}` |
| 工作目录 | `{args.cd}` |
| timeout | {args.timeout}s(~{args.timeout // 60} min 上限) |
| 上下文 | {args.banner_detail or "(none)"} |
| 下一步自动会做 | {next_step} |
| **是否需要人介入** | **❌ 否**(自动推进) |

🤖 controller status banner

⟦AI:AUTO-LOOP⟧
"""
    tmp = tempfile.NamedTemporaryFile(mode="w", suffix=".md", delete=False)
    tmp.write(body)
    tmp.close()
    cmd = ["gh", args.banner_kind, "comment", str(args.banner_target), "--body-file", tmp.name]
    r = subprocess.run(cmd, capture_output=True, text=True)
    os.unlink(tmp.name)
    if r.returncode != 0:
        sys.stderr.write(f"FAIL banner post: {r.stderr.strip()}\n")
        return 1
    url = r.stdout.strip()
    sys.stderr.write(f"BANNER_POSTED: {args.banner_kind} #{args.banner_target} {url}\n")
    return 0


def spawn_codex(args: argparse.Namespace) -> int:
    cmd = [str(SPAWN_CODEX),
           "--cd", args.cd,
           "--prompt", args.prompt,
           "--log", args.log,
           "--timeout", str(args.timeout)]
    if args.add_dir:
        cmd.extend(["--add-dir", args.add_dir])
    # nohup + start_new_session(脱离 parent)
    subprocess.Popen(
        ["nohup", *cmd],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        start_new_session=True,
    )
    sys.stderr.write(f"CODEX_SPAWNED: log={args.log}\n")
    return 0


def main() -> int:
    args = parse_args()
    if args.timeout < 3600:
        sys.stderr.write(f"FATAL: timeout {args.timeout} < 3600 (per CLAUDE.md floor)\n")
        return 2
    # 1. Post banner first(让 maintainer 立即看到状态)
    rc = post_banner(args)
    if rc != 0:
        sys.stderr.write("WARN: banner post failed, continuing with spawn anyway\n")
    # 2. Spawn codex
    rc = spawn_codex(args)
    return rc


if __name__ == "__main__":
    sys.exit(main())
