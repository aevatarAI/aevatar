local h = require("tests.devloop_core_helpers")
local core = h.core
local t = h.t
local comment_strings = require("devloop.strings")

local issue_proposal_id = "github-devloop/issue/owner/repo/42"
local issue_version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z"

local function collect_markers(body)
  local markers = {}
  for marker in tostring(body or ""):gmatch("<!%-%- fkst:github%-devloop:.-%-%->") do
    table.insert(markers, marker)
  end
  return table.concat(markers, "\n")
end

local function strip_markers(body)
  return tostring(body or ""):gsub("<!%-%- fkst:github%-devloop:.-%-%->", "")
end

local function decompose_case()
  local decompose = {
    proposal_id = issue_proposal_id,
    version = issue_version,
    pr_number = 7,
  }
  return { id = "decomposed", request = { body = core.decomposed_comment_body(decompose, 2) } }
end

return {
  test_decomposed_comment_localization_keeps_machine_markers_stable = function()
    comment_strings.configure_output_lang(core, "en")
    local en = decompose_case()
    comment_strings.configure_output_lang(core, "zh")
    local zh = decompose_case()
    comment_strings.configure_output_lang(core, nil)

    t.eq(collect_markers(en.request.body), collect_markers(zh.request.body))
    t.is_true(strip_markers(en.request.body):find("github-devloop decomposed blocked PR into 2 follow-up issue", 1, true) ~= nil)
    t.is_true(strip_markers(zh.request.body):find("github-devloop 已将阻塞 PR 拆分为 2 个后续 issue", 1, true) ~= nil)
  end,
}
