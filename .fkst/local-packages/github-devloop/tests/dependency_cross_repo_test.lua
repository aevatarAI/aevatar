local devloop_base = require("devloop.base")
local base_ids = require("devloop.base_ids")
local h = require("tests.devloop_helpers")
local m_builders = require("devloop.markers.builders")
local t = h.t
local core = h.core

local repo = "owner/repo"
local sibling_repo = "owner/substrate"
local foreign_repo = "other/repo"

local function encode_json_string(value)
  return tostring(value)
    :gsub("\\", "\\\\")
    :gsub('"', '\\"')
    :gsub("\n", "\\n")
end

local function render_comment(comment)
  if type(comment) ~= "table" then
    comment = { body = tostring(comment or ""), author_login = "fkst-test-bot" }
  end
  return string.format(
    '{"body":"%s","author":{"login":"%s"},"createdAt":"2026-06-03T01:00:00Z"}',
    encode_json_string(comment.body or ""),
    encode_json_string(comment.author_login or "fkst-test-bot")
  )
end

local function issue_comments_json(comments)
  local rendered = {}
  for _, comment in ipairs(comments or {}) do
    table.insert(rendered, render_comment(comment))
  end
  return table.concat(rendered, ",")
end

local function blocked_by_json(nodes)
  local rendered = {}
  for _, node in ipairs(nodes or {}) do
    table.insert(rendered, string.format(
      '{"number":%s,"state":"%s","stateReason":"%s","repository":{"nameWithOwner":"%s"}}',
      tostring(node.number),
      encode_json_string(node.state or "OPEN"),
      encode_json_string(node.state_reason or node.stateReason or ""),
      encode_json_string(node.repo or repo)
    ))
  end
  return '{"data":{"repository":{"issue":{"blockedBy":{"nodes":[' .. table.concat(rendered, ",") .. ']}}}}}\n'
end

local function mock_managed_repos(value)
  t.mock_command(devloop_base.read_env_command("FKST_DEVLOOP_MANAGED_SIBLING_REPOS"), {
    stdout = value or "",
    stderr = "",
    exit_code = 0,
  })
end

local function mock_blocked_by(issue_number, nodes)
  t.mock_command(core.gh_blocked_by_cmd(repo, issue_number), {
    stdout = blocked_by_json(nodes),
    stderr = "",
    exit_code = 0,
  })
end

local function mock_repo_blocker_issue(target_repo, issue_number, comments)
  t.mock_command(core.gh_issue_view_observe_cmd(target_repo, issue_number), {
    stdout = '{"state":"CLOSED","stateReason":"COMPLETED","comments":[' .. issue_comments_json(comments) .. ']}\n',
    stderr = "",
    exit_code = 0,
  })
end

local function mock_repo_blocker_issue_failure(target_repo, issue_number)
  t.mock_command(core.gh_issue_view_observe_cmd(target_repo, issue_number), {
    stdout = "",
    stderr = "issue view failed",
    exit_code = 1,
  })
end

local function mock_repo_blocker_pr(target_repo, pr_number, link, comments)
  t.mock_command(core.gh_pr_view_observe_cmd(target_repo, pr_number), {
    stdout = '{"headRefName":"' .. encode_json_string(link.branch)
      .. '","headRefOid":"abc123","baseRefName":"' .. encode_json_string(link.base_branch)
      .. '","state":"MERGED","comments":[' .. issue_comments_json(comments) .. ']}\n',
    stderr = "",
    exit_code = 0,
  })
end

local function state_comment(target_repo, issue_number, state_name, author_login)
  return {
    body = core.state_marker(base_ids.proposal_id(target_repo, issue_number), state_name, "v-" .. tostring(issue_number)),
    author_login = author_login or "fkst-test-bot",
  }
end

local function sibling_link(issue_number, pr_number)
  local proposal_id = base_ids.proposal_id(sibling_repo, issue_number)
  return {
    proposal_id = proposal_id,
    branch = "devloop-owner-substrate-" .. tostring(issue_number) .. "-01HY",
    impl_version = "v-" .. tostring(issue_number),
    base_branch = "dev",
    pr_number = pr_number,
  }
end

return {
  test_dependency_gate_releases_managed_sibling_with_trusted_merged_marker = function()
    mock_managed_repos("bad/repo? " .. sibling_repo)
    mock_blocked_by(42, { { number = 61, repo = sibling_repo, state = "OPEN" } })
    mock_repo_blocker_issue(sibling_repo, 61, {
      state_comment(sibling_repo, 61, "merged"),
    })
    local gate = core.dependency_gate(repo, 42)
    t.eq(gate.ok, true)
    t.eq(gate.kind, "satisfied")
  end,

  test_dependency_gate_releases_managed_sibling_with_trusted_pr_merged_marker = function()
    mock_managed_repos(sibling_repo)
    mock_blocked_by(42, { { number = 62, repo = sibling_repo, state = "OPEN" } })
    local link = sibling_link(62, 63)
    mock_repo_blocker_issue(sibling_repo, 62, {
      state_comment(sibling_repo, 62, "pr-open"),
      {
        body = m_builders.pr_link_marker(core, link.proposal_id, link.pr_number, link.branch, link.impl_version, link.base_branch),
        author_login = "fkst-test-bot",
      },
    })
    mock_repo_blocker_pr(sibling_repo, 63, link, {
      {
        body = m_builders.pr_origin_marker(core, link.proposal_id, 62, link.branch, link.impl_version, link.base_branch),
        author_login = "fkst-test-bot",
      },
      {
        body = core.state_marker(link.proposal_id, "merged", "merge-version-7"),
        author_login = "fkst-test-bot",
      },
      {
        body = m_builders.merged_marker(core, link.proposal_id, 63, "merge-version-7", "def456"),
        author_login = "fkst-test-bot",
      },
    })
    local gate = core.dependency_gate(repo, 42)
    t.eq(gate.ok, true)
    t.eq(gate.kind, "satisfied")
  end,

  test_dependency_gate_holds_managed_sibling_without_trusted_marker = function()
    mock_managed_repos(sibling_repo)
    mock_blocked_by(42, { { number = 64, repo = sibling_repo, state = "CLOSED", state_reason = "COMPLETED" } })
    mock_repo_blocker_issue(sibling_repo, 64, {
      state_comment(sibling_repo, 64, "merged", "ordinary-user"),
    })
    local stranger = core.dependency_gate(repo, 42)
    t.eq(stranger.ok, false)
    t.eq(stranger.kind, "waiting")
    t.eq(stranger.unmet[1], 64)

    mock_managed_repos(sibling_repo)
    mock_blocked_by(42, { { number = 65, repo = sibling_repo, state = "OPEN" } })
    mock_repo_blocker_issue(sibling_repo, 65, {
      state_comment(sibling_repo, 65, "ready"),
    })
    local open = core.dependency_gate(repo, 42)
    t.eq(open.ok, false)
    t.eq(open.kind, "waiting")
    t.eq(open.unmet[1], 65)
  end,

  test_dependency_gate_ignores_closed_completed_without_managed_marker = function()
    mock_managed_repos(sibling_repo)
    mock_blocked_by(42, { { number = 69, repo = sibling_repo, state = "CLOSED", state_reason = "COMPLETED" } })
    mock_repo_blocker_issue(sibling_repo, 69, {})
    local gate = core.dependency_gate(repo, 42)
    t.eq(gate.ok, false)
    t.eq(gate.kind, "waiting")
    t.eq(gate.reason, "waiting-on-dependency")
    t.eq(gate.unmet[1], 69)
  end,

  test_dependency_gate_fails_closed_for_unmanaged_sibling = function()
    mock_managed_repos("")
    mock_blocked_by(42, { { number = 66, repo = sibling_repo, state = "CLOSED", state_reason = "COMPLETED" } })
    local unmanaged = core.dependency_gate(repo, 42)
    t.eq(unmanaged.ok, false)
    t.eq(unmanaged.kind, "unresolvable")
    t.eq(unmanaged.reason, "cross-repo-blocker")
  end,

  test_dependency_gate_fails_closed_for_different_owner_sibling = function()
    mock_managed_repos(foreign_repo .. "," .. sibling_repo)
    mock_blocked_by(42, { { number = 67, repo = foreign_repo, state = "CLOSED", state_reason = "COMPLETED" } })
    local different_owner = core.dependency_gate(repo, 42)
    t.eq(different_owner.ok, false)
    t.eq(different_owner.kind, "unresolvable")
    t.eq(different_owner.reason, "cross-repo-blocker")
  end,

  test_dependency_gate_fails_closed_for_managed_sibling_fetch_failure = function()
    mock_managed_repos(sibling_repo)
    mock_blocked_by(42, { { number = 68, repo = sibling_repo, state = "CLOSED", state_reason = "COMPLETED" } })
    mock_repo_blocker_issue_failure(sibling_repo, 68)
    local failed = core.dependency_gate(repo, 42)
    t.eq(failed.ok, false)
    t.eq(failed.kind, "unresolvable")
    t.eq(failed.reason, "gh-failed")
    t.eq(failed.unmet[1], 68)
  end,
}
