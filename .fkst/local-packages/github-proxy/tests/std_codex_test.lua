local codex = require("workflow.codex")
local t = fkst.test

return {
  test_judgment_codex_opts_carries_read_only_intent = function()
    local opts = codex.judgment_codex_opts("prompt", "/tmp/fkst-rt/judgment-worktrees/demo")

    t.eq(opts.prompt, "prompt")
    t.eq(opts.worktree, "/tmp/fkst-rt/judgment-worktrees/demo")
    t.eq(opts.sandbox, "read-only")
  end,
}
