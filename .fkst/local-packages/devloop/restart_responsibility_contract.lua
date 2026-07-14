local C = {}

local state_kinds = {
  queue_wait = true,
  worker = true,
  decision = true,
  gate = true,
  terminal_hold = true,
  budget_bounded_recovery = true,
}

local gate_kinds = {
  monotone_milestone = true,
  decision = true,
  current_route = true,
}

local milestone_accessors = {
  ["devloop.state.reached"] = true, ["devloop.gate.holds"] = true,
  reached = true,
  holds = true,
}

local known_god_states = {}

local function copy_table(map)
  local out = {}
  for key, value in pairs(map or {}) do
    if type(value) == "table" then
      local nested = {}
      for nested_key, nested_value in pairs(value) do
        nested[nested_key] = nested_value
      end
      out[key] = nested
    else
      out[key] = value
    end
  end
  return out
end

function C.known_god_states(M)
  return copy_table(known_god_states)
end

local function state_name(row)
  return tostring(row and (row.from_state or row.state) or "?")
end

local function non_empty_string(value)
  return type(value) == "string" and value ~= ""
end

local function list_count(values)
  if type(values) ~= "table" then
    return 0
  end
  local count = 0
  for _, _ in ipairs(values) do
    count = count + 1
  end
  return count
end

local function has_value(values, expected)
  for _, value in ipairs(values or {}) do
    if value == expected then
      return true
    end
  end
  return false
end

local function edge_by_state(signature)
  local by_state = {}
  for _, edge in ipairs(signature and signature.successors or {}) do
    if non_empty_string(edge.state) then
      by_state[edge.state] = edge
    end
  end
  return by_state
end

local function copy_edge(edge, state)
  local out = {}
  if type(edge) == "table" then
    for key, value in pairs(edge) do
      out[key] = value
    end
  end
  out.state = state
  return out
end

local function actual_successor_edges(row, signature)
  local by_state = edge_by_state(signature)
  local out = {}
  for _, next_state in ipairs(row.to_states or {}) do
    table.insert(out, copy_edge(by_state[next_state], next_state))
  end
  return out
end

local function rows_by_state(rows)
  local by_state = {}
  for _, row in ipairs(rows or {}) do
    if row ~= nil and row.from_state ~= nil then
      by_state[row.from_state] = row
    end
  end
  return by_state
end

local function edge_is_terminal(edge)
  return edge ~= nil and edge.terminal == true
end

local function edge_is_failure(edge)
  return edge ~= nil and edge.failure == true
end

local function edge_is_normal(edge)
  return not edge_is_terminal(edge) and not edge_is_failure(edge)
end

local function edge_is_generation_replacement(edge)
  return edge ~= nil
    and edge.replacement == true
    and edge.bump == true
end

local function edge_is_ready_dependency_regression(row, edge)
  return state_name(row) == "ready"
    and edge ~= nil
    and edge.state == "dependency_wait"
    and edge.failure == true
    and edge.bump == true
    and edge.regression == "blocker_reappeared"
    and edge.output_variant == "blocker_reappeared"
end

local function successor_list(subject)
  if type(subject) ~= "table" then
    return {}
  end
  return subject.successors or subject
end

local function normal_edges(subject)
  local out = {}
  for _, edge in ipairs(successor_list(subject)) do
    if edge_is_normal(edge) then
      table.insert(out, edge)
    end
  end
  return out
end

local function failure_edges(subject)
  local out = {}
  for _, edge in ipairs(successor_list(subject)) do
    if edge_is_failure(edge) then
      table.insert(out, edge)
    end
  end
  return out
end

local function missing_signature_field(signature, field)
  return signature[field] == nil
    or (type(signature[field]) == "string" and signature[field] == "")
    or (type(signature[field]) == "table" and next(signature[field]) == nil)
end

local function validate_signature_shape(M, row, signature, errors)
  local state = state_name(row)
  if type(signature.receiver_kind) == "table" then
    table.insert(errors, state .. ": responsibility_signature.receiver_kind must be exactly one receiver")
  elseif not non_empty_string(signature.receiver_kind) then
    table.insert(errors, state .. ": responsibility_signature.receiver_kind must be a non-empty string")
  end
  if signature.driving_queue ~= row.driving_queue then
    table.insert(errors, state .. ": responsibility_signature.driving_queue must match row.driving_queue")
  end
  if state_kinds[signature.state_kind] ~= true then
    table.insert(errors, state .. ": responsibility_signature.state_kind must be queue_wait, worker, decision, gate, terminal_hold, or budget_bounded_recovery")
  end
  if signature.liveness_class ~= row.liveness_class_id then
    table.insert(errors, state .. ": responsibility_signature.liveness_class must match row.liveness_class_id")
  end
  for _, field in ipairs({ "input_fact_family", "output_postcondition_family" }) do
    if missing_signature_field(signature, field) then
      table.insert(errors, state .. ": responsibility_signature." .. field .. " must be declared")
    end
  end
  if tonumber(signature.phase_rank) == nil then
    table.insert(errors, state .. ": responsibility_signature.phase_rank must be declared")
  elseif M.stage_rank ~= nil and tonumber(signature.phase_rank) ~= M.stage_rank(row.from_state) then
    table.insert(errors, state .. ": responsibility_signature.phase_rank must match stage_rank")
  end
  if type(signature.lineage_keys) ~= "table" or #signature.lineage_keys == 0 then
    table.insert(errors, state .. ": responsibility_signature.lineage_keys must be declared")
  end
  if type(signature.successors) ~= "table" then
    table.insert(errors, state .. ": responsibility_signature.successors must be declared")
  end
end

local function validate_successor_coverage(row, signature, errors)
  local state = state_name(row)
  local by_state = edge_by_state(signature)
  local declared_seen = {}
  for _, next_state in ipairs(row.to_states or {}) do
    if by_state[next_state] == nil then
      table.insert(errors, state .. ": responsibility_signature.successors missing row successor " .. tostring(next_state))
    end
  end
  for _, edge in ipairs(signature.successors or {}) do
    if not non_empty_string(edge.state) then
      table.insert(errors, state .. ": responsibility_signature.successor.state must be declared")
    elseif declared_seen[edge.state] == true then
      table.insert(errors, state .. ": responsibility_signature.successors duplicate state: " .. tostring(edge.state))
    elseif not has_value(row.to_states or {}, edge.state) then
      table.insert(errors, state .. ": responsibility_signature.successor is not in row.to_states: " .. tostring(edge.state))
    end
    if non_empty_string(edge.state) then
      declared_seen[edge.state] = true
    end
    if not non_empty_string(edge.output_variant) then
      table.insert(errors, state .. ": responsibility_signature.successor output_variant must be declared")
    end
    if edge.monotonic ~= true and edge.bump ~= true then
      table.insert(errors, state .. ": responsibility_signature.successor must declare monotonic or bump")
    end
  end
  return actual_successor_edges(row, signature)
end

local function terminal_class_state(row)
  if row == nil then
    return false
  end
  if row.terminal == true then
    return true
  end
  local signature = row.responsibility_signature
  if type(signature) == "table" and signature.state_kind == "terminal_hold" then
    return true
  end
  return row.from_state == "blocked" or row.from_state == "impl-failed"
end

local function validate_terminal_escape_targets(row, edges, all_rows, errors)
  local state = state_name(row)
  for _, edge in ipairs(edges or {}) do
    if edge_is_terminal(edge) and not terminal_class_state(all_rows[edge.state]) then
      table.insert(errors, state .. ": terminal-escape successor must point to a terminal-class state: " .. tostring(edge.state))
    end
  end
end

local function validate_output_family(row, signature, edges, errors)
  local state = state_name(row)
  for _, edge in ipairs(normal_edges(edges)) do
    if edge.postcondition_family ~= nil
      and edge.postcondition_family ~= signature.output_postcondition_family then
      table.insert(errors, state .. ": normal successor has unrelated output_postcondition_family: " .. tostring(edge.state))
    end
  end
end

local function validate_kind_fanout(M, row, signature, edges, errors)
  local state = state_name(row)
  local normal = normal_edges(edges)
  local failures = failure_edges(edges)
  if signature.state_kind == "queue_wait" then
    if #normal ~= 1 then
      table.insert(errors, state .. ": queue_wait state must declare exactly one normal successor")
    end
    for _, edge in ipairs(edges or {}) do
      if not edge_is_normal(edge)
        and edge_is_terminal(edge) ~= true
        and not edge_is_generation_replacement(edge)
        and not edge_is_ready_dependency_regression(row, edge) then
        table.insert(errors, state .. ": queue_wait may only add terminal cancel/block successors")
      end
    end
  elseif signature.state_kind == "worker" then
    if #normal ~= 1 then
      table.insert(errors, state .. ": worker state must declare exactly one success successor family")
    end
    if #failures > 1 then
      table.insert(errors, state .. ": worker state may declare at most one failure successor family")
    end
  elseif signature.state_kind == "decision" or signature.state_kind == "gate" then
    if #normal == 0 then
      table.insert(errors, state .. ": " .. tostring(signature.state_kind) .. " state must declare a decision successor")
    end
    if not non_empty_string(signature.decision_type) then
      table.insert(errors, state .. ": " .. tostring(signature.state_kind) .. " state must declare decision_type")
    end
    for _, edge in ipairs(normal) do
      if edge.decision_type ~= signature.decision_type then
        table.insert(errors, state .. ": decision successor must be a variant of decision_type " .. tostring(signature.decision_type))
      end
    end
  elseif signature.state_kind == "terminal_hold" then
    if list_count(signature.successors) > 0 or list_count(row.to_states) > 0 then
      table.insert(errors, state .. ": terminal_hold state must not declare autonomous successors")
    end
    if row.terminal ~= true then
      table.insert(errors, state .. ": terminal_hold state must be terminal")
    end
    if row.on_timeout ~= nil then
      table.insert(errors, state .. ": terminal_hold state must not declare on_timeout")
    end
    if row.operator_reentry ~= nil then
      table.insert(errors, state .. ": terminal_hold state must not declare operator_reentry")
    end
  elseif signature.state_kind == "budget_bounded_recovery" then
    if row.terminal == true then
      table.insert(errors, state .. ": budget_bounded_recovery state must be non-terminal")
    end
    if list_count(signature.successors) > 0 or list_count(row.to_states) > 0 then
      table.insert(errors, state .. ": budget_bounded_recovery state must not declare autonomous successors")
    end
    if signature.worker_dispatch == true then
      table.insert(errors, state .. ": budget_bounded_recovery state must not declare worker dispatch")
    end
    if signature.decision_type ~= nil or signature.decision_fanout == true then
      table.insert(errors, state .. ": budget_bounded_recovery state must not declare decision fanout")
    end
    local reentry = row.operator_reentry
    if type(reentry) ~= "table" or reentry.kind ~= "external_command" then
      table.insert(errors, state .. ": budget_bounded_recovery state must declare external operator_reentry")
    elseif reentry.not_autonomous_successor ~= true or reentry.resets_budget ~= true then
      table.insert(errors, state .. ": budget_bounded_recovery operator_reentry must be external and reset budget")
    end
    if type(row.watchdog) ~= "table" or row.watchdog.mode ~= "row-budget-bounds-receiver" then
      table.insert(errors, state .. ": budget_bounded_recovery watchdog.mode must be row-budget-bounds-receiver")
    end
    local decompose_queue = type(M.decompose_package_queue) == "function" and M.decompose_package_queue() or "devloop_decompose"
    local escape = signature.watchdog_escape
    if type(escape) ~= "table" or escape.kind ~= "watchdog_escape" or escape.queue ~= decompose_queue then
      table.insert(errors, state .. ": budget_bounded_recovery must declare decompose watchdog escape")
    end
    if type(row.on_timeout) ~= "table" or row.on_timeout.queue ~= decompose_queue then
      table.insert(errors, state .. ": budget_bounded_recovery on_timeout must target decompose queue")
    end
  end
end

local function validate_gate_kind(M, row, signature, errors)
  local state = state_name(row)
  if signature.gate_kind == nil then
    return
  end
  if gate_kinds[signature.gate_kind] ~= true then
    table.insert(errors, state .. ": responsibility_signature.gate_kind must be monotone_milestone, decision, or current_route")
    return
  end
  if signature.gate_kind ~= "monotone_milestone" then
    return
  end
  if signature.state_kind ~= "gate" then
    table.insert(errors, state .. ": monotone_milestone gate_kind requires state_kind=gate")
  end
  if milestone_accessors[signature.milestone_accessor] ~= true then
    table.insert(errors, state .. ": monotone_milestone gate must declare an approved positive milestone accessor")
  end
  if not non_empty_string(signature.milestone) then
    table.insert(errors, state .. ": monotone_milestone gate must declare milestone")
  elseif M.stage_rank ~= nil and M.stage_rank(signature.milestone) == 0 then
    table.insert(errors, state .. ": monotone_milestone gate milestone must be a lifecycle state")
  end
  if not non_empty_string(signature.milestone_domain) then
    table.insert(errors, state .. ": monotone_milestone gate must declare milestone_domain")
  end
  if not non_empty_string(signature.milestone_implementation) then
    table.insert(errors, state .. ": monotone_milestone gate must declare milestone_implementation")
  end
  if type(signature.lineage_keys) ~= "table" or #signature.lineage_keys == 0 then
    table.insert(errors, state .. ": monotone_milestone gate must declare lineage_keys")
  end
  if signature.cursor_accessor ~= nil
    or signature.current_accessor ~= nil
    or signature.current_state_accessor ~= nil then
    table.insert(errors, state .. ": monotone_milestone gate must not declare current cursor accessors")
  end
end

local function validate_phase_monotonicity(M, row, signature, edges, errors)
  local state = state_name(row)
  local current_rank = tonumber(signature.phase_rank)
  for _, edge in ipairs(edges or {}) do
    local next_rank = M.stage_rank and M.stage_rank(edge.state) or nil
    if current_rank ~= nil and next_rank ~= nil and next_rank < current_rank and edge.bump ~= true then
      table.insert(errors, state .. ": backward successor requires generation bump: " .. tostring(edge.state))
    end
  end
end

local function generation_entry_requires_bump(policy, from_state)
  if policy == nil then
    return false
  end
  if policy == "always" then
    return true
  end
  if type(policy) ~= "table" then
    return false
  end
  if policy.birth_from == from_state then
    return false
  end
  return policy.reentry_bump == true
end

local function validate_generation_entry_policy(row, edges, all_rows, errors)
  local state = state_name(row)
  for _, edge in ipairs(edges or {}) do
    local target = all_rows[edge.state]
    -- Target rows are the sole source for generation-entry requirements.
    local policy = target and target.generation_entry
    if generation_entry_requires_bump(policy, state) and edge.bump ~= true then
      table.insert(errors, state .. ": generation-bearing successor requires generation bump: " .. tostring(edge.state))
    end
  end
end

local function canonical_value(value)
  if type(value) ~= "table" then
    return tostring(value)
  end
  local keys = {}
  for key, _ in pairs(value) do
    table.insert(keys, key)
  end
  table.sort(keys, function(a, b) return tostring(a) < tostring(b) end)
  local parts = {}
  for _, key in ipairs(keys) do
    table.insert(parts, tostring(key) .. "=" .. canonical_value(value[key]))
  end
  return "{" .. table.concat(parts, ",") .. "}"
end

local function responsibility_fingerprint(signature)
  return table.concat({
    canonical_value(signature.receiver_kind),
    canonical_value(signature.driving_queue),
    canonical_value(signature.state_kind),
    canonical_value(signature.liveness_class),
    canonical_value(signature.input_fact_family),
    canonical_value(signature.output_postcondition_family),
    canonical_value(signature.phase_rank),
    canonical_value(signature.lineage_keys),
  }, "|")
end

local function validate_unique_signature(row, signature, seen, errors)
  local fingerprint = responsibility_fingerprint(signature)
  local previous = seen[fingerprint]
  if previous ~= nil then
    table.insert(errors, state_name(row) .. ": duplicate responsibility_signature shared with " .. tostring(previous))
    return
  end
  seen[fingerprint] = row.from_state
end

local function validate_blocked_by_partition_invariant(row, signature, errors)
  local state = state_name(row)
  local input = tostring(signature.input_fact_family or "")
  local output = tostring(signature.output_postcondition_family or "")
  local defer = row and row.defer or nil
  if input:find("blocked", 1, true) ~= nil
    and input:find("no-open-blockers", 1, true) ~= nil then
    table.insert(errors, state .. ": invariant #6 forbids a hold state partitioned by issue.blockedBy empty/nonempty")
  end
  if output:find("implementation_kickoff", 1, true) ~= nil
    and output:find("dependency", 1, true) ~= nil then
    table.insert(errors, state .. ": invariant #6 forbids mixing implementation kickoff and dependency release/blocker tracking")
  end
  if state == "ready" and type(defer) == "table" and defer.kind == "release_gate" then
    table.insert(errors, state .. ": invariant #6 forbids dependency release_gate defer on actionable ready")
  end
  if state == "ready" and tostring(row.liveness_class_id or "") ~= "actionable_kickoff" then
    table.insert(errors, state .. ": invariant #6 requires ready to be pure actionable_kickoff")
  end
  if state == "dependency_wait" and tostring(row.liveness_class_id or "") ~= "dependency_held_blocker_bound" then
    table.insert(errors, state .. ": invariant #6 requires dependency_wait to be dependency_held_blocker_bound")
  end
end

local function validate_row(M, row, seen, all_rows, errors)
  if row == nil or row.terminal == true then
    return
  end
  local state = state_name(row)
  local signature = row.responsibility_signature
  if type(signature) ~= "table" then
    table.insert(errors, state .. ": non-terminal row must declare responsibility_signature")
    return
  end
  validate_signature_shape(M, row, signature, errors)
  local actual_edges = validate_successor_coverage(row, signature, errors)
  validate_terminal_escape_targets(row, actual_edges, all_rows, errors)
  validate_output_family(row, signature, actual_edges, errors)
  validate_kind_fanout(M, row, signature, actual_edges, errors)
  validate_gate_kind(M, row, signature, errors)
  validate_phase_monotonicity(M, row, signature, actual_edges, errors)
  validate_generation_entry_policy(row, actual_edges, all_rows, errors)
  validate_blocked_by_partition_invariant(row, signature, errors); if signature.state_kind == "worker" then local contract = row.span_contract; if type(contract) ~= "table" then table.insert(errors, state .. ": worker row must declare span_contract") else for _, field in ipairs({ "department", "durable_start_marker", "spawn_predecessor" }) do if not non_empty_string(contract[field]) then table.insert(errors, state .. ": span_contract." .. field .. " must be declared") end end; if tostring(contract.durable_start_marker or ""):find(":v1", 1, true) == nil then table.insert(errors, state .. ": span_contract.durable_start_marker must name a durable marker family") end; if contract.spawn_function ~= nil and not non_empty_string(contract.spawn_function) then table.insert(errors, state .. ": span_contract.spawn_function must be a non-empty string when declared") end end end
  validate_unique_signature(row, signature, seen, errors)
end

function C.strict_restart_responsibility_contract_errors(M, rows)
  local errors = {}
  local seen = {}
  local source_rows = rows or M.restart_transition_table()
  local all_rows = rows_by_state(M.restart_transition_table())
  for state, row in pairs(rows_by_state(source_rows)) do
    all_rows[state] = row
  end
  for _, row in ipairs(source_rows) do
    validate_row(M, row, seen, all_rows, errors)
  end
  return errors
end

local function error_state(error_text)
  return tostring(error_text or ""):match("^([^:]+):")
end

function C.restart_responsibility_inventory_errors(M, rows, inventory)
  local strict_errors = C.strict_restart_responsibility_contract_errors(M, rows)
  local listed = inventory or known_god_states
  local observed_listed_errors = {}
  local errors = {}
  for _, err in ipairs(strict_errors) do
    local state = error_state(err)
    local expected = state ~= nil and listed[state] or nil
    if type(expected) == "table" and expected[err] ~= nil then
      observed_listed_errors[err] = true
      goto continue
    end
    table.insert(errors, err)
    ::continue::
  end
  for state, expected_errors in pairs(listed) do
    for expected_error, _ in pairs(expected_errors or {}) do
      if observed_listed_errors[expected_error] ~= true then
        table.insert(errors, state .. ": listed known_god_states entry is stale and must be removed: " .. expected_error)
      end
    end
  end
  return errors
end

function C.responsibility_contract_inventory_is_listed_violation(M, state, errors)
  for _, err in ipairs(errors or {}) do
    if error_state(err) == state then
      return true
    end
  end
  return false
end

return C
