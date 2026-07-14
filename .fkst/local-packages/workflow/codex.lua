local M = {}

function M.judgment_codex_opts(prompt, worktree)
  return {
    prompt = prompt,
    worktree = worktree,
    sandbox = "read-only",
  }
end

return M
