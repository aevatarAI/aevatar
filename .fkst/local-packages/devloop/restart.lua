local parsers_misc = require("devloop.parsers.misc")
local conv_rounds = require("devloop.convergence.rounds")
local S = {}
local registry = require("workflow.registry")
local transition_version = require("contract.transition_version")

local source_ref_derivations = {
  entity = true,
  issue = true,
  pr = true,
}

local payload_derivations = {
  ["literal:github-devloop.fixing.v1"] = true,
  ["dedup:replayed-fixing"] = true,
  ["comment_body:fix-feedback"] = true,
}

local function fact(family, freshness)
  return { family = family, freshness = freshness }
end

local function advancing_fact(fact_family, successor, observe_surfaces, source_ref_derivation)
  return {
    fact_family = fact_family,
    successor = successor,
    observe_surfaces = observe_surfaces,
    source_ref_derivation = source_ref_derivation,
  }
end

local function obligation(kinds, exits)
  return {
    kinds = kinds,
    exits = exits,
  }
end

local function effect(kinds, completeness, completeness_derivation)
  local declared_kinds = kinds
  if type(kinds) ~= "table" then
    declared_kinds = {}
    for index = 1, tonumber(kinds) or 0 do
      declared_kinds[index] = "effect-" .. tostring(index)
    end
  end
  return {
    intent_count = #declared_kinds,
    kinds = declared_kinds,
    completeness = completeness,
    completeness_derivation = completeness_derivation,
  }
end

local function budget(minutes, receiver_max_work_justification)
  return {
    minutes = minutes,
    receiver_max_work_justification = receiver_max_work_justification,
  }
end

local function timeout(queue)
  return {
    action = "redrive",
    queue = queue,
    escalate_after_attempts = 3,
    on_escalate = {
      action = "force-terminate",
      terminal_state = "blocked",
      reason = "state-output-obligation-timeout",
    },
  }
end

local function liveness(contract)
  return contract
end

local function watchdog(mode, minutes)
  return {
    mode = mode,
    budget_ms = tonumber(minutes) * 60 * 1000,
  }
end

local function actionable_epoch(source)
  return {
    source = source,
    generation_source = "same_as_actionable_epoch",
  }
end

local function responsibility_signature(signature)
  return signature
end

local transition_helpers = {
  fact = fact,
  advancing_fact = advancing_fact,
  obligation = obligation,
  effect = effect,
  budget = budget,
  timeout = timeout,
  liveness = liveness,
  watchdog = watchdog,
  actionable_epoch = actionable_epoch,
  responsibility_signature = responsibility_signature, span_contract = responsibility_signature,
}

local function restart_deps(M, resolved)
  resolved = resolved or {}
  local package_name = M.restart_package_name or "github-devloop"
  local ops = {
    version_fix_round = M.version_fix_round,
    fixing_version_matches_link = M.fixing_version_matches_link,
    latest_complete_converge_round = M.latest_complete_converge_round,
    liveness_heartbeat_version = M.liveness_heartbeat_version,
    liveness_signal_producer_contract = M.liveness_signal_producer_contract,
    stage_rank = M.stage_rank,
    decompose_package_queue = M.decompose_package_queue,
  }
  for key, value in pairs(resolved.ops or {}) do
    ops[key] = value
  end
  return {
    config = {
      registry_package_name = package_name,
      restart_package_name = package_name,
      restart_consumer_sources = M.restart_consumer_sources,
      limits = {
        _max_blocking_gap_len = M._max_blocking_gap_len,
        _max_dedup_len = M._max_dedup_len,
        _max_key_len = M._max_key_len,
      },
      spec = {
        transitions_label = resolved.transitions_label,
        transitions_index = assert(resolved.transitions_index, package_name .. ": missing resolved restart transitions_index"),
        transitions = assert(resolved.transitions, package_name .. ": missing resolved restart transitions"),
        marker_fields = resolved.marker_fields,
        replay_payload_fields = resolved.replay_payload_fields,
      },
    },
    ops = ops,
  }
end

local REQUIRED_OPS = {
  "version_fix_round",
  "fixing_replay_feedback_fact",
  "fixing_version_matches_link",
  "latest_complete_converge_round",
  "liveness_heartbeat_version",
  "liveness_signal_producer_contract",
  "review_meta_replay_fact",
  "review_meta_replay_fact_from_state",
  "stage_rank",
  "decompose_package_queue",
}

local function required_op_set()
  local set = {}
  for _, key in ipairs(REQUIRED_OPS) do
    set[key] = true
  end
  return set
end

local RESTART_OP_KEYS = required_op_set()

local function assert_restart_ops_table(ops)
  if type(ops) ~= "table" then
    error("restart kernel: ops must be a table")
  end
end

local function validate_restart_ops(ops, used_ops)
  for _, key in ipairs(REQUIRED_OPS) do
    if used_ops[key] and ops[key] == nil then
      error("restart kernel: missing op " .. key)
    end
  end
end

local function restart_build_env(values, ops, used_ops)
  return setmetatable({}, {
    __index = function(_, key)
      if RESTART_OP_KEYS[key] then
        used_ops[key] = true
        if ops[key] == nil then
          error("restart kernel: missing op " .. key)
        end
        return ops[key]
      end
      return values[key]
    end,
  })
end

function S.build_kernel(deps)
  local config = assert(deps and deps.config, "restart kernel missing config")
  local ops = assert(deps.ops, "restart kernel missing ops")
  assert_restart_ops_table(ops)
  local limits = config.limits or {}
  local spec = assert(config.spec, "restart kernel missing spec")
  local build_values = {
    _max_blocking_gap_len = limits._max_blocking_gap_len,
    _max_dedup_len = limits._max_dedup_len,
    _max_key_len = limits._max_key_len,
    restart_package_name = config.restart_package_name,
    restart_consumer_sources = config.restart_consumer_sources,
  }
  local used_ops = {}
  local build_env = restart_build_env(build_values, ops, used_ops)
  local transition_table = registry.build_indexed_array(
    spec.transitions_label or "restart.transitions",
    spec.transitions_index,
    spec.transitions,
    "from_state",
    build_env,
    transition_helpers,
    config.registry_package_name
  )
  validate_restart_ops(ops, used_ops)
  return {
    transition_table = transition_table,
    marker_fields = spec.marker_fields,
    replay_payload_fields = spec.replay_payload_fields,
    restart_transition_table = function()
      return transition_table
    end,
  }
end

function S.transition_table(M, resolved)
return S.build_kernel(restart_deps(M, resolved)).transition_table
end

function S.install(M, resolved)
resolved = resolved or {}

local package_name = M.restart_package_name or "github-devloop"
local default_consumer_sources = M.restart_consumer_sources or {}

local kernel = nil
local transition_table = nil
local marker_fields = nil
local required_replay_payload_fields = nil
local audit_by_state = {}

local function field_reference_error(reference)
  local marker_family, attr = tostring(reference or ""):match("^marker:([^%.]+)%.(.+)$")
  if marker_family ~= nil then
    if marker_fields[marker_family] == nil then
      return "unknown marker family " .. marker_family
    end
    if marker_fields[marker_family][attr] ~= true then
      return "unknown marker attr " .. marker_family .. "." .. attr
    end
    return nil
  end
  local derivation = tostring(reference or ""):match("^source_ref:(.+)$")
  if derivation ~= nil then
    if source_ref_derivations[derivation] == true then
      return nil
    end
    return "unknown source_ref derivation " .. derivation
  end
  if payload_derivations[tostring(reference or "")] == true then
    return nil
  end
  return "unsupported payload field source " .. tostring(reference)
end

function M.restart_field_coverage_errors(rows)
  local errors = {}
  for _, row in ipairs(rows or transition_table) do
    local required_fields = required_replay_payload_fields[row.from_state] or {}
    for field, reason in pairs(required_fields) do
      if (row.payload_fields or {})[field] == nil then
        table.insert(errors, tostring(row.from_state or "?") .. "." .. tostring(field) .. ": missing required replay payload field: " .. tostring(reason))
      end
    end
    for field, reference in pairs(row.payload_fields or {}) do
      local err = field_reference_error(reference)
      if err ~= nil then
        table.insert(errors, tostring(row.from_state or "?") .. "." .. tostring(field) .. ": " .. err)
      end
    end
  end
  return errors
end

local function source_contains_any(paths, needle)
  if needle == nil or needle == "" then
    return false
  end
  for _, path in ipairs(paths or {}) do
    local ok, text = pcall(file.read, path)
    if ok and tostring(text or ""):find(tostring(needle), 1, true) ~= nil then
      return true
    end
  end
  return false
end

function M.restart_effect_contract_errors(rows, consumer_sources)
  local errors = {}
  local sources = consumer_sources or default_consumer_sources
  for _, row in ipairs(rows or transition_table) do
    local effects = row.effects or {}
    local kinds = effects.kinds or {}
    local count = tonumber(effects.intent_count) or #kinds
    if count > 1 then
      if type(kinds) ~= "table" or #kinds ~= count then
        table.insert(errors, tostring(row.from_state or "?") .. ": multi-effect row must enumerate declared effects")
      end
      if type(effects.completeness_derivation) ~= "string" or effects.completeness_derivation == "" then
        table.insert(errors, tostring(row.from_state or "?") .. ": multi-effect row must declare a completeness derivation")
      elseif not source_contains_any(sources, effects.completeness_derivation) then
        table.insert(errors, tostring(row.from_state or "?") .. ": completeness derivation is not called by consumer sources")
      end
    end
  end
  return errors
end

function M.latest_complete_converge_round(comments, proposal_id, _base_version, _source_ref)
  local latest = nil
  local facts = conv_rounds.converge_round_facts_for_proposal(comments, proposal_id)
  for _, fact in ipairs(facts) do
    if fact.narrowed_question ~= nil
      and fact.narrowed_question ~= ""
      and type(fact.angle_digests) == "table"
      and #fact.angle_digests > 0
      and (latest == nil or fact.round > latest.round) then
      latest = fact
    end
  end
  return latest
end

function M.fixing_version_matches_link(issue_version, link_version)
  local current = tostring(issue_version or "")
  local linked = tostring(link_version or "")
  if current == linked or M._strip_latest_fix_version_suffix(current) == linked then
    return true
  end
  local current_base = transition_version.strip_suffixes(current)
  local linked_base = transition_version.strip_suffixes(linked)
  if current_base == "" or linked_base == "" then
    return false
  end
  return transition_version.safe_version_segment(current_base) == transition_version.safe_version_segment(linked_base)
end

local deps = restart_deps(M, resolved)
kernel = S.build_kernel(deps)
transition_table = kernel.transition_table
marker_fields = assert(kernel.marker_fields, package_name .. ": missing resolved restart marker_fields")
required_replay_payload_fields = assert(kernel.replay_payload_fields, package_name .. ": missing resolved restart replay_payload_fields")

for _, row in ipairs(transition_table) do
  audit_by_state[row.from_state] = row
end

function M.restart_completeness_audit()
  local rows = {}
  for _, row in ipairs(transition_table) do
    table.insert(rows, {
      state = row.from_state,
      marker_facts = row.marker_facts,
      kickoff = row.kickoff,
      replay = row.replay,
    })
  end
  return rows
end

function M.restart_completeness_audit_for_state(state)
  return audit_by_state[state]
end

function M.restart_transition_table()
  return kernel.restart_transition_table()
end

function M.restart_durable_marker_fields()
  return marker_fields
end

function M.restart_source_ref_derivations()
  return source_ref_derivations
end

function M.restart_required_replay_payload_fields()
  return required_replay_payload_fields
end

end

return S
