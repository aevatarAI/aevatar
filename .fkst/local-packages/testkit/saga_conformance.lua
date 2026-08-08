-- testkit.saga_conformance: generic saga progress and idempotency test oracle.
local C = {}

local function write_classifier(case)
  if type(case) == "table" and type(case.is_write_class) == "function" then
    return case.is_write_class
  end
  if type(C.is_write_class) == "function" then
    return C.is_write_class
  end
  return nil
end

local function count_write_calls(start_index, is_write_class)
  local count = 0
  local calls = fkst.test.command_calls()
  for index = start_index + 1, #calls do
    if is_write_class(calls[index]) then
      count = count + 1
    end
  end
  return count
end

local function count_raises(result)
  if type(result) == "table" and type(result.raises) == "table" then
    return #result.raises
  end
  return 0
end

local function assert_delivery_succeeded(label, ok, result_or_err)
  if not ok then
    error(
      "testkit.saga_conformance: "
        .. label
        .. " delivery errored; idempotent no-op not proven: "
        .. tostring(result_or_err)
    )
  end
  if type(result_or_err) == "table"
    and result_or_err.exit_code ~= nil
    and tonumber(result_or_err.exit_code) ~= 0 then
    error(
      "testkit.saga_conformance: "
        .. label
        .. " delivery failed with exit_code="
        .. tostring(result_or_err.exit_code)
        .. "; idempotent no-op not proven"
    )
  end
end

local function validate_case(name, case)
  if type(case) ~= "table" then
    error("testkit.saga_conformance: " .. name .. " requires case")
  end
end

local function is_non_empty_string(value)
  return type(value) == "string" and value ~= ""
end

local function assert_non_empty_table(name, value)
  if type(value) ~= "table" or #value == 0 then
    error("testkit.saga_conformance: " .. name .. " requires non-empty table")
  end
end

local function validate_post_condition(step, condition)
  if type(condition) ~= "table" then
    error("testkit.saga_conformance: step " .. tostring(step.id) .. " has malformed post_condition")
  end
  if not is_non_empty_string(condition.id) then
    error("testkit.saga_conformance: step " .. tostring(step.id) .. " post_condition requires id")
  end
  if not is_non_empty_string(condition.kind) then
    error("testkit.saga_conformance: post_condition " .. tostring(condition.id) .. " requires kind")
  end
end

local function validate_step(step)
  if type(step) ~= "table" then
    error("testkit.saga_conformance: saga step is malformed")
  end
  if not is_non_empty_string(step.id) then
    error("testkit.saga_conformance: saga step requires id")
  end
  if not is_non_empty_string(step.effect) then
    error("testkit.saga_conformance: step " .. tostring(step.id) .. " requires effect")
  end
  if not is_non_empty_string(step.request_queue) then
    error("testkit.saga_conformance: step " .. tostring(step.id) .. " requires request_queue")
  end
  if type(step.post_conditions) ~= "table" or #step.post_conditions == 0 then
    error("testkit.saga_conformance: step " .. tostring(step.id) .. " requires non-empty post_conditions")
  end
  for _, condition in ipairs(step.post_conditions) do
    validate_post_condition(step, condition)
  end
end

function C.assert_external_effect_saga(saga_def)
  if type(saga_def) ~= "table" then
    error("testkit.saga_conformance: external-effect saga requires definition")
  end
  if not is_non_empty_string(saga_def.id) then
    error("testkit.saga_conformance: external-effect saga requires id")
  end
  assert_non_empty_table("external-effect saga steps", saga_def.steps)
  for _, step in ipairs(saga_def.steps) do
    validate_step(step)
  end
end

local function require_fragment(text, fragment, message)
  if tostring(text or ""):find(tostring(fragment or ""), 1, true) == nil then
    error(message)
  end
end

local function assert_graphql_add_blocked_by(condition, evidence)
  local command = tostring(evidence and (evidence.query or evidence.command) or "")
  require_fragment(
    command,
    "addBlockedBy",
    "testkit.saga_conformance: post_condition " .. tostring(condition.id) .. " requires addBlockedBy mutation"
  )
  local blocked_field = condition.blocked_field or "issueId"
  local blocking_field = condition.blocking_field or "blockingIssueId"
  require_fragment(
    command,
    blocked_field .. ":",
    "testkit.saga_conformance: post_condition " .. tostring(condition.id)
      .. " requires GraphQL field " .. tostring(blocked_field)
  )
  require_fragment(
    command,
    blocking_field .. ":",
    "testkit.saga_conformance: post_condition " .. tostring(condition.id)
      .. " requires GraphQL field " .. tostring(blocking_field)
  )
  for _, forbidden in ipairs(condition.forbidden_fields or {}) do
    if command:find(tostring(forbidden), 1, true) ~= nil then
      error(
        "testkit.saga_conformance: post_condition " .. tostring(condition.id)
          .. " forbids GraphQL field " .. tostring(forbidden)
      )
    end
  end
end

local function assert_trusted_comment_marker(condition, evidence)
  local body = evidence and evidence.body
  if type(body) ~= "string" or body == "" then
    error(
      "testkit.saga_conformance: post_condition " .. tostring(condition.id)
        .. " requires marker body evidence"
    )
  end
  if condition.marker ~= nil then
    require_fragment(
      body,
      condition.marker,
      "testkit.saga_conformance: post_condition " .. tostring(condition.id) .. " requires declared marker"
    )
  end
  for _, fragment in ipairs(condition.required_body_fragments or {}) do
    require_fragment(
      body,
      fragment,
      "testkit.saga_conformance: post_condition " .. tostring(condition.id)
        .. " requires marker fragment " .. tostring(fragment)
    )
  end
  if evidence.dedup_key ~= nil then
    require_fragment(
      body,
      'dedup="' .. tostring(evidence.dedup_key) .. '"',
      "testkit.saga_conformance: post_condition " .. tostring(condition.id) .. " requires dedup marker"
    )
  end
  if condition.issue_number_attr ~= nil and evidence.issue_number ~= nil then
    require_fragment(
      body,
      tostring(condition.issue_number_attr) .. '="' .. tostring(evidence.issue_number) .. '"',
      "testkit.saga_conformance: post_condition " .. tostring(condition.id) .. " requires issue number marker"
    )
  end
end

function C.assert_external_effect_post_condition(condition, evidence)
  if type(condition) ~= "table" then
    error("testkit.saga_conformance: external-effect post_condition requires definition")
  end
  if condition.kind == "github-add-blocked-by-edge" then
    assert_graphql_add_blocked_by(condition, evidence or {})
    return
  end
  if condition.kind == "trusted-comment-marker" then
    assert_trusted_comment_marker(condition, evidence or {})
    return
  end
  error("testkit.saga_conformance: unsupported external-effect post_condition kind " .. tostring(condition.kind))
end

function C.assert_progress(_t, case)
  validate_case("assert_progress", case)
  if type(case.first) ~= "function" then
    error("testkit.saga_conformance: assert_progress requires first")
  end
  local classifier = write_classifier(case)
  if classifier == nil then
    error("testkit.saga_conformance: assert_progress requires is_write_class")
  end
  local before = #fkst.test.command_calls()
  local result = case.first()
  if count_write_calls(before, classifier) + count_raises(result) == 0 then
    error("testkit.saga_conformance: assert_progress observed no write-class commands")
  end
end

function C.assert_idempotent(_t, case)
  validate_case("assert_idempotent", case)
  if type(case.first) ~= "function" then
    error("testkit.saga_conformance: assert_idempotent requires first")
  end
  if type(case.second) ~= "function" then
    error("testkit.saga_conformance: assert_idempotent requires second")
  end
  local classifier = write_classifier(case)
  if classifier == nil then
    error("testkit.saga_conformance: assert_idempotent requires is_write_class")
  end
  local before_first = #fkst.test.command_calls()
  local first_result = case.first()
  local first_effects = count_write_calls(before_first, classifier) + count_raises(first_result)
  if first_effects == 0 then
    error("testkit.saga_conformance: assert_idempotent: first delivery made no write-class effect; nothing to prove")
  end
  local before_second = #fkst.test.command_calls()
  local ok, second_result = pcall(case.second)
  assert_delivery_succeeded("second", ok, second_result)
  local second_effects = count_write_calls(before_second, classifier) + count_raises(second_result)
  if second_effects ~= 0 then
    error("testkit.saga_conformance: assert_idempotent observed effects on second delivery")
  end
end

return C
