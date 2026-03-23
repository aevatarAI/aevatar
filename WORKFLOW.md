---
tracker:
  kind: github
  api_key: $GITHUB_TOKEN
  project_slug: aevatarAI/aevatar
  active_states:
    - Todo
    - In Progress
    - Rework
  terminal_states:
    - Done
    - Closed
    - Cancelled
    - Canceled
    - Duplicate

polling:
  interval_ms: 30000

workspace:
  root: $SYMPHONY_WORKSPACE_ROOT

git:
  user_name: eanzhao
  email: yiqi.zhao@aelf.io

hooks:
  after_create: |
    gh auth setup-git --hostname github.com --force
    gh repo clone aevatarAI/aevatar . -- --depth=1
  before_run: |
    set -euo pipefail
    gh auth setup-git --hostname github.com --force
    git fetch origin
    DEFAULT_BRANCH="${SYMPHONY_DEFAULT_BRANCH:-dev}"
    BRANCH_PATTERN="*_issue-${SYMPHONY_ISSUE_NUMBER}-symphony"
    EXISTING_REMOTE="$(git for-each-ref --format='%(refname:short)' "refs/remotes/origin/${BRANCH_PATTERN}" | head -n1 || true)"
    EXISTING_LOCAL="$(git for-each-ref --format='%(refname:short)' "refs/heads/${BRANCH_PATTERN}" | head -n1 || true)"
    if [ -n "$EXISTING_REMOTE" ]; then
      BRANCH="${EXISTING_REMOTE#origin/}"
      git checkout "$BRANCH"
      git pull origin "$BRANCH"
    elif [ -n "$EXISTING_LOCAL" ]; then
      BRANCH="$EXISTING_LOCAL"
      git checkout "$BRANCH"
    else
      BRANCH="chore/$(date +%F)_issue-${SYMPHONY_ISSUE_NUMBER}-symphony"
      git checkout "$DEFAULT_BRANCH"
      git pull origin "$DEFAULT_BRANCH"
      git checkout -b "$BRANCH" "origin/$DEFAULT_BRANCH"
    fi
    dotnet restore aevatar.slnx --nologo
  after_run: |
    echo "Symphony finished ${SYMPHONY_ISSUE_IDENTIFIER}"
  timeout_ms: 1200000

agent:
  default: codex
  max_concurrent_agents: 1
  max_turns: 20
  max_retry_backoff_ms: 300000
  auto_merge: false
  require_label: symphony

agents:
  codex:
    command: codex app-server
    approval_policy: never
    thread_sandbox: workspace-write
    network_access: true
    turn_timeout_ms: 3600000
    read_timeout_ms: 10000
    stall_timeout_ms: 600000

server:
  port: 8081
---

You are working on Aevatar issue {{ issue.identifier }}: {{ issue.title }}.

Read `AGENTS.md` and `CLAUDE.md` before making changes. If they conflict, follow `AGENTS.md`.

## Repository Context

- Default branch: `dev`
- Main solution: `aevatar.slnx`
- .NET SDK: `10.0.103`
- Main workflow host: `src/workflow/Aevatar.Workflow.Host.Api`
- Mainnet host: `src/Aevatar.Mainnet.Host.Api`
- Frontend app: `apps/aevatar-console-web` using `pnpm`
- Do not introduce or document services on ports `5000` or `5050`

## Highest-Priority Repo Rules

1. Keep strict layering: `Domain / Application / Infrastructure / Host`
2. Keep CQRS, projection, actor, and read-model boundaries intact
3. Do not add process-local runtime state registries or generic query/reply shortcuts
4. Use strong typing for stable business semantics; do not push clear semantics into generic bags
5. Delete dead code instead of preserving compatibility shells

## Issue

- Identifier: {{ issue.identifier }}
- State: {{ issue.state }}
- URL: {{ issue.url }}

{% if issue.description %}
{{ issue.description }}
{% endif %}

{% if attempt %}
This is continuation attempt {{ attempt }}. Resume from the current workspace state and do not redo completed work.
{% endif %}

## Required Execution Flow

1. If the issue has label `todo`, move it to `in-progress` before coding:
   `gh issue edit {{ issue.identifier }} --repo aevatarAI/aevatar --remove-label todo --add-label in-progress`
2. Work on the branch already checked out by the hook:
   `git branch --show-current`
   Do not create a different branch name manually.
3. Stay inside the issue scope. Do not fix unrelated problems. If you find one, create a separate issue.
4. Use one persistent issue comment as a workpad with marker `## Symphony Workpad`.
5. After implementation and verification, push the current branch and create or update a PR.
6. When ready for handoff, move the issue to `human-review`:
   - from `in-progress`: remove `in-progress`, add `human-review`
   - from `rework`: remove `rework`, add `human-review`
7. If blocked, update the workpad with the blocker, leave a concise explanation, and move the issue to `human-review`.

## Workpad Commands

```bash
MARKER="## Symphony Workpad"
COMMENT_ID="$(gh api repos/aevatarAI/aevatar/issues/{{ issue.identifier | remove: "#" }}/comments --jq ".[] | select(.body | startswith(\"$MARKER\")) | .id" | head -n1)"
if [ -z "$COMMENT_ID" ]; then
  gh issue comment {{ issue.identifier }} --repo aevatarAI/aevatar --body "$MARKER
- [ ] Understand issue
- [ ] Implement change
- [ ] Verify
- [ ] Prepare PR / handoff"
  COMMENT_ID="$(gh api repos/aevatarAI/aevatar/issues/{{ issue.identifier | remove: "#" }}/comments --jq ".[] | select(.body | startswith(\"$MARKER\")) | .id" | head -n1)"
fi
```

Update the existing workpad comment instead of creating new progress comments:

```bash
gh api repos/aevatarAI/aevatar/issues/comments/$COMMENT_ID -X PATCH -f body="$MARKER
- [x] Understand issue
- [ ] Implement change
- [ ] Verify
- [ ] Prepare PR / handoff

Current focus: ..."
```

## Verification Expectations

Run the smallest relevant validation set that honestly covers your changes:

- Default backend validation: `dotnet build aevatar.slnx --nologo`
- Run targeted tests for touched projects, for example:
  `dotnet test test/<RelevantProject>.Tests/<RelevantProject>.Tests.csproj --nologo`
- If you touch `apps/aevatar-console-web`, run:
  `pnpm --dir apps/aevatar-console-web lint`
- If you change workflow, projection, query/read-model, or architecture-sensitive code, run the relevant guard scripts from `AGENTS.md`

Do not claim tests passed unless you actually ran them.

## PR Flow

Use the current checked-out branch for push and PR creation:

```bash
BRANCH="$(git branch --show-current)"
git push -u origin "$BRANCH"
PR="$(gh pr list --repo aevatarAI/aevatar --head "$BRANCH" --json number --jq '.[0].number')"
if [ -z "$PR" ]; then
  gh pr create \
    --repo aevatarAI/aevatar \
    --base dev \
    --head "$BRANCH" \
    --title "{{ issue.identifier }}: {{ issue.title }}" \
    --body "Closes {{ issue.identifier }}"
fi
```

## Finish Conditions

- Requested work is implemented
- Relevant validation ran
- Changes are committed and pushed
- PR exists or was updated
- Issue label moved to `human-review`

Once those are done, stop. Do not keep polishing beyond the issue scope.
