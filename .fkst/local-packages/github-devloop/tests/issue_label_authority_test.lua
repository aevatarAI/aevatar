local h = require("tests.devloop_helpers")
local m_builders = require("devloop.markers.builders")
local t = h.t
local core = h.core
local opts = h.opts
local issue = h.issue
local run_observe = h.run_observe
local mock_issue_state = h.mock_issue_state
local mock_pr_origin_for = h.mock_pr_origin_for
local find_raise = h.find_raise
local count_calls = h.count_calls

local package_root = "packages/github-devloop"

local function read_source(path)
  local handle = assert(io.open(package_root .. "/" .. path, "r"))
  local body = handle:read("*a")
  handle:close()
  return body
end

local function department_main_paths()
  local root = package_root
  local paths = {}
  local find = assert(io.popen("find " .. root .. "/departments -mindepth 2 -maxdepth 2 -name main.lua | sort"))
  for path in find:lines() do
    table.insert(paths, path:sub(#root + 2))
  end
  local ok = find:close()
  if ok == false then
    error("github-devloop: department discovery failed")
  end
  return paths
end

local function writes_direct_pr_open_issue_label(body)
  return body:find('build_state_label_request%([^%)]-"pr%-open"', 1, false) ~= nil
    or body:find('build_reconcile_state_label_request%([^%)]-"pr%-open"', 1, false) ~= nil
    or body:find('state_label_changes%("pr%-open"%)', 1, false) ~= nil
    or body:find('state_label_reconcile_changes%([^%)]-"pr%-open"', 1, false) ~= nil
end

local function contains_value(values, expected)
  for _, value in ipairs(values or {}) do
    if value == expected then
      return true
    end
  end
  return false
end

return {
  test_observe_issue_reconciles_pr_open_label_when_backing_pr_exists = function()
    local proposal_id = "github-devloop/issue/owner/repo/42"
    local impl_version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z"
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:implementing" }, "OPEN", {
      core.state_marker(proposal_id, "pr-open", impl_version),
      m_builders.pr_link_marker(core, proposal_id, 7, "devloop-owner-repo-42-01HY", impl_version, "dev"),
    })
    mock_pr_origin_for({
      comments = {
        m_builders.pr_origin_marker(core, proposal_id, "42", "devloop-owner-repo-42-01HY", impl_version, "dev"),
      },
      times = 2,
    })

    local result = run_observe(issue({ labels = { "fkst-dev:enabled", "fkst-dev:implementing" } }), opts("observe-pr-open-label-authority"))
    t.eq(result.exit_code, 0)
    local label_raise = find_raise(result.raises, "github-proxy.github_issue_label_request", function(payload)
      return tostring(payload.target_kind or "issue") == "issue"
    end)
    t.eq(label_raise.payload.add_labels[1], "fkst-dev:awaiting-pr")
    t.is_true(contains_value(label_raise.payload.remove_labels, "fkst-dev:implementing"))
    t.eq(count_calls("--json body"), 0)
  end,

  test_pr_open_issue_state_label_authority_stays_in_observe_issue = function()
    for _, path in ipairs(department_main_paths()) do
      local body = read_source(path)
      if path ~= "departments/observe_issue/main.lua" then
        t.eq(writes_direct_pr_open_issue_label(body), false)
      end
    end

    local observe_body = read_source("departments/observe_issue/main.lua")
    t.is_true(observe_body:find("linked_entity_snapshot", 1, true) == nil)
    t.is_true(observe_body:find("linked_snapshot_issue_state", 1, true) == nil)
    t.is_true(observe_body:find("linked_pr_surface_snapshot", 1, true) ~= nil)
    t.is_true(observe_body:find("issue_label_projection_state(issue_state, link, snapshot)", 1, true) ~= nil)
    t.is_true(observe_body:find('issue_state.state == "pr-open"', 1, true) ~= nil)
    t.is_true(observe_body:find("linked_open_pr(snapshot, link.pr_number)", 1, true) ~= nil)
    t.is_true(observe_body:find("state_label_reconcile_changes", 1, true) ~= nil)
    t.is_true(observe_body:find("github-proxy.github_issue_label_request", 1, true) ~= nil)
  end,
}
