local h = require("tests.devloop_helpers")
local t = h.t
local core = h.core
local opts = h.opts
local issue = h.issue
local run_observe = h.run_observe
local mock_issue_state = h.mock_issue_state
local find_raise = h.find_raise
local render_comment = h.render_comment
local entity_read_mocks = require("tests.entity_read_mock_helpers")
local m_builders = require("devloop.markers.builders")

local proposal_id = "github-devloop/issue/owner/repo/42"
local impl_version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z"
local branch = "devloop-owner-repo-42-01HY"
local pr_proposal_id = "github-devloop/pr/owner/repo/7"

local function pr_link()
  return m_builders.pr_link_marker(core, proposal_id, 7, branch, impl_version, "dev")
end

local function mock_linked_pr(state, comments)
  entity_read_mocks.mock_pr_view_selector(t, {
    repo = "owner/repo",
    number = 7,
	    comments = comments or {
	      render_comment(m_builders.pr_origin_marker(core, proposal_id, 42, branch, impl_version, "dev")
	        .. "\n" .. core.state_marker(proposal_id, "pr-open", impl_version)),
	    },
    head = branch,
    head_sha = "def456",
    base_branch = "dev",
    state = state or "OPEN",
  }, entity_read_mocks.pr_origin_selector)
end

local function find_comment_with(raises, text)
  return find_raise(raises, "github-proxy.github_issue_comment_request", function(payload)
    return tostring(payload.body or ""):find(text, 1, true) ~= nil
  end)
end

return {
  test_legacy_issue_pr_open_with_open_pr_canonicalizes_to_awaiting_pr = function()
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:pr-open" }, "OPEN", {
      core.state_marker(proposal_id, "pr-open", impl_version),
      pr_link(),
    })
    mock_linked_pr("OPEN")

    local result = run_observe(issue({ labels = { "fkst-dev:enabled", "fkst-dev:pr-open" } }), opts("legacy-pr-open-canonicalize"))

    t.eq(result.exit_code, 0)
    local comment = find_comment_with(result.raises, 'state="awaiting-pr"')
    t.is_true(comment ~= nil)
    t.is_true(tostring(comment.payload.body):find("fkst:github-devloop:pr-delegation:v1", 1, true) ~= nil)
    t.is_true(tostring(comment.payload.body):find('pr_proposal="' .. pr_proposal_id .. '"', 1, true) ~= nil)
    local label = find_raise(result.raises, "github-proxy.github_issue_label_request", function(payload)
      return payload.add_labels[1] == "fkst-dev:awaiting-pr"
    end)
    t.is_true(label ~= nil)
    t.eq(label.payload.add_labels[1], "fkst-dev:awaiting-pr")
  end,

  test_awaiting_pr_issue_is_idempotent_for_legacy_canonicalizer = function()
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:awaiting-pr" }, "OPEN", {
      core.state_marker(proposal_id, "awaiting-pr", impl_version),
      m_builders.pr_delegation_marker(core, proposal_id, pr_proposal_id, 7, impl_version, "g1"),
    })
    mock_linked_pr("OPEN")

    local result = run_observe(issue({ labels = { "fkst-dev:enabled", "fkst-dev:awaiting-pr" } }), opts("legacy-pr-open-canonicalize-idempotent"))

    t.eq(result.exit_code, 0)
    t.eq(find_comment_with(result.raises, 'state="awaiting-pr"'), nil)
  end,

  test_legacy_pr_open_without_link_fails_closed_without_canonicalizing = function()
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:pr-open" }, "OPEN", {
      core.state_marker(proposal_id, "pr-open", impl_version),
    })

    local result = run_observe(issue({ labels = { "fkst-dev:enabled", "fkst-dev:pr-open" } }), opts("legacy-pr-open-canonicalize-missing-link"))

    t.eq(result.exit_code, 0)
    t.eq(find_comment_with(result.raises, 'state="awaiting-pr"'), nil)
  end,

  test_legacy_pr_open_with_closed_linked_pr_fails_closed_without_canonicalizing = function()
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:pr-open" }, "OPEN", {
      core.state_marker(proposal_id, "pr-open", impl_version),
      pr_link(),
    })
    mock_linked_pr("CLOSED")

    local result = run_observe(issue({ labels = { "fkst-dev:enabled", "fkst-dev:pr-open" } }), opts("legacy-pr-open-canonicalize-closed-pr"))

    t.eq(result.exit_code, 0)
    t.eq(find_comment_with(result.raises, 'state="awaiting-pr"'), nil)
  end,
}
