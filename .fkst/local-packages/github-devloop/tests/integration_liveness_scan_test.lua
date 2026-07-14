local base_ids, h, entity_lib, devloop_base = require("devloop.base_ids"), require("tests.devloop_helpers"), require("devloop.entity"), require("devloop.base")
local cache_seed_helpers = require("tests.cache_seed_helpers")
local contract_time = require("contract.time")
local conv_reconcile, conv_attempts = require("devloop.convergence.reconcile"), require("devloop.convergence.attempts")
local m_rae = require("devloop.restart_actionable_epoch")
local t = h.t
local core = h.core
local opts = h.opts
local decompose_lib = require("devloop.decompose")
local issue = h.issue
local mock_issue_state = h.mock_issue_state
local run_observe = h.run_observe
local find_raise = h.find_raise
local render_comment = h.render_comment
local json_string = h.json_string
local ready = h.ready
local replay_fields = require("devloop.replay_fields")
local mock_issue_reconcile = h.mock_issue_reconcile
local entity_read_mocks = require("tests.entity_read_mock_helpers")
local codex_status = require("tests.codex_status_helpers")
local m_builders = require("devloop.markers.builders")
local ISSUE_REDRIVE_QUEUE = "devloop_observe_issue"
local _cache_seed_helpers = cache_seed_helpers

local function restart_transition_row(state_name)
  return replay_fields.restart_transition_row(core.restart_transition_table(), state_name)
end

local function run_timeout_reconcile(payload, run_opts)
  return t.run_department("departments/reconcile/main.lua", {
    queue = "devloop_timeout_reconcile",
    payload = payload,
  }, run_opts)
end

local repo = "owner/repo"
local proposal_id = "github-devloop/issue/owner/repo/42"
local version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z"

-- A marker createdAt this many seconds before the (real) wall clock. Liveness budgets
-- compare marker age against now(); hardcoded absolute dates make "not over budget"
-- cases flip to over-budget as wall time advances past the budget window (non-hermetic).
-- Use a recent createdAt so a not-over-budget setup stays not-over-budget deterministically.
local function recent_iso(seconds_ago)
  return os.date("!%Y-%m-%dT%H:%M:%SZ", now() - (seconds_ago or 60))
end

local function run_liveness_scan(name, run_opts)
  return t.run_department("departments/liveness_scan/main.lua", {
    queue = "devloop_liveness_tick",
    payload = {
      schema = "github-devloop.tick.v1",
    },
    ts = "2026-06-03T01:32:03Z",
  }, run_opts or opts(name or "liveness-scan"))
end

local function run_liveness_scan_at(name, ts, run_opts)
  return t.run_department("departments/liveness_scan/main.lua", {
    queue = "devloop_liveness_tick",
    payload = {
      schema = "github-devloop.tick.v1",
    },
    ts = ts,
  }, run_opts or opts(name or "liveness-scan"))
end

local function mock_repo()
  t.mock_command(devloop_base.read_env_command("FKST_GITHUB_REPO"), {
    stdout = repo,
    stderr = "",
    exit_code = 0,
  })
end

local function numbered_list_json(items)
  local rendered = {}
  for _, item in ipairs(items or {}) do
    table.insert(rendered, string.format(
      '{"number":%d,"state":"%s","updated_at":"%s"}',
      tonumber(item.number),
      json_string(item.state or "open"),
      json_string(item.updated_at or "")
    ))
  end
  return "[" .. table.concat(rendered, ",") .. "]\n"
end

local function blocked_by_json(nodes)
  local rendered = {}
  for _, node in ipairs(nodes or {}) do
    table.insert(rendered, string.format(
      '{"number":%s,"state":"%s","stateReason":"%s","repository":{"nameWithOwner":"%s"}}',
      tostring(node.number),
      json_string(node.state or "OPEN"),
      json_string(node.state_reason or node.stateReason or ""),
      json_string(node.repo or repo)
    ))
  end
  return '{"data":{"repository":{"issue":{"blockedBy":{"totalCount":'
    .. tostring(#(nodes or {}))
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

local function mock_issue_list(items)
  t.mock_command(core.gh_issue_list_observe_cmd(repo), {
    stdout = numbered_list_json(items),
    stderr = "",
    exit_code = 0,
  })
end

local function mock_issue_state_number(issue_number, labels, state, comments, updated_at)
  entity_read_mocks.mock_issue_read_forms(t, {
    repo = repo,
    number = issue_number,
    title = "Issue " .. tostring(issue_number),
    body = "",
    state = state or "OPEN",
    updated_at = updated_at or "2026-06-03T01:02:03Z",
    labels = labels,
    comments = comments,
    assignees = { "fkst-test-bot" },
    times = 1,
  })
end

local function mock_empty_pr_list()
  t.mock_command(core.gh_pr_list_observe_cmd(repo), {
    stdout = "[]\n",
    stderr = "",
    exit_code = 0,
  })
end

local function mock_branch_config()
  t.mock_command(devloop_base.read_env_command("FKST_DEVLOOP_UPSTREAM_BRANCH"), {
    stdout = "dev",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command(devloop_base.read_env_command("FKST_DEVLOOP_INTEGRATION_BRANCH"), {
    stdout = "dev",
    stderr = "",
    exit_code = 0,
  })
end

local function mock_empty_implementation_pr_list(issue_number, impl_version)
  local branch = devloop_base.implement_branch(repo, issue_number, core.implementation_base_version(impl_version))
  mock_branch_config()
  t.mock_command(core.gh_pr_list_head_base_cmd(repo, branch, "dev"), {
    stdout = "[[]]\n",
    stderr = "",
    exit_code = 0,
  })
end

local function mock_pr_list(items)
  t.mock_command(core.gh_pr_list_observe_cmd(repo), {
    stdout = numbered_list_json(items),
    stderr = "",
    exit_code = 0,
  })
end

local function mock_pr_state(comments, state)
  entity_read_mocks.mock_pr_read_forms(t, {
    repo = repo,
    number = 7,
    head = "devloop-owner-repo-42-01HY",
    head_sha = "def456",
    base_branch = "dev",
    state = state or "OPEN",
    updated_at = "2026-06-04T01:02:03Z",
    comments = comments,
    times = 1,
  })
end

local function mock_linked_pr_state(comments, state, exit_code, times, run_opts)
  local rendered = {}
  for _, comment in ipairs(comments or {}) do
    table.insert(rendered, render_comment(comment))
  end
  local stderr = ""
  if exit_code ~= nil and exit_code ~= 0 then
    stderr = "pr view failed"
  end
  local stdout = string.format(
    '{"headRefName":"devloop-owner-repo-42-01HY","headRefOid":"def456","baseRefName":"dev","state":"%s","updatedAt":"2026-06-04T01:02:03Z","comments":[%s]}\n',
    json_string(state or "OPEN"),
    table.concat(rendered, ",")
  )
  entity_read_mocks.mock_pr_view_raw_selector(t, { repo = repo, number = 7 }, entity_read_mocks.pr_origin_selector, {
    stdout = stdout,
    stderr = stderr,
    exit_code = exit_code or 0,
  }, times or 1)
  if exit_code == nil or exit_code == 0 then
    t.run_department("departments/test_cache_seed/main.lua", { queue = "cache_seed", payload = { key = require("devloop.github_proxy_entity_view").entity_view_cache_key(core, repo, "pr", 7), value = '{"updated_at":"2026-06-04T01:02:03Z","producer":"observe_pr","stdout":"' .. json_string(stdout) .. '"}' } }, run_opts or opts("liveness-scan-linked-pr-cache-seed"))
    entity_read_mocks.mock_pr_read_forms(t, {
      repo = repo,
      number = 7,
      head = "devloop-owner-repo-42-01HY",
      head_sha = "def456",
      base_branch = "dev",
      state = state or "OPEN",
      updated_at = "2026-06-04T01:02:03Z",
      comments = comments,
      times = times or 1,
    })
  end
end

local function mock_linked_pr_absent(times)
  entity_read_mocks.mock_pr_view_raw_selector(t, { repo = repo, number = 7 }, entity_read_mocks.pr_origin_selector, {
    stdout = "",
    stderr = "HTTP 404: Not Found",
    exit_code = 1,
  }, times or 1)
end

local function assert_no_entity_change(result)
  t.eq(result.exit_code, 0)
  t.eq(find_raise(result.raises, ISSUE_REDRIVE_QUEUE), nil)
end

local function entity_change_issue_numbers(result)
  local numbers = {}
  for _, raised in ipairs(result.raises or {}) do
    if raised.queue == ISSUE_REDRIVE_QUEUE
      and raised.payload ~= nil
      and raised.payload.type == "issue" then
      numbers[tonumber(raised.payload.number)] = true
    end
  end
  return numbers
end

local function has_liveness_action_for_proposal(result, target_proposal_id)
  for _, raised in ipairs(result.raises or {}) do
    local payload = raised.payload or {}
    if payload.proposal_id == target_proposal_id
      or (raised.queue == ISSUE_REDRIVE_QUEUE
        and payload.type == "issue"
        and base_ids.proposal_id(payload.repo, payload.number) == target_proposal_id) then
      return true
    end
  end
  return false
end

local function timeout_state_comment(state_name, state_version, created_at)
  return {
    body = core.state_marker(proposal_id, state_name, state_version),
    author_login = "fkst-test-bot",
    created_at = created_at or "2026-06-03T00:00:00Z",
  }
end
local function ready_state_comment(comment_id, state_version, created_at)
  return { id = comment_id, body = core.state_marker(proposal_id, "ready", state_version, "result-marker,ready-label,devloop-ready"), author_login = "fkst-test-bot", created_at = created_at or "2026-06-03T00:00:00Z" }
end
local function timeout_attempt_comment(state_name, state_version, round, created_at)
  return {
    body = conv_attempts.timeout_attempt_marker(core, proposal_id, state_version, state_name, round, entity_lib.issue_source_ref(repo, 42)),
    author_login = "fkst-test-bot",
    created_at = created_at or "2026-06-03T00:00:00Z",
  }
end

local function timeout_attempt_v2_comment(row, generation_key, round, created_at)
  return {
    body = conv_attempts.timeout_attempt_v2_marker(core, proposal_id, row.from_state, row.liveness_class_id, generation_key, round, entity_lib.issue_source_ref(repo, 42)),
    author_login = "fkst-test-bot",
    created_at = created_at or "2026-06-03T00:00:00Z",
  }
end

local function with_codex_runs(fn)
  local original = fkst.codex_runs
  local ok, err = pcall(fn)
  fkst.codex_runs = original
  if not ok then
    error(err)
  end
end

local function capture_timeout_raises_and_logs(fn)
  local raised = {}
  local logs = {}
  local original_log_raise = core.log_raise
  local original_log_line = core.log_line
  core.log_raise = function(_, _, queue, payload)
    table.insert(raised, { queue = queue, payload = payload })
  end
  core.log_line = function(level, dept, proposal, tag, fields)
    table.insert(logs, { level = level, dept = dept, proposal = proposal, tag = tag, fields = fields })
  end
  local ok, err = pcall(fn)
  core.log_raise = original_log_raise
  core.log_line = original_log_line
  if not ok then
    error(err)
  end
  return raised, logs
end

local function captured_raise(raises, queue, predicate)
  for _, raised in ipairs(raises or {}) do
    if raised.queue == queue
      and (predicate == nil or predicate(raised.payload, raised)) then
      return raised
    end
  end
  return nil
end

local function assert_no_observe_reinject(result)
  t.eq(find_raise(result.raises, ISSUE_REDRIVE_QUEUE), nil)
end

local function issue_rest_view_number(rendered)
  local text = tostring(rendered or "")
  return text:match("gh api 'repos/owner/repo/issues/(%d+)'$")
    or text:match("gh api repos/owner/repo/issues/(%d+)$")
end

return {
  test_liveness_scan_skips_terminal_issue = function()
    mock_repo()
    mock_issue_list({ { number = 42, state = "open", updated_at = "2026-06-03T01:02:03Z" } })
    mock_issue_state({ "fkst-dev:merged" }, "OPEN", {
      core.state_marker(proposal_id, "merged", version),
    })
    mock_empty_pr_list()

    assert_no_entity_change(run_liveness_scan("liveness-scan-terminal"))
  end,

  test_liveness_scan_skips_issue_without_state = function()
    mock_repo()
    mock_issue_list({ { number = 42, state = "open", updated_at = "2026-06-03T01:02:03Z" } })
    mock_issue_state({ "fkst-dev:enabled" }, "OPEN", {})
    mock_empty_pr_list()

    assert_no_entity_change(run_liveness_scan("liveness-scan-no-state"))
  end,

  test_liveness_scan_requeues_every_non_terminal_issue_marker_state = function()
    local issues = {}
    local expected = {}
    local live_defer = {}
    local terminal = {}
    local number = 100
    local run_opts = opts("liveness-scan-non-terminal-issue-marker-conformance")
    for _, row in ipairs(core.restart_transition_table()) do
      number = number + 1
      local state = row.from_state
      table.insert(issues, {
        number = number,
        state = "open",
        updated_at = "2026-06-03T01:02:03Z",
      })
      local fresh_version = state == "implementing" and "ready/2999-01-01T00-00-00Z" or "2999-01-01T00-00-00Z"
      local proposal = base_ids.proposal_id(repo, number)
      local comments = { { body = core.state_marker(proposal, state, fresh_version), author_login = "fkst-test-bot", created_at = "2999-01-01T00:00:00Z" } }
      if state == "implementing" then table.insert(comments, { body = core.implement_attempt_marker(proposal, fresh_version, 1, tostring(now() - 60)), author_login = "fkst-test-bot", created_at = "2999-01-01T00:00:00Z" }) end
      mock_issue_state_number(number, { "fkst-dev:enabled", core.state_label(state) }, "OPEN", comments)
      if row.terminal == false then
        if row.liveness_contract
          and row.liveness_contract.real_execution
          and row.liveness_contract.real_execution.primitive == "fkst.codex_runs" then
          live_defer[number] = state
          codex_status.seed_role_codex_run(run_opts, row.liveness_contract.real_execution.match.role, proposal, fresh_version)
        else
          expected[number] = state
        end
      else
        terminal[number] = state
      end
    end
    mock_repo()
    mock_issue_list(issues)
    mock_empty_pr_list()

    local result = run_liveness_scan("liveness-scan-non-terminal-issue-marker-conformance", run_opts)
    t.eq(result.exit_code, 0)
    for issue_number, state in pairs(expected) do
      t.eq(has_liveness_action_for_proposal(result, base_ids.proposal_id(repo, issue_number)), true, "non-terminal issue marker state not sweep-reachable: " .. tostring(state))
    end
    for issue_number, state in pairs(live_defer) do
      local target_proposal = base_ids.proposal_id(repo, issue_number)
      t.eq(find_raise(result.raises, "devloop_timeout_reconcile", function(payload)
        return payload.proposal_id == target_proposal
      end), nil, "live codex-run state should not timeout-reconcile: " .. tostring(state))
      t.eq(find_raise(result.raises, "devloop_ready", function(payload)
        return payload.proposal_id == target_proposal
      end), nil, "live codex-run state should not respawn implement: " .. tostring(state))
      t.eq(find_raise(result.raises, "consensus.proposal", function(payload)
        return payload.proposal_id == target_proposal
      end), nil, "live codex-run state should not respawn consensus: " .. tostring(state))
    end
    local raised = entity_change_issue_numbers(result)
    for issue_number, state in pairs(terminal) do
      t.eq(raised[issue_number], nil, "terminal issue marker state was requeued: " .. tostring(state))
    end
  end,

  test_liveness_scan_requeues_ready_dependency_hold = function()
    mock_repo()
    mock_issue_list({ { number = 42, state = "open", updated_at = "2026-06-03T01:02:03Z" } })
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:ready", "fkst-dev:blocked-on-dependency" }, "OPEN", {
      core.state_marker(proposal_id, "dependency_wait", version),
      core.dependency_wait_marker(proposal_id, version, { 7 }),
    })
    mock_empty_pr_list()

    local result = run_liveness_scan("liveness-scan-ready-dependency-hold")
    t.eq(result.exit_code, 0)
    local raised = find_raise(result.raises, ISSUE_REDRIVE_QUEUE)
    t.is_true(raised ~= nil)
    t.eq(raised.payload.type, "issue")
    t.eq(raised.payload.source, "liveness-scan")
    t.is_true(tostring(raised.payload.dedup_key):find("liveness%-scan", 1) ~= nil)
  end,

  test_liveness_scan_skips_over_budget_ready_dependency_hold_timeout = function()
    mock_repo()
    mock_issue_list({ { number = 42, state = "open", updated_at = "2026-06-03T01:02:03Z" } })
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:ready", "fkst-dev:blocked-on-dependency" }, "OPEN", {
      timeout_state_comment("dependency_wait", version, "2026-06-03T00:00:00Z"),
      "github-devloop dependency hold: waiting\n\nReason: waiting-on-dependency\n\n"
        .. core.dependency_wait_marker(proposal_id, version, { 271 }),
    })
    mock_empty_pr_list()

    local result = run_liveness_scan("liveness-scan-ready-dependency-hold-timeout-skip")
    t.eq(result.exit_code, 0)
    t.eq(find_raise(result.raises, "devloop_timeout_reconcile"), nil)
    t.eq(find_raise(result.raises, "devloop_ready"), nil)
    local raised = find_raise(result.raises, ISSUE_REDRIVE_QUEUE)
    t.is_true(raised ~= nil)
    t.eq(raised.payload.type, "issue")
  end,

  test_liveness_scan_over_budget_ready_writes_timeout_redrive_without_observe = function()
    mock_blocked_by(42, {})
    mock_repo()
    mock_issue_list({ { number = 42, state = "open", updated_at = "2026-06-03T01:02:03Z" } })
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:ready" }, "OPEN", { ready_state_comment("IC_ready_timeout", version) })
    mock_empty_pr_list()
    local result = run_liveness_scan("liveness-scan-ready-timeout-redrive")
    t.eq(result.exit_code, 0)
    assert_no_observe_reinject(result)
    local ready_raise = find_raise(result.raises, "devloop_ready")
    t.is_true(ready_raise ~= nil)
    t.eq(ready_raise.payload.proposal_id, proposal_id)
    t.is_true(ready_raise.payload.dedup_key:find("/redrive/ready/1", 1, true) ~= nil)
    t.eq(ready_raise.payload.ready_hand_off.comment_id, "IC_ready_timeout")
    t.eq(ready_raise.payload.ready_hand_off.marker_version, version)
    t.eq(ready_raise.payload.source_ref.ref, "owner/repo#issue/42")
    local attempt = find_raise(result.raises, "github-proxy.github_issue_comment_request")
    t.is_true(attempt ~= nil)
    t.is_true(attempt.payload.body:find("fkst:github-devloop:timeout-attempt:v1", 1, true) ~= nil)
    t.is_true(attempt.payload.body:find("fkst:github-devloop:timeout-attempt:latest:v1", 1, true) ~= nil)
    t.is_true(tostring(attempt.payload.replace_marker):find("fkst:github-devloop:timeout-attempt:latest:v1", 1, true) ~= nil)
    t.is_true(attempt.payload.body:find('state="ready"', 1, true) ~= nil)
    t.is_true(attempt.payload.body:find('round="1"', 1, true) ~= nil)
  end,

  test_liveness_scan_over_budget_thinking_redrives_live_version_and_writes_attempt = function()
    local timeout_version = version .. "/timeout/thinking/1"
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:thinking" }, "OPEN", {
      timeout_state_comment("thinking", version, "2026-06-03T00:00:00Z"),
      timeout_state_comment("thinking", timeout_version, "2026-06-03T00:10:00Z"),
    })

    local result = run_observe(issue({
      dedup_key = "liveness-scan/thinking-timeout",
      source = "liveness-scan",
    }), opts("liveness-scan-thinking-timeout-redrive-next-round"))
    t.eq(result.exit_code, 0)
    local proposal = find_raise(result.raises, "consensus.proposal")
    t.is_true(proposal ~= nil)
    t.eq(proposal.payload.proposal_id, proposal_id)
    t.eq(core.version_timeout_round(proposal.payload.dedup_key, "thinking"), 1)
    t.is_true(tostring(proposal.payload.dedup_key):find("/replay", 1, true) ~= nil)
    local attempt = find_raise(result.raises, "github-proxy.github_issue_comment_request")
    t.is_true(attempt ~= nil)
    t.is_true(attempt.payload.body:find("fkst:github-devloop:timeout-attempt:v2", 1, true) ~= nil and attempt.payload.body:find('state="thinking"', 1, true) ~= nil)
    t.is_true(attempt.payload.body:find('round="2"', 1, true) ~= nil)
  end,

  test_liveness_scan_bare_observe_reinject_does_not_increment_timeout_attempt = function()
    mock_repo()
    mock_issue_list({ { number = 42, state = "open", updated_at = "2026-06-03T01:02:03Z" } })
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:ready" }, "OPEN", {
      timeout_state_comment("ready", version, recent_iso(60)),
    })
    mock_empty_pr_list()

    local result = run_liveness_scan("liveness-scan-ready-bare-observe-no-timeout-increment")
    t.eq(result.exit_code, 0)
    local changed = find_raise(result.raises, ISSUE_REDRIVE_QUEUE)
    t.is_true(changed ~= nil)
    t.eq(find_raise(result.raises, "devloop_ready"), nil)
    t.eq(core.version_timeout_round(changed.payload.dedup_key, "ready"), 0)
  end,

  test_liveness_scan_over_budget_blocked_redrives_decompose = function()
    local review_proposal = devloop_base.pr_review_proposal_id(repo, 7, version, "def456")
    mock_repo()
    mock_issue_list({ { number = 42, state = "open", updated_at = "2026-06-03T01:02:03Z" } })
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:blocked" }, "OPEN", {
      timeout_state_comment("blocked", version, "2026-06-01T00:00:00Z"),
      m_builders.pr_link_marker(core, proposal_id, 7, "devloop-owner-repo-42-01HY", version, "dev"),
      decompose_lib.decomposed_marker(core, proposal_id, version, 7, 1),
      m_builders.review_result_marker(core, review_proposal, proposal_id, "reject", "consensus:" .. review_proposal .. "/review", 1, "missing decomposition"),
    })
    t.mock_command(core.gh_issue_list_decompose_children_cmd(repo, proposal_id), {
      stdout = "[]\n",
      stderr = "",
      exit_code = 0,
    })
    mock_empty_pr_list()

    local result = run_liveness_scan("liveness-scan-blocked-timeout-redrive")
    t.eq(result.exit_code, 0)
    assert_no_observe_reinject(result)
    local decompose = find_raise(result.raises, "github-devloop-decompose.devloop_decompose")
    t.is_true(decompose ~= nil)
    t.eq(decompose.payload.proposal_id, proposal_id)
    t.eq(decompose.payload.version, version)
    local attempt = find_raise(result.raises, "github-proxy.github_issue_comment_request")
    t.is_true(attempt ~= nil)
    t.is_true(attempt.payload.body:find(conv_attempts.timeout_attempt_marker(core, proposal_id, version, "blocked", 1, entity_lib.issue_source_ref(repo, 42)), 1, true) ~= nil)
  end,

  test_liveness_scan_over_budget_ready_escalates_to_timeout_reconcile = function()
    local timeout_version = version .. "/timeout/ready/3"
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:ready" }, "OPEN", {
      {
        body = core.state_marker(proposal_id, "ready", timeout_version),
        author_login = "fkst-test-bot",
        created_at = "2026-06-03T01:02:03Z",
      },
    })

    local result = run_observe(issue({
      dedup_key = "liveness-scan/ready-timeout",
      source = "liveness-scan",
    }), opts("liveness-scan-ready-timeout"))
    t.eq(result.exit_code, 0)
    t.eq(find_raise(result.raises, "devloop_ready"), nil)
    local reconcile = find_raise(result.raises, "devloop_timeout_reconcile")
    t.is_true(reconcile ~= nil)
    t.eq(reconcile.payload.schema, "github-devloop.timeout-reconcile.v1")
    t.eq(reconcile.payload.state, "ready")
    t.eq(reconcile.payload.issue_version, timeout_version)
    t.eq(reconcile.payload.round, 3)
    t.eq(reconcile.payload.source_ref.ref, "owner/repo#issue/42")
  end,

  test_liveness_scan_timeout_attempt_climbs_to_escalation_across_frozen_sweeps = function()
    local comments = { ready_state_comment("IC_ready_timeout_sweep", version, "2026-06-03T00:00:00Z") }
    for sweep = 1, 3 do
      mock_blocked_by(42, {})
      mock_repo()
      local updated_at = "2026-06-03T02:00:0" .. tostring(sweep) .. "Z"
      mock_issue_list({ { number = 42, state = "open", updated_at = updated_at } })
      mock_issue_state_number(42, { "fkst-dev:enabled", "fkst-dev:ready" }, "OPEN", comments, updated_at)
      mock_empty_pr_list()
      local result = run_liveness_scan("liveness-scan-ready-timeout-sweep-" .. tostring(sweep))
      t.eq(result.exit_code, 0)
      assert_no_observe_reinject(result)
      if sweep < 3 then
        local ready_raise = find_raise(result.raises, "devloop_ready")
        t.is_true(ready_raise ~= nil)
        t.is_true(ready_raise.payload.dedup_key:find("/redrive/ready/" .. tostring(sweep), 1, true) ~= nil)
        t.eq(ready_raise.payload.ready_hand_off.comment_id, "IC_ready_timeout_sweep")
        t.eq(core.version_timeout_round(ready_raise.payload.dedup_key, "ready"), 0)
        local attempt = find_raise(result.raises, "github-proxy.github_issue_comment_request")
        t.is_true(attempt ~= nil)
        t.is_true(attempt.payload.body:find('round="' .. tostring(sweep) .. '"', 1, true) ~= nil)
        table.insert(comments, timeout_attempt_comment("ready", version, sweep, "2026-06-03T00:0" .. tostring(sweep) .. ":00Z"))
        t.eq(find_raise(result.raises, "devloop_timeout_reconcile"), nil)
      else
        t.eq(find_raise(result.raises, "devloop_ready"), nil)
        local reconcile = find_raise(result.raises, "devloop_timeout_reconcile")
        t.is_true(reconcile ~= nil)
        t.eq(reconcile.payload.state, "ready")
        t.eq(reconcile.payload.issue_version, version)
        t.eq(reconcile.payload.round, 3)
      end
    end
  end,

  test_liveness_scan_timeout_escalation_reconcile_blocks_ready_current_marker = function()
    local live_version = version .. "/timeout/ready/2"
    mock_blocked_by(42, {})
    mock_repo()
    mock_issue_list({ { number = 42, state = "open", updated_at = "2026-06-03T02:00:03Z" } })
    mock_issue_state_number(42, { "fkst-dev:enabled", "fkst-dev:ready" }, "OPEN", {
      timeout_state_comment("ready", version, "2026-06-03T00:00:00Z"),
      timeout_state_comment("ready", version .. "/timeout/ready/1", "2026-06-03T00:01:00Z"),
      timeout_state_comment("ready", live_version, "2026-06-03T00:02:00Z"),
    }, "2026-06-03T02:00:03Z")
    mock_empty_pr_list()

    local scanned = run_liveness_scan("liveness-scan-ready-timeout-reconcile-chain")
    t.eq(scanned.exit_code, 0)
    local reconcile = find_raise(scanned.raises, "devloop_timeout_reconcile")
    t.is_true(reconcile ~= nil)
    t.eq(reconcile.payload.issue_version, live_version)
    t.eq(reconcile.payload.round, 3)

    mock_issue_reconcile({ "fkst-dev:ready" }, {
      timeout_state_comment("ready", live_version, "2026-06-03T00:02:00Z"),
    })
    mock_blocked_by(42, {})
    local reconciled = run_timeout_reconcile(reconcile.payload, opts("liveness-scan-ready-timeout-reconcile-applies"))
    t.eq(reconciled.exit_code, 0)
    local comment = find_raise(reconciled.raises, "github-proxy.github_issue_comment_request")
    local label = find_raise(reconciled.raises, "github-proxy.github_issue_label_request")
    local blocked_version = conv_reconcile.timeout_reconcile_state_version(core, live_version, "ready", 3)
    t.is_true(comment ~= nil)
    t.is_true(label ~= nil)
    t.is_true(comment.payload.body:find(core.state_marker(proposal_id, "blocked", blocked_version), 1, true) ~= nil)
    t.is_true(comment.payload.body:find("reason_class=state-output-obligation-timeout", 1, true) ~= nil)
    t.is_true(comment.payload.body:find("from_state=ready", 1, true) ~= nil)
    t.is_true(comment.payload.body:find("from_version=" .. live_version, 1, true) ~= nil)
    t.is_true(comment.payload.body:find("age_minutes=", 1, true) ~= nil)
    t.is_true(comment.payload.body:find("budget_minutes=", 1, true) ~= nil)
    t.is_true(comment.payload.body:find("attempt=3", 1, true) ~= nil)
    t.is_true(comment.payload.body:find("attempt_limit=3", 1, true) ~= nil)
    t.is_true(comment.payload.body:find("driving_queue=devloop_ready", 1, true) ~= nil)
    t.is_true(comment.payload.body:find("source_ref.ref=owner/repo#issue/42", 1, true) ~= nil)
    t.is_true(comment.payload.body:find('from_state="ready"', 1, true) ~= nil)
    t.is_true(comment.payload.body:find('from_version="' .. live_version .. '"', 1, true) ~= nil)
    t.is_true(comment.payload.body:find('reason_class="state-output-obligation-timeout"', 1, true) ~= nil)
    t.eq(label.payload.add_labels[1], "fkst-dev:blocked")
  end,

  test_liveness_scan_timeout_reconcile_blocks_ready_when_payload_version_is_stale_but_live_state_is_stuck = function()
    local stale_version = version .. "/timeout/ready/1"
    local live_version = version .. "/timeout/ready/2"
    local payload = conv_reconcile.build_devloop_timeout_reconcile_payload(core,
      restart_transition_row("ready"),
      {
        state = "ready",
        version = stale_version,
        proposal_id = proposal_id,
      },
      proposal_id,
      entity_lib.issue_source_ref(repo, 42),
      3
    )
    mock_issue_reconcile({ "fkst-dev:ready" }, {
      timeout_state_comment("ready", live_version, "2026-06-03T00:02:00Z"),
    })
    mock_blocked_by(42, {})

    local reconciled = run_timeout_reconcile(payload, opts("liveness-scan-ready-timeout-reconcile-live-stale-applies"))
    t.eq(reconciled.exit_code, 0)
    local comment = find_raise(reconciled.raises, "github-proxy.github_issue_comment_request")
    local label = find_raise(reconciled.raises, "github-proxy.github_issue_label_request")
    local blocked_version = conv_reconcile.timeout_reconcile_state_version(core, live_version, "ready", 3)
    t.is_true(comment ~= nil)
    t.is_true(label ~= nil)
    t.is_true(comment.payload.body:find(core.state_marker(proposal_id, "blocked", blocked_version), 1, true) ~= nil)
    t.is_true(comment.payload.body:find('from_version="' .. live_version .. '"', 1, true) ~= nil)
    t.is_true(comment.payload.body:find('version="' .. blocked_version .. '"', 1, true) ~= nil)
    t.eq(label.payload.add_labels[1], "fkst-dev:blocked")
  end,

  test_liveness_scan_timeout_reconcile_skips_when_ready_state_advanced = function()
    local stale_version = version .. "/timeout/ready/2"
    local advanced_version = version .. "/timeout/ready/2"
    local payload = conv_reconcile.build_devloop_timeout_reconcile_payload(core,
      restart_transition_row("ready"),
      {
        state = "ready",
        version = stale_version,
        proposal_id = proposal_id,
      },
      proposal_id,
      entity_lib.issue_source_ref(repo, 42),
      3
    )
    mock_issue_reconcile({ "fkst-dev:implementing" }, {
      timeout_state_comment("implementing", advanced_version, "2026-06-03T00:02:00Z"),
    })

    local reconciled = run_timeout_reconcile(payload, opts("liveness-scan-ready-timeout-reconcile-advanced-skips"))
    t.eq(reconciled.exit_code, 0)
    t.eq(find_raise(reconciled.raises, "github-proxy.github_issue_comment_request"), nil)
    t.eq(find_raise(reconciled.raises, "github-proxy.github_issue_label_request"), nil)
  end,

  test_liveness_scan_implementing_emits_timeout_ready_with_frozen_version = function()
    local event = ready()
    local run_opts = opts("liveness-scan-implementing-redrive-scan")
    local exec_ref = core.implement_exec_ref(event.proposal_id, event.dedup_key)
    local stuck = {
      core.state_marker(event.proposal_id, "implementing", event.dedup_key),
      core.implement_attempt_marker(event.proposal_id, event.dedup_key, 1, tostring(now() - 60), exec_ref),
    }
    mock_repo()
    mock_issue_list({ { number = 42, state = "open", updated_at = "2026-06-03T01:02:03Z" } })
    h.mock_issue_implement({ "fkst-dev:enabled", "fkst-dev:implementing" }, stuck)
    mock_empty_pr_list()

    local scanned = run_liveness_scan("liveness-scan-implementing-redrive-scan", run_opts)
    t.eq(scanned.exit_code, 0)
    local reraised = find_raise(scanned.raises, "devloop_ready")
    t.eq(reraised ~= nil, true)
    t.eq(reraised.payload.proposal_id, event.proposal_id)
    t.eq(reraised.payload.dedup_key, event.dedup_key)
    t.eq(core.implementation_attempt_version(reraised.payload.dedup_key, reraised.payload.impl_retry_attempt), event.dedup_key)
    local attempt = find_raise(scanned.raises, "github-proxy.github_issue_comment_request")
    t.is_true(attempt ~= nil)
    t.is_true(attempt.payload.body:find("fkst:github-devloop:timeout-attempt:v2", 1, true) ~= nil)
    t.is_true(attempt.payload.body:find('state="implementing"', 1, true) ~= nil)

    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:implementing" }, "OPEN", stuck)
    local branch = devloop_base.implement_branch(repo, 42, core.implementation_base_version(reraised.payload.dedup_key))
    t.mock_command(core.git_fetch_branch_cmd("origin", branch), { stdout = "", stderr = "", exit_code = 0 })
    t.mock_command(core.git_remote_branch_head_cmd("origin", branch), { stdout = "abc123\n", stderr = "", exit_code = 0 })
    local implemented = h.run_implement(reraised.payload, opts("liveness-scan-implementing-redrive-consumable"))
    t.eq(implemented.exit_code, 0)
  end,

  test_liveness_scan_drops_stale_implement_attempt_when_codex_run_is_running = function()
    local event = ready()
    local run_opts = opts("liveness-scan-live-codex-run-drops-redrive")
    local exec_ref = core.implement_exec_ref(event.proposal_id, event.dedup_key)
    local stale = {
      core.state_marker(event.proposal_id, "implementing", event.dedup_key),
      core.implement_attempt_marker(event.proposal_id, event.dedup_key, 1, tostring(now() - 7201), exec_ref),
    }
    codex_status.seed_implement_codex_run(run_opts, event.proposal_id, event.dedup_key)
    mock_repo()
    mock_issue_list({ { number = 42, state = "open", updated_at = "2026-06-03T01:02:03Z" } })
    h.mock_issue_implement({ "fkst-dev:enabled", "fkst-dev:implementing" }, stale)
    mock_empty_pr_list()

    local scanned = run_liveness_scan("liveness-scan-live-codex-run-drops-redrive", run_opts)
    t.eq(scanned.exit_code, 0)
    t.eq(find_raise(scanned.raises, "devloop_ready"), nil)
    t.eq(find_raise(scanned.raises, "devloop_timeout_reconcile"), nil)
    t.eq(find_raise(scanned.raises, "github-proxy.github_issue_comment_request", function(payload)
      return tostring(payload.body or ""):find("fkst:github-devloop:timeout-attempt:v2", 1, true) ~= nil
    end), nil)
  end,

  test_liveness_scan_absent_codex_run_still_force_terminates_after_budget = function()
    local event = ready()
    local row = restart_transition_row("implementing")
    local timeout_version = event.dedup_key .. "/timeout/implementing/2"
    local state = {
      state = "implementing",
      version = timeout_version,
      proposal_id = event.proposal_id,
      marker_created_at = "2026-06-03T00:00:00Z",
    }
    local exec_ref = core.implement_exec_ref(event.proposal_id, event.dedup_key)
    local attempt_comment = {
      body = core.implement_attempt_marker(event.proposal_id, event.dedup_key, 1, tostring(now() - 7201), exec_ref),
      author_login = "fkst-test-bot",
      created_at = "2026-06-03T00:00:00Z",
    }
    local eval
    with_codex_runs(function()
      fkst.codex_runs = function()
        return { running = {}, recent = {} }
      end
      eval = m_rae.actionable_epoch_resolve(core, row, state, {
        proposal_id = event.proposal_id,
        current = { comments = { attempt_comment } },
      }, contract_time.iso_timestamp_epoch_seconds("2026-06-03T03:00:00Z"))
    end)
    local comments = {
      timeout_state_comment("implementing", timeout_version, "2026-06-03T00:00:00Z"),
      attempt_comment,
      timeout_attempt_v2_comment(row, eval.generation_key, 1, "2026-06-03T00:01:00Z"),
      timeout_attempt_v2_comment(row, eval.generation_key, 2, "2026-06-03T00:02:00Z"),
    }

    mock_repo()
    mock_issue_list({ { number = 42, state = "open", updated_at = "2026-06-03T03:00:00Z" } })
    h.mock_issue_implement({ "fkst-dev:enabled", "fkst-dev:implementing" }, comments)
    mock_empty_pr_list()

    local scanned = run_liveness_scan("liveness-scan-absent-codex-run-escalates")
    t.eq(scanned.exit_code, 0)
    t.eq(find_raise(scanned.raises, "devloop_ready"), nil)
    local reconcile = find_raise(scanned.raises, "devloop_timeout_reconcile")
    t.is_true(reconcile ~= nil)
    t.eq(reconcile.payload.state, "implementing")
    t.eq(reconcile.payload.issue_version, timeout_version)
    t.eq(reconcile.payload.round, 3)

    h.mock_issue_reconcile({ "fkst-dev:enabled", "fkst-dev:implementing" }, comments)
    mock_empty_implementation_pr_list(42, event.dedup_key)
    local reconciled = run_timeout_reconcile(reconcile.payload, opts("liveness-scan-absent-codex-run-reconciles-blocked"))
    t.eq(reconciled.exit_code, 0)
    local comment = find_raise(reconciled.raises, "github-proxy.github_issue_comment_request")
    local label = find_raise(reconciled.raises, "github-proxy.github_issue_label_request")
    t.is_true(comment ~= nil)
    t.is_true(comment.payload.body:find('state="blocked"', 1, true) ~= nil)
    t.is_true(comment.payload.body:find("state-output-obligation-timeout", 1, true) ~= nil)
    t.is_true(label ~= nil)
    t.eq(label.payload.add_labels[1], "fkst-dev:blocked")
  end,

  test_codex_runs_error_over_budget_escalates_timeout_decision = function()
    local event = ready()
    local row = restart_transition_row("implementing")
    local exec_ref = core.implement_exec_ref(event.proposal_id, event.dedup_key)
    local timeout_version = event.dedup_key .. "/timeout/implementing/2"
    local state = {
      state = "implementing",
      version = timeout_version,
      proposal_id = event.proposal_id,
      marker_created_at = "2026-06-03T00:00:00Z",
    }
    local comments = {
      {
        body = core.state_marker(event.proposal_id, "implementing", timeout_version),
        author_login = "fkst-test-bot",
        created_at = "2026-06-03T00:00:00Z",
      },
      {
        body = core.implement_attempt_marker(event.proposal_id, event.dedup_key, 1, tostring(now() - 60), exec_ref),
        author_login = "fkst-test-bot",
        created_at = "2026-06-03T00:00:00Z",
      },
    }
    local facts = {
      proposal_id = event.proposal_id,
      source_ref = event.source_ref,
      current = { comments = comments },
      fresh_current_state = state,
      now_seconds = contract_time.iso_timestamp_epoch_seconds("2026-06-03T03:00:00Z"),
    }

    with_codex_runs(function()
      fkst.codex_runs = function()
        error("synthetic codex_runs failure")
      end
      local due, age = core.liveness_timeout_due_with_facts(row, state, facts, facts.now_seconds)
      t.eq(due, true)
      t.eq(age, 180)
      local eval = facts.actionable_epoch_eval
      t.eq(eval.status, "actionable")
      t.eq(eval.signal.reason, "codex-runs-unavailable")
      t.eq(eval.signal.codex_runs_fallback, true)
      table.insert(facts.current.comments, timeout_attempt_v2_comment(row, eval.generation_key, 1, "2026-06-03T00:01:00Z"))
      table.insert(facts.current.comments, timeout_attempt_v2_comment(row, eval.generation_key, 2, "2026-06-03T00:02:00Z"))
      local raised, logs = capture_timeout_raises_and_logs(function()
        local applied = core.maybe_timeout_redrive_from_table("liveness_scan", {
          repo = repo,
          number = 42,
          source_ref = entity_lib.issue_source_ref(repo, 42),
        }, state, row, facts)
        t.eq(applied, true)
      end)
      t.eq(captured_raise(raised, "devloop_ready"), nil)
      t.eq(captured_raise(raised, "github-proxy.github_issue_comment_request"), nil)
      local reconcile = captured_raise(raised, "devloop_timeout_reconcile")
      t.is_true(reconcile ~= nil)
      t.eq(reconcile.payload.state, "implementing")
      t.eq(reconcile.payload.round, 3)
      local logged_fallback = false
      for _, log in ipairs(logs) do
        if log.tag == "CODEX_RUNS" and table.concat(log.fields or {}, " "):find("outcome=defer", 1, true) ~= nil then
          logged_fallback = true
        end
      end
      t.eq(logged_fallback, true)
    end)
  end,

  test_liveness_scan_caps_before_fresh_entity_views = function()
    local items = {}
    for number = 1, 101 do
      table.insert(items, { number = number, state = "open", updated_at = "2026-06-03T01:02:03Z" })
    end
    mock_repo()
    mock_issue_list(items)
    mock_empty_pr_list()
    for number = 1, 101 do
      mock_issue_state_number(number, { "fkst-dev:enabled", "fkst-dev:merged" }, "OPEN", {
        core.state_marker(base_ids.proposal_id(repo, number), "merged", "v-" .. tostring(number)),
      })
    end

    local result = run_liveness_scan("liveness-scan-cap-before-views")
    t.eq(result.exit_code, 0)
    t.eq(find_raise(result.raises, ISSUE_REDRIVE_QUEUE), nil)
    local views = 0
    for _, call in ipairs(t.command_calls()) do
      if issue_rest_view_number(call.rendered) ~= nil then
        views = views + 1
      end
    end
    t.eq(views, 100)
  end,

  test_liveness_scan_uses_cursor_first_batch_on_large_board = function()
    local items = {}
    for number = 1, 101 do
      table.insert(items, { number = number, state = "open", updated_at = "2026-06-03T01:02:03Z" })
    end
    mock_repo()
    mock_issue_list(items)
    mock_empty_pr_list()
    for number = 1, 101 do
      mock_issue_state_number(number, { "fkst-dev:enabled", "fkst-dev:merged" }, "OPEN", {
        core.state_marker(base_ids.proposal_id(repo, number), "merged", "v-" .. tostring(number)),
      })
    end

    local tick = "2026-06-03T01:32:04Z"
    local result = run_liveness_scan_at("liveness-scan-rotates-large-board", tick)
    t.eq(result.exit_code, 0)

    local viewed = {}
    for _, call in ipairs(t.command_calls()) do
      local issue_number = issue_rest_view_number(call.rendered)
      if issue_number ~= nil then
        viewed[tonumber(issue_number)] = true
      end
    end
    t.eq(viewed[1], true)
    t.eq(viewed[100], true)
    t.eq(viewed[101], nil)
  end,

  test_liveness_scan_cursor_covers_large_board_across_k_ticks = function()
    local items = {}
    for number = 1, 250 do
      table.insert(items, { number = number, state = "open", updated_at = "2026-06-03T01:02:03Z" })
    end

    local viewed = {}
    local run_opts = opts("liveness-scan-cursor-k")
    for tick = 1, 3 do
      mock_repo()
      mock_issue_list(items)
      mock_empty_pr_list()
      for number = 1, 250 do
        mock_issue_state_number(number, { "fkst-dev:enabled", "fkst-dev:merged" }, "OPEN", {
          core.state_marker(base_ids.proposal_id(repo, number), "merged", "v-" .. tostring(number)),
        })
      end

      local result = run_liveness_scan_at("liveness-scan-cursor-k", tostring(tick), run_opts)
      t.eq(result.exit_code, 0)

      for _, call in ipairs(t.command_calls()) do
        local issue_number = issue_rest_view_number(call.rendered)
        if issue_number ~= nil then
          viewed[tonumber(issue_number)] = true
        end
      end
    end

    for number = 1, 250 do
      t.eq(viewed[number], true)
    end
  end,

  test_liveness_scan_defers_slow_issue_view_without_retry_failure = function()
    mock_repo()
    mock_issue_list({ { number = 42, state = "open", updated_at = "2026-06-03T01:02:03Z" } })
    mock_empty_pr_list()
    t.mock_command("gh api 'repos/owner/repo/issues/42'", {
      stdout = "",
      stderr = "timed out",
      exit_code = 124,
    })

    local result = run_liveness_scan("liveness-scan-view-timeout-deferred")
    t.eq(result.exit_code, 0)
    t.eq(find_raise(result.raises, ISSUE_REDRIVE_QUEUE), nil)
  end,

}
