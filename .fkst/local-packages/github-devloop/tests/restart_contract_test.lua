local entity_lib = require("devloop.entity")
local convergence_shared = require("devloop.convergence.shared")
local contract_time = require("contract.time")
local operator_commands = require("devloop.operator_commands")
local replayer = require("devloop.replayer")
local transition_version = require("contract.transition_version")
local h = require("tests.devloop_core_helpers")
local payloads_builders = require("devloop.payloads.builders")
local conv_rounds = require("devloop.convergence.rounds")
local core = h.core
local t = h.t
local replay_fields = require("devloop.replay_fields")

local function restart_transition_row(state_name)
  return replay_fields.restart_transition_row(core.restart_transition_table(), state_name)
end

local function has_value(values, expected)
  for _, value in ipairs(values or {}) do
    if value == expected then
      return true
    end
  end
  return false
end

local function copy_rows(rows)
  local copied = {}
  local function copy_value(value)
    if type(value) ~= "table" then
      return value
    end
    local nested = {}
    for nested_key, nested_value in pairs(value) do
      nested[nested_key] = copy_value(nested_value)
    end
    return nested
  end
  for index, row in ipairs(rows or {}) do
    local next_row = {}
    for key, value in pairs(row) do
      next_row[key] = copy_value(value)
    end
    copied[index] = next_row
  end
  return copied
end

local function parse_marker_builders(paths)
  local families = {}
  for _, path in ipairs(paths) do
    local text = file.read(path)
    for family in text:gmatch("fkst:github%-devloop:([%w%-]+):v1") do
      families[family] = families[family] or {}
    end
    for family, attrs in pairs(families) do
      local family_pattern = "fkst:github%-devloop:" .. family:gsub("%-", "%%-") .. ":v1"
      local start_pos = text:find(family_pattern)
      if start_pos ~= nil then
        local function_pos = text:sub(1, start_pos):match("^.*()\nfunction [MC]%.[^\n]+")
        local next_function = text:find("\nfunction [MC]%.", start_pos + 1)
        local block = text:sub(function_pos or start_pos, next_function or #text)
        for attr in block:gmatch('" ([%w_]+)="') do
          attrs[attr] = true
        end
        for attr in block:gmatch('([%w_]+)="') do
          attrs[attr] = true
        end
      end
    end
  end
  return families
end

local function marker_builder_paths()
  return {
    "libraries/devloop/state.lua",
    "libraries/devloop/markers/builders.lua",
    "libraries/devloop/autonomy_ledger.lua",
    "packages/github-devloop/core/impl_failure.lua",
    "libraries/devloop/convergence/rounds.lua",
    "libraries/devloop/convergence/reconcile.lua",
    "libraries/devloop/convergence/attempts.lua",
    "libraries/devloop/decompose.lua",
    "packages/github-devloop/core/dependencies.lua",
    "packages/github-devloop/core/implement_attempt.lua",
  }
end

local function table_by_state()
  local by_state = {}
  for _, row in ipairs(core.restart_transition_table()) do
    by_state[row.from_state] = row
  end
  return by_state
end

local function rows_by_state(rows)
  local by_state = {}
  for _, row in ipairs(rows or {}) do
    by_state[row.from_state] = row
  end
  return by_state
end

local function allowed_extra_transition(state, next_state)
  return state == "impl-failed" and next_state == "implementing"
end

local function capture_raises(fn)
  local raised = {}
  local original_log_raise = core.log_raise
  core.log_raise = function(_, _, queue, payload)
    table.insert(raised, { queue = queue, payload = payload })
  end
  local ok, err = pcall(fn, raised)
  core.log_raise = original_log_raise
  if not ok then
    error(err)
  end
  return raised
end

local function with_codex_runs(running, fn)
  local original = fkst.codex_runs
  fkst.codex_runs = function()
    return { running = running or {}, recent = {} }
  end
  local ok, err = pcall(fn)
  fkst.codex_runs = original
  if not ok then
    error(err)
  end
end

local function synthetic_heartbeat_row()
  local row = copy_rows(core.restart_transition_table())[1]
  row.from_state = "synthetic-heartbeat"
  row.terminal = false
  row.liveness_class_id = "synthetic.heartbeat"
  row.watchdog = {
    mode = "live-defer",
    budget_ms = 45 * 60 * 1000,
    on_stale = {
      op = "redrive_receiver",
      producer = "converge-round",
    },
  }
  row.actionable_epoch = {
    source = "live_defer_heartbeat:v1",
    generation_source = "same_as_actionable_epoch",
    live_marker = "converge-round:v1",
    producer = "converge-round",
  }
  row.defer = {
    kind = "heartbeat",
    live_marker = "converge-round:v1",
    producer = "converge-round",
    freshness_ms = 45 * 60 * 1000,
    redrive_opens_generation = true,
  }
  row.budget = {
    minutes = 45,
    receiver_max_work_justification = "Synthetic heartbeat fixture.",
  }
  row.liveness_contract = {
    mode = "live-defer",
    signal = {
      family = "converge-round",
      producer = "converge-round",
      surface = "issue-comment-stream",
      version_form = "raw",
      max_age_minutes = 45,
    },
  }
  row.span_contract = nil
  return row
end

return {
  test_executable_restart_table_covers_non_terminal_states = function()
    local expected = { "thinking", "dependency_wait", "ready", "implementing", "awaiting-pr", "impl-failed", "blocked", "merged" }
    local by_state = table_by_state()
    t.eq(#core.liveness_contract_errors(), 0)
    for _, state in ipairs(expected) do
      local row = by_state[state]
      t.is_true(row ~= nil)
      t.eq(row.from_state, state)
      t.is_true(type(row.to_states) == "table")
      t.is_true(type(row.terminal) == "boolean")
      if row.terminal == false then
        t.is_true(type(row.driving_queue) == "string" and row.driving_queue ~= "")
        t.is_true(type(row.output_obligation) == "table")
        t.is_true(type(row.budget) == "table")
        t.is_true(type(row.budget.receiver_max_work_justification) == "string")
        t.is_true(row.budget.receiver_max_work_justification ~= "")
        t.is_true(type(row.liveness_contract) == "table")
        t.is_true(type(row.on_timeout) == "table")
        if row.payload_builder ~= nil then
          t.is_true(type(row.payload_builder) == "function")
        end
        t.is_true(type(row.dedup_shape) == "string" and row.dedup_shape ~= "")
        t.is_true(type(row.required_facts) == "table" and #row.required_facts > 0)
        t.is_true(type(row.payload_fields) == "table")
        t.is_true(type(row.version_identity) == "string" and row.version_identity ~= "")
        t.is_true(type(row.effects) == "table")
        t.is_true(tonumber(row.effects.intent_count) ~= nil)
        t.is_true(type(row.effects.kinds) == "table")
        t.eq(#row.effects.kinds, row.effects.intent_count)
        t.is_true(type(row.effects.completeness) == "string" and row.effects.completeness ~= "")
      end
    end
    t.eq(#core.restart_transition_table(), #expected)
  end,

  test_liveness_contract_declares_terminal_taxonomy_and_backstop = function()
    local errors = core.liveness_contract_errors()
    t.eq(#errors, 0)
    local terminals = core.liveness_terminal_states()
    t.eq(#terminals, 1)
    t.eq(terminals[1], "merged")
    local by_state = table_by_state()
    t.eq(by_state["impl-failed"].terminal, false)
    t.eq(by_state["impl-failed"].on_timeout.queue, "devloop_ready")
    t.eq(by_state["impl-failed"].reentry_commands[1], "reready")
    for _, row in ipairs(core.restart_transition_table()) do
      if row.terminal == false then
        t.is_true(row.output_obligation ~= nil)
        t.is_true(tonumber(row.budget.minutes) > 0)
        t.is_true(type(row.budget.receiver_max_work_justification) == "string")
        t.is_true(row.budget.receiver_max_work_justification ~= "")
        t.is_true(type(row.liveness_contract) == "table")
        t.eq(row.on_timeout.action, "redrive")
        t.eq(row.on_timeout.queue, row.driving_queue)
        t.is_true(row.on_timeout.queue ~= "none")
        t.eq(row.on_timeout.on_escalate.action, "force-terminate")
        t.eq(row.on_timeout.on_escalate.terminal_state, "blocked")
        t.eq(row.on_timeout.on_escalate.reason, "state-output-obligation-timeout")
      end
    end
  end,

  test_timeout_reconcile_capable_rows_declare_terminal_obligation_fact = function()
    local by_state = table_by_state()
    for _, state in ipairs({
      "thinking",
      "dependency_wait",
      "ready",
      "implementing",
      "awaiting-pr",
      "impl-failed",
    }) do
      local row = by_state[state]
      t.is_true(row ~= nil)
      t.is_true(has_value(row.output_obligation.kinds, "timeout-reconcile:v1"), state .. " must declare timeout-reconcile:v1 as a terminal output-obligation fact")
      t.eq(row.on_timeout.on_escalate.reason, "state-output-obligation-timeout")
      t.eq(row.on_timeout.on_escalate.terminal_state, "blocked")
    end

    t.eq(has_value(by_state.blocked.output_obligation.kinds, "timeout-reconcile:v1"), false)
    t.is_true(has_value(by_state.blocked.output_obligation.kinds, "decomposed:v1"))
  end,

  test_non_terminal_issue_marker_states_are_liveness_sweep_reachable = function()
    local errors = core.issue_marker_liveness_sweep_contract_errors()
    t.eq(#errors, 0)
    local sweep_states = core.issue_marker_liveness_sweep_states()
    for _, row in ipairs(core.restart_transition_table()) do
      if row.terminal == false then
        t.eq(sweep_states[row.from_state], true)
      else
        t.eq(sweep_states[row.from_state], nil)
      end
    end
    local liveness_scan = file.read("packages/github-devloop/departments/liveness_scan/main.lua")
    local observe_issue = file.read("packages/github-devloop/departments/observe_issue/main.lua")
    t.is_true(liveness_scan:find("liveness_scan.liveness_scan_maybe_timeout_action(core", 1, true) ~= nil)
    t.is_true(liveness_scan:find("should_reinject_state", 1, true) ~= nil)
    t.is_true(observe_issue:find("core.restart_row_observable_on", 1, true) ~= nil)
    t.is_true(observe_issue:find("maybe_reconcile_issue_local_orphaned_pr", 1, true) ~= nil)
  end,

  test_issue_marker_liveness_sweep_contract_rejects_missing_non_terminal_state = function()
    local sweep_states = core.issue_marker_liveness_sweep_states()
    sweep_states.ready = nil
    local errors = core.issue_marker_liveness_sweep_contract_errors(nil, sweep_states)
    t.eq(#errors, 1)
    t.is_true(errors[1]:find("ready", 1, true) ~= nil)
    t.is_true(errors[1]:find("liveness sweep", 1, true) ~= nil)
  end,

  test_implementing_restart_row_replays_ready_with_frozen_version_identity = function()
    local row = table_by_state().implementing
    t.eq(row.driving_queue, "devloop_ready")
    t.eq(row.on_timeout.queue, "devloop_ready")
    t.eq(row.kickoff, "devloop_ready")
    t.eq(row.effects.kinds[1], "devloop_ready")
    t.eq(row.payload_builder, payloads_builders.build_devloop_ready_payload)
    t.eq(row.payload_fields.proposal_id, "marker:state.proposal")
    t.eq(row.payload_fields.dedup_key, "marker:state.version")
    t.is_true(row.version_identity:find("ready_payload_inner_version", 1, true) ~= nil)
  end,

  test_impl_failed_restart_row_replays_ready_with_frozen_version_identity = function()
    local row = table_by_state()["impl-failed"]
    t.eq(row.driving_queue, "devloop_ready")
    t.eq(row.on_timeout.queue, "devloop_ready")
    t.eq(row.kickoff, "devloop_ready")
    t.eq(row.effects.kinds[1], "devloop_ready")
    t.eq(row.payload_builder, payloads_builders.build_devloop_ready_payload)
    t.eq(row.payload_fields.proposal_id, "marker:state.proposal")
    t.eq(row.payload_fields.dedup_key, "marker:impl-failure.dedup")
    t.is_true(row.version_identity:find("ready_payload_inner_version", 1, true) ~= nil)
  end,

  test_reentry_commands_are_supported_by_operator_parser = function()
    for _, row in ipairs(core.restart_transition_table()) do
      for _, command_name in ipairs(row.reentry_commands or {}) do
        local fact = operator_commands.operator_command_fact(core, {
          {
            id = "IC_" .. tostring(row.from_state) .. "_" .. tostring(command_name),
            body = "fkst: " .. tostring(command_name),
            author_login = "fkst-test-bot",
            created_at = "2026-06-04T03:00:00Z",
          },
        }, command_name)
        t.is_true(fact ~= nil, "unsupported reentry command " .. tostring(command_name))
      end
    end
  end,

  test_liveness_contract_rejects_non_terminal_without_output_obligation = function()
    local rows = copy_rows(core.restart_transition_table())
    rows_by_state(rows).ready.output_obligation = nil
    local errors = core.liveness_contract_errors(rows)
    t.eq(#errors, 1)
    t.is_true(errors[1]:find("ready", 1, true) ~= nil)
    t.is_true(errors[1]:find("output_obligation", 1, true) ~= nil)
  end,

  test_liveness_contract_rejects_non_terminal_without_force_termination = function()
    local rows = copy_rows(core.restart_transition_table())
    rows_by_state(rows).ready.on_timeout.on_escalate = nil
    local errors = core.liveness_contract_errors(rows)
    t.eq(#errors, 1)
    t.is_true(errors[1]:find("ready", 1, true) ~= nil)
    t.is_true(errors[1]:find("force-terminate", 1, true) ~= nil)
    t.is_true(errors[1]:find("blocked", 1, true) ~= nil)
  end,

  test_liveness_contract_declares_receiver_liveness_for_every_non_terminal_row = function()
    local by_state = table_by_state()
    local expected = {
      thinking = { mode = "live-defer", codex_run = true, role = "consensus", budget = 150 },
      dependency_wait = { mode = "live-defer", family = "dependency-wait", resolver = "dependency-hold", max_age = 525600, budget = 525600 },
      ready = { mode = "row-budget-bounds-receiver", receiver = 15, external = 0, budget = 120 },
      implementing = { mode = "live-defer", codex_run = true, role = "implement", budget = 120 },
      ["awaiting-pr"] = { mode = "live-defer", family = "state", producer = "child-state", resolver = "child-state", max_age = 1440, budget = 259200 },
      ["impl-failed"] = { mode = "row-budget-bounds-receiver", receiver = 0, external = 1410, budget = 1440 },
      blocked = { mode = "row-budget-bounds-receiver", receiver = 0, external = 1410, budget = 1440 },
    }
    for state, spec in pairs(expected) do
      local row = by_state[state]
      t.is_true(row ~= nil, state)
      t.eq(row.terminal, false)
      t.eq(row.budget.minutes, spec.budget)
      t.eq(row.liveness_contract.mode, spec.mode)
      if spec.mode == "live-defer" then
        if spec.codex_run then
          t.eq(row.liveness_contract.signal, nil)
          t.eq(row.liveness_contract.real_execution.primitive, "fkst.codex_runs")
          t.eq(row.liveness_contract.real_execution.match.role, spec.role)
          t.eq(row.liveness_contract.real_execution.match.proposal_id, "state.proposal_id")
          t.eq(row.liveness_contract.real_execution.match.dedup_key, "state.version")
        else
          t.eq(row.liveness_contract.signal.family, spec.family)
          t.eq(row.liveness_contract.signal.resolver, spec.resolver)
          t.eq(row.liveness_contract.signal.producer, spec.producer or spec.family)
          t.eq(row.liveness_contract.signal.max_age_minutes, spec.max_age)
        end
      else
        t.eq(row.liveness_contract.receiver_bound_minutes, spec.receiver)
        t.eq(row.liveness_contract.external_wait_bound_minutes, spec.external)
      end
    end
  end,

  test_liveness_contract_rejects_non_terminal_without_receiver_liveness = function()
    local rows = copy_rows(core.restart_transition_table())
    rows_by_state(rows).ready.liveness_contract = nil
    local errors = core.liveness_contract_errors(rows)
    t.eq(#errors, 1)
    t.is_true(errors[1]:find("ready", 1, true) ~= nil)
    t.is_true(errors[1]:find("liveness_contract", 1, true) ~= nil)
  end,

  test_liveness_contract_rejects_budget_without_receiver_max_work_justification = function()
    local rows = copy_rows(core.restart_transition_table())
    rows_by_state(rows).ready.budget.receiver_max_work_justification = nil
    local errors = core.liveness_contract_errors(rows)
    t.eq(#errors, 1)
    t.is_true(errors[1]:find("ready", 1, true) ~= nil)
    t.is_true(errors[1]:find("receiver_max_work_justification", 1, true) ~= nil)
  end,

  test_liveness_contract_rejects_under_budget_receiver_bound = function()
    local rows = copy_rows(core.restart_transition_table())
    local row = rows_by_state(rows).ready
    row.budget.minutes = 10
    local errors = core.liveness_contract_errors(rows)
    local joined = table.concat(errors, "\n")
    t.is_true(joined:find("ready", 1, true) ~= nil)
    t.is_true(joined:find("budget.minutes", 1, true) ~= nil)
  end,

  test_liveness_contract_rejects_live_defer_without_resolver_or_existing_family = function()
    local row = synthetic_heartbeat_row()
    row.liveness_contract.signal.family = "missing-family"
    row.liveness_contract.signal.resolver = "missing-resolver"
    row.liveness_contract.signal.max_age_minutes = nil
    local errors = core.liveness_contract_errors({ row })
    local joined = table.concat(errors, "\n")
    t.is_true(joined:find("missing-family", 1, true) ~= nil)
    t.is_true(joined:find("missing-resolver", 1, true) ~= nil)
    t.is_true(joined:find("max_age_minutes", 1, true) ~= nil)
    t.is_true(joined:find("resolver mismatch", 1, true) ~= nil)
  end,

  test_liveness_contract_rejects_live_defer_without_producer_binding = function()
    local row = synthetic_heartbeat_row()
    row.liveness_contract.signal.producer = nil
    local errors = core.liveness_contract_errors({ row })
    local joined = table.concat(errors, "\n")
    t.is_true(joined:find("synthetic-heartbeat", 1, true) ~= nil, joined)
    t.is_true(joined:find("producer binding", 1, true) ~= nil, joined)
  end,

  test_liveness_contract_rejects_live_defer_family_resolver_producer_mismatch = function()
    local row = synthetic_heartbeat_row()
    row.liveness_contract.signal.family = "dependency-wait"
    row.liveness_contract.signal.producer = "converge-round"
    local errors = core.liveness_contract_errors({ row })
    local joined = table.concat(errors, "\n")
    t.is_true(joined:find("producer binding family mismatch", 1, true) ~= nil)
    t.is_true(joined:find("producer binding resolver mismatch", 1, true) ~= nil)
  end,

  test_liveness_timeout_versions_preserve_lineage_and_attempts = function()
    local row = table_by_state()["impl-failed"]
    local base = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z"
    local state = {
      state = "impl-failed",
      version = base,
      marker_created_at = "2026-06-03T01:02:03Z",
    }
    local decision = core.liveness_timeout_decision(row, state, contract_time.iso_timestamp_epoch_seconds("2026-06-04T01:02:03Z"))
    t.eq(decision.action, "redrive")
    t.eq(decision.attempt, 1)
    t.eq(core.version_timeout_round(decision.version, "impl-failed"), 1)
    t.eq(transition_version.strip_suffixes(decision.version), transition_version.strip_suffixes(base))
    local over = {
      state = "impl-failed",
      version = base .. "/timeout/impl-failed/3",
      marker_created_at = "2026-06-03T01:02:03Z",
    }
    local escalated = core.liveness_timeout_decision(row, over, contract_time.iso_timestamp_epoch_seconds("2026-06-04T01:02:03Z"))
    t.eq(escalated.action, "escalate")
  end,

  test_replay_timeout_classification_counts_declines_as_stuck = function()
    local declined = {
      "skip-idempotent(retry-limit)",
      "skip-foreign(decomposed)",
      "skip-foreign(pr-link)",
      "skip-pending(no-attempt-marker)",
      "skip-pending(codex-run-live)",
      "skip-stale(head-advanced)",
    }
    for _, outcome in ipairs(declined) do
      local previous = replayer.replay_from_table
      replayer.replay_from_table = function()
        replayer.replay_log_skip(core, "test", nil, { state = "ready" }, "ready", "ready", outcome, "declined")
        return false
      end
      local ok, classified = pcall(function()
        return replayer.replay_from_table_classified(core, "test", {}, { state = "ready" }, restart_transition_row("ready"), {})
      end)
      replayer.replay_from_table = previous
      if not ok then error(classified) end
      t.eq(classified.kind, "stuck")
      t.eq(classified.outcome, outcome)
    end
  end,

  test_live_thinking_codex_run_defers_timeout_count = function()
    local row = table_by_state().thinking
    local version = "consensus:github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z"
    local source_ref = entity_lib.issue_source_ref("owner/repo", 42)
    local raised = capture_raises(function()
      with_codex_runs({
        {
          status = "running",
          role = "consensus",
          proposal_id = "github-devloop/issue/owner/repo/42",
          dedup_key = version,
          started_at = "2026-06-04T00:30:00Z",
          timeout_seconds = 7200,
        },
      }, function()
      local applied = core.maybe_timeout_redrive_from_table("liveness_scan", {
        repo = "owner/repo",
        number = 42,
        source_ref = source_ref,
      }, {
        state = "thinking",
        version = version,
        proposal_id = "github-devloop/issue/owner/repo/42",
        marker_created_at = "2026-06-03T00:00:00Z",
      }, row, {
        proposal_id = "github-devloop/issue/owner/repo/42",
        source_ref = source_ref,
        current = { comments = {} },
        now_seconds = contract_time.iso_timestamp_epoch_seconds("2026-06-04T01:02:03Z"),
      })
      t.eq(applied, true)
      end)
    end)
    t.eq(#raised, 0)
  end,

  test_stale_thinking_converge_round_climbs_to_blocked_reconcile = function()
    local row = table_by_state().thinking
    local version = "consensus:github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z"
    local source_ref = entity_lib.issue_source_ref("owner/repo", 42)
    local raised = capture_raises(function()
      local applied = core.maybe_timeout_redrive_from_table("liveness_scan", {
        repo = "owner/repo",
        number = 42,
        source_ref = source_ref,
      }, {
        state = "thinking",
        version = version .. "/timeout/thinking/3",
        proposal_id = "github-devloop/issue/owner/repo/42",
        marker_created_at = "2026-06-03T00:00:00Z",
      }, row, {
        proposal_id = "github-devloop/issue/owner/repo/42",
        source_ref = source_ref,
        current = {
          comments = {
            {
              body = conv_rounds.converge_round_marker(core, "github-devloop/issue/owner/repo/42", version, convergence_shared.source_ref_digest(source_ref), 1, "consensus:github-devloop/issue/owner/repo/42/loop/1", "Stale convergence", {
                { angle = "minimal", verdict = "continue", digest = "stale" },
              }),
              author_login = "fkst-test-bot",
              created_at = "2026-06-03T00:00:00Z",
            },
          },
        },
        now_seconds = contract_time.iso_timestamp_epoch_seconds("2026-06-04T01:02:03Z"),
      })
      t.eq(applied, true)
    end)
    t.eq(#raised, 1)
    t.eq(raised[1].queue, "devloop_timeout_reconcile")
    t.eq(raised[1].payload.state, "thinking")
  end,

  test_liveness_timeout_escalates_thinking_to_timeout_reconcile_event = function()
    local row = table_by_state().thinking
    local base = "consensus:github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z"
    local raised = {}
    local original_log_raise = core.log_raise
    core.log_raise = function(_, _, queue, payload)
      table.insert(raised, { queue = queue, payload = payload })
    end
    local ok, err = pcall(function()
      local applied = core.maybe_timeout_redrive_from_table("observe_issue", {
        repo = "owner/repo",
        number = 42,
        source_ref = entity_lib.issue_source_ref("owner/repo", 42),
      }, {
        state = "thinking",
        version = base .. "/timeout/thinking/3",
        proposal_id = "github-devloop/issue/owner/repo/42",
        marker_created_at = "2026-06-03T01:02:03Z",
      }, row, {
        proposal_id = "github-devloop/issue/owner/repo/42",
        now_seconds = contract_time.iso_timestamp_epoch_seconds("2026-06-04T01:02:03Z"),
      })
      t.eq(applied, true)
    end)
    core.log_raise = original_log_raise
    if not ok then
      error(err)
    end
    t.eq(#raised, 1)
    t.eq(raised[1].queue, "devloop_timeout_reconcile")
    t.eq(raised[1].payload.schema, "github-devloop.timeout-reconcile.v1")
    t.eq(raised[1].payload.state, "thinking")
    t.eq(raised[1].payload.issue_version, base .. "/timeout/thinking/3")
    t.eq(raised[1].payload.round, 3)
    t.eq(raised[1].payload.dedup_key, "timeout-reconcile:" .. base .. "/timeout/thinking/3/timeout-reconcile/thinking/3")
  end,

  test_restart_table_matches_state_graph_and_stage_rank = function()
    local by_state = table_by_state()
    local expected = {
      thinking = true,
      ready = true,
      implementing = true,
      ["awaiting-pr"] = true,
      ["impl-failed"] = true,
      blocked = true, merged = true,
    }
    for state in pairs(core.lifecycle_state_set()) do
      if expected[state] then
        local next_states = core.state_successors(state)
        local row = by_state[state]
        t.is_true(row ~= nil)
        for _, next_state in ipairs(row.to_states) do
          t.is_true(has_value(next_states, next_state) or allowed_extra_transition(state, next_state))
        end
        t.is_true(core.stage_rank(state) > 0)
      end
    end
    for state in pairs(expected) do
      t.is_true(by_state[state] ~= nil)
    end
  end,

  test_restart_required_facts_declare_freshness_modes = function()
    for _, row in ipairs(core.restart_transition_table()) do
      if row.terminal == true then
        goto continue
      end
      local saw_marker = false
      for _, required in ipairs(row.required_facts) do
        t.is_true(type(required.family) == "string" and required.family ~= "")
        t.is_true(required.freshness == "marker-read" or required.freshness == "fetch-before-compare")
        if required.freshness == "marker-read" then
          saw_marker = true
        end
      end
      t.is_true(saw_marker)
      ::continue::
    end
  end,

  test_restart_payload_fields_are_covered_by_durable_fields = function()
    local errors = core.restart_field_coverage_errors()
    t.eq(#errors, 0)
  end,

  test_multi_effect_rows_declare_and_call_completeness_derivation = function()
    local by_state = table_by_state()
    t.eq(by_state.ready.effects.intent_count, 1)
    t.eq(by_state.ready.effects.kinds[1], "devloop_ready")
    t.eq(by_state.dependency_wait.effects.completeness_derivation, "dependency_gate_rederive")
    t.eq(by_state["awaiting-pr"].effects.completeness_derivation, "replay_awaiting_pr_state")
    t.eq(by_state.blocked.effects.intent_count, 2)
    t.eq(by_state.blocked.effects.completeness_derivation, "decompose_children_complete")
    t.eq(#core.restart_effect_contract_errors(), 0)
  end,

  test_multi_effect_contract_rejects_marker_only_rows = function()
    local rows = copy_rows(core.restart_transition_table())
    local ready = rows_by_state(rows).dependency_wait
    ready.effects.completeness_derivation = nil
    local errors = core.restart_effect_contract_errors(rows)
    t.eq(#errors, 1)
    t.is_true(errors[1]:find("dependency_wait", 1, true) ~= nil)
    t.is_true(errors[1]:find("completeness derivation", 1, true) ~= nil)
  end,

  test_declared_marker_fields_exist_in_marker_builders = function()
    local parsed = parse_marker_builders(marker_builder_paths())
    for family, attrs in pairs(core.restart_durable_marker_fields()) do
      t.is_true(parsed[family] ~= nil, "missing marker family " .. tostring(family))
      for attr in pairs(attrs) do
        t.is_true(parsed[family][attr] == true, "missing marker attr " .. tostring(family) .. "." .. tostring(attr))
      end
    end
  end,

  test_source_ref_derivations_are_declared = function()
    local derivations = core.restart_source_ref_derivations()
    t.eq(derivations.issue, true)
    t.eq(derivations.pr, true)
    t.eq(derivations.entity, true)
  end,

  test_observe_issue_replay_is_table_driven = function()
    local text = file.read("packages/github-devloop/departments/observe_issue/main.lua")
    t.is_true(text:find("replayer.replay_from_table", 1, true) ~= nil)
    t.eq(text:find("build_replayed_fixing_payload", 1, true), nil)
    t.eq(text:find("build_devloop_review_meta_payload", 1, true), nil)
    t.eq(text:find("build_decompose_replay_payload", 1, true), nil)
    t.eq(text:find("build_devloop_reviewing_payload", 1, true), nil)
  end,

}
