local M = {}

M.JSON_NULL = json.decode("null")
M.JSON_ARRAY_TAG = getmetatable(json.decode("[]"))
M.JSON_OBJECT_TAG = getmetatable(json.decode("{}"))

local function json_string(value)
  return '"' .. tostring(value)
    :gsub("\\", "\\\\")
    :gsub('"', '\\"')
    :gsub("\b", "\\b")
    :gsub("\f", "\\f")
    :gsub("\n", "\\n")
    :gsub("\r", "\\r")
    :gsub("\t", "\\t")
    :gsub("[%z\1-\31]", function(char)
      return string.format("\\u%04x", string.byte(char))
    end)
    .. '"'
end

local function array_length(value)
  local count = 0
  local maximum = 0
  for key, _ in pairs(value) do
    if type(key) ~= "number" or key < 1 or key % 1 ~= 0 then
      return nil
    end
    count = count + 1
    if key > maximum then
      maximum = key
    end
  end
  if maximum ~= count then
    return nil
  end
  return maximum
end

function M.json_array(values)
  local array = json.decode("[]")
  for index, value in ipairs(values or {}) do
    array[index] = value
  end
  return array
end

function M.is_json_array(value)
  return type(value) == "table" and getmetatable(value) == M.JSON_ARRAY_TAG
end

local function json_container_kind(value)
  if M.is_json_array(value) then
    return "array"
  end
  local length = array_length(value)
  if length ~= nil and length > 0 then
    return "array"
  end
  return "object"
end

function M.copy_value(value)
  if type(value) ~= "table" or value == M.JSON_NULL then
    return value
  end
  local length = array_length(value)
  local copy = {}
  if M.is_json_array(value) or (length ~= nil and length > 0) then
    copy = M.json_array()
  end
  for key, field in pairs(value) do
    copy[M.copy_value(key)] = M.copy_value(field)
  end
  return copy
end

function M.nullable(value)
  if value == nil then
    return M.JSON_NULL
  end
  return value
end

function M.canonical_json(value)
  if value == M.JSON_NULL then
    return "null"
  end
  local kind = type(value)
  if kind == "string" then
    return json_string(value)
  end
  if kind == "number" or kind == "boolean" then
    return tostring(value)
  end
  if kind ~= "table" then
    error("OLD observation canonical JSON cannot encode " .. kind)
  end

  if json_container_kind(value) == "array" then
    local length = array_length(value)
    if length == nil then
      error("OLD observation canonical JSON array must be a contiguous sequence")
    end
    local items = {}
    for index = 1, length do
      items[index] = M.canonical_json(value[index])
    end
    return "[" .. table.concat(items, ",") .. "]"
  end

  local keys = {}
  for key, _ in pairs(value) do
    if type(key) ~= "string" then
      error("OLD observation canonical JSON object key must be a string")
    end
    table.insert(keys, key)
  end
  table.sort(keys)
  local fields = {}
  for _, key in ipairs(keys) do
    table.insert(fields, json_string(key) .. ":" .. M.canonical_json(value[key]))
  end
  return "{" .. table.concat(fields, ",") .. "}"
end

function M.first_difference(actual, expected, path)
  if actual == expected then
    return nil
  end
  if actual == M.JSON_NULL or expected == M.JSON_NULL then
    return path .. " (actual=" .. M.canonical_json(actual) .. ", expected=" .. M.canonical_json(expected) .. ")"
  end
  if type(actual) ~= type(expected) then
    return path .. " (actual type=" .. type(actual) .. ", expected type=" .. type(expected) .. ")"
  end
  if type(actual) ~= "table" then
    return path .. " (actual=" .. M.canonical_json(actual) .. ", expected=" .. M.canonical_json(expected) .. ")"
  end
  local actual_container = json_container_kind(actual)
  local expected_container = json_container_kind(expected)
  if actual_container ~= expected_container then
    return path .. " (actual container=" .. actual_container
      .. ", expected container=" .. expected_container .. ")"
  end
  local keys = {}
  for key, _ in pairs(actual) do keys[key] = true end
  for key, _ in pairs(expected) do keys[key] = true end
  local ordered = {}
  for key, _ in pairs(keys) do table.insert(ordered, key) end
  table.sort(ordered, function(left, right) return tostring(left) < tostring(right) end)
  for _, key in ipairs(ordered) do
    if actual[key] == nil then
      return path .. "." .. tostring(key) .. " (missing from runtime capture)"
    end
    if expected[key] == nil then
      return path .. "." .. tostring(key) .. " (missing from committed record)"
    end
    local difference = M.first_difference(actual[key], expected[key], path .. "." .. tostring(key))
    if difference ~= nil then
      return difference
    end
  end
  return nil
end

function M.observe_department(opts)
  local config = opts.config or error("OLD observation config dependency is required")
  local devloop_logging = opts.devloop_logging or error("OLD observation logging dependency is required")
  local devloop_state = opts.devloop_state or error("OLD observation state dependency is required")
  local captured = {
    probes = M.json_array(),
    decisions = M.json_array(),
    applies = M.json_array(),
    raises = M.json_array(),
    lines = M.json_array(),
    handoff_direct_lookup_count = 0,
    liveness_read_count = 0,
  }
  local transition_kind = opts.transition_kind or "cyclic_transition_status"
  local original_transition = devloop_state[transition_kind]
  if type(original_transition) ~= "function" then
    error("OLD observation transition resolver is not callable: " .. tostring(transition_kind))
  end
  local original_decision = devloop_logging.log_cas_decision
  local original_apply = devloop_logging.log_apply
  local original_raise = devloop_logging.log_raise
  local original_line = devloop_logging.log_line
  local original_codex_runs = fkst.codex_runs
  local original_write_mode = config.write_mode

  config.write_mode = function()
    return opts.write_mode or "real"
  end
  fkst.codex_runs = function()
    captured.liveness_read_count = captured.liveness_read_count + 1
    local running = opts.codex_runs_for_read
    if type(running) == "function" then
      running = running(captured.liveness_read_count)
    end
    return { running = running or M.json_array() }
  end
  devloop_state[transition_kind] = function(current, from_states, to_state, incoming_version, target_version)
    local outcome = original_transition(current, from_states, to_state, incoming_version, target_version)
    table.insert(captured.probes, {
      current = M.copy_value(current),
      from_states = M.copy_value(from_states),
      to_state = to_state,
      incoming_version = incoming_version,
      target_version = target_version,
      outcome = outcome,
    })
    return outcome
  end
  devloop_logging.log_cas_decision = function(dept, proposal_id, current, from_state, to_state, outcome, reason)
    if dept == opts.dept and from_state == opts.from_state then
      table.insert(captured.decisions, {
        dept = dept,
        proposal_id = proposal_id,
        current = M.copy_value(current),
        from_state = from_state,
        to_state = to_state,
        outcome = outcome,
        reason = reason,
      })
    end
    return original_decision(dept, proposal_id, current, from_state, to_state, outcome, reason)
  end
  devloop_logging.log_apply = function(dept, proposal_id, to_state, version, labels, queues)
    if dept == opts.dept then
      table.insert(captured.applies, {
        proposal_id = proposal_id,
        to_state = to_state,
        version = version,
        labels = M.copy_value(labels),
        queues = M.copy_value(queues),
      })
    end
    return original_apply(dept, proposal_id, to_state, version, labels, queues)
  end
  devloop_logging.log_raise = function(dept, proposal_id, queue, payload)
    if dept == opts.dept then
      table.insert(captured.raises, {
        proposal_id = proposal_id,
        queue = queue,
        payload = M.copy_value(payload),
      })
    end
    return original_raise(dept, proposal_id, queue, payload)
  end
  devloop_logging.log_line = function(level, dept, proposal_id, event, fields)
    if dept == opts.dept then
      table.insert(captured.lines, {
        level = level,
        event = event,
        fields = M.copy_value(fields),
      })
    end
    return original_line(level, dept, proposal_id, event, fields)
  end

  local ok, result = pcall(opts.run)
  fkst.codex_runs = original_codex_runs
  config.write_mode = original_write_mode
  devloop_logging.log_line = original_line
  devloop_logging.log_raise = original_raise
  devloop_logging.log_apply = original_apply
  devloop_logging.log_cas_decision = original_decision
  devloop_state[transition_kind] = original_transition
  if not ok then
    error(result, 0)
  end
  return result, captured
end

function M.build_record(opts)
  local captured = opts.captured
  local result = opts.result
  local event = opts.event
  opts.t.eq(#captured.probes, 1, "real " .. opts.dept .. " CAS probe count")
  opts.t.is_true(#captured.decisions <= 1, "real " .. opts.dept .. " CAS decision count is zero only for defer")
  opts.t.eq(#captured.raises, #result.raises, "logger and run_fake raise counts")
  for index, raised in ipairs(result.raises) do
    opts.t.eq(M.canonical_json(captured.raises[index].payload), M.canonical_json(raised.payload), "captured raise payload " .. index)
    opts.t.eq(captured.raises[index].queue, raised.queue, "captured raise queue " .. index)
  end

  local probe = captured.probes[1]
  local decision = captured.decisions[1]
  local apply = captured.applies[1]
  local status, reason_code, cas_outcome = opts.outcome_status(probe, decision, apply, captured)
  local emitted_effects, observable_writes = opts.effects_from_raises(result.raises)
  local observation_id_parts = { opts.observation_prefix }
  if opts.observation_variant ~= nil then
    table.insert(observation_id_parts, tostring(opts.observation_variant))
  end
  for _, value in ipairs({
    probe.to_state,
    status,
    reason_code,
    apply and apply.to_state or "none",
  }) do
    table.insert(observation_id_parts, tostring(value))
  end
  local observation_id = table.concat(observation_id_parts, "/")
  local source_state = probe.from_states[1]
  if type(opts.source_state) == "function" then
    source_state = opts.source_state(probe, event)
  end
  local source_boundary = M.JSON_NULL
  if type(opts.source_boundary) == "function" then
    source_boundary = M.nullable(opts.source_boundary(probe, event))
  end
  local transition_kind = opts.transition_kind or "cyclic_transition_status"

  return {
    schema = "restart-old-behavior-observation.v2",
    observation_id = observation_id,
    owner = opts.owner,
    site = M.copy_value(opts.site),
    boundary = "writer",
    typed_intent = {
      kind = transition_kind,
      source_state = M.nullable(source_state),
      source_boundary = source_boundary,
      target = probe.to_state,
      cause_schema_id = event.payload.schema,
      generation_epoch = {
        current_version = M.nullable(probe.current.version),
        incoming_version = probe.incoming_version,
        target_version = M.nullable(probe.target_version),
      },
      lineage = opts.lineage(event.payload, probe, decision),
    },
    old_inputs = {
      current_fact = {
        state = M.nullable(probe.current.state),
        version = M.nullable(probe.current.version),
        stage_rank = M.nullable(probe.current.stage_rank),
      },
      caller_from_states = M.copy_value(probe.from_states),
      incoming_version = probe.incoming_version,
      target_version = M.nullable(probe.target_version),
      handoff_reference = M.JSON_NULL,
    },
    old_outcome = {
      status = status,
      reason_code = reason_code,
      cas_outcome = cas_outcome,
      emitted_effects = emitted_effects,
      observable_writes = observable_writes,
      handoff_direct_lookup_count = captured.handoff_direct_lookup_count,
      timeout_evidence_source = M.JSON_NULL,
    },
    evidence_refs = M.json_array({
      {
        kind = "runtime-cas-probe",
        ref = "devloop.state." .. transition_kind .. ":" .. tostring(probe.outcome),
      },
      {
        kind = "runtime-event-source",
        ref = tostring(event.payload.source_ref and event.payload.source_ref.ref),
      },
    }),
  }
end

return M
