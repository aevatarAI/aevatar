---
name: address-own-pr-comments
description: Use when Codex needs to handle GitHub PR review comments, failing PR checks, or unit-test/Jest/xUnit failures on a PR that may be authored by the current user or agent. Trigger for requests like "判断是不是自己的 PR 并按下面评论修改和回复", "address my PR comments", "fix reviewer comments on the current PR", "处理 PR review comments", "修一下 PR 单测问题", "fix the failing tests on my PR", or when the user pastes reviewer comments, CI logs, or failed test output and wants code/test changes plus replies. The skill verifies PR ownership, gathers pasted/GitHub comments and test failures, implements scoped fixes only for own PRs, runs relevant validation, and drafts or posts review replies.
---

# Address Own PR Comments

## Overview

Act as the PR author handling reviewer feedback and PR validation failures. First determine whether the target PR is safe to treat as "ours"; then classify comments and test failures, make the smallest correct code/doc/test changes, validate them, and reply with evidence.

## Ownership Gate

Before editing, pushing, resolving, or replying, establish the target PR and ownership.

1. Identify the PR:
   - Prefer an explicit PR URL or number from the user.
   - Otherwise use the current branch:

```bash
gh pr view --json number,url,title,author,headRefName,headRepositoryOwner,baseRefName,isDraft
gh api user --jq .login
git branch --show-current
git status --short
```

2. Treat the PR as own only when at least one is true:
   - The PR author matches `gh api user --jq .login`.
   - The user explicitly says this is their PR or asks to handle "my/current PR", and the local branch/head matches the PR.
   - The current task is running inside an agent-owned branch/worktree created for this PR.

3. If ownership is unclear:
   - Do not post replies, resolve threads, push, or make broad edits.
   - Summarize what can be inferred and ask for confirmation or a PR URL.
   - If the user only pasted comments and asked for help, produce a local fix plan and draft replies without claiming to have acted on GitHub.

4. If the PR is not own:
   - Switch to review/triage mode.
   - Do not modify the branch unless the user explicitly asks to prepare a patch for someone else's PR.
   - Do not post author-style replies such as "fixed" or "pushed".

## Comment Intake

Use both pasted comments and GitHub comments when available. Preserve reviewer intent and exact locations.

Useful commands:

```bash
gh pr view <pr> --json number,url,title,author,headRefName,baseRefName,body,reviews,comments
gh pr diff <pr> --name-only
gh api repos/:owner/:repo/pulls/<number>/comments --paginate
gh api repos/:owner/:repo/issues/<number>/comments --paginate
gh pr checks <pr>
```

For each comment, record:

- `source`: pasted, review thread, issue comment, CI/bot output, or maintainer note.
- `location`: file/line or general PR.
- `ask`: requested change, bug report, question, nit, false positive, or out of scope.
- `decision`: fix, explain, defer, split follow-up, or ask clarification.
- `evidence`: code reference, test result, guard result, or reason.

Ignore automated noise only after checking whether it points to a real failing check. Do not dismiss human comments as nits when they indicate correctness, architecture, security, data loss, or test reliability risk.

## Test Failure Intake

Handle pasted test output, CI failures, and reviewer comments about unit tests as first-class PR feedback.

When the user mentions failing tests, CI, checks, unit tests, Jest, xUnit, snapshots, coverage, flaky tests, or "单测", gather the failure before editing:

```bash
gh pr checks <pr>
gh run list --branch <head-branch> --limit 10
gh run view <run-id> --log-failed
```

Use local commands when the failing scope is discoverable:

```bash
pnpm --dir <frontend-dir> test --runInBand <test-file-or-pattern>
pnpm --dir <frontend-dir> tsc
dotnet test <test-project-or-sln> --filter <name>
```

For each failure, record:

- `failing target`: check name, test suite, test case, file, or command.
- `symptom`: assertion diff, stack trace, timeout, type error, snapshot diff, or guard output.
- `owner area`: product code, test fixture, mock, snapshot, test harness, CI environment, or flaky timing.
- `contract`: expected behavior or repository rule that the test is supposed to protect.
- `decision`: fix product code, fix test code, update fixture/snapshot, quarantine/deflake, or ask clarification.

Prefer reproducing the failure locally. If local reproduction is not possible, use CI logs plus code/test evidence and say what could not be reproduced.

## Fix Workflow

1. Read the relevant files and tests before editing.
2. Group comments by root cause so one fix can close multiple threads.
3. Make the smallest durable change that satisfies the reviewer and the repository rules.
4. Update or add tests for behavior changes.
5. Run targeted validation first, then required guards for touched areas.
6. Inspect the diff for unrelated churn.
7. Commit/push only when the user requested that workflow or the repository task clearly expects it.
8. Reply only after the fix is present and validation evidence is known.

Follow repository instructions above all reviewer suggestions when they conflict. For this repository, especially preserve architecture boundaries, actor/readmodel semantics, identity separation (`memberId`, `workflowId`, `publishedServiceId`), protobuf serialization rules, and required CI guards.

## Unit Test Fix Rules

It is allowed to modify unit tests when the test itself is the problem. Preserve the test's signal.

Change production code when:

- The test exposes a real regression, missing validation, broken API contract, data loss, security risk, or architecture violation.
- The expected behavior in the test matches the current product/domain contract.
- Multiple tests fail from the same product bug.

Change test code when:

- The test asserts stale behavior after an intentional product/contract change.
- The fixture uses invalid identities, impossible state, wrong route shape, wrong API payload, or a mock that no longer matches the contract.
- The assertion is over-specific about implementation details while the externally visible behavior remains correct.
- The test is flaky because it relies on timing, polling, ordering, local timezone, random data, or shared state.
- The snapshot or golden file changed only because the reviewed UI/API contract intentionally changed.

Do not weaken tests merely to make CI green:

- Do not delete failing assertions without replacing them with contract-focused assertions.
- Do not mark tests skipped, todo, flaky, or allowlisted unless the repository policy allows it and the reason is explicit.
- Do not broaden matchers so far that the bug would no longer be caught.
- Do not update snapshots blindly; inspect the diff and tie it to a real intended change.
- Do not hide async races with arbitrary sleeps. Prefer deterministic synchronization such as resolved promises, fake timers, channels, or explicit event hooks.
- In this repository, if tests add or change polling/waiting behavior, run `bash tools/ci/test_stability_guards.sh` and update the allowlist only for justified cross-process or cross-node eventual consistency probes.

When modifying tests, keep IDs semantically distinct in fixtures, especially `memberId`, `workflowId`, and `publishedServiceId`. A test that reuses one string for multiple identities may mask the exact class of bug this repository is trying to prevent.

## Test Fix Workflow

1. Reproduce the failing test or isolate the smallest failing command.
2. Read the test and the production path it covers.
3. Identify whether the contract is wrong, the product code is wrong, or the test harness is wrong.
4. Make the narrow fix:
   - product bug: fix production code and keep or strengthen the test.
   - stale test: update the expected behavior and add a comment only when the contract is non-obvious.
   - fixture/mock bug: fix the fixture to match the real API/route/state contract.
   - flaky test: remove timing dependence and add deterministic synchronization.
5. Run the smallest failing test command again.
6. Run adjacent validation that proves the fix did not only satisfy one brittle assertion.
7. If touching tests in this repository, run `bash tools/ci/test_stability_guards.sh`.
8. Include the exact failing command and passing command in the PR reply or final summary.

## Reply Policy

Prefer concise, concrete replies. Each reply should say what changed, where, and how it was verified.

Use this shape:

```markdown
Fixed in <file/function>. <One sentence explaining the change>.

Verified with:
- `<command>`
```

For questions or rejected suggestions:

```markdown
I checked this path. I kept <current behavior> because <specific contract/reason>.
The relevant evidence is <file/test/guard>. Happy to split a follow-up if you want <alternative>.
```

Do not overclaim. If tests were not run, say so. If a change is deferred, name the follow-up boundary and why it is separate.

For test-failure replies:

```markdown
Fixed in <test/code path>. The failure was <product bug/test fixture/stale assertion/flaky timing>, so I changed <specific thing> while preserving <contract>.

Verified with:
- `<previously failing command>`
- `<required guard>`
```

## Posting Replies

When the user wants actual GitHub replies, prefer `gh` commands and keep replies tied to the original thread when possible.

Before posting:

```bash
git diff --check
git status --short
```

For general PR comments:

```bash
gh pr comment <pr> --body-file <reply-file>
```

For review-thread replies, use the GitHub API only after confirming the thread/comment id from `gh api`. If the correct endpoint or id is unclear, draft the reply in the final answer instead of guessing.

After posting or drafting, provide a compact table:

| Comment | Decision | Evidence | Reply |
| --- | --- | --- | --- |
| `<reviewer/file>` | fixed/explained/deferred | `<test or file>` | posted/drafted |

## Guardrails

- Do not modify someone else's PR as if it is own.
- Do not push, resolve threads, or post comments without a clear target PR and user authorization.
- Do not treat a review comment as resolved until code/docs/tests actually address it.
- Do not make broad refactors while addressing review comments unless the reviewer request requires it.
- Do not hide validation failures; report them and either fix or explain the remaining blocker.
- Do not use identity guesses, branch prefixes, or string equality as business facts in aevatar code changes.
- Do not introduce compatibility shims or stale routes just to satisfy a review comment; prefer the repository's "delete over compatibility" rule unless the user explicitly asks otherwise.
