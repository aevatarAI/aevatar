local core = require("core")
local operator_commands = require("devloop.operator_commands")
local M = setmetatable({}, { __index = core })

local required_negative_controls = {
  "001-release-replay-uses-split-version",
  "002-queue-wait-extra-successor",
  "003-dependency-hold-marker-families",
  "004-operator-waiver-does-not-write-raw-ready",
  "005-ready-replay-uses-inner-version",
  "006-ready-dependency-partition-boundary",
  "007-partial-write-idempotency-completeness",
}

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
    if type(row) == "table" and row.from_state ~= nil then
      by_state[row.from_state] = row
    end
  end
  return by_state
end

local function contains_error(errors, needle)
  local joined = table.concat(errors or {}, "\n")
  return joined:find(tostring(needle or ""), 1, true) ~= nil
end

local function append_errors(target, source)
  for _, err in ipairs(source or {}) do
    table.insert(target, err)
  end
end

local function has_declared_effect(row, expected)
  for _, kind in ipairs(row and row.effects and row.effects.kinds or {}) do
    if kind == expected then
      return true
    end
  end
  return false
end

local function operator_dependency_waiver_contract(opts)
  local options = opts or {}
  local proposal_id = options.proposal_id or "github-devloop/issue/owner/repo/42"
  local version = options.version or "ready/base/dependency-wait"
  local blocker_number = options.blocker_number or 60
  local command = options.command or {
    command = "dependency-waiver",
    key = "operator-command-key",
    blocker_number = blocker_number,
  }
  local source_ref = options.source_ref or {
    kind = "external",
    ref = "owner/repo#issue/42",
  }
  local expected_waiver_marker = M.dependency_waiver_marker(
    proposal_id,
    version,
    blocker_number,
    "operator-waiver"
  )
  local forbidden_ready_marker = M.state_marker(proposal_id, "ready", version)
  local request = nil
  if options.operator_dependency_waiver_request_body == nil then
    request = operator_commands.build_operator_issue_dependency_waiver_comment_request(
      core,
      options.repo or "owner/repo",
      options.issue_number or 42,
      command,
      proposal_id,
      version,
      blocker_number,
      source_ref
    )
  end
  return {
    body = options.operator_dependency_waiver_request_body or request.body or "",
    expected_waiver_marker = expected_waiver_marker,
    forbidden_ready_marker = forbidden_ready_marker,
  }
end

local function static_obligation_errors(rows, opts)
  local options = opts or {}
  local by_state = rows_by_state(rows or M.restart_transition_table())
  local marker_fields = options.marker_fields or M.restart_durable_marker_fields()
  local errors = {}

  local dependency_wait = by_state.dependency_wait
  if dependency_wait == nil then
    table.insert(errors, "dependency_wait: restart row must exist for ready-split release replay")
  else
    if tostring(dependency_wait.dedup_shape or ""):find("<state.version>", 1, true) == nil then
      table.insert(errors, "dependency_wait: release replay must preserve split state.version in devloop_ready dedup_shape")
    end
    if dependency_wait.payload_fields == nil
      or dependency_wait.payload_fields.dedup_key ~= "marker:state.version" then
      table.insert(errors, "dependency_wait: release replay dedup_key must derive from marker:state.version")
    end
    if not has_declared_effect(dependency_wait, "devloop_ready") then
      table.insert(errors, "dependency_wait: release replay must declare devloop_ready as a recoverable effect")
    end
  end

  for _, state in ipairs({ "implementing", "impl-failed" }) do
    local row = by_state[state]
    if row == nil then
      table.insert(errors, state .. ": restart row must exist for implementation backstop replay")
    elseif tostring(row.version_identity or ""):find("ready_payload_inner_version", 1, true) == nil then
      table.insert(errors, state .. ": ready replay must use ready_payload_inner_version")
    end
  end
  local impl_failed = by_state["impl-failed"]
  if impl_failed ~= nil
    and (impl_failed.payload_fields == nil
      or impl_failed.payload_fields.dedup_key ~= "marker:impl-failure.dedup") then
    table.insert(errors, "impl-failed: ready replay dedup_key must derive from marker:impl-failure.dedup")
  end

  for _, family in ipairs({ "dependency-wait", "dependency-cycle", "dependency-unresolvable" }) do
    if marker_fields[family] == nil then
      table.insert(errors, "dependency-hold: canonical marker family missing: " .. family)
    end
  end

  local waiver_contract = operator_dependency_waiver_contract(options)
  if waiver_contract.body:find(waiver_contract.expected_waiver_marker, 1, true) == nil then
    table.insert(errors, "dependency-waiver: operator path must write dependency-waiver marker")
  end
  if waiver_contract.body:find(waiver_contract.forbidden_ready_marker, 1, true) ~= nil then
    table.insert(errors, "dependency-waiver: operator path must not write raw state:v1 ready")
  end

  return errors
end

function M.competence_gate_errors(rows, opts)
  local errors = {}
  local source_rows = rows or M.restart_transition_table()
  append_errors(errors, M.liveness_contract_errors(source_rows))
  append_errors(errors, M.restart_field_coverage_errors(source_rows))
  append_errors(errors, M.restart_effect_contract_errors(source_rows, opts and opts.consumer_sources))
  append_errors(errors, static_obligation_errors(source_rows, opts))
  return errors
end

local function default_marker_fields_without(...)
  local fields = copy_value(M.restart_durable_marker_fields())
  for _, family in ipairs({ ... }) do
    fields[family] = nil
  end
  return fields
end

local challenge_fixtures = {
  {
    id = "001",
    negative_control = "001-release-replay-uses-split-version",
    bug_class = "strand",
    title = "migration version-suffix mismatch",
    expect = "dependency_wait: release replay must preserve split state.version in devloop_ready dedup_shape",
    errors = function()
      local rows = copy_rows(M.restart_transition_table())
      rows_by_state(rows).dependency_wait.dedup_shape = "ready/<base_version>"
      return M.competence_gate_errors(rows)
    end,
  },
  {
    id = "002",
    negative_control = "002-queue-wait-extra-successor",
    bug_class = "grader-weakening",
    title = "weakened queue_wait grader",
    expect = "ready: queue_wait may only add terminal cancel/block successors",
    errors = function()
      local rows = copy_rows(M.restart_transition_table())
      local ready = rows_by_state(rows).ready
      table.insert(ready.to_states, "fixing")
      table.insert(ready.responsibility_signature.successors, {
        state = "fixing",
        output_variant = "blanket_relaxed_queue_wait",
        failure = true,
        bump = true,
      })
      return M.competence_gate_errors(rows)
    end,
  },
  {
    id = "003",
    negative_control = "003-dependency-hold-marker-families",
    bug_class = "false-terminal",
    title = "unrecognized dependency-hold marker family",
    expect = "dependency-hold: canonical marker family missing: dependency-cycle",
    errors = function()
      return M.competence_gate_errors(M.restart_transition_table(), {
        marker_fields = default_marker_fields_without("dependency-cycle", "dependency-unresolvable"),
      })
    end,
  },
  {
    id = "004",
    negative_control = "004-operator-waiver-does-not-write-raw-ready",
    bug_class = "operator-path-partial-migration",
    title = "half-migrated operator waiver path",
    expect = "dependency-waiver: operator path must write dependency-waiver marker",
    errors = function()
      local proposal_id = "github-devloop/issue/owner/repo/42"
      local version = "ready/base/dependency-wait"
      return M.competence_gate_errors(M.restart_transition_table(), {
        operator_dependency_waiver_request_body = "waived\n" .. M.state_marker(proposal_id, "ready", version),
        proposal_id = proposal_id,
        version = version,
        blocker_number = 60,
      })
    end,
  },
  {
    id = "005",
    negative_control = "005-ready-replay-uses-inner-version",
    bug_class = "version-lineage-mismatch",
    title = "wrapper-vs-inner version mismatch",
    expect = "implementing: ready replay must use ready_payload_inner_version",
    errors = function()
      local rows = copy_rows(M.restart_transition_table())
      rows_by_state(rows).implementing.version_identity = "strip_transition_version_suffixes(state.version)"
      return M.competence_gate_errors(rows)
    end,
  },
  {
    id = "006",
    negative_control = "006-ready-dependency-partition-boundary",
    bug_class = "partition-boundary",
    title = "missing ready/dependency boundary edge case",
    expect = "ready: invariant #6 forbids dependency release_gate defer on actionable ready",
    errors = function()
      local rows = copy_rows(M.restart_transition_table())
      local ready = rows_by_state(rows).ready
      ready.watchdog = {
        mode = "live-defer",
        budget_ms = 45 * 60 * 1000,
      }
      ready.defer = {
        kind = "release_gate",
        live_marker = "dependency-wait:v1",
        freshness_ms = 525600 * 60 * 1000,
        clear_fact = "dependency-release:v1",
        observed_fact = "dependency-wait-observed:v1",
        clear_opens_generation = true,
      }
      ready.responsibility_signature.input_fact_family = "ready-base-preconditions partitioned by blockedBy empty/nonempty"
      ready.responsibility_signature.output_postcondition_family = "implementation_kickoff and dependency-release-or-blocker-tracking"
      return M.competence_gate_errors(rows)
    end,
  },
  {
    id = "007",
    negative_control = "007-partial-write-idempotency-completeness",
    bug_class = "partial-write-idempotency",
    title = "consensus_result partial-write idempotency strand",
    expect = "ready: multi-effect row must declare a completeness derivation",
    errors = function()
      local rows = copy_rows(M.restart_transition_table())
      rows_by_state(rows).ready.effects = {
        intent_count = 3,
        kinds = { "ready-state-marker", "ready-label", "devloop_ready" },
        completeness = "result effects must be derivable before idempotent skip",
      }
      return M.competence_gate_errors(rows)
    end,
  },
}

function M.competence_gate_challenge_definitions()
  local out = {}
  for _, challenge in ipairs(challenge_fixtures) do
    table.insert(out, {
      id = challenge.id,
      bug_class = challenge.bug_class,
      title = challenge.title,
      negative_control = challenge.negative_control,
      expect = challenge.expect,
    })
  end
  return out
end

local function ratio(numerator, denominator)
  if denominator == 0 then
    return 1
  end
  return numerator / denominator
end

function M.competence_gate_report()
  local clean_errors = M.competence_gate_errors()
  local results = {}
  local classes = {}
  local rejected_classes = {}
  local rejected = 0
  for _, challenge in ipairs(challenge_fixtures) do
    classes[challenge.bug_class] = true
    local errors = challenge.errors()
    local matched = contains_error(errors, challenge.expect)
    if matched then
      rejected = rejected + 1
      rejected_classes[challenge.bug_class] = true
    end
    table.insert(results, {
      id = challenge.id,
      bug_class = challenge.bug_class,
      title = challenge.title,
      negative_control = challenge.negative_control,
      expected_error = challenge.expect,
      rejected = matched,
      errors = errors,
    })
  end
  local class_count = 0
  for _ in pairs(classes) do
    class_count = class_count + 1
  end
  local rejected_class_count = 0
  for _ in pairs(rejected_classes) do
    rejected_class_count = rejected_class_count + 1
  end
  return {
    schema = "github-devloop.competence-gate-report.v1",
    framing = "evidence-carrying adversarial review for durable state-machine changes",
    clean_errors = clean_errors,
    challenges = results,
    negative_controls = copy_value(required_negative_controls),
    metrics = {
      challenge_recall = ratio(rejected, #challenge_fixtures),
      bug_class_recall = ratio(rejected_class_count, class_count),
      false_reject_rate = #clean_errors == 0 and 0 or 1,
      operator_escape_rate = ratio(#challenge_fixtures - rejected, #challenge_fixtures),
    },
  }
end

return M
