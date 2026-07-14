local h = require("tests.devloop_core_helpers")
local m_rrc = require("devloop.restart_responsibility_contract")
local core = h.core
local t = h.t

local function copy_value(value)
  if type(value) ~= "table" then
    return value
  end
  local out = {}
  for key, nested in pairs(value) do
    out[key] = copy_value(nested)
  end
  return out
end

local function copy_rows(rows)
  local copied = {}
  for index, row in ipairs(rows or {}) do
    copied[index] = copy_value(row)
  end
  return copied
end

local function rows_by_state(rows)
  local by_state = {}
  for _, row in ipairs(rows or {}) do
    by_state[row.from_state] = row
  end
  return by_state
end

local function joined_errors(errors)
  return table.concat(errors or {}, "\n")
end

local function contains_error(errors, needle)
  return joined_errors(errors):find(needle, 1, true) ~= nil
end

local function successor_by_state(row, state)
  for _, edge in ipairs(row.responsibility_signature and row.responsibility_signature.successors or {}) do
    if edge.state == state then
      return edge
    end
  end
  return nil
end

local function assert_inventory_errors(inventory, state, expected)
  local listed = inventory[state]
  t.eq(type(listed), "table", state)
  local count = 0
  for err, reason in pairs(listed) do
    t.eq(type(reason), "string", err)
    t.is_true(reason ~= "", err)
    t.is_true(expected[err] == true, err)
    count = count + 1
  end
  local expected_count = 0
  for err, _ in pairs(expected) do
    t.is_true(listed[err] ~= nil, err)
    expected_count = expected_count + 1
  end
  t.eq(count, expected_count, state)
end

local function clean_row()
  return {
    from_state = "synthetic-clean",
    terminal = false,
    to_states = { "synthetic-done" },
    driving_queue = "synthetic_queue",
    liveness_class_id = "synthetic.clean",
    responsibility_signature = {
      receiver_kind = "synthetic-worker",
      driving_queue = "synthetic_queue",
      state_kind = "worker",
      liveness_class = "synthetic.clean",
      input_fact_family = "synthetic-input",
      output_postcondition_family = "synthetic-output",
      phase_rank = 0,
      lineage_keys = { "state.version" },
      successors = {
        {
          state = "synthetic-done",
          output_variant = "done",
          postcondition_family = "synthetic-output",
          monotonic = true,
        },
      },
    },
    span_contract = {
      department = "synthetic",
      durable_start_marker = "synthetic-start:v1",
      spawn_predecessor = "raise_synthetic_start",
    },
  }
end

local function set_clean_signature(row)
  local signature = clean_row().responsibility_signature
  signature.driving_queue = row.driving_queue
  signature.liveness_class = row.liveness_class_id
  signature.phase_rank = core.stage_rank(row.from_state)
  signature.successors = {}
  for _, next_state in ipairs(row.to_states or {}) do
    table.insert(signature.successors, {
      state = next_state,
      output_variant = next_state,
      postcondition_family = signature.output_postcondition_family,
      monotonic = true,
    })
  end
  row.responsibility_signature = signature
  return signature
end

return {
  test_known_god_states_inventory_is_exact = function()
    local inventory = m_rrc.known_god_states(core)
    local count = 0
    for _ in pairs(inventory) do
      count = count + 1
    end
    t.eq(count, 0)
  end,

  test_inventory_ratchet_keeps_main_conformance_green = function()
    t.eq(#core.liveness_contract_errors(), 0)
    local strict = m_rrc.strict_restart_responsibility_contract_errors(core)
    t.eq(m_rrc.responsibility_contract_inventory_is_listed_violation(core, "ready", strict), false)
    t.eq(m_rrc.responsibility_contract_inventory_is_listed_violation(core, "dependency_wait", strict), false)
    t.eq(m_rrc.responsibility_contract_inventory_is_listed_violation(core, "blocked", strict), false)
    t.eq(m_rrc.responsibility_contract_inventory_is_listed_violation(core, "implementing", strict), false)
    t.eq(m_rrc.responsibility_contract_inventory_is_listed_violation(core, "awaiting-pr", strict), false)
  end,

  test_clean_single_responsibility_rows_pass_strict_contract = function()
    local by_state = rows_by_state(core.restart_transition_table())
    for _, state in ipairs({ "thinking", "dependency_wait", "ready", "implementing", "awaiting-pr", "impl-failed", "blocked" }) do
      local errors = m_rrc.strict_restart_responsibility_contract_errors(core, { by_state[state] })
      t.eq(#errors, 0, state .. ": " .. joined_errors(errors))
    end
    t.eq(#m_rrc.strict_restart_responsibility_contract_errors(core, { clean_row() }), 0)
  end,

  test_blocked_is_clean_budget_bounded_recovery_signature = function()
    local row = rows_by_state(core.restart_transition_table()).blocked
    local signature = row.responsibility_signature
    t.eq(signature.state_kind, "budget_bounded_recovery")
    t.eq(signature.receiver_kind, "operator-reentry")
    t.eq(signature.driving_queue, "github-devloop-decompose.devloop_decompose")
    t.eq(signature.liveness_class, "blocked.operator_reentry")
    t.eq(signature.phase_rank, core.stage_rank("blocked"))
    t.eq(row.terminal, false)
    t.eq(#row.to_states, 0)
    t.eq(row.watchdog.mode, "row-budget-bounds-receiver")
    t.eq(row.watchdog.budget_ms, 1440 * 60 * 1000)
    t.eq(row.actionable_epoch.source, "state_entry:v1")
    t.eq(row.on_timeout.queue, "github-devloop-decompose.devloop_decompose")
    t.eq(row.operator_reentry.kind, "external_command")
    t.eq(row.operator_reentry.not_autonomous_successor, true)
    t.eq(row.operator_reentry.resets_budget, true)
    t.eq(signature.watchdog_escape.kind, "watchdog_escape")
    t.eq(signature.watchdog_escape.queue, "github-devloop-decompose.devloop_decompose")
    t.eq(#signature.successors, 0)
    t.eq(#m_rrc.strict_restart_responsibility_contract_errors(core, { row }), 0)
  end,

  test_budget_bounded_recovery_rejects_autonomous_successor = function()
    local row = copy_value(rows_by_state(core.restart_transition_table()).blocked)
    row.from_state = "synthetic-bad-recovery"
    row.to_states = { "implementing" }
    row.responsibility_signature.successors = {
      {
        state = "implementing",
        output_variant = "autonomous-retry",
        postcondition_family = "blocked-decompose-escape",
        bump = true,
      },
    }
    row.responsibility_signature.phase_rank = 10
    local original_stage_rank = core.stage_rank
    core.stage_rank = function(state)
      if state == "synthetic-bad-recovery" then
        return 10
      end
      return original_stage_rank(state)
    end
    local ok, errors = pcall(function()
      return m_rrc.strict_restart_responsibility_contract_errors(core, { row })
    end)
    core.stage_rank = original_stage_rank
    if not ok then
      error(errors)
    end
    t.is_true(contains_error(errors, "synthetic-bad-recovery: budget_bounded_recovery state must not declare autonomous successors"), joined_errors(errors))
  end,

  test_terminal_escape_to_non_terminal_state_fails = function()
    local row = {
      from_state = "synthetic-terminal-escape",
      terminal = false,
      to_states = { "synthetic-forward", "blocked" },
      driving_queue = "synthetic_decision",
      liveness_class_id = "synthetic.terminal_escape",
      responsibility_signature = {
        receiver_kind = "synthetic-judge",
        driving_queue = "synthetic_decision",
        state_kind = "decision",
        liveness_class = "synthetic.terminal_escape",
        input_fact_family = "synthetic-input",
        output_postcondition_family = "synthetic-decision-result",
        decision_type = "synthetic-decision-result",
        phase_rank = 10,
        lineage_keys = { "state.version" },
        successors = {
          {
            state = "synthetic-forward",
            output_variant = "forward",
            terminal = true,
            monotonic = true,
          },
          {
            state = "blocked",
            output_variant = "blocked-terminal",
            terminal = true,
            monotonic = true,
          },
        },
      },
    }
    local forward = copy_value(row)
    forward.from_state = "synthetic-forward"
    forward.to_states = { "synthetic-done" }
    forward.driving_queue = "synthetic_forward"
    forward.liveness_class_id = "synthetic.forward"
    forward.responsibility_signature.receiver_kind = "synthetic-worker"
    forward.responsibility_signature.driving_queue = "synthetic_forward"
    forward.responsibility_signature.state_kind = "worker"
    forward.responsibility_signature.liveness_class = "synthetic.forward"
    forward.responsibility_signature.input_fact_family = "synthetic-forward-input"
    forward.responsibility_signature.output_postcondition_family = "synthetic-forward-output"
    forward.responsibility_signature.phase_rank = 20
    forward.responsibility_signature.decision_type = nil
    forward.responsibility_signature.successors = {
      {
        state = "synthetic-done",
        output_variant = "done",
        postcondition_family = "synthetic-forward-output",
        monotonic = true,
      },
    }
    local blocked = copy_value(row)
    blocked.from_state = "blocked"
    blocked.to_states = {}
    blocked.responsibility_signature = nil
    local original_stage_rank = core.stage_rank
    core.stage_rank = function(state)
      if state == "synthetic-terminal-escape" then
        return 10
      end
      if state == "synthetic-forward" then
        return 20
      end
      if state == "synthetic-done" then
        return 30
      end
      return original_stage_rank(state)
    end
    local ok, errors = pcall(function()
      return m_rrc.strict_restart_responsibility_contract_errors(core, { row, forward, blocked })
    end)
    core.stage_rank = original_stage_rank
    if not ok then
      error(errors)
    end
    t.is_true(contains_error(errors, "synthetic-terminal-escape: terminal-escape successor must point to a terminal-class state: synthetic-forward"), joined_errors(errors))
    t.is_true(not contains_error(errors, "synthetic-terminal-escape: terminal-escape successor must point to a terminal-class state: blocked"), joined_errors(errors))
  end,

  test_negative_control_unrelated_families_backward_edge_and_two_receivers_fail = function()
    local row = clean_row()
    row.from_state = "synthetic-bad"
    row.responsibility_signature.receiver_kind = { "worker-a", "worker-b" }
    row.responsibility_signature.successors[1].postcondition_family = "other-family"
    row.responsibility_signature.phase_rank = 20
    row.responsibility_signature.successors[1].state = "synthetic-earlier"
    row.to_states = { "synthetic-earlier" }
    local original_stage_rank = core.stage_rank
    core.stage_rank = function(state)
      if state == "synthetic-bad" then
        return 20
      end
      if state == "synthetic-earlier" then
        return 10
      end
      return original_stage_rank(state)
    end
    local ok, errors = pcall(function()
      return m_rrc.strict_restart_responsibility_contract_errors(core, { row })
    end)
    core.stage_rank = original_stage_rank
    if not ok then
      error(errors)
    end
    t.is_true(contains_error(errors, "synthetic-bad: responsibility_signature.receiver_kind must be exactly one receiver"))
    t.is_true(contains_error(errors, "synthetic-bad: normal successor has unrelated output_postcondition_family: synthetic-earlier"))
    t.is_true(contains_error(errors, "synthetic-bad: backward successor requires generation bump: synthetic-earlier"))
  end,

  test_worker_rows_require_span_contract = function()
    local row = clean_row()
    row.from_state = "synthetic-worker-without-span"
    row.span_contract = nil
    local errors = m_rrc.strict_restart_responsibility_contract_errors(core, { row })
    t.is_true(contains_error(errors, "synthetic-worker-without-span: worker row must declare span_contract"), joined_errors(errors))
  end,

  test_worker_span_contract_requires_durable_start_marker = function()
    local row = clean_row()
    row.from_state = "synthetic-worker-bad-span"
    row.span_contract = {
      department = "synthetic",
      durable_start_marker = "synthetic-start",
      spawn_predecessor = "raise_synthetic_start",
    }
    local errors = m_rrc.strict_restart_responsibility_contract_errors(core, { row })
    t.is_true(contains_error(errors, "synthetic-worker-bad-span: span_contract.durable_start_marker must name a durable marker family"), joined_errors(errors))
  end,

  test_worker_span_contract_requires_all_start_fields = function()
    for _, field in ipairs({ "department", "durable_start_marker", "spawn_predecessor" }) do
      local row = clean_row()
      row.from_state = "synthetic-worker-missing-" .. field
      row.span_contract[field] = nil
      local errors = m_rrc.strict_restart_responsibility_contract_errors(core, { row })
      t.is_true(contains_error(errors, row.from_state .. ": span_contract." .. field .. " must be declared"), joined_errors(errors))
    end
  end,

  test_worker_span_contract_rejects_empty_spawn_function = function()
    local row = clean_row()
    row.from_state = "synthetic-worker-empty-spawn-function"
    row.span_contract.spawn_function = ""
    local errors = m_rrc.strict_restart_responsibility_contract_errors(core, { row })
    t.is_true(contains_error(errors, "synthetic-worker-empty-spawn-function: span_contract.spawn_function must be a non-empty string when declared"), joined_errors(errors))
  end,

  test_anti_allowlist_extra_error_on_listed_state_is_not_suppressed = function()
    local rows = copy_rows(core.restart_transition_table())
    local ready = rows_by_state(rows).ready
    ready.responsibility_signature = {
      receiver_kind = { "dependency-gate", "implementation-kickoff" },
      driving_queue = "wrong_queue",
      state_kind = "queue_wait",
      liveness_class = "ready.actionable",
      input_fact_family = "ready-input",
      output_postcondition_family = "ready-output",
      phase_rank = core.stage_rank("ready"),
      lineage_keys = { "state.version" },
      successors = {
        {
          state = "implementing",
          output_variant = "implementation",
          postcondition_family = "ready-output",
          monotonic = true,
        },
      },
    }
    local errors = m_rrc.restart_responsibility_inventory_errors(core, rows)
    t.is_true(contains_error(errors, "ready: responsibility_signature.receiver_kind must be exactly one receiver"))
    t.is_true(contains_error(errors, "ready: responsibility_signature.driving_queue must match row.driving_queue"))
  end,

  test_queue_wait_arbitrary_extra_non_terminal_successor_still_fails = function()
    local row = copy_value(rows_by_state(core.restart_transition_table()).ready)
    row.from_state = "synthetic-queue-wait-extra"
    row.to_states = { "implementing", "fixing", "blocked" }
    row.responsibility_signature.phase_rank = core.stage_rank("ready")
    row.responsibility_signature.successors = {
      {
        state = "implementing",
        output_variant = "implementation_kicked_off",
        postcondition_family = "implementation_kickoff",
        monotonic = true,
      },
      {
        state = "fixing",
        output_variant = "arbitrary_extra",
        failure = true,
        bump = true,
      },
      {
        state = "blocked",
        output_variant = "actionable_kickoff_timeout",
        terminal = true,
        monotonic = true,
      },
    }
    local original_stage_rank = core.stage_rank
    core.stage_rank = function(state)
      if state == "synthetic-queue-wait-extra" then
        return original_stage_rank("ready")
      end
      return original_stage_rank(state)
    end
    local ok, errors = pcall(function()
      return m_rrc.strict_restart_responsibility_contract_errors(core, { row })
    end)
    core.stage_rank = original_stage_rank
    if not ok then
      error(errors)
    end
    t.is_true(contains_error(errors, "synthetic-queue-wait-extra: queue_wait may only add terminal cancel/block successors"), joined_errors(errors))
  end,

  test_known_god_states_inventory_remains_empty = function()
    local errors = m_rrc.restart_responsibility_inventory_errors(core)
    t.eq(#errors, 0, joined_errors(errors))
  end,

  test_known_god_state_with_duplicate_signature_still_fails_rule_6 = function()
    local rows = copy_rows(core.restart_transition_table())
    local by_state = rows_by_state(rows)
    local ready_signature = set_clean_signature(by_state.ready)
    local other = clean_row()
    other.from_state = "synthetic-ready-duplicate"
    other.to_states = { "synthetic-ready-done" }
    other.driving_queue = ready_signature.driving_queue
    other.liveness_class_id = ready_signature.liveness_class
    other.responsibility_signature = copy_value(ready_signature)
    other.responsibility_signature.successors = {
      {
        state = "synthetic-ready-done",
        output_variant = "done",
        postcondition_family = ready_signature.output_postcondition_family,
        monotonic = true,
      },
    }
    table.insert(rows, other)
    local errors = m_rrc.restart_responsibility_inventory_errors(core, rows)
    t.is_true(contains_error(errors, "synthetic-ready-duplicate: duplicate responsibility_signature shared with ready"), joined_errors(errors))
  end,

  test_invariant_6_rejects_old_fused_ready_dependency_hold = function()
    local row = copy_value(rows_by_state(core.restart_transition_table()).ready)
    row.liveness_class_id = "ready.actionable"
    row.watchdog = {
      mode = "live-defer",
      budget_ms = 45 * 60 * 1000,
    }
    row.defer = {
      kind = "release_gate",
      live_marker = "dependency-wait:v1",
      freshness_ms = 525600 * 60 * 1000,
      clear_fact = "dependency-release:v1",
      observed_fact = "dependency-wait-observed:v1",
      clear_opens_generation = true,
    }
    row.responsibility_signature.liveness_class = "ready.actionable"
    row.responsibility_signature.input_fact_family = "ready-base-preconditions partitioned by blockedBy empty/nonempty"
    row.responsibility_signature.output_postcondition_family = "implementation_kickoff and dependency-release-or-blocker-tracking"
    local errors = m_rrc.strict_restart_responsibility_contract_errors(core, { row })
    t.is_true(contains_error(errors, "ready: invariant #6 forbids dependency release_gate defer on actionable ready"), joined_errors(errors))
    t.is_true(contains_error(errors, "ready: invariant #6 forbids mixing implementation kickoff and dependency release/blocker tracking"), joined_errors(errors))
  end,

  test_signature_omitting_real_successor_fails_successor_set_check = function()
    local row = clean_row()
    row.from_state = "synthetic-omits-edge"
    row.to_states = { "synthetic-done", "synthetic-also-done" }
    local errors = m_rrc.strict_restart_responsibility_contract_errors(core, { row })
    t.is_true(contains_error(errors, "synthetic-omits-edge: responsibility_signature.successors missing row successor synthetic-also-done"), joined_errors(errors))
  end,

  test_real_restart_responsibility_table_passes_generation_entry_policy = function()
    local errors = m_rrc.restart_responsibility_inventory_errors(core)
    t.eq(#errors, 0, joined_errors(errors))
  end,

  test_legit_forward_decision_row_passes = function()
    local row = {
      from_state = "synthetic-decision",
      terminal = false,
      to_states = { "synthetic-forward-a", "synthetic-forward-b" },
      driving_queue = "synthetic_decision",
      liveness_class_id = "synthetic.decision",
      responsibility_signature = {
        receiver_kind = "synthetic-judge",
        driving_queue = "synthetic_decision",
        state_kind = "decision",
        liveness_class = "synthetic.decision",
        input_fact_family = "synthetic-input",
        output_postcondition_family = "synthetic-decision-result",
        decision_type = "synthetic-decision-result",
        phase_rank = 10,
        lineage_keys = { "state.version" },
        successors = {
          {
            state = "synthetic-forward-a",
            output_variant = "a",
            postcondition_family = "synthetic-decision-result",
            decision_type = "synthetic-decision-result",
            monotonic = true,
          },
          {
            state = "synthetic-forward-b",
            output_variant = "b",
            postcondition_family = "synthetic-decision-result",
            decision_type = "synthetic-decision-result",
            monotonic = true,
          },
        },
      },
    }
    local original_stage_rank = core.stage_rank
    core.stage_rank = function(state)
      if state == "synthetic-decision" then
        return 10
      end
      if state == "synthetic-forward-a" or state == "synthetic-forward-b" then
        return 20
      end
      return original_stage_rank(state)
    end
    local ok, errors = pcall(function()
      return m_rrc.strict_restart_responsibility_contract_errors(core, { row })
    end)
    core.stage_rank = original_stage_rank
    if not ok then
      error(errors)
    end
    t.eq(#errors, 0, joined_errors(errors))
  end,
  test_monotone_milestone_gate_signature_passes = function()
    local row = {
      from_state = "synthetic-milestone-gate",
      terminal = false,
      to_states = { "synthetic-forward-a" },
      driving_queue = "synthetic_gate",
      liveness_class_id = "synthetic.milestone",
      responsibility_signature = {
        receiver_kind = "synthetic-gate",
        driving_queue = "synthetic_gate",
        state_kind = "gate",
        gate_kind = "monotone_milestone",
        milestone_accessor = "devloop.gate.holds",
        milestone_implementation = "packages/github-devloop/core/pr_delegation.lua:M.ensure_pr_child",
        milestone = "pr-open",
        milestone_domain = "github-devloop-pr",
        liveness_class = "synthetic.milestone",
        input_fact_family = "synthetic-pr-origin",
        output_postcondition_family = "synthetic-pr-start-visible",
        decision_type = "synthetic-pr-start-visible",
        phase_rank = 600,
        lineage_keys = { "state.version", "pr-origin.proposal" },
        successors = {
          {
            state = "synthetic-forward-a",
            output_variant = "visible",
            postcondition_family = "synthetic-pr-start-visible",
            decision_type = "synthetic-pr-start-visible",
            monotonic = true,
          },
        },
      },
    }
    local original_stage_rank = core.stage_rank
    core.stage_rank = function(state)
      if state == "synthetic-milestone-gate" then
        return 600
      end
      if state == "synthetic-forward-a" then
        return 650
      end
      return original_stage_rank(state)
    end
    local ok, errors = pcall(function()
      return m_rrc.strict_restart_responsibility_contract_errors(core, { row })
    end)
    core.stage_rank = original_stage_rank
    if not ok then
      error(errors)
    end
    t.eq(#errors, 0, joined_errors(errors))
  end,
  test_monotone_milestone_gate_rejects_cursor_accessor = function()
    local row = copy_value(rows_by_state(core.restart_transition_table())["awaiting-pr"])
    row.responsibility_signature.gate_kind = "monotone_milestone"
    row.responsibility_signature.milestone_accessor = "current_state"
    row.responsibility_signature.milestone_implementation = "packages/github-devloop/core/pr_delegation.lua:M.ensure_pr_child"
    row.responsibility_signature.milestone = "pr-open"
    row.responsibility_signature.milestone_domain = "github-devloop-pr"
    row.responsibility_signature.current_state_accessor = "devloop.state.current_state"
    local errors = m_rrc.strict_restart_responsibility_contract_errors(core, { row })
    t.is_true(contains_error(errors, "awaiting-pr: monotone_milestone gate must declare an approved positive milestone accessor"), joined_errors(errors))
    t.is_true(contains_error(errors, "awaiting-pr: monotone_milestone gate must not declare current cursor accessors"), joined_errors(errors))
  end,
  test_monotone_milestone_gate_requires_domain = function()
    local row = copy_value(rows_by_state(core.restart_transition_table())["awaiting-pr"])
    row.responsibility_signature.gate_kind = "monotone_milestone"
    row.responsibility_signature.milestone_accessor = "devloop.state.reached"
    row.responsibility_signature.milestone_implementation = "packages/github-devloop/core/pr_delegation.lua:M.ensure_pr_child"
    row.responsibility_signature.milestone = "pr-open"
    row.responsibility_signature.milestone_domain = nil
    local errors = m_rrc.strict_restart_responsibility_contract_errors(core, { row })
    t.is_true(contains_error(errors, "awaiting-pr: monotone_milestone gate must declare milestone_domain"), joined_errors(errors))
  end,
  test_monotone_milestone_gate_requires_bound_implementation = function()
    local row = copy_value(rows_by_state(core.restart_transition_table())["awaiting-pr"])
    row.responsibility_signature.gate_kind = "monotone_milestone"
    row.responsibility_signature.milestone_accessor = "devloop.state.reached"
    row.responsibility_signature.milestone = "pr-open"
    row.responsibility_signature.milestone_domain = "github-devloop-pr"
    row.responsibility_signature.milestone_implementation = nil
    local errors = m_rrc.strict_restart_responsibility_contract_errors(core, { row })
    t.is_true(contains_error(errors, "awaiting-pr: monotone_milestone gate must declare milestone_implementation"), joined_errors(errors))
  end,
}
