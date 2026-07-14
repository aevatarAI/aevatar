local requests_lifecycle = require("devloop.requests.lifecycle")
local convergence_shared = require("devloop.convergence.shared")
local contract_time = require("contract.time")
local replayer = require("devloop.replayer")
local m_rrc = require("devloop.restart_responsibility_contract")
local h = require("tests.devloop_core_helpers")
local conv_rounds = require("devloop.convergence.rounds")
local core = h.core
local t = h.t
local span = require("core.span_conformance")
local hidden_state_conformance = require("devloop.hidden_state_conformance")

local function contains_error(errors, needle)
  for _, err in ipairs(errors or {}) do
    local text = tostring(err.message or err)
    if text:find(needle, 1, true) ~= nil then
      return true
    end
  end
  return false
end

local function join_error_messages(errors)
  local lines = {}
  for _, err in ipairs(errors or {}) do
    table.insert(lines, tostring(err.message or err))
  end
  return table.concat(lines, "\n")
end

local function capture_raises(fn)
  local raised = {}
  local previous = core.log_raise
  core.log_raise = function(_, _, queue, payload)
    table.insert(raised, { queue = queue, payload = payload })
  end
  local ok, err = pcall(fn)
  core.log_raise = previous
  if not ok then
    error(err)
  end
  return raised
end

local function find_raise(raised, queue)
  for _, item in ipairs(raised or {}) do
    if item.queue == queue then
      return item
    end
  end
  return nil
end

local function transition_source(contract_body)
  return [[
return function(M, h)
  local responsibility_signature = h.responsibility_signature; local span_contract = h.span_contract
  return {
    from_state = "implementing",
    responsibility_signature = responsibility_signature({
      state_kind = "worker",
    }),
]] .. contract_body .. [[
  }
end
]]
end

return {
  test_current_tree_has_no_gspan_errors = function()
    local errors = core.span_conformance_errors()
    t.is_true(#errors == 0, join_error_messages(errors))
  end,

  test_hidden_state_conformance_passes_with_seeded_allowlist = function()
    local errors = hidden_state_conformance.hidden_state_conformance_errors(core)
    t.is_true(#errors == 0, join_error_messages(errors))
    local by_state = {}
    for _, row in ipairs(core.restart_transition_table()) do
      by_state[row.from_state] = row
    end
    local declared = {}
    for _, fact in ipairs(by_state.dependency_wait.advancing_facts) do
      declared[tostring(fact.fact_family) .. "->" .. tostring(fact.successor)] = true
    end
    t.eq(#by_state.dependency_wait.advancing_facts, 3)
    t.is_true(declared["dependency-gate->dependency_wait"])
    t.is_true(declared["dependency-gate->ready"])
  end,

  test_hidden_state_behavior_fixture_builds_poll_shape = function()
    local row = nil
    for _, candidate in ipairs(core.restart_transition_table()) do
      if candidate.from_state == "dependency_wait" then
        row = candidate
      end
    end
    local declared = nil
    for _, fact in ipairs(row.advancing_facts) do
      if fact.fact_family == "dependency-gate" and fact.successor == "ready" then
        declared = fact
      end
    end
    local entity, state, facts = hidden_state_conformance.hidden_state_behavior_fixture(core, row, declared, true)
    t.eq(entity.repo, "owner/repo")
    t.eq(state.state, "dependency_wait")
    t.is_true(facts.current == entity)
    t.is_true(facts["dependency-gate"] ~= nil)
  end,

  test_hidden_state_converge_round_fixture_requires_true_stall = function()
    local row = nil
    for _, candidate in ipairs(core.restart_transition_table()) do
      if candidate.from_state == "thinking" then
        row = candidate
      end
    end
    local declared = nil
    for _, fact in ipairs(row.advancing_facts) do
      if fact.fact_family == "converge-round" and fact.successor == "blocked" then
        declared = fact
      end
    end
    local positive_entity, positive_state = hidden_state_conformance.hidden_state_behavior_fixture(core, row, declared, true)
    local positive_facts = conv_rounds.converge_round_facts(core,
      positive_entity.comments,
      "github-devloop/issue/owner/repo/42",
      positive_state.version,
      convergence_shared.source_ref_digest(positive_entity.source_ref)
    )
    local positive_round = conv_rounds.max_converge_round(core, positive_facts)
    t.eq(positive_round, 3)
    t.is_true(conv_rounds.is_true_stall(core, positive_facts, positive_round))

    local negative_entity, negative_state = hidden_state_conformance.hidden_state_behavior_fixture(core, row, declared, false)
    local negative_facts = conv_rounds.converge_round_facts(core,
      negative_entity.comments,
      "github-devloop/issue/owner/repo/42",
      negative_state.version,
      convergence_shared.source_ref_digest(negative_entity.source_ref)
    )
    local negative_round = conv_rounds.max_converge_round(core, negative_facts)
    t.eq(negative_round, 3)
    t.eq(conv_rounds.is_true_stall(core, negative_facts, negative_round), false)
  end,

  test_hidden_state_implementing_fixture_uses_over_budget_fact = function()
    local row = nil
    for _, candidate in ipairs(core.restart_transition_table()) do
      if candidate.from_state == "implementing" then
        row = candidate
      end
    end
    local declared = nil
    for _, fact in ipairs(row.advancing_facts) do
      if fact.fact_family == "implementing" and fact.successor == "implementing" then
        declared = fact
      end
    end
    local _, state, positive = hidden_state_conformance.hidden_state_behavior_fixture(core, row, declared, true)
    local age = math.floor((positive.now_seconds - contract_time.iso_timestamp_epoch_seconds(state.marker_created_at)) / 60)
    t.eq(age, row.budget.minutes + 1)
    t.is_true(positive.implementing ~= nil)

    local _, _, negative = hidden_state_conformance.hidden_state_behavior_fixture(core, row, declared, false)
    t.eq(negative.implementing, nil)
  end,

  test_hidden_state_implementing_fixture_does_not_redrive_fresh_fact = function()
    local row = nil
    for _, candidate in ipairs(core.restart_transition_table()) do
      if candidate.from_state == "implementing" then
        row = candidate
      end
    end
    local declared = nil
    for _, fact in ipairs(row.advancing_facts) do
      if fact.fact_family == "implementing" and fact.successor == "implementing" then
        declared = fact
      end
    end
    local entity, state, facts = hidden_state_conformance.hidden_state_behavior_fixture(core, row, declared, true)
    facts.now_seconds = contract_time.iso_timestamp_epoch_seconds(state.marker_created_at) + 60

    local raised = capture_raises(function()
      local issued = replayer.replay_from_table(core, "observe_issue", entity, state, row, facts)
      t.eq(issued, false)
    end)
    t.eq(find_raise(raised, "devloop_ready"), nil)
  end,

  test_hidden_state_conformance_uses_observe_issue_production_replay_path = function()
    local seen = {}
    local fake_core = setmetatable({
      restart_package_name = core.restart_package_name,
      restart_consumer_sources = core.restart_consumer_sources,
    }, { __index = core })
    local previous = replayer.replay_from_table
    replayer.replay_from_table = function(replay_core, dept)
      t.eq(replay_core, fake_core)
      seen[dept] = true
      return false
    end
    local rows = {
      {
        from_state = "ready",
        to_states = { "implementing" },
        observe_surfaces = { issue = true },
        terminal = false,
        advancing_facts = {
          {
            fact_family = "state",
            successor = "implementing",
            observe_surfaces = { issue = true },
            source_ref_derivation = "source_ref:issue",
          },
        },
      },
    }
    local ok, err = pcall(function()
      hidden_state_conformance.hidden_state_conformance_errors(fake_core, rows, {})
    end)
    replayer.replay_from_table = previous
    if not ok then error(err) end
    t.eq(seen.observe_issue, true)
    t.eq(seen.behavioral_hidden_state_conformance, nil)
  end,

  test_hidden_state_conformance_rejects_non_poll_declaration = function()
    local rows = {}
    for index, row in ipairs(core.restart_transition_table()) do
      rows[index] = row
    end
    rows[1] = {
      from_state = "thinking",
      to_states = { "blocked" },
      observe_surfaces = { event = true },
      advancing_facts = {
        {
          fact_family = "converge-round",
          successor = "blocked",
          observe_surfaces = { event = true },
          source_ref_derivation = "source_ref:issue",
        },
      },
    }
    local errors = hidden_state_conformance.hidden_state_conformance_errors(core, rows, {})
    local joined = table.concat(errors, "\n")
    t.is_true(joined:find("poll observe surface", 1, true) ~= nil, joined)
  end,

  test_hidden_state_conformance_rejects_unaccounted_non_terminal_row = function()
    local rows = {
      {
        from_state = "impl-failed",
        to_states = { "implementing" },
        observe_surfaces = { issue = true },
        terminal = false,
      },
    }
    local errors = hidden_state_conformance.hidden_state_conformance_errors(core, rows, {})
    local joined = table.concat(errors, "\n")
    t.is_true(joined:find("non-terminal row must declare advancing_facts", 1, true) ~= nil, joined)
  end,

  test_hidden_state_conformance_accepts_operator_only_non_durable_hold = function()
    local rows = {
      {
        from_state = "blocked",
        to_states = {},
        observe_surfaces = { issue = true },
        terminal = false,
        non_durable_advance = {
          category = "operator-reentry",
          reason = "operator command re-entry is the only advance; no poll-derived durable fact autonomously advances this hold",
        },
      },
    }
    local errors = hidden_state_conformance.hidden_state_conformance_errors(core, rows, {})
    t.eq(#errors, 0)
  end,

  test_hidden_state_conformance_accepts_real_blocked_exemption_with_all_durable_facts = function()
    local rows = {}
    for index, row in ipairs(core.restart_transition_table()) do
      rows[index] = row
    end
    local errors = hidden_state_conformance.hidden_state_conformance_errors(core, rows, {})
    t.is_true(not contains_error(errors, "github-devloop|blocked|*: non_durable_advance exemption advanced"), join_error_messages(errors))
  end,

  test_hidden_state_conformance_rejects_false_non_durable_exemption = function()
    local fake_core
    fake_core = setmetatable({
      restart_package_name = core.restart_package_name,
    }, { __index = core })
    local previous = replayer.replay_from_table
    replayer.replay_from_table = function(replay_core, dept, issue, state, row, facts)
      t.eq(replay_core, fake_core)
      if state.state ~= "impl-failed" or facts.impl_failure == nil then
        return false
      end
      fake_core.log_cas_decision(dept, facts.proposal_id, state, "impl-failed", "implementing", "applied(synthetic-false-exemption)", "synthetic durable impl-failure advanced an exempt row")
      fake_core.log_apply(dept, facts.proposal_id, "implementing", state.version, { add = {}, remove = {} }, { "devloop_ready" })
      return true
    end
    local rows = {
      {
        from_state = "impl-failed",
        to_states = { "implementing" },
        observe_surfaces = { issue = true, liveness_scan = true },
        terminal = false,
        required_facts = {
          { family = "state", freshness = "marker-read" },
          { family = "impl-failure", freshness = "marker-read" },
        },
        non_durable_advance = {
          category = "terminal-hold",
          reason = "synthetic false exemption: impl-failure is durable and should be caught",
        },
      },
      {
        from_state = "ready",
        to_states = { "implementing" },
        observe_surfaces = { issue = true, liveness_scan = true },
        terminal = false,
        advancing_facts = {
          {
            fact_family = "impl-failure",
            successor = "implementing",
            observe_surfaces = { issue = true, liveness_scan = true },
            source_ref_derivation = "source_ref:issue",
          },
        },
      },
    }
    local ok, errors = pcall(function()
      return hidden_state_conformance.hidden_state_conformance_errors(fake_core, rows, {})
    end)
    replayer.replay_from_table = previous
    if not ok then error(errors) end
    local joined = table.concat(errors, "\n")
    t.is_true(joined:find("github-devloop|impl-failed|*: non_durable_advance exemption advanced to successor implementing", 1, true) ~= nil, joined)
  end,

  test_hidden_state_conformance_rejects_row_local_required_fact_false_exemption = function()
    local fake_core
    fake_core = setmetatable({
      restart_package_name = core.restart_package_name,
    }, { __index = core })
    local previous = replayer.replay_from_table
    replayer.replay_from_table = function(replay_core, dept, issue, state, row, facts)
      t.eq(replay_core, fake_core)
      if state.state ~= "impl-failed" or facts["hidden-durable-trigger"] == nil then
        return false
      end
      fake_core.log_cas_decision(dept, facts.proposal_id, state, "impl-failed", "implementing", "applied(synthetic-row-local-false-exemption)", "synthetic row-local durable fact advanced an exempt row")
      fake_core.log_apply(dept, facts.proposal_id, "implementing", state.version, { add = {}, remove = {} }, { "devloop_ready" })
      return true
    end
    local rows = {
      {
        from_state = "impl-failed",
        to_states = { "implementing" },
        observe_surfaces = { issue = true, liveness_scan = true },
        terminal = false,
        required_facts = {
          { family = "state", freshness = "marker-read" },
          { family = "hidden-durable-trigger", freshness = "marker-read" },
        },
        non_durable_advance = {
          category = "terminal-hold",
          reason = "synthetic false exemption: row-local hidden durable trigger is durable and should be caught",
        },
      },
    }
    local ok, errors = pcall(function()
      return hidden_state_conformance.hidden_state_conformance_errors(fake_core, rows, {})
    end)
    replayer.replay_from_table = previous
    if not ok then error(errors) end
    local joined = table.concat(errors, "\n")
    t.is_true(joined:find("github-devloop|impl-failed|*: non_durable_advance exemption advanced to successor implementing", 1, true) ~= nil, joined)
  end,

  test_completion_comment_key_start_wording_fails = function()
    local errors = span.errors_from_sources({
      ["packages/github-devloop/core/strings.lua"] = [[
local strings = { en = { implementation_started = "github-devloop implementation started" } }
]],
      ["packages/github-devloop/core/requests/lifecycle.lua"] = [[
local C = {}
function C.build_implementing_comment_request(M, repo, issue_number, ready, worktree, branch, head_sha)
  return { body = comment_strings.comment_string(M, "implementation_started") .. "\nHead: " .. tostring(head_sha) }
end
]],
    })
    t.is_true(contains_error(errors, "completion/output comment uses start wording key"), "missing wording key error")
    t.is_true(contains_error(errors, "implementation_started"), "missing key in message")
  end,

  test_completion_comment_literal_start_wording_fails = function()
    local errors = span.errors_from_sources({
      ["packages/github-devloop/core/requests/lifecycle.lua"] = [[
local C = {}
function C.build_implementing_comment_request(M, repo, issue_number, ready, worktree, branch, head_sha)
  return { body = "github-devloop implementation started" .. "\nHead: " .. tostring(head_sha) }
end
]],
    })
    t.is_true(contains_error(errors, "completion/output comment uses start wording literal"), "missing wording literal error")
  end,

  test_spawn_before_declared_start_predecessor_fails = function()
    local errors = span.errors_from_sources({
      ["libraries/devloop/restart/issue/transitions/implementing.lua"] = transition_source([[
    span_contract = span_contract({
      department = "implement",
      durable_start_marker = "implement-attempt:v1",
      spawn_predecessor = "raise_implementing_state",
    }),
]]),
      ["packages/github-devloop/departments/implement/main.lua"] = [[
local function raise_implementing_state(repo, issue_number, ready)
  local marker = core.implement_attempt_marker(ready.proposal_id, ready.dedup_key, 1, now())
  raise("github-proxy.github_issue_comment_request", { body = marker })
end

local result = spawn_codex_sync({ prompt = prompt })
raise_implementing_state(repo, issue_number, ready)
]],
    })
    t.is_true(contains_error(errors, "spawn_codex_sync must be preceded by span start predecessor"), "missing spawn order error")
  end,

  test_declared_start_predecessor_can_bind_marker_through_shared_helper = function()
    local errors = span.errors_from_sources({
      ["libraries/devloop/restart/issue/transitions/implementing.lua"] = transition_source([[
    span_contract = span_contract({
      department = "implement",
      durable_start_marker = "implement-attempt:v1",
      spawn_predecessor = "raise_implementing_state",
    }),
]]),
      ["packages/github-devloop/departments/implement/main.lua"] = [[
local function raise_implementing_state(repo, issue_number, ready)
  local request = requests_lifecycle.build_implementing_state_comment_request(core, repo, issue_number, ready)
  raise("github-proxy.github_issue_comment_request", request)
end

raise_implementing_state(repo, issue_number, ready)
local result = spawn_codex_sync({ prompt = prompt })
]],
      ["libraries/devloop/requests/lifecycle.lua"] = [[
local C = {}
function C.build_implementing_state_comment_request(M, repo, issue_number, ready)
  local marker = M.implement_attempt_marker(ready.proposal_id, ready.dedup_key, 1, now())
  return { body = marker }
end
]],
    })
    t.eq(#errors, 0)
  end,

  test_state_start_predecessor_can_bind_current_state_check = function()
    local errors = span.errors_from_sources({
      ["packages/github-devloop-pr/core/restart/transitions/fixing.lua"] = [[
return function(M, h)
  local responsibility_signature = h.responsibility_signature; local span_contract = h.span_contract
  return {
    from_state = "fixing",
    responsibility_signature = responsibility_signature({
      state_kind = "worker",
    }),
    span_contract = span_contract({
      department = "fix",
      durable_start_marker = "state:v1 fixing",
      spawn_predecessor = "precheck_fix_write_gate",
      spawn_function = "run_fix_attempt",
    }),
  }
end
]],
      ["packages/github-devloop-pr/departments/fix/main.lua"] = [[
local function validate_fix_write_gate_snapshot(pr, fix)
  local rechecked_state = require("devloop.entity").current_entity_state(core, pr.comments, fix.proposal_id)
  if rechecked_state.state ~= "fixing" then
    return nil
  end
  return pr
end

local function precheck_fix_write_gate(repo, fix, branch)
  return validate_fix_write_gate_snapshot(pr, fix) ~= nil
end

local function run_fix_attempt(plan)
  local result = spawn_codex_sync({ prompt = prompt })
  return result
end

precheck_fix_write_gate(repo, fix, branch)
local outcome = run_fix_attempt(attempt_plan)
]],
    })
    t.eq(#errors, 0)
  end,

  test_long_running_dispatch_spawn_without_live_run_dedup_fails = function()
    local errors = span.errors_from_sources({
      ["packages/github-devloop-pr/core/restart/transitions/fixing.lua"] = [[
return function(M, h)
  local responsibility_signature = h.responsibility_signature; local span_contract = h.span_contract
  return {
    from_state = "fixing",
    responsibility_signature = responsibility_signature({
      state_kind = "worker",
    }),
    liveness_contract = {
      real_execution = {
        primitive = "fkst.codex_runs",
        match = {
          role = "fix",
          proposal_id = "state.proposal_id",
          dedup_key = "state.version",
        },
      },
    },
    span_contract = span_contract({
      department = "fix",
      durable_start_marker = "state:v1 fixing",
      spawn_predecessor = "precheck_fix_write_gate",
      spawn_function = "run_fix_attempt",
    }),
  }
end
]],
      ["packages/github-devloop-pr/departments/fix/main.lua"] = [[
local function validate_fix_write_gate_snapshot(pr, fix)
  local rechecked_state = require("devloop.entity").current_entity_state(core, pr.comments, fix.proposal_id)
  if rechecked_state.state ~= "fixing" then
    return nil
  end
  return pr
end

local function precheck_fix_write_gate(repo, fix, branch)
  return validate_fix_write_gate_snapshot(pr, fix) ~= nil
end

local function run_fix_attempt(plan)
  return spawn_codex_sync({ prompt = prompt })
end

precheck_fix_write_gate(repo, fix, branch)
local outcome = run_fix_attempt(attempt_plan)
]],
    })
    t.is_true(contains_error(errors, "run_fix_attempt call must be preceded by dispatch_live_run_dedup"), "missing live-run dispatch dedup error")
  end,

  test_long_running_dispatch_spawn_with_live_run_dedup_passes = function()
    local errors = span.errors_from_sources({
      ["packages/github-devloop-pr/core/restart/transitions/fixing.lua"] = [[
return function(M, h)
  local responsibility_signature = h.responsibility_signature; local span_contract = h.span_contract
  return {
    from_state = "fixing",
    responsibility_signature = responsibility_signature({
      state_kind = "worker",
    }),
    liveness_contract = {
      real_execution = {
        primitive = "fkst.codex_runs",
        match = {
          role = "fix",
          proposal_id = "state.proposal_id",
          dedup_key = "state.version",
        },
      },
    },
    span_contract = span_contract({
      department = "fix",
      durable_start_marker = "state:v1 fixing",
      spawn_predecessor = "precheck_fix_write_gate",
      spawn_function = "run_fix_attempt",
    }),
  }
end
]],
      ["packages/github-devloop-pr/departments/fix/main.lua"] = [[
local function validate_fix_write_gate_snapshot(pr, fix)
  local rechecked_state = require("devloop.entity").current_entity_state(core, pr.comments, fix.proposal_id)
  if rechecked_state.state ~= "fixing" then
    return nil
  end
  return pr
end

local function precheck_fix_write_gate(repo, fix, branch)
  return validate_fix_write_gate_snapshot(pr, fix) ~= nil
end

local function run_fix_attempt(plan)
  return spawn_codex_sync({ prompt = prompt })
end

precheck_fix_write_gate(repo, fix, branch)
local dispatch_live_run = require("devloop.dispatch_live_run")
if dispatch_live_run.dispatch_live_run_dedup(core, "fix", attempt_plan.fix.proposal_id, attempt_plan.fix.version) then
  return
end
local outcome = run_fix_attempt(attempt_plan)
]],
    })
    t.eq(#errors, 0)
  end,

  test_worker_span_contract_declaration_reuses_strict_contract = function()
    local rows = {}
    for index, row in ipairs(core.restart_transition_table()) do
      rows[index] = row
    end
    for _, row in ipairs(rows) do
      if row.from_state == "implementing" then
        row.span_contract = nil
      end
    end
    local errors = m_rrc.strict_restart_responsibility_contract_errors(core, rows)
    t.is_true(contains_error(errors, "implementing: worker row must declare span_contract"), "missing strict span error")
  end,

  test_source_list_tracks_old_gspan_scan_surface = function()
    local listed = {}
    for _, path in ipairs(span.source_paths()) do
      listed[path] = true
    end
    t.eq(listed["libraries/devloop/requests/lifecycle.lua"], true)
    t.eq(listed["libraries/devloop/restart/issue/transitions/implementing.lua"], true)
    t.eq(listed["packages/github-devloop/departments/implement/main.lua"], true)
    t.eq(listed["packages/github-devloop-pr/core/restart/transitions/fixing.lua"], true)
    t.eq(listed["packages/github-devloop-pr/departments/fix/main.lua"], true)
  end,
}
