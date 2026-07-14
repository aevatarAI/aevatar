local devloop_base = require("devloop.base")
local base_ids = require("devloop.base_ids")
local h = require("tests.devloop_helpers")
local t = h.t
local core = h.core
local entity_read_mocks = require("tests.entity_read_mock_helpers")
local gh_argv = require("testkit.gh_argv_mock")
local m_builders = require("devloop.markers.builders")

local repo = "owner/repo"
local proposal_id = "github-devloop/issue/owner/repo/42"
local version = "consensus:github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z"

local function source_ref()
  return {
    kind = "external",
    ref = "owner/repo#issue/42",
  }
end

local function encode_json_string(value)
  return tostring(value)
    :gsub("\\", "\\\\")
    :gsub('"', '\\"')
    :gsub("\n", "\\n")
end

local function render_comment(body)
  return string.format(
    '{"body":"%s","author":{"login":"fkst-test-bot"},"createdAt":"2026-06-03T01:00:00Z"}',
    encode_json_string(body or "")
  )
end

local function issue_comments_json(comments)
  local rendered = {}
  for _, comment in ipairs(comments or {}) do
    table.insert(rendered, render_comment(comment))
  end
  return table.concat(rendered, ",")
end

local function issue_view_json(labels, comments, state)
  local rendered_labels = {}
  for _, label in ipairs(labels or {}) do
    table.insert(rendered_labels, string.format('{"name":"%s"}', encode_json_string(label)))
  end
  return string.format(
    '{"title":"Implement dependency cascade","state":"%s","labels":[%s],"comments":[%s],"assignees":[{"login":"fkst-test-bot"}]}\n',
    encode_json_string(state or "OPEN"),
    table.concat(rendered_labels, ","),
    issue_comments_json(comments)
  )
end

local function observe_issue_state_json(labels, comments, state)
  local rendered_labels = {}
  for _, label in ipairs(labels or {}) do
    table.insert(rendered_labels, string.format('{"name":"%s"}', encode_json_string(label)))
  end
  return string.format(
    '{"state":"%s","labels":[%s],"comments":[%s],"assignees":[{"login":"fkst-test-bot"}]}\n',
    encode_json_string(state or "OPEN"),
    table.concat(rendered_labels, ","),
    issue_comments_json(comments)
  )
end

local function blocked_by_json(nodes)
  local rendered = {}
  local input = nodes or {}
  for _, node in ipairs(input) do
    local state_reason = node.state_reason or node.stateReason or ""
    table.insert(rendered, string.format(
      '{"number":%s,"state":"%s","stateReason":"%s","repository":{"nameWithOwner":"%s"}}',
      tostring(node.number),
      encode_json_string(node.state or "OPEN"),
      encode_json_string(state_reason),
      encode_json_string(node.repo or repo)
    ))
  end
  return '{"data":{"repository":{"issue":{"blockedBy":{"totalCount":'
    .. tostring(#input)
    .. ',"pageInfo":{"hasNextPage":false},"nodes":['
    .. table.concat(rendered, ",")
    .. ']}}}}}\n'
end

local function mock_blocked_by(issue_number, nodes)
  t.mock_command(core.gh_blocked_by_cmd(repo, issue_number), {
    stdout = blocked_by_json(nodes),
    stderr = "",
    exit_code = 0,
  })
end

local function mock_blocked_by_failure(issue_number)
  t.mock_command(core.gh_blocked_by_cmd(repo, issue_number), {
    stdout = "",
    stderr = "graphql failed",
    exit_code = 1,
  })
end

local function mock_blocked_by_malformed(issue_number)
  t.mock_command(core.gh_blocked_by_cmd(repo, issue_number), {
    stdout = "{",
    stderr = "",
    exit_code = 0,
  })
end

-- gh succeeds but the blockedBy list is truncated (more blockers than the page
-- returns). An unseen unmet blocker must fail-closed, never read as absent.
local function mock_blocked_by_truncated(issue_number)
  t.mock_command(core.gh_blocked_by_cmd(repo, issue_number), {
    stdout = '{"data":{"repository":{"issue":{"blockedBy":{"totalCount":51,"pageInfo":{"hasNextPage":true},"nodes":[{"number":7,"state":"CLOSED","repository":{"nameWithOwner":"' .. repo .. '"}}]}}}}}\n',
    stderr = "",
    exit_code = 0,
  })
end

local function mock_blocker_issue(issue_number, state_name)
  local comments = {}
  if state_name ~= nil then
    table.insert(comments, core.state_marker(base_ids.proposal_id(repo, issue_number), state_name, "v-" .. tostring(issue_number)))
  end
  t.mock_command(core.gh_issue_view_observe_cmd(repo, issue_number), {
    stdout = '{"state":"OPEN","comments":[' .. issue_comments_json(comments) .. ']}\n',
    stderr = "",
    exit_code = 0,
  })
end

local function dependency_waiver_comment(blocker_number, waiver_version)
  return core.dependency_waiver_marker(
    proposal_id,
    waiver_version or version,
    blocker_number,
    "operator-waiver"
  )
end

local function mock_blocker_issue_failure(issue_number)
  t.mock_command(core.gh_issue_view_observe_cmd(repo, issue_number), {
    stdout = "",
    stderr = "issue view failed",
    exit_code = 1,
  })
end

local function mock_blocker_issue_with_pr_link(issue_number, pr_number, state_name)
  local blocker_proposal_id = base_ids.proposal_id(repo, issue_number)
  local branch = "devloop-owner-repo-" .. tostring(issue_number) .. "-01HY"
  local impl_version = "v-" .. tostring(issue_number)
  local comments = {}
  if state_name ~= nil then
    table.insert(comments, core.state_marker(blocker_proposal_id, state_name, impl_version))
  end
  table.insert(comments, m_builders.pr_link_marker(core, blocker_proposal_id, pr_number, branch, impl_version, "dev"))
  t.mock_command(core.gh_issue_view_observe_cmd(repo, issue_number), {
    stdout = '{"state":"OPEN","comments":[' .. issue_comments_json(comments) .. ']}\n',
    stderr = "",
    exit_code = 0,
  })
  return {
    proposal_id = blocker_proposal_id,
    branch = branch,
    impl_version = impl_version,
    base_branch = "dev",
  }
end

local function mock_blocker_pr(issue_number, pr_number, link, comments)
  local rendered_comments = comments or {
    m_builders.pr_origin_marker(core, link.proposal_id, issue_number, link.branch, link.impl_version, link.base_branch),
  }
  t.mock_command(core.gh_pr_view_observe_cmd(repo, pr_number), {
    stdout = '{"headRefName":"' .. encode_json_string(link.branch)
      .. '","headRefOid":"abc123","baseRefName":"' .. encode_json_string(link.base_branch)
      .. '","state":"MERGED","comments":[' .. issue_comments_json(rendered_comments) .. ']}\n',
    stderr = "",
    exit_code = 0,
  })
end

local function mock_blocker_pr_failure(pr_number)
  t.mock_command(core.gh_pr_view_observe_cmd(repo, pr_number), {
    stdout = "",
    stderr = "pr view failed",
    exit_code = 1,
  })
end

local function mock_result_issue(labels, comments)
  h.mock_issue_result(labels or { "fkst-dev:thinking" }, comments or {
    core.state_marker(proposal_id, "thinking", "2026-06-02T00-00-00Z"),
  }, {
    title = "Implement dependency cascade",
  })
end

local function mock_observe_issue(labels, comments)
  entity_read_mocks.mock_issue_read_forms(t, {
    repo = repo,
    number = 42,
    labels = labels or { "fkst-dev:enabled", "fkst-dev:ready" },
    comments = comments or {
      core.state_marker(proposal_id, "ready", version),
    },
    times = 1,
  })
  t.mock_command(core.gh_issue_view_entity_cmd(repo, 42), {
    stdout = issue_view_json(labels or { "fkst-dev:enabled", "fkst-dev:ready" }, comments or {
      core.state_marker(proposal_id, "ready", version),
    }),
    stderr = "",
    exit_code = 0,
  })
end

local function mock_implement_issue(labels, comments)
  t.mock_command(core.gh_issue_view_implement_cmd(repo, 42), {
    stdout = issue_view_json(labels or { "fkst-dev:ready" }, comments or {
      core.state_marker(proposal_id, "ready", h.ready().dedup_key),
    }),
    stderr = "",
    exit_code = 0,
  })
end

local function mock_repo()
  t.mock_command(devloop_base.read_env_command("FKST_GITHUB_REPO"), {
    stdout = repo,
    stderr = "",
    exit_code = 0,
  })
end

local function mock_liveness_issue_list(items)
  local rendered = {}
  for _, item in ipairs(items or {}) do
    table.insert(rendered, string.format(
      '{"number":%d,"state":"%s","updated_at":"%s"}',
      tonumber(item.number),
      encode_json_string(item.state or "open"),
      encode_json_string(item.updated_at or "")
    ))
  end
  t.mock_command(core.gh_issue_list_observe_cmd(repo), {
    stdout = "[" .. table.concat(rendered, ",") .. "]\n",
    stderr = "",
    exit_code = 0,
  })
end

local function mock_liveness_pr_list(items)
  local rendered = {}
  for _, item in ipairs(items or {}) do
    table.insert(rendered, string.format(
      '{"number":%d,"state":"%s","updated_at":"%s"}',
      tonumber(item.number),
      encode_json_string(item.state or "open"),
      encode_json_string(item.updated_at or "")
    ))
  end
  t.mock_command(core.gh_pr_list_observe_cmd(repo), {
    stdout = "[" .. table.concat(rendered, ",") .. "]\n",
    stderr = "",
    exit_code = 0,
  })
end

local function reached()
  return {
    schema = "consensus.consensus_reached.v1",
    proposal_id = proposal_id,
    decision = "approve",
    body = "Approved.",
    dedup_key = version,
    source_ref = source_ref(),
  }
end

local function run_result()
  return h.run_result(reached(), h.opts("dependency-result"))
end

local function run_observe()
  return t.run_department("departments/observe_issue/main.lua", {
    queue = "github-proxy.github_entity_changed",
    payload = h.issue(),
  }, h.opts("dependency-observe"))
end

local function run_liveness_scan()
  return t.run_department("departments/liveness_scan/main.lua", {
    queue = "devloop_liveness_tick",
    payload = { schema = "github-devloop.tick.v1" },
    ts = "2026-06-03T01:32:03Z",
  }, h.opts("dependency-liveness-scan"))
end

local function run_implement()
  return t.run_department("departments/implement/main.lua", {
    queue = "devloop_ready",
    payload = h.ready(),
  }, h.opts("dependency-implement"))
end

local function find_raise(raises, queue, predicate)
  for _, item in ipairs(raises or {}) do
    if item.queue == queue and (predicate == nil or predicate(item.payload)) then
      return item
    end
  end
  return nil
end

local function has_queue(raises, queue)
  return find_raise(raises, queue) ~= nil
end

local function count_queue(raises, queue)
  local count = 0
  for _, item in ipairs(raises or {}) do
    if item.queue == queue then
      count = count + 1
    end
  end
  return count
end

local function has_marker(raises, marker_text)
  return find_raise(raises, "github-proxy.github_issue_comment_request", function(payload)
    return tostring(payload.body or ""):find(marker_text, 1, true) ~= nil
  end) ~= nil
end

local function count_calls(needle)
  local count = 0
  for _, call in ipairs(t.command_calls()) do
    if gh_argv.call_contains(call, needle) then
      count = count + 1
    end
  end
  return count
end

local function marker_body(raises, needle)
  local raise = find_raise(raises, "github-proxy.github_issue_comment_request", function(payload)
    return type(payload.body) == "string" and payload.body:find(needle, 1, true) ~= nil
  end)
  return raise and raise.payload.body or nil
end

local function ready_handoff_raise(raises)
  return find_raise(raises, "github-proxy.github_issue_comment_request", function(payload)
    return type(payload.handoff) == "table"
      and payload.handoff.kind == "github-devloop.ready"
  end)
end

return {
  test_dependency_graphql_contract_is_named = function()
    local operations = core.github_graphql_queries

    t.eq(type(operations), "table")
    t.eq(type(operations.dependency_blocked_by), "string")
    t.eq(operations.dependency_blocked_by:find("blockedBy(first:50)", 1, true) ~= nil, true)
    t.eq(operations.dependency_blocked_by:find("nodes{number state stateReason repository{nameWithOwner}}", 1, true) ~= nil, true)
    t.eq(
      core.render_github_graphql_query("dependency_blocked_by", {
        owner = "owner",
        name = "repo",
        issue_number = 42,
      }),
      '{repository(owner:"owner",name:"repo"){issue(number:42){blockedBy(first:50){totalCount pageInfo{hasNextPage} nodes{number state stateReason repository{nameWithOwner}}}}}}'
    )
  end,

  test_dependency_gate_satisfied_without_blockers = function()
    mock_blocked_by(42, {})
    local gate = core.dependency_gate(repo, 42)
    t.eq(gate.ok, true)
    t.eq(gate.kind, "satisfied")
  end,

  test_dependency_markers_are_versioned_and_bounded = function()
    t.eq(
      core.dependency_wait_marker(proposal_id, "v1", { 1, 2, 3 }),
      '<!-- fkst:github-devloop:dependency-wait:v1 proposal="github-devloop/issue/owner/repo/42" version="v1" hold_kind="waiting" reason="waiting-on-dependency" unmet="1,2,3" -->'
    )
    t.eq(
      core.dependency_cycle_marker(proposal_id, "v1"),
      '<!-- fkst:github-devloop:dependency-cycle:v1 proposal="github-devloop/issue/owner/repo/42" version="v1" -->'
    )
    t.eq(
      core.dependency_unresolvable_marker(proposal_id, "v1", { 1, 2, 3 }),
      '<!-- fkst:github-devloop:dependency-unresolvable:v1 proposal="github-devloop/issue/owner/repo/42" version="v1" hold_kind="unresolvable" reason="gh-failed" unmet="1,2,3" -->'
    )
    t.eq(
      core.dependency_release_marker(proposal_id, "v1"),
      '<!-- fkst:github-devloop:dependency-release:v1 proposal="github-devloop/issue/owner/repo/42" version="v1" -->'
    )
  end,

  test_dependency_gate_waiting_for_open_blocker = function()
    mock_blocked_by(42, { { number = 11 } })
    mock_blocked_by(11, {})
    mock_blocker_issue(11, "ready")
    local gate = core.dependency_gate(repo, 42)
    t.eq(gate.ok, false)
    t.eq(gate.kind, "waiting")
    t.eq(gate.unmet[1], 11)
  end,

  test_dependency_gate_satisfied_for_merged_blocker = function()
    mock_blocked_by(42, { { number = 12 } })
    mock_blocked_by(12, {})
    mock_blocker_issue(12, "merged")
    local gate = core.dependency_gate(repo, 42)
    t.eq(gate.ok, true)
    t.eq(gate.kind, "satisfied")
  end,

  test_dependency_gate_caches_terminal_merged_blocker = function()
    mock_blocked_by(42, { { number = 17, state = "CLOSED" } })
    mock_blocked_by_failure(17)
    mock_blocker_issue(17, "merged")
    local first = core.dependency_gate(repo, 42)
    t.eq(first.ok, true)
    t.eq(first.kind, "satisfied")
    local graphql_calls_after_first = count_calls("gh api graphql")
    t.eq(graphql_calls_after_first, 1)

    mock_blocked_by(42, { { number = 17, state = "CLOSED" } })
    mock_blocked_by_failure(17)
    local second = core.dependency_gate(repo, 42)
    t.eq(second.ok, true)
    t.eq(second.kind, "satisfied")
    t.eq(count_calls("gh api graphql"), graphql_calls_after_first + 1)

    mock_blocked_by(42, { { number = 17, state = "CLOSED" }, { number = 18 } })
    mock_blocked_by(18, {})
    mock_blocker_issue(18, "ready")
    local changed_root_edges = core.dependency_gate(repo, 42)
    t.eq(changed_root_edges.ok, false)
    t.eq(changed_root_edges.kind, "waiting")
    t.eq(changed_root_edges.unmet[1], 18)

    mock_blocked_by(42, { { number = 17, state = "CLOSED" } })
    mock_blocked_by(17, {})
    mock_blocker_issue_failure(17)
    local third = core.dependency_gate(repo, 42)
    t.eq(third.ok, true)
    t.eq(third.kind, "satisfied")

    t.eq(core.merged_blocker_cache_key(repo, 17), "github-devloop/dependency/merged/owner/repo/issue/17")
  end,

  test_dependency_gate_does_not_cache_waiting_blocker = function()
    local graphql_calls_before = count_calls("gh api graphql")
    mock_blocked_by(42, { { number = 27 } })
    mock_blocked_by(27, {})
    mock_blocker_issue(27, "ready")
    local first = core.dependency_gate(repo, 42)
    t.eq(first.ok, false)
    t.eq(first.kind, "waiting")
    t.eq(first.unmet[1], 27)

    mock_blocked_by(42, { { number = 27 } })
    mock_blocked_by(27, {})
    mock_blocker_issue(27, "ready")
    local second = core.dependency_gate(repo, 42)
    t.eq(second.ok, false)
    t.eq(second.kind, "waiting")
    t.eq(second.unmet[1], 27)
    t.eq(count_calls("gh api graphql"), graphql_calls_before + 4)
  end,

  test_dependency_gate_satisfied_for_pr_stream_merged_blocker = function()
    mock_blocked_by(42, { { number = 31, state = "CLOSED" } })
    mock_blocked_by_failure(31)
    local link = mock_blocker_issue_with_pr_link(31, 32, "pr-open")
    mock_blocker_pr(31, 32, link, {
      m_builders.pr_origin_marker(core, link.proposal_id, 31, link.branch, link.impl_version, link.base_branch),
      core.state_marker(link.proposal_id, "merged", "merge-version-7"),
      m_builders.merged_marker(core, link.proposal_id, 32, "merge-version-7", "def456"),
    })
    local gate = core.dependency_gate(repo, 42)
    t.eq(gate.ok, true)
    t.eq(gate.kind, "satisfied")
  end,

  test_dependency_gate_waits_when_linked_pr_has_no_merged_fact = function()
    mock_blocked_by(42, { { number = 33 } })
    mock_blocked_by(33, {})
    local link = mock_blocker_issue_with_pr_link(33, 34, "pr-open")
    mock_blocker_pr(33, 34, link, {
      m_builders.pr_origin_marker(core, link.proposal_id, 33, link.branch, link.impl_version, link.base_branch),
      core.state_marker(link.proposal_id, "merge-ready", "merge-version-7"),
    })
    local gate = core.dependency_gate(repo, 42)
    t.eq(gate.ok, false)
    t.eq(gate.kind, "waiting")
    t.eq(gate.unmet[1], 33)
  end,

  test_dependency_gate_closed_completed_without_merge_requires_waiver = function()
    mock_blocked_by(42, { { number = 28, state = "CLOSED", state_reason = "COMPLETED" } })
    mock_blocker_issue(28, "ready")
    local gate = core.dependency_gate(repo, 42)
    t.eq(gate.ok, false)
    t.eq(gate.kind, "waiting")
    t.eq(gate.reason, "dependency-waiver-required")
    t.eq(gate.unmet[1], 28)
  end,

  test_dependency_gate_closed_completed_with_waiver_is_satisfied = function()
    mock_blocked_by(42, { { number = 29, state = "CLOSED", state_reason = "COMPLETED" } })
    mock_blocker_issue(29, "ready")
    local gate = core.dependency_gate(repo, 42, {
      proposal_id = proposal_id,
      version = version,
      comments = {
        dependency_waiver_comment(29),
      },
    })
    t.eq(gate.ok, true)
    t.eq(gate.kind, "satisfied")
    t.eq(gate.reason, "dependency-waiver")
  end,

  test_dependency_gate_closed_not_planned_voids_edge = function()
    mock_blocked_by(42, { { number = 30, state = "CLOSED", state_reason = "NOT_PLANNED" } })
    local gate = core.dependency_gate(repo, 42)
    t.eq(gate.ok, true)
    t.eq(gate.kind, "satisfied")
    t.eq(gate.reason, "dependency-void")
    t.eq(gate.notes[1].kind, "dependency-void")
    t.eq(gate.notes[1].blocker_number, 30)
  end,

  test_dependency_gate_pr_stream_fetch_failure_fails_closed = function()
    mock_blocked_by(42, { { number = 35 } })
    mock_blocked_by(35, {})
    mock_blocker_issue_with_pr_link(35, 36, "pr-open")
    mock_blocker_pr_failure(36)
    local gate = core.dependency_gate(repo, 42)
    t.eq(gate.ok, false)
    t.eq(gate.kind, "unresolvable")
    t.eq(gate.unmet[1], 35)
  end,

  test_dependency_gate_cycle = function()
    mock_blocked_by(42, { { number = 37 } })
    mock_blocked_by(37, { { number = 42 } })
    local gate = core.dependency_gate(repo, 42)
    t.eq(gate.ok, false)
    t.eq(gate.kind, "cycle")
  end,

  test_dependency_gate_cross_repo_and_failures_unresolvable = function()
    t.mock_command(devloop_base.read_env_command("FKST_DEVLOOP_MANAGED_SIBLING_REPOS"), {
      stdout = "",
      stderr = "",
      exit_code = 0,
    })
    mock_blocked_by(42, { { number = 41, repo = "other/repo" } })
    local cross_repo = core.dependency_gate(repo, 42)
    t.eq(cross_repo.ok, false)
    t.eq(cross_repo.kind, "unresolvable")

    mock_blocked_by_failure(42)
    local failed = core.dependency_gate(repo, 42)
    t.eq(failed.ok, false)
    t.eq(failed.kind, "unresolvable")

    mock_blocked_by_malformed(42)
    local malformed = core.dependency_gate(repo, 42)
    t.eq(malformed.ok, false)
    t.eq(malformed.kind, "unresolvable")
  end,

  test_dependency_gate_truncated_blockedby_fails_closed = function()
    -- 51 blockers exist but the page returns 1 (merged); the unseen 50 must not
    -- be read as absent. The gate must fail-closed, NOT return ok=true.
    mock_blocked_by_truncated(42)
    local gate = core.dependency_gate(repo, 42)
    t.eq(gate.ok, false)
    t.eq(gate.kind, "unresolvable")
  end,

  test_consensus_result_holds_for_unmet_dependency = function()
    mock_result_issue()
    mock_blocked_by(42, { { number = 51 } })
    mock_blocked_by(51, {})
    mock_blocker_issue(51, "ready")
    local result = run_result()
    t.eq(result.exit_code, 0)
    t.eq(has_queue(result.raises, "devloop_ready"), false)
    t.is_true(has_marker(result.raises, "fkst:github-devloop:dependency-wait:v1"))
    local label = find_raise(result.raises, "github-proxy.github_issue_label_request", function(payload)
      return h.has_value(payload.add_labels, "fkst-dev:blocked-on-dependency")
    end)
    t.is_true(label ~= nil)
  end,

  test_consensus_result_raises_ready_for_satisfied_dependency = function()
    mock_result_issue()
    mock_blocked_by(42, { { number = 52 } })
    mock_blocked_by(52, {})
    mock_blocker_issue(52, "merged")
    local result = run_result()
    t.eq(result.exit_code, 0)
    t.eq(has_queue(result.raises, "devloop_ready"), false)
    t.is_true(ready_handoff_raise(result.raises) ~= nil)
  end,

  test_observe_issue_ready_holds_then_cascades_when_satisfied = function()
    mock_observe_issue()
    mock_blocked_by(42, { { number = 53 } })
    mock_blocked_by(53, {})
    mock_blocker_issue(53, "ready")
    local held = run_observe()
    t.eq(held.exit_code, 0)
    t.eq(has_queue(held.raises, "devloop_ready"), false)
    t.is_true(has_marker(held.raises, "fkst:github-devloop:dependency-wait:v1"))

    mock_observe_issue(
      { "fkst-dev:enabled", "fkst-dev:ready", "fkst-dev:blocked-on-dependency" },
      {
        core.state_marker(proposal_id, "dependency_wait", version),
        "github-devloop dependency hold: waiting\n\nReason: waiting-on-dependency\n\n"
          .. core.dependency_wait_marker(proposal_id, version, { 53 }),
      }
    )
    mock_blocked_by(42, { { number = 53 } })
    mock_blocked_by(53, {})
    mock_blocker_issue(53, "merged")
    local cascaded = run_observe()
    t.eq(cascaded.exit_code, 0)
    t.eq(has_queue(cascaded.raises, "devloop_ready"), false)
    t.is_true(ready_handoff_raise(cascaded.raises) ~= nil)
    t.is_true(has_marker(cascaded.raises, "fkst:github-devloop:dependency-release:v1"))
    local clear = find_raise(cascaded.raises, "github-proxy.github_issue_label_request", function(payload)
      return h.has_value(payload.remove_labels, "fkst-dev:blocked-on-dependency")
    end)
    t.is_true(clear ~= nil)
  end,

  test_legacy_ready_cycle_hold_canonicalizes_to_dependency_wait = function()
    mock_observe_issue(
      { "fkst-dev:enabled", "fkst-dev:ready", "fkst-dev:blocked-on-dependency" },
      {
        core.state_marker(proposal_id, "ready", version),
        "github-devloop dependency hold: cycle\n\nReason: dependency-cycle\n\n"
          .. core.dependency_cycle_marker(proposal_id, version),
      }
    )
    mock_blocked_by(42, { { number = 54 } })
    mock_blocked_by(54, { { number = 42 } })
    local result = run_observe()
    t.eq(result.exit_code, 0)
    t.eq(has_queue(result.raises, "devloop_ready"), false)
    local split_version = core.ready_split_version(version)
    local body = marker_body(result.raises, "ready-split-canonicalized:v1")
    t.is_true(body ~= nil)
    t.is_true(body:find('derived_state="dependency_wait"', 1, true) ~= nil)
    t.is_true(body:find('state="dependency_wait"', 1, true) ~= nil)
    t.is_true(body:find('version="' .. split_version .. '"', 1, true) ~= nil)
    t.is_true(body:find("fkst:github-devloop:dependency-wait:v1", 1, true) ~= nil)
  end,

  test_legacy_ready_satisfied_split_raises_ready_at_split_version_once = function()
    mock_observe_issue(
      { "fkst-dev:enabled", "fkst-dev:ready", "fkst-dev:blocked-on-dependency" },
      {
        core.state_marker(proposal_id, "ready", version),
        "github-devloop dependency hold: waiting\n\nReason: waiting-on-dependency\n\n"
          .. core.dependency_wait_marker(proposal_id, version, { 53 }),
      }
    )
    mock_blocked_by(42, { { number = 53 } })
    mock_blocked_by(53, {})
    mock_blocker_issue(53, "merged")
    local result = run_observe()
    t.eq(result.exit_code, 0)
    t.eq(count_queue(result.raises, "devloop_ready"), 0)
    local split_version = core.ready_split_version(version)
    local ready_comment = ready_handoff_raise(result.raises)
    t.is_true(ready_comment ~= nil)
    t.eq(ready_comment.payload.handoff.marker_version, split_version)
    local body = marker_body(result.raises, "ready-split-canonicalized:v1")
    t.is_true(body ~= nil)
    t.is_true(body:find('state="ready"', 1, true) ~= nil)
    t.is_true(body:find('version="' .. split_version .. '"', 1, true) ~= nil)
  end,

  test_liveness_scan_reinjected_dependency_hold_uses_observe_gate = function()
    mock_repo()
    mock_liveness_issue_list({ { number = 42, state = "open", updated_at = "2026-06-03T01:02:03Z" } })
    mock_liveness_pr_list({})
    mock_observe_issue(
      { "fkst-dev:enabled", "fkst-dev:ready", "fkst-dev:blocked-on-dependency" },
      {
        core.state_marker(proposal_id, "dependency_wait", version),
        "github-devloop dependency hold: waiting\n\nReason: waiting-on-dependency\n\n"
          .. core.dependency_wait_marker(proposal_id, version, { 53 }),
      }
    )
    local scanned = run_liveness_scan()
    t.eq(scanned.exit_code, 0)
    local changed = find_raise(scanned.raises, "devloop_observe_issue")
    t.is_true(changed ~= nil)

    mock_observe_issue(
      { "fkst-dev:enabled", "fkst-dev:ready", "fkst-dev:blocked-on-dependency" },
      {
        core.state_marker(proposal_id, "dependency_wait", version),
        "github-devloop dependency hold: waiting\n\nReason: waiting-on-dependency\n\n"
          .. core.dependency_wait_marker(proposal_id, version, { 53 }),
      }
    )
    mock_blocked_by(42, { { number = 53 } })
    mock_blocked_by(53, {})
    mock_blocker_issue(53, "merged")
    local observed = t.run_department("departments/observe_issue/main.lua", {
      queue = "devloop_observe_issue",
      payload = h.issue({
        dedup_key = changed.payload.dedup_key,
        source_ref = changed.payload.source_ref,
      }),
    }, h.opts("dependency-liveness-observe"))
    t.eq(observed.exit_code, 0)
    t.eq(has_queue(observed.raises, "devloop_ready"), false)
    t.is_true(ready_handoff_raise(observed.raises) ~= nil)
    t.is_true(has_marker(observed.raises, "fkst:github-devloop:dependency-release:v1"))
  end,

  test_consensus_result_releases_not_planned_blocker_with_void_audit = function()
    mock_result_issue()
    mock_blocked_by(42, { { number = 56, state = "CLOSED", state_reason = "NOT_PLANNED" } })
    local result = run_result()
    t.eq(result.exit_code, 0)
    t.eq(has_queue(result.raises, "devloop_ready"), false)
    t.is_true(ready_handoff_raise(result.raises) ~= nil)
    t.is_true(has_marker(result.raises, "fkst:github-devloop:dependency-release:v1"))
    t.is_true(has_marker(result.raises, "fkst:github-devloop:dependency-void:v1"))
    t.is_true(has_marker(result.raises, 'blocker="56"'))
  end,

  test_observe_issue_hold_releases_not_planned_blocker_with_void_audit = function()
    mock_observe_issue(
      { "fkst-dev:enabled", "fkst-dev:ready", "fkst-dev:blocked-on-dependency" },
      {
        core.state_marker(proposal_id, "dependency_wait", version),
        "github-devloop dependency hold: waiting\n\nReason: waiting-on-dependency\n\n"
          .. core.dependency_wait_marker(proposal_id, version, { 57 }),
      }
    )
    mock_blocked_by(42, { { number = 57, state = "CLOSED", state_reason = "NOT_PLANNED" } })
    local released = run_observe()
    t.eq(released.exit_code, 0)
    t.eq(has_queue(released.raises, "devloop_ready"), false)
    t.is_true(ready_handoff_raise(released.raises) ~= nil)
    t.is_true(has_marker(released.raises, "fkst:github-devloop:dependency-release:v1"))
    t.is_true(has_marker(released.raises, "fkst:github-devloop:dependency-void:v1"))
    local clear = find_raise(released.raises, "github-proxy.github_issue_label_request", function(payload)
      return h.has_value(payload.remove_labels, "fkst-dev:blocked-on-dependency")
    end)
    t.is_true(clear ~= nil)
  end,

  test_consensus_result_holds_completed_blocker_without_waiver = function()
    mock_result_issue()
    mock_blocked_by(42, { { number = 58, state = "CLOSED", state_reason = "COMPLETED" } })
    mock_blocker_issue(58, "ready")
    local result = run_result()
    t.eq(result.exit_code, 0)
    t.eq(has_queue(result.raises, "devloop_ready"), false)
    t.is_true(has_marker(result.raises, "fkst:github-devloop:dependency-wait:v1"))
    t.is_true(has_marker(result.raises, 'reason="dependency-waiver-required"'))
  end,

  test_trusted_dependency_waiver_command_creates_waiver_and_requeues_ready = function()
    mock_observe_issue(
      { "fkst-dev:enabled", "fkst-dev:ready", "fkst-dev:blocked-on-dependency" },
      {
        core.state_marker(proposal_id, "dependency_wait", version),
        "fkst: dependency-waiver 60",
        "github-devloop dependency hold: waiting\n\nReason: dependency-waiver-required\n\n"
          .. core.dependency_wait_marker(proposal_id, version, { 60 }, "waiting", "dependency-waiver-required"),
      }
    )
    mock_blocked_by(42, { { number = 60, state = "CLOSED", state_reason = "COMPLETED" } })
    mock_blocker_issue(60, "ready")
    local result = run_observe()
    t.eq(result.exit_code, 0)
    t.eq(has_queue(result.raises, "devloop_ready"), false)
    local split_version = core.ready_split_version(version)
    local ready_comment = ready_handoff_raise(result.raises)
    t.is_true(ready_comment ~= nil)
    t.eq(ready_comment.payload.handoff.marker_version, split_version)
    t.is_true(has_marker(result.raises, "fkst:github-devloop:dependency-waiver:v1"))
    t.is_true(has_marker(result.raises, "fkst:github-devloop:dependency-release:v1"))
    t.is_true(has_marker(result.raises, "fkst:github-devloop:ready-split-canonicalized:v1"))
    t.is_true(has_marker(result.raises, 'state="ready"'))
    t.is_true(has_marker(result.raises, 'version="' .. split_version .. '"'))
    t.is_true(has_marker(result.raises, "fkst:github-devloop:operator-command:v1"))
    t.is_true(has_marker(result.raises, 'command="dependency-waiver"'))
    t.is_true(has_marker(result.raises, 'blocker="60"'))
    t.is_true(has_marker(result.raises, 'reason="operator-waiver"'))
  end,

  test_observe_issue_releases_completed_blocker_with_waiver = function()
    mock_observe_issue(
      { "fkst-dev:enabled", "fkst-dev:ready", "fkst-dev:blocked-on-dependency" },
      {
        core.state_marker(proposal_id, "dependency_wait", version),
        dependency_waiver_comment(59),
        "github-devloop dependency hold: waiting\n\nReason: dependency-waiver-required\n\n"
          .. core.dependency_wait_marker(proposal_id, version, { 59 }, "waiting", "dependency-waiver-required"),
      }
    )
    mock_blocked_by(42, { { number = 59, state = "CLOSED", state_reason = "COMPLETED" } })
    mock_blocker_issue(59, "ready")
    local released = run_observe()
    t.eq(released.exit_code, 0)
    t.eq(has_queue(released.raises, "devloop_ready"), false)
    t.is_true(ready_handoff_raise(released.raises) ~= nil)
    t.is_true(has_marker(released.raises, "fkst:github-devloop:dependency-release:v1"))
    t.is_true(has_marker(released.raises, "fkst:github-devloop:dependency-waiver:v1"))
    t.is_true(has_marker(released.raises, 'blocker="59"'))
    t.is_true(has_marker(released.raises, 'reason="completed_without_merged_marker"'))
  end,

  test_observe_issue_existing_hold_still_waiting_does_not_refresh = function()
    mock_observe_issue(
      { "fkst-dev:enabled", "fkst-dev:ready", "fkst-dev:blocked-on-dependency" },
      {
        core.state_marker(proposal_id, "dependency_wait", version),
        "github-devloop dependency hold: waiting\n\nReason: waiting-on-dependency\n\n"
          .. core.dependency_wait_marker(proposal_id, version, { 7 }),
      }
    )
    mock_blocked_by(42, { { number = 7 } })
    mock_blocked_by(7, {})
    mock_blocker_issue(7, "ready")
    local result = run_observe()
    t.eq(result.exit_code, 0)
    t.eq(count_queue(result.raises, "github-proxy.github_issue_comment_request"), 0)
    t.eq(count_queue(result.raises, "github-proxy.github_issue_label_request"), 0)
    t.eq(has_queue(result.raises, "devloop_ready"), false)
  end,

  test_cycle_holds_with_cycle_marker = function()
    mock_result_issue()
    mock_blocked_by(42, { { number = 54 } })
    mock_blocked_by(54, { { number = 42 } })
    local result = run_result()
    t.eq(result.exit_code, 0)
    t.eq(has_queue(result.raises, "devloop_ready"), false)
    t.is_true(has_marker(result.raises, "fkst:github-devloop:dependency-cycle:v1"))
  end,

  test_unresolvable_holds_fail_closed = function()
    mock_result_issue()
    mock_blocked_by_malformed(42)
    local result = run_result()
    t.eq(result.exit_code, 0)
    t.eq(has_queue(result.raises, "devloop_ready"), false)
    t.is_true(has_marker(result.raises, "fkst:github-devloop:dependency-unresolvable:v1"))
  end,

  test_dependency_hold_fact_reads_marker_semantics_not_prose = function()
    local gh_failed = core.dependency_hold_fact({
      core.state_marker(proposal_id, "ready", version),
      "localized prose and arbitrary reason noise\n\n"
        .. core.dependency_unresolvable_marker(proposal_id, version, { 42 }, "unresolvable", "gh-failed"),
    }, proposal_id)
    t.eq(gh_failed.marker_kind, "dependency-unresolvable")
    t.eq(gh_failed.hold_kind, "unresolvable")
    t.eq(gh_failed.reason, "gh-failed")

    local old_gh_failed = core.dependency_hold_fact({
      core.state_marker(proposal_id, "dependency_wait", version),
      "github-devloop dependency hold: unresolvable\n\nReason: gh-failed\n\n"
        .. core.dependency_wait_marker(proposal_id, version, { 42 }),
    }, proposal_id)
    t.eq(old_gh_failed.marker_kind, "dependency-wait")
    t.eq(old_gh_failed.hold_kind, "waiting")
    t.eq(old_gh_failed.reason, "waiting-on-dependency")

    local attr_gh_failed = core.dependency_hold_fact({
      core.state_marker(proposal_id, "ready", version),
      "localized prose and arbitrary reason noise\n\n"
        .. core.dependency_wait_marker(proposal_id, version, { 42 }, "unresolvable", "gh-failed"),
    }, proposal_id)
    t.eq(attr_gh_failed.marker_kind, "dependency-wait")
    t.eq(attr_gh_failed.hold_kind, "unresolvable")
    t.eq(attr_gh_failed.reason, "gh-failed")

    local cycle = core.dependency_hold_fact({
      core.state_marker(proposal_id, "ready", version),
      "localized prose and arbitrary reason noise\n\n"
        .. core.dependency_cycle_marker(proposal_id, version),
    }, proposal_id)
    t.eq(cycle.marker_kind, "dependency-cycle")
    t.eq(cycle.reason, "dependency-cycle")
  end,

  test_gh_failed_hold_rechecks_and_releases_on_next_poll = function()
    mock_observe_issue()
    mock_blocked_by_failure(42)
    local held = run_observe()
    t.eq(held.exit_code, 0)
    t.eq(has_queue(held.raises, "devloop_ready"), false)
    t.is_true(has_marker(held.raises, 'hold_kind="unresolvable"'))
    t.is_true(has_marker(held.raises, 'reason="gh-failed"'))

    mock_observe_issue(
      { "fkst-dev:enabled", "fkst-dev:ready", "fkst-dev:blocked-on-dependency" },
      {
        core.state_marker(proposal_id, "dependency_wait", version),
        "github-devloop dependency hold: unresolvable\n\nReason: gh-failed\n\n"
          .. core.dependency_unresolvable_marker(proposal_id, version, { 42 }),
      }
    )
    mock_blocked_by(42, {})
    local released = run_observe()
    t.eq(released.exit_code, 0)
    t.eq(has_queue(released.raises, "devloop_ready"), false)
    t.is_true(ready_handoff_raise(released.raises) ~= nil)
    t.is_true(has_marker(released.raises, "fkst:github-devloop:dependency-release:v1"))
    local clear = find_raise(released.raises, "github-proxy.github_issue_label_request", function(payload)
      return h.has_value(payload.remove_labels, "fkst-dev:blocked-on-dependency")
    end)
    t.is_true(clear ~= nil)
  end,

  test_old_gh_failed_wait_hold_rechecks_and_releases_on_next_poll = function()
    mock_observe_issue(
      { "fkst-dev:enabled", "fkst-dev:ready", "fkst-dev:blocked-on-dependency" },
      {
        core.state_marker(proposal_id, "dependency_wait", version),
        "github-devloop dependency hold: unresolvable\n\nReason: gh-failed\n\n"
          .. core.dependency_wait_marker(proposal_id, version, { 42 }),
      }
    )
    mock_blocked_by(42, {})
    local released = run_observe()
    t.eq(released.exit_code, 0)
    t.eq(has_queue(released.raises, "devloop_ready"), false)
    t.is_true(ready_handoff_raise(released.raises) ~= nil)
    t.is_true(has_marker(released.raises, "fkst:github-devloop:dependency-release:v1"))
  end,

  test_non_hold_state_clears_stale_dependency_label = function()
    mock_observe_issue(
      { "fkst-dev:enabled", "fkst-dev:implementing", "fkst-dev:blocked-on-dependency" },
      {
        core.state_marker(proposal_id, "implementing", "ready-consensus-github-devloop-issue-owner-repo-42-2026-06-03T01-02-03Z"),
        "github-devloop dependency hold: waiting\n\nReason: waiting-on-dependency\n\n"
          .. core.dependency_wait_marker(proposal_id, version, { 7 }),
      }
    )
    local result = run_observe()
    t.eq(result.exit_code, 0)
    local clear = find_raise(result.raises, "github-proxy.github_issue_label_request", function(payload)
      return h.has_value(payload.remove_labels, "fkst-dev:blocked-on-dependency")
    end)
    t.is_true(clear ~= nil)
    t.eq(has_queue(result.raises, "devloop_ready"), false)
  end,

  test_implement_backstop_moves_ready_to_dependency_wait = function()
    mock_blocked_by(42, { { number = 55 } })
    mock_blocked_by(55, {})
    mock_blocker_issue(55, "ready")
    mock_implement_issue()
    local result = run_implement()
    t.eq(result.exit_code, 0)
    t.is_true(has_marker(result.raises, 'state="dependency_wait"'))
  end,

  test_no_blockers_unaffected = function()
    mock_result_issue()
    mock_blocked_by(42, {})
    local result = run_result()
    t.eq(result.exit_code, 0)
    t.eq(has_queue(result.raises, "devloop_ready"), false)
    t.is_true(ready_handoff_raise(result.raises) ~= nil)
  end,
}
