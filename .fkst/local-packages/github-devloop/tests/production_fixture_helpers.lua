local M = {}

function M.cjk_char()
  return string.char(0xe6, 0xb5, 0x8b)
end

function M.emoji_char()
  return string.char(0xf0, 0x9f, 0x98, 0x80)
end

function M.mixed_boundary_title()
  return "Implement boundary tests " .. M.cjk_char() .. " CJK " .. M.emoji_char() .. " emoji " .. string.rep("x", 260)
end

function M.board_digest_boundary_title()
  return string.rep("a", 59) .. M.cjk_char() .. "tail"
end

function M.long_repo()
  return string.rep("o", 45) .. "/" .. string.rep("r", 46)
end

function M.review_head_sha()
  return string.rep("a", 40)
end

function M.full_review_issue_version(repo)
  local value = repo or M.long_repo()
  return "ready/consensus-github-devloop/issue/" .. value .. "/42/2026-06-03T01-02-03Z/loop/1"
end

function M.unbounded_full_review_proposal_id()
  return "github-devloop/pr-review/"
    .. M.long_repo()
    .. "/187/"
    .. M.full_review_issue_version(M.long_repo())
    .. "/"
    .. M.review_head_sha()
end

return M
