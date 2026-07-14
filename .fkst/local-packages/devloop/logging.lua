local devloop_base = require("devloop.base")
local parsers_misc = require("devloop.parsers.misc")
local S = {}
local error_facts = require("contract.error_facts")
local logging = require("workflow.logging")
local config = require("devloop.config")

function S.install(M)

function M.error_fingerprint(error_class, queue, dept, message)
  return error_facts.error_fingerprint(error_class, queue, dept, message)
end

function M.error_class_from_message(message)
  local text = tostring(message or "")
  if text:match("github%-devloop: .-codex failed:") then
    return "codex-failed"
  end
  local class = text:match("github%-devloop: [^:]+ failed: ([%w%-]+):")
    or text:match("github%-devloop: ([%w%-]+):")
    or text:match("github%-devloop: ([%w%-]+) failed:")
    or text:match("github%-devloop: ([%w%-]+) retrying")
  return class or "caught-failure"
end

function M.log_error_fact(level, dept, proposal_id, tag, error_class, queue, message, context)
  local fields = error_facts.error_fact_fields(error_class, queue, dept, message, context)
  table.insert(fields, "queue=" .. error_facts.one_line(queue))
  table.insert(fields, "error=" .. error_facts.one_line(message))
  M.log_line(level or "error", dept, proposal_id, tag or "FAILURE", fields)
end

local event_source_ref = error_facts.event_source_ref

function M.wrap_pipeline_failure(dept, fn)
  return function(event)
    local ok, err = pcall(fn, event)
    if ok then
      return err
    end
    local payload = type(event) == "table" and event.payload or nil
    local proposal_id = type(payload) == "table" and payload.proposal_id or "unknown"
    M.log_error_fact("error", dept, proposal_id, "FAILURE", M.error_class_from_message(err), type(event) == "table" and event.queue or nil, err, {
      source_ref = event_source_ref(event),
      attempt = type(event) == "table" and event.attempt or nil,
    })
    error(err, 0)
  end
end

function M.log_line(level, dept, proposal_id, tag, fields)
  return logging.log_line("github-devloop", level, dept, proposal_id, tag, fields)
end

function M.log_entry(dept, event, proposal_id, dedup_key)
  return logging.log_entry("github-devloop", dept, event, proposal_id, dedup_key)
end

M.payload_field = logging.payload_field

function M.log_cas_decision(dept, proposal_id, current, from_state, to_state, outcome, reason)
  local current_state = current
  local current_version = type(current) == "table" and current.version or nil
  if type(current) == "table" then
    current_state = current.state
  end
  M.log_line("info", dept, proposal_id, "CAS", {
    "current_state=" .. tostring(current_state or "unmanaged"),
    "current_version=" .. tostring(current_version or ""),
    "current_source=trusted-marker",
    "transition=" .. tostring(from_state or "unknown") .. "->" .. tostring(to_state or "unknown"),
    "outcome=" .. tostring(outcome or "unknown"),
    "reason=" .. error_facts.one_line(reason or ""),
  })
end

function M.log_apply(dept, proposal_id, to_state, version, labels, events)
  local add_labels = labels and labels.add or {}
  local remove_labels = labels and labels.remove or {}
  M.log_line("info", dept, proposal_id, "APPLY", {
    "state_marker_state=" .. tostring(to_state or "none"),
    "state_marker_version=" .. tostring(version or ""),
    "set_exclusive_add=" .. table.concat(add_labels, ","),
    "set_exclusive_remove=" .. table.concat(remove_labels, ","),
    "raised=" .. table.concat(events or {}, ","),
  })
end

function M.log_outbound(dept, proposal_id, queue, request)
  M.log_line("info", dept, proposal_id, "OUTBOUND", {
    "mode=" .. config.write_mode(M),
    "queue=" .. tostring(queue or ""),
    "repo=" .. tostring(request and request.repo or ""),
    "issue=" .. tostring(request and request.issue_number or ""),
    "branch=" .. tostring(request and request.branch or ""),
    "pr=" .. tostring(request and request.pr_number or ""),
    "dedup_key=" .. tostring(request and request.dedup_key or ""),
  })
end

function M.log_raise(dept, proposal_id, queue, payload)
  if queue == "github-proxy.github_issue_label_request"
    or queue == "github-proxy.github_issue_comment_request"
    or queue == "github-proxy.github_pr_comment_request"
    or queue == "github-proxy.github_issue_create_request" then
    M.log_outbound(dept, proposal_id, queue, payload)
  end
  raise(queue, payload)
end

function M.log_codex_start(dept, proposal_id, role)
  M.log_line("info", dept, proposal_id, "CODEX", {
    "phase=start",
    "role=" .. tostring(role or dept),
  })
end

function M.log_codex_result(dept, proposal_id, role, result, parsed, failure, context)
  local level = failure and "error" or "info"
  local fields = {
    "phase=result",
    "role=" .. tostring(role or dept),
    "exit_code=" .. tostring(type(result) == "table" and result.exit_code or "nil"),
  }
  if parsed ~= nil then
    table.insert(fields, "parsed=" .. error_facts.one_line(parsed))
  end
  if failure ~= nil then
    for _, field in ipairs(error_facts.error_fact_fields(
      context and context.error_class or "codex-failed",
      context and context.queue,
      dept,
      failure,
      context
    )) do
      table.insert(fields, field)
    end
    table.insert(fields, "failure=" .. error_facts.one_line(failure))
  end
  M.log_line(level, dept, proposal_id, "CODEX", fields)
end

function M.log_forged_markers(dept, proposal_id, comments)
  if type(comments) ~= "table" then
    return
  end

  local marker_pattern = "<!%-%- fkst:github%-devloop:([%w%-]+):v1.-%-%->"
  for _, comment in ipairs(comments) do
    if not parsers_misc._is_trusted_comment(M, comment) then
      for marker, marker_kind in parsers_misc._comment_body(M, comment):gmatch("(" .. marker_pattern .. ")") do
        local marker_proposal = marker:match('proposal="([^"]+)"')
        if marker_proposal == proposal_id then
          M.log_line("warn", dept, proposal_id, "FORGE", {
            "marker_kind=" .. tostring(marker_kind),
            "ignored_author=" .. tostring(parsers_misc._comment_author_login(M, comment) or ""),
            "trusted_bot=" .. tostring(devloop_base.trusted_bot_login()),
          })
        end
      end
    end
  end
end

end

return S
