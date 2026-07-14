local h = require("tests.devloop_helpers")
local t = h.t
local core = h.core
local entity_read_mocks = require("tests.entity_read_mock_helpers")
local m_builders = require("devloop.markers.builders")

local repo = "owner/repo"
local dependent_number = 42
local blocker_number = 61
local child_pr_number = 62
local dependent_proposal = "github-devloop/issue/owner/repo/42"
local blocker_proposal = "github-devloop/issue/owner/repo/61"
local child_pr_proposal = "github-devloop/pr/owner/repo/62"
local dependent_version = "consensus:github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z"
local blocker_version = "ready/consensus-github-devloop/issue/owner/repo/61/2026-06-03T01-02-03Z"
local child_head_sha = "0123456789abcdef0123456789abcdef01234567"

local function blocked_by_json(nodes)
  local rendered = {}
  for _, node in ipairs(nodes or {}) do
    table.insert(rendered, string.format(
      '{"number":%d,"state":"%s","stateReason":"%s","repository":{"nameWithOwner":"%s"}}',
      tonumber(node.number),
      tostring(node.state or "OPEN"),
      tostring(node.state_reason or node.stateReason or ""),
      tostring(node.repo or repo)
    ))
  end
  return '{"data":{"repository":{"issue":{"blockedBy":{"totalCount":'
    .. tostring(#rendered)
    .. ',"pageInfo":{"hasNextPage":false},"nodes":['
    .. table.concat(rendered, ",")
    .. "]}}}}}\n"
end

local function mock_blocked_by(issue_number, nodes)
  t.mock_command(core.gh_blocked_by_cmd(repo, issue_number), {
    stdout = blocked_by_json(nodes),
    stderr = "",
    exit_code = 0,
  })
end

local function mock_dependent_issue()
  entity_read_mocks.mock_issue_read_forms(t, {
    repo = repo,
    number = dependent_number,
    labels = { "fkst-dev:enabled", "fkst-dev:ready", "fkst-dev:blocked-on-dependency" },
    comments = {
      core.state_marker(dependent_proposal, "dependency_wait", dependent_version),
      core.dependency_wait_marker(dependent_proposal, dependent_version, { blocker_number }),
    },
    assignees = { "fkst-test-bot" },
    author_login = "fkst-test-bot",
  })
end

local function mock_delegated_blocker_issue()
  entity_read_mocks.mock_issue_view_selector(t, {
    repo = repo,
    number = blocker_number,
    labels = { "fkst-dev:enabled", "fkst-dev:awaiting-pr" },
    comments = {
      core.state_marker(blocker_proposal, "awaiting-pr", blocker_version),
      m_builders.pr_delegation_marker(core, blocker_proposal, child_pr_proposal, child_pr_number, blocker_version, "g1"),
    },
    assignees = { "fkst-test-bot" },
    author_login = "fkst-test-bot",
  }, "title,comments,state,stateReason,assignees,author")
end

local function mock_merged_child_pr()
  entity_read_mocks.mock_pr_view_selector(t, {
    repo = repo,
    number = child_pr_number,
    state = "MERGED",
    head = "devloop-owner-repo-61-01HY",
    head_sha = child_head_sha,
    base_branch = "dev",
    comments = {
      m_builders.pr_origin_marker(core, blocker_proposal, blocker_number, "devloop-owner-repo-61-01HY", blocker_version, "dev"),
      core.state_marker(blocker_proposal, "merged", blocker_version),
      m_builders.merged_marker(core, blocker_proposal, child_pr_number, blocker_version, child_head_sha),
    },
  }, entity_read_mocks.pr_origin_selector)
end

local function find_raise(raises, queue, predicate)
  for _, item in ipairs(raises or {}) do
    if item.queue == queue and (predicate == nil or predicate(item.payload or {})) then
      return item
    end
  end
  return nil
end

local function ready_handoff_raise(raises)
  return find_raise(raises, "github-proxy.github_issue_comment_request", function(payload)
    return type(payload.handoff) == "table"
      and payload.handoff.kind == "github-devloop.ready"
  end)
end

local function has_marker(raises, marker_text)
  return find_raise(raises, "github-proxy.github_issue_comment_request", function(payload)
    return tostring(payload.body or ""):find(marker_text, 1, true) ~= nil
  end) ~= nil
end

return {
  test_dependency_wait_releases_when_blocker_delegated_pr_is_merged = function()
    mock_dependent_issue()
    mock_blocked_by(dependent_number, {
      { number = blocker_number, state = "CLOSED", state_reason = "COMPLETED" },
    })
    mock_delegated_blocker_issue()
    mock_merged_child_pr()

    local result = t.run_department("departments/observe_issue/main.lua", {
      queue = "github-proxy.github_entity_changed",
      payload = h.issue({
        number = dependent_number,
        labels = { "fkst-dev:enabled", "fkst-dev:ready", "fkst-dev:blocked-on-dependency" },
      }),
    }, h.opts("dependency-pr-delegation-cascade"))

    t.eq(result.exit_code, 0)
    t.is_true(ready_handoff_raise(result.raises) ~= nil)
    t.is_true(has_marker(result.raises, "fkst:github-devloop:dependency-release:v1"))
  end,
}
