local h = require("tests.devloop_core_helpers")
local transition_version = require("contract.transition_version")
local t = h.t

local cases = {
  { value = "ready/consensus-2026-06-17T22:18:19Z/loop/12", expected = "ready-consensus-2026-06-17T22-2609426986" },
  { value = "", expected = "empty" },
  { value = nil, expected = "empty" },
  { value = "###", expected = "version" },
  { value = "/reviewing#head//fix/1/", expected = "reviewing-head-fix-1" },
  { value = "ready/consensus-owner-repo-42-2026-06-17T22:18:19Z/loop/12", expected = "ready-consensus-owner-repo-42-0920351821" },
}

return {
  test_safe_version_segment_matches_captured_devloop_goldens = function()
    for _, case in ipairs(cases) do
      t.eq(transition_version.safe_version_segment(case.value), case.expected)
    end
  end,
}
