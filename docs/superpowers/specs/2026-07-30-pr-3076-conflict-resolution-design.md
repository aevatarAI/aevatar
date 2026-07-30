# PR 3076 Squash-History Bridge Design

## Root Cause

PR #2996 was squash-merged from `feature/integrate` into `dev`. Its original head
`86c5688ec7553ac9cd4a2c19b498e4a5f1acc39d` and the resulting `dev` commit
`0073f0eb41b925197bf1d6ca5fec21c18598889d` have the same tree
`44feb0aebf925a72861db5c7d66a424e608a11c3`, but the original head is not an ancestor
of `dev`. Git therefore selects an older merge base and reports conflicts for changes whose
content is already present on both branches.

## Resolution

1. Use the tree-equivalent PR #2996 head as an explicit virtual merge base for the current
   `origin/feature/integrate` and `origin/dev` heads.
2. Create the result with `git merge-tree --write-tree --merge-base`. This combines only the
   real changes made after PR #2996 on each side and must report zero conflicts.
3. Replace the complete `apps/**` subtree with `origin/dev`; the user designated that tree
   as authoritative even for files that would merge cleanly.
4. Create one ordinary two-parent commit whose first parent is the current production branch
   and whose second parent is the current `dev` head. Do not rebase or force-push.

## Verification

- Prove the PR #2996 head tree and squash-merge tree are identical before constructing the bridge.
- Require `apps/**` to be byte-for-byte identical to `origin/dev`.
- Require both current branch heads to be ancestors of the bridge commit.
- Run repository guards, full .NET build/test, and frontend typecheck/test/build.
- Fetch immediately before push and require the remote production head to still equal the bridge
  commit's first parent.

## Branch Policy

PR #3076 must be merged with a real merge commit, not squash or rebase. After it lands, move
`feature/integrate` to the resulting `dev` head (fast-forward when possible, otherwise delete
and recreate it from `dev`). Future PRs from this long-lived integration branch must also use
merge commits so Git retains the shared ancestry.
